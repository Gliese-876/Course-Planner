using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CoursePlanner.Services;

/// <summary>
/// Serializes content dialogs per UI dispatcher. WinUI permits only one
/// ContentDialog per thread, including dialogs owned by different windows.
/// Each request still retains an independent XamlRoot lifetime.
/// </summary>
public static class ContentDialogCoordinator
{
    private static readonly ConditionalWeakTable<DispatcherQueue, DispatcherDialogQueue> Queues = new();
    private static readonly AsyncLocal<DispatcherDialogQueue?> ActiveQueue = new();

    public static Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var root = dialog.XamlRoot;
        if (root is null || !IsRootAvailable(root))
            return Task.FromResult(ContentDialogResult.None);

        var dispatcherQueue = dialog.DispatcherQueue;
        if (dispatcherQueue is null || !dispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Content dialogs must be queued from their owning UI thread.");

        var queue = Queues.GetValue(
            dispatcherQueue,
            static value => new DispatcherDialogQueue(value));

        // A dialog callback that synchronously awaits another dialog on the
        // same dispatcher would otherwise wait on itself forever. Independent
        // operations do not inherit this pump-local marker and are queued.
        if (ReferenceEquals(ActiveQueue.Value, queue))
            return Task.FromResult(ContentDialogResult.None);

        return queue.Enqueue(dialog, root);
    }

    private static bool IsRootAvailable(XamlRoot root) =>
        root.Content is not null &&
        (root.Content is not FrameworkElement content || content.IsLoaded);

    private sealed class DispatcherDialogQueue
    {
        private readonly object _syncRoot = new();
        private readonly DispatcherQueue _dispatcherQueue;
        private Queue<DialogRequest> _requests = new();
        private readonly HashSet<ContentDialog> _registeredDialogs =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<XamlRoot, RootLifetime> _rootLifetimes =
            new(ReferenceEqualityComparer.Instance);
        private DialogRequest? _activeRequest;
        private bool _processing;

        public DispatcherDialogQueue(DispatcherQueue dispatcherQueue) =>
            _dispatcherQueue = dispatcherQueue;

        public Task<ContentDialogResult> Enqueue(ContentDialog dialog, XamlRoot root)
        {
            if (!_dispatcherQueue.HasThreadAccess)
                throw new InvalidOperationException("The dialog dispatcher changed threads while queuing.");

            DialogRequest request;
            var startPump = false;
            lock (_syncRoot)
            {
                if (!IsRootAvailable(root) || !TryRegisterDialog(dialog))
                    return Task.FromResult(ContentDialogResult.None);

                var rootLifetime = GetOrCreateRootLifetime(root);
                if (rootLifetime.IsClosed)
                {
                    _registeredDialogs.Remove(dialog);
                    return Task.FromResult(ContentDialogResult.None);
                }

                rootLifetime.RequestCount++;
                request = new DialogRequest(dialog, rootLifetime);
                _requests.Enqueue(request);
                if (!_processing)
                {
                    _processing = true;
                    startPump = true;
                }
            }

            if (startPump)
                _ = ProcessQueueAsync();

            return request.Completion.Task;
        }

        private bool TryRegisterDialog(ContentDialog dialog) =>
            _registeredDialogs.Add(dialog);

        private RootLifetime GetOrCreateRootLifetime(XamlRoot root)
        {
            if (_rootLifetimes.TryGetValue(root, out var existing))
                return existing;

            var created = new RootLifetime(root, CancelRoot);
            _rootLifetimes.Add(root, created);
            return created;
        }

        private async Task ProcessQueueAsync()
        {
            while (true)
            {
                DialogRequest request;
                lock (_syncRoot)
                {
                    if (_requests.Count == 0)
                    {
                        _processing = false;
                        return;
                    }

                    request = _requests.Dequeue();
                    _activeRequest = request;
                }

                var previousActiveQueue = ActiveQueue.Value;
                try
                {
                    if (request.IsCanceled || !IsRootAvailable(request.RootLifetime.Root))
                    {
                        request.Cancel();
                        continue;
                    }

                    ActiveQueue.Value = this;
                    var result = await request.Dialog.ShowAsync();
                    request.Completion.TrySetResult(result);
                }
                catch (Exception exception) when (
                    (request.IsCanceled || !IsRootAvailable(request.RootLifetime.Root)) &&
                    !RuntimeOperationExceptionPolicy.IsFatal(exception))
                {
                    request.Cancel();
                }
                catch (Exception exception)
                {
                    request.Completion.TrySetException(exception);
                }
                finally
                {
                    ActiveQueue.Value = previousActiveQueue;
                    ReleaseRequest(request);
                }
            }
        }

        private void CancelRoot(RootLifetime rootLifetime)
        {
            List<DialogRequest> canceledRequests = [];
            DialogRequest? activeRequest = null;
            RootLifetime? lifetimeToDispose = null;
            lock (_syncRoot)
            {
                if (rootLifetime.IsClosed)
                    return;

                rootLifetime.IsClosed = true;
                var retainedRequests = new Queue<DialogRequest>(_requests.Count);
                while (_requests.TryDequeue(out var request))
                {
                    if (ReferenceEquals(request.RootLifetime, rootLifetime))
                    {
                        _registeredDialogs.Remove(request.Dialog);
                        rootLifetime.RequestCount--;
                        canceledRequests.Add(request);
                    }
                    else
                    {
                        retainedRequests.Enqueue(request);
                    }
                }

                _requests = retainedRequests;
                if (_activeRequest is not null &&
                    ReferenceEquals(_activeRequest.RootLifetime, rootLifetime))
                {
                    activeRequest = _activeRequest;
                    canceledRequests.Add(activeRequest);
                }

                if (rootLifetime.RequestCount == 0)
                    lifetimeToDispose = RemoveRootLifetime(rootLifetime);
            }

            foreach (var request in canceledRequests)
                request.Cancel();

            lifetimeToDispose?.Dispose();
            if (activeRequest is null)
                return;

            try
            {
                activeRequest.Dialog.Hide();
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
            {
                // This root is already being torn down. Other roots queued on
                // the dispatcher remain intact and continue in FIFO order.
            }
        }

        private void ReleaseRequest(DialogRequest request)
        {
            RootLifetime? lifetimeToDispose = null;
            lock (_syncRoot)
            {
                _registeredDialogs.Remove(request.Dialog);
                if (ReferenceEquals(_activeRequest, request))
                    _activeRequest = null;

                request.RootLifetime.RequestCount--;
                if (request.RootLifetime.RequestCount == 0)
                    lifetimeToDispose = RemoveRootLifetime(request.RootLifetime);
            }

            lifetimeToDispose?.Dispose();
        }

        private RootLifetime? RemoveRootLifetime(RootLifetime rootLifetime)
        {
            if (!_rootLifetimes.TryGetValue(rootLifetime.Root, out var registered) ||
                !ReferenceEquals(registered, rootLifetime))
            {
                return null;
            }

            _rootLifetimes.Remove(rootLifetime.Root);
            return rootLifetime;
        }

        private sealed class RootLifetime : IDisposable
        {
            private readonly Action<RootLifetime> _unloaded;
            private readonly FrameworkElement? _contentRoot;

            public RootLifetime(XamlRoot root, Action<RootLifetime> unloaded)
            {
                Root = root;
                _unloaded = unloaded;
                _contentRoot = root.Content as FrameworkElement;
                if (_contentRoot is not null)
                    _contentRoot.Unloaded += ContentRoot_Unloaded;
            }

            public XamlRoot Root { get; }
            public int RequestCount { get; set; }
            public bool IsClosed { get; set; }

            public void Dispose()
            {
                if (_contentRoot is not null)
                    _contentRoot.Unloaded -= ContentRoot_Unloaded;
            }

            private void ContentRoot_Unloaded(object sender, RoutedEventArgs args) =>
                _unloaded(this);
        }

        private sealed class DialogRequest(ContentDialog dialog, RootLifetime rootLifetime)
        {
            private int _isCanceled;

            public ContentDialog Dialog { get; } = dialog;
            public RootLifetime RootLifetime { get; } = rootLifetime;
            public bool IsCanceled => Volatile.Read(ref _isCanceled) != 0;
            public TaskCompletionSource<ContentDialogResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Cancel()
            {
                Interlocked.Exchange(ref _isCanceled, 1);
                Completion.TrySetResult(ContentDialogResult.None);
            }
        }
    }
}
