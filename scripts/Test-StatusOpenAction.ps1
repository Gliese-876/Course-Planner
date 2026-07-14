param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$AppPid,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ("StatusOpenActionNative" -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public struct StatusOpenActionRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StatusOpenActionMouseInput
{
    public int Dx;
    public int Dy;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public UIntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StatusOpenActionKeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public UIntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StatusOpenActionHardwareInput
{
    public uint Message;
    public ushort ParameterLow;
    public ushort ParameterHigh;
}

[StructLayout(LayoutKind.Explicit)]
internal struct StatusOpenActionInputUnion
{
    [FieldOffset(0)] public StatusOpenActionMouseInput Mouse;
    [FieldOffset(0)] public StatusOpenActionKeyboardInput Keyboard;
    [FieldOffset(0)] public StatusOpenActionHardwareInput Hardware;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StatusOpenActionInput
{
    public uint Type;
    public StatusOpenActionInputUnion Data;
}

public sealed class StatusOpenActionWindow
{
    public long Hwnd { get; set; }
    public long OwnerHwnd { get; set; }
    public int ProcessId { get; set; }
    public string Title { get; set; }
    public string ClassName { get; set; }
}

public static class StatusOpenActionNative
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out StatusOpenActionRect rect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        StatusOpenActionInput[] inputs,
        int inputSize);

    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;

    private const uint KeyboardInput = 1;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;

    public static bool IsOwnedBy(IntPtr hwnd, IntPtr expectedOwner)
    {
        var current = hwnd;
        for (var depth = 0; depth < 32; depth++)
        {
            current = GetWindow(current, 4); // GW_OWNER
            if (current == IntPtr.Zero)
                return false;
            if (current == expectedOwner)
                return true;
        }
        return false;
    }

    public static void ReplaceFocusedText(string value)
    {
        SendKeyboardInputs(new[]
        {
            VirtualKeyInput(0x11, false), // Ctrl down
            VirtualKeyInput(0x41, false), // A down
            VirtualKeyInput(0x41, true),
            VirtualKeyInput(0x11, true)
        });

        foreach (char character in value)
        {
            SendKeyboardInputs(new[]
            {
                UnicodeInput(character, false),
                UnicodeInput(character, true)
            });
        }
    }

    private static StatusOpenActionInput VirtualKeyInput(ushort virtualKey, bool keyUp)
    {
        return new StatusOpenActionInput
        {
            Type = KeyboardInput,
            Data = new StatusOpenActionInputUnion
            {
                Keyboard = new StatusOpenActionKeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyUp : 0
                }
            }
        };
    }

    private static StatusOpenActionInput UnicodeInput(char character, bool keyUp)
    {
        return new StatusOpenActionInput
        {
            Type = KeyboardInput,
            Data = new StatusOpenActionInputUnion
            {
                Keyboard = new StatusOpenActionKeyboardInput
                {
                    ScanCode = character,
                    Flags = Unicode | (keyUp ? KeyUp : 0)
                }
            }
        };
    }

    private static void SendKeyboardInputs(StatusOpenActionInput[] inputs)
    {
        var sent = SendInput(
            unchecked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf(typeof(StatusOpenActionInput)));
        if (sent != inputs.Length)
            throw new InvalidOperationException(
                "SendInput wrote " + sent + " of " + inputs.Length + " keyboard events (Win32 " +
                Marshal.GetLastWin32Error() + ").");
    }

    public static StatusOpenActionWindow[] Snapshot()
    {
        var windows = new List<StatusOpenActionWindow>();
        EnumWindows(delegate(IntPtr hwnd, IntPtr parameter)
        {
            if (!IsWindowVisible(hwnd))
                return true;

            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            var title = new StringBuilder(512);
            var className = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            GetClassName(hwnd, className, className.Capacity);
            windows.Add(new StatusOpenActionWindow
            {
                Hwnd = hwnd.ToInt64(),
                OwnerHwnd = GetWindow(hwnd, 4).ToInt64(), // GW_OWNER
                ProcessId = unchecked((int)processId),
                Title = title.ToString(),
                ClassName = className.ToString()
            });
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
}
"@
}

$OpenNames = @("Open", "$([char]0x6253)$([char]0x5F00)")
$ExportNames = @("Export", "$([char]0x5BFC)$([char]0x51FA)")
$SaveNamePattern = "^(Save|$([char]0x4FDD)$([char]0x5B58))(\s|\(|$)"
$GenericCompletionPattern = "^(Completed|Complete|Done|Success|$([char]0x5DF2)$([char]0x5B8C)$([char]0x6210)|$([char]0x5B8C)$([char]0x6210)|$([char]0x6210)$([char]0x529F))[.!$([char]0x3002)$([char]0xFF01)]?$"
$StatusIconTextPattern = "(?i)(icon|$([char]0x56FE)$([char]0x6807))[`"'$([char]0x201D)]?$"
$checks = [System.Collections.Generic.List[object]]::new()
$evidence = [ordered]@{}
$currentStep = "initialization"
$pickerHwnd = [IntPtr]::Zero
$mainWindowHwnd = [IntPtr]::Zero
$outputFile = $null
$saveInvokedAt = $null
$screenshotPath = $null

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $script:currentStep = $Name
    $started = [Diagnostics.Stopwatch]::StartNew()
    & $Action
    $started.Stop()
    $script:checks.Add([pscustomobject]@{
        name = $Name
        status = "PASS"
        elapsedMilliseconds = $started.ElapsedMilliseconds
    })
}

function Get-RectangleObject {
    param([Parameter(Mandatory = $true)]$Rectangle)

    [ordered]@{
        left = [Math]::Round($Rectangle.Left, 2)
        top = [Math]::Round($Rectangle.Top, 2)
        right = [Math]::Round($Rectangle.Right, 2)
        bottom = [Math]::Round($Rectangle.Bottom, 2)
        width = [Math]::Round($Rectangle.Width, 2)
        height = [Math]::Round($Rectangle.Height, 2)
    }
}

function Test-ElementVisible {
    param([AllowNull()][System.Windows.Automation.AutomationElement]$Element)

    if ($null -eq $Element) {
        return $false
    }
    try {
        $bounds = $Element.Current.BoundingRectangle
        return -not $Element.Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0
    }
    catch {
        return $false
    }
}

function Get-MainRoot {
    [System.Windows.Automation.AutomationElement]::FromHandle($script:mainWindowHwnd)
}

function Find-DescendantByAutomationId {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][string]$AutomationId
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-MainElement {
    param(
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [int]$Timeout = 5000,
        [switch]$Visible
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $lastError = $null
    do {
        try {
            $element = Find-DescendantByAutomationId (Get-MainRoot) $AutomationId
            if ($null -ne $element -and (-not $Visible -or (Test-ElementVisible $element))) {
                return $element
            }
        }
        catch {
            $lastError = $_
        }
        Start-Sleep -Milliseconds 40
    } while ((Get-Date) -lt $deadline)

    $detail = if ($null -eq $lastError) { "" } else { " Last UIA error: $lastError" }
    throw "UI element '$AutomationId' was not available within $Timeout ms.$detail"
}

function Find-VisibleProcessElementByAutomationId {
    param(
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $AppPid)
    $idCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $idCondition)
    do {
        try {
            $matches = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $condition)
            for ($index = 0; $index -lt $matches.Count; $index++) {
                $candidate = $matches.Item($index)
                if (Test-ElementVisible $candidate) {
                    return $candidate
                }
            }
        }
        catch {
        }
        Start-Sleep -Milliseconds 40
    } while ((Get-Date) -lt $deadline)
    throw "Visible process element '$AutomationId' was not available within $Timeout ms."
}

function Find-VisibleProcessElementByNames {
    param(
        [Parameter(Mandatory = $true)][string[]]$Names,
        [Parameter(Mandatory = $true)][System.Windows.Automation.ControlType]$ControlType,
        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $AppPid)
    do {
        try {
            $matches = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $processCondition)
            for ($index = 0; $index -lt $matches.Count; $index++) {
                $candidate = $matches.Item($index)
                if ($candidate.Current.ControlType -eq $ControlType -and
                    $Names -contains $candidate.Current.Name -and
                    (Test-ElementVisible $candidate)) {
                    return $candidate
                }
            }
        }
        catch {
        }
        Start-Sleep -Milliseconds 40
    } while ((Get-Date) -lt $deadline)
    throw "No visible $ControlType named '$($Names -join "' or '")' appeared within $Timeout ms."
}

function Invoke-Element {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element)

    if (-not $Element.Current.IsEnabled) {
        throw "UI element '$($Element.Current.AutomationId)' ('$($Element.Current.Name)') is disabled."
    }

    $pattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$pattern)) {
        ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
        Start-Sleep -Milliseconds 100
        return
    }
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$pattern)) {
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        Start-Sleep -Milliseconds 100
        return
    }

    $bounds = $Element.Current.BoundingRectangle
    if ($Element.Current.IsOffscreen -or $bounds.Width -le 0 -or $bounds.Height -le 0) {
        throw "UI element '$($Element.Current.AutomationId)' has neither an invokable pattern nor a click target."
    }
    [StatusOpenActionNative]::SetCursorPos(
        [int][Math]::Round($bounds.Left + ($bounds.Width / 2)),
        [int][Math]::Round($bounds.Top + ($bounds.Height / 2))) | Out-Null
    [StatusOpenActionNative]::mouse_event(
        [StatusOpenActionNative]::LeftDown,
        0,
        0,
        0,
        [UIntPtr]::Zero)
    [StatusOpenActionNative]::mouse_event(
        [StatusOpenActionNative]::LeftUp,
        0,
        0,
        0,
        [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
}

function Get-DialogPrimaryButton {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Dialog)

    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $Dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $namedCandidates = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $buttons.Count; $index++) {
        $button = $buttons.Item($index)
        if (-not (Test-ElementVisible $button) -or -not $button.Current.IsEnabled) {
            continue
        }
        if ($button.Current.AutomationId -match 'PrimaryButton') {
            return $button
        }
        if ($ExportNames -contains $button.Current.Name) {
            $namedCandidates.Add($button)
        }
    }
    if ($namedCandidates.Count -eq 1) {
        return $namedCandidates[0]
    }
    throw "The export ContentDialog did not expose one enabled primary Export button."
}

function Get-PickerFileNameElement {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root)

    foreach ($id in @("1001", "1148", "FileNameControlHost")) {
        $element = Find-DescendantByAutomationId $Root $id
        if ($null -ne $element -and (Test-ElementVisible $element)) {
            return $element
        }
    }

    $editCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edits = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
    for ($index = 0; $index -lt $edits.Count; $index++) {
        $edit = $edits.Item($index)
        if ((Test-ElementVisible $edit) -and
            ($edit.Current.AutomationId -match 'FileName|1001|1148' -or
             $edit.Current.Name -match 'File name|$([char]0x6587)$([char]0x4EF6)$([char]0x540D)')) {
            return $edit
        }
    }
    return $null
}

function Get-PickerWindow {
    param(
        [Parameter(Mandatory = $true)][System.Collections.Generic.HashSet[long]]$BaselineHandles,
        [int]$Timeout = 10000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    do {
        foreach ($window in [StatusOpenActionNative]::Snapshot()) {
            if ($BaselineHandles.Contains($window.Hwnd) -and $window.Hwnd -ne $script:mainWindowHwnd.ToInt64()) {
                continue
            }
            if ($window.Hwnd -eq $script:mainWindowHwnd.ToInt64()) {
                continue
            }
            if (-not [StatusOpenActionNative]::IsOwnedBy(
                    [IntPtr]$window.Hwnd,
                    $script:mainWindowHwnd)) {
                continue
            }
            try {
                $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$window.Hwnd)
                $fileNameElement = Get-PickerFileNameElement $root
                if ($null -ne $fileNameElement) {
                    return [pscustomobject]@{
                        Hwnd = [IntPtr]$window.Hwnd
                        Root = $root
                        FileNameElement = $fileNameElement
                        ProcessId = $window.ProcessId
                        Title = $window.Title
                        ClassName = $window.ClassName
                    }
                }
            }
            catch {
            }
        }
        Start-Sleep -Milliseconds 75
    } while ((Get-Date) -lt $deadline)
    throw "The FileSavePicker (including transient PickerHost windows) did not appear within $Timeout ms."
}

function Set-PickerFileName {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element,
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$PickerRoot,
        [Parameter(Mandatory = $true)][IntPtr]$PickerHwnd,
        [Parameter(Mandatory = $true)][string]$FullPath
    )

    $valueElement = $Element
    $valuePattern = $null
    if (-not $valueElement.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$valuePattern)) {
        $editCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)
        $edits = $Element.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $editCondition)
        for ($index = 0; $index -lt $edits.Count; $index++) {
            $candidate = $edits.Item($index)
            $candidatePattern = $null
            if ((Test-ElementVisible $candidate) -and
                $candidate.TryGetCurrentPattern(
                    [System.Windows.Automation.ValuePattern]::Pattern,
                    [ref]$candidatePattern)) {
                $valueElement = $candidate
                $valuePattern = $candidatePattern
                break
            }
        }
    }
    if ($null -eq $valuePattern) {
        $bounds = $valueElement.Current.BoundingRectangle
        if ($valueElement.Current.IsOffscreen -or $bounds.Width -le 0 -or $bounds.Height -le 0) {
            throw "The FileSavePicker filename control exposes neither ValuePattern nor a visible keyboard target."
        }
        [StatusOpenActionNative]::SetForegroundWindow($PickerHwnd) | Out-Null
        $foregroundDeadline = (Get-Date).AddSeconds(2)
        while ([StatusOpenActionNative]::GetForegroundWindow() -ne $PickerHwnd -and
               (Get-Date) -lt $foregroundDeadline) {
            Start-Sleep -Milliseconds 25
        }
        if ([StatusOpenActionNative]::GetForegroundWindow() -ne $PickerHwnd) {
            throw "The verified FileSavePicker could not be brought to the foreground; refusing to type."
        }

        [StatusOpenActionNative]::SetCursorPos(
            [int][Math]::Round($bounds.Left + ($bounds.Width / 2)),
            [int][Math]::Round($bounds.Top + ($bounds.Height / 2))) | Out-Null
        [StatusOpenActionNative]::mouse_event(
            [StatusOpenActionNative]::LeftDown,
            0,
            0,
            0,
            [UIntPtr]::Zero)
        [StatusOpenActionNative]::mouse_event(
            [StatusOpenActionNative]::LeftUp,
            0,
            0,
            0,
            [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 100

        if ([StatusOpenActionNative]::GetForegroundWindow() -ne $PickerHwnd) {
            throw "The FileSavePicker lost foreground focus before filename input; refusing to type."
        }
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($null -eq $focused -or $focused.Current.ProcessId -ne $PickerRoot.Current.ProcessId) {
            throw "Keyboard focus did not remain inside the verified FileSavePicker; refusing to type."
        }
        $focusedBounds = $focused.Current.BoundingRectangle
        $focusedCenterX = $focusedBounds.Left + ($focusedBounds.Width / 2)
        $focusedCenterY = $focusedBounds.Top + ($focusedBounds.Height / 2)
        if ($focusedBounds.Width -le 0 -or $focusedBounds.Height -le 0 -or
            $focusedCenterX -lt ($bounds.Left - 1) -or $focusedCenterX -gt ($bounds.Right + 1) -or
            $focusedCenterY -lt ($bounds.Top - 1) -or $focusedCenterY -gt ($bounds.Bottom + 1)) {
            throw "Keyboard focus is not within the verified FileSavePicker filename field; refusing to type."
        }

        [StatusOpenActionNative]::ReplaceFocusedText($FullPath)
        Start-Sleep -Milliseconds 150
        return
    }
    $value = [System.Windows.Automation.ValuePattern]$valuePattern
    if ($value.Current.IsReadOnly) {
        throw "The FileSavePicker filename control is read-only."
    }
    $value.SetValue($FullPath)
    Start-Sleep -Milliseconds 100
    $observed = $value.Current.Value
    if ([string]::IsNullOrWhiteSpace($observed)) {
        throw "The FileSavePicker cleared the requested full output path."
    }
}

function Get-PickerSaveButton {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root)

    # The legacy UIAutomationClient bridge exposes classic common-dialog buttons
    # as ControlType.Pane even though their Win32 class is Button. Search the
    # verified picker subtree by semantic name/class instead of assuming a peer type.
    $buttons = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $fallback = $null
    for ($index = 0; $index -lt $buttons.Count; $index++) {
        $button = $buttons.Item($index)
        if (-not (Test-ElementVisible $button) -or -not $button.Current.IsEnabled) {
            continue
        }
        if ($button.Current.Name -match $SaveNamePattern) {
            return $button
        }
        if ($button.Current.AutomationId -eq "1" -and $button.Current.ClassName -eq "Button") {
            $fallback = $button
        }
    }
    if ($null -ne $fallback) {
        return $fallback
    }
    throw "The FileSavePicker did not expose an enabled Save button in English or Chinese."
}

function Wait-FileCreated {
    param([Parameter(Mandatory = $true)][string]$Path, [int]$Timeout = 15000)

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    do {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            $item = Get-Item -LiteralPath $Path
            if ($item.Length -gt 0) {
                return $item
            }
        }
        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)
    throw "The export did not create a non-empty JSON file at '$Path' within $Timeout ms."
}

function Get-StatusCloseButton {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Status,
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$OpenButton
    )

    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $Status.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $candidates = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $buttons.Count; $index++) {
        $button = $buttons.Item($index)
        if ($button.Current.AutomationId -eq $OpenButton.Current.AutomationId -or
            -not (Test-ElementVisible $button) -or
            -not $button.Current.IsEnabled) {
            continue
        }
        $bounds = $button.Current.BoundingRectangle
        $candidates.Add([pscustomobject]@{ bounds = $bounds; element = $button })
    }
    if ($candidates.Count -ne 1) {
        throw "The visible StatusBar must expose exactly one enabled non-Open button; found $($candidates.Count)."
    }
    $candidate = $candidates[0]
    $openBounds = $OpenButton.Current.BoundingRectangle
    $verticalOverlap = [Math]::Min($openBounds.Bottom, $candidate.bounds.Bottom) -
        [Math]::Max($openBounds.Top, $candidate.bounds.Top)
    if ($verticalOverlap -le 0) {
        throw "The candidate close button does not vertically overlap StatusOpenButton."
    }
    $candidate.element
}

function Get-StatusMessageTexts {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Status,
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$OpenButton
    )

    $openBounds = $OpenButton.Current.BoundingRectangle
    $textCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $textElements = $Status.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
    $texts = [System.Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $textElements.Count; $index++) {
        $textElement = $textElements.Item($index)
        if (-not (Test-ElementVisible $textElement) -or [string]::IsNullOrWhiteSpace($textElement.Current.Name)) {
            continue
        }
        $bounds = $textElement.Current.BoundingRectangle
        $insideOpenButton =
            $bounds.Left -ge ($openBounds.Left - 1) -and
            $bounds.Right -le ($openBounds.Right + 1) -and
            $bounds.Top -ge ($openBounds.Top - 1) -and
            $bounds.Bottom -le ($openBounds.Bottom + 1)
        if (-not $insideOpenButton -and -not $texts.Contains($textElement.Current.Name)) {
            $texts.Add($textElement.Current.Name)
        }
    }
    @($texts)
}

function Save-MainWindowScreenshot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $rect = New-Object StatusOpenActionRect
    if (-not [StatusOpenActionNative]::GetWindowRect($script:mainWindowHwnd, [ref]$rect)) {
        throw "GetWindowRect failed for the app's main window."
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) {
        throw "The app's main window has invalid bounds ${width}x${height}."
    }

    [StatusOpenActionNative]::SetForegroundWindow($script:mainWindowHwnd) | Out-Null
    $foregroundDeadline = (Get-Date).AddSeconds(2)
    while ([StatusOpenActionNative]::GetForegroundWindow() -ne $script:mainWindowHwnd -and
           (Get-Date) -lt $foregroundDeadline) {
        Start-Sleep -Milliseconds 25
    }
    if ([StatusOpenActionNative]::GetForegroundWindow() -ne $script:mainWindowHwnd) {
        throw "The app main window could not be foregrounded; refusing to capture a potentially obstructed desktop region."
    }

    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $rect.Left,
            $rect.Top,
            0,
            0,
            [System.Drawing.Size]::new($width, $height),
            [System.Drawing.CopyPixelOperation]::SourceCopy)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Wait-StatusGone {
    param([int]$Timeout = 1400)

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $lastAutomationError = $null
    do {
        if ($null -eq (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) {
            throw "App PID $AppPid exited while waiting for StatusBar to close."
        }
        try {
            $status = Find-DescendantByAutomationId (Get-MainRoot) "StatusBar"
            if ($null -eq $status -or -not (Test-ElementVisible $status)) {
                $watch.Stop()
                return $watch.ElapsedMilliseconds
            }
            $lastAutomationError = $null
        }
        catch {
            $lastAutomationError = "$_"
        }
        Start-Sleep -Milliseconds 20
    } while ((Get-Date) -lt $deadline)
    $errorSuffix = if ($null -eq $lastAutomationError) { "" } else { " Last UIA error: $lastAutomationError" }
    throw "StatusBar could not be proven gone within $Timeout ms after invoking Open.$errorSuffix"
}

function Get-NewTopLevelWindowEvidence {
    param(
        [Parameter(Mandatory = $true)][System.Collections.Generic.HashSet[long]]$BaselineHandles,
        [Parameter(Mandatory = $true)][string]$ExpectedFileName,
        [int]$Timeout = 1200
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $observed = [System.Collections.Generic.Dictionary[long, object]]::new()
    do {
        foreach ($window in [StatusOpenActionNative]::Snapshot()) {
            if (-not $BaselineHandles.Contains($window.Hwnd) -and
                $window.ProcessId -ne $AppPid -and
                $window.Hwnd -ne $script:pickerHwnd.ToInt64() -and
                $window.Title.IndexOf($ExpectedFileName, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $observed[$window.Hwnd] = [ordered]@{
                    hwnd = $window.Hwnd
                    processId = $window.ProcessId
                    title = $window.Title
                    className = $window.ClassName
                }
            }
        }
        Start-Sleep -Milliseconds 80
    } while ((Get-Date) -lt $deadline)
    @($observed.Values)
}

function Get-CoursePlannerMainWindowHandle {
    foreach ($window in [StatusOpenActionNative]::Snapshot()) {
        if ($window.ProcessId -ne $AppPid) {
            continue
        }
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$window.Hwnd)
            if ($null -ne (Find-DescendantByAutomationId $root "RootNavigation") -and
                $null -ne (Find-DescendantByAutomationId $root "PlannerItem")) {
                return [IntPtr]$window.Hwnd
            }
        }
        catch {
        }
    }
    throw "No top-level window for app PID $AppPid exposed the Course Planner shell UI."
}

function Write-ResultAndExit {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("PASS", "FAIL")][string]$Status,
        [AllowNull()][string]$Failure
    )

    $resultPath = Join-Path $OutputDirectory "Test-StatusOpenAction-results.json"
    $result = [ordered]@{
        test = "StatusBar saved-file Open action"
        status = $Status
        timestampUtc = [DateTime]::UtcNow.ToString("o")
        appPid = $AppPid
        failedAt = if ($Status -eq "FAIL") { $script:currentStep } else { $null }
        failure = $Failure
        checks = @($script:checks)
        evidence = $script:evidence
    }
    $json = $result | ConvertTo-Json -Depth 10
    Set-Content -LiteralPath $resultPath -Value $json -Encoding utf8
    Write-Output $json
    if ($Status -eq "FAIL") {
        exit 1
    }
    exit 0
}

try {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
    $process = Get-Process -Id $AppPid -ErrorAction Stop
    if ($process.HasExited) {
        throw "App PID $AppPid has already exited."
    }

    Invoke-Step "Attach to the existing app and navigate to Planner" {
        $process.Refresh()
        $script:mainWindowHwnd = Get-CoursePlannerMainWindowHandle
        Get-MainRoot | Out-Null

        $plannerReady = Find-DescendantByAutomationId (Get-MainRoot) "ToggleLibraryButton"
        if ($null -eq $plannerReady) {
            $plannerItem = Wait-MainElement "PlannerItem" -Visible
            Invoke-Element $plannerItem
            Wait-MainElement "ToggleLibraryButton" -Timeout 7000 -Visible | Out-Null
        }
        $exportButton = Find-DescendantByAutomationId (Get-MainRoot) "ExportButton"
        $moreButton = Find-DescendantByAutomationId (Get-MainRoot) "ToolbarMoreButton"
        if (-not (Test-ElementVisible $exportButton) -and -not (Test-ElementVisible $moreButton)) {
            throw "Planner is visible, but neither Export nor its More overflow host is available."
        }
    }

    $baselineBeforePicker = [System.Collections.Generic.HashSet[long]]::new()
    foreach ($window in [StatusOpenActionNative]::Snapshot()) {
        $baselineBeforePicker.Add($window.Hwnd) | Out-Null
    }

    Invoke-Step "Open Export whether direct or folded into More" {
        $exportButton = Find-DescendantByAutomationId (Get-MainRoot) "ExportButton"
        if (Test-ElementVisible $exportButton) {
            Invoke-Element $exportButton
        }
        else {
            $moreButton = Wait-MainElement "ToolbarMoreButton" -Visible
            if (-not $moreButton.Current.IsEnabled) {
                throw "Export is folded into More, but ToolbarMoreButton is disabled."
            }
            Invoke-Element $moreButton
            $menuLookup = @{
                Names = $ExportNames
                ControlType = [System.Windows.Automation.ControlType]::MenuItem
                Timeout = 3000
            }
            $exportMenuItem = Find-VisibleProcessElementByNames @menuLookup
            Invoke-Element $exportMenuItem
        }
        Wait-MainElement "ImportExportDialog" -Timeout 5000 -Visible | Out-Null
    }

    Invoke-Step "Choose ExportContentCurrentPlan and submit the ContentDialog" {
        $combo = Wait-MainElement "ExportContentComboBox" -Timeout 5000 -Visible
        $expandPattern = $null
        if (-not $combo.TryGetCurrentPattern(
                [System.Windows.Automation.ExpandCollapsePattern]::Pattern,
                [ref]$expandPattern)) {
            throw "ExportContentComboBox does not expose ExpandCollapsePattern."
        }
        ([System.Windows.Automation.ExpandCollapsePattern]$expandPattern).Expand()
        $currentPlanItem = Find-VisibleProcessElementByAutomationId "ExportContentCurrentPlan" -Timeout 4000
        if (-not $currentPlanItem.Current.IsEnabled) {
            throw "ExportContentCurrentPlan is disabled; this UI run needs an open plan and selected semester."
        }
        Invoke-Element $currentPlanItem
        try {
            ([System.Windows.Automation.ExpandCollapsePattern]$expandPattern).Collapse()
        }
        catch {
        }

        $dialog = Wait-MainElement "ImportExportDialog" -Timeout 3000 -Visible
        $primaryButton = Get-DialogPrimaryButton $dialog
        Invoke-Element $primaryButton
    }

    Invoke-Step "Save a unique JSON through the transient FileSavePicker" {
        $picker = Get-PickerWindow $baselineBeforePicker -Timeout 10000
        $script:pickerHwnd = $picker.Hwnd
        $uniqueName = "status-open-action-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8)).json"
        $script:outputFile = Join-Path $OutputDirectory $uniqueName
        Set-PickerFileName `
            -Element $picker.FileNameElement `
            -PickerRoot $picker.Root `
            -PickerHwnd $picker.Hwnd `
            -FullPath $script:outputFile
        $saveButton = Get-PickerSaveButton $picker.Root
        $script:saveInvokedAt = [Diagnostics.Stopwatch]::StartNew()
        Invoke-Element $saveButton
        $created = Wait-FileCreated $script:outputFile -Timeout 15000
        try {
            $parsedExport = Get-Content -Raw -LiteralPath $created.FullName |
                ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            throw "The exported file is not valid JSON: $_"
        }
        $propertyNames = @($parsedExport.PSObject.Properties.Name)
        if ($propertyNames -notcontains "kind" -or
            $parsedExport.kind -ne "selectionPlan" -or
            $propertyNames -notcontains "schemaVersion" -or
            $propertyNames -notcontains "plan" -or
            $null -eq $parsedExport.plan) {
            throw "The exported JSON does not contain the expected selection-plan schema."
        }
        $script:evidence.outputFile = $created.FullName
        $script:evidence.outputFileBytes = $created.Length
        $script:evidence.outputKind = $parsedExport.kind
        $script:evidence.outputSchemaVersion = $parsedExport.schemaVersion
        $script:evidence.picker = [ordered]@{
            hwnd = $picker.Hwnd.ToInt64()
            processId = $picker.ProcessId
            title = $picker.Title
            className = $picker.ClassName
        }
    }

    $status = $null
    Invoke-Step "Assert saved-file banner semantics, Open placement, and screenshot" {
        $status = Wait-MainElement "StatusBar" -Timeout 8000 -Visible
        $openButton = Wait-MainElement "StatusOpenButton" -Timeout 1000 -Visible
        if (-not $openButton.Current.IsEnabled) {
            throw "StatusOpenButton is visible but disabled before the banner starts closing."
        }
        if ($OpenNames -notcontains $openButton.Current.Name) {
            throw "StatusOpenButton has unexpected localized name '$($openButton.Current.Name)'."
        }

        $closeButton = Get-StatusCloseButton $status $openButton
        $openBounds = $openButton.Current.BoundingRectangle
        $closeBounds = $closeButton.Current.BoundingRectangle
        if ($openBounds.Right -gt ($closeBounds.Left + 0.5)) {
            throw "StatusOpenButton is not wholly left of the close button (Open right=$($openBounds.Right), close left=$($closeBounds.Left))."
        }

        $visibleStatusTexts = @(Get-StatusMessageTexts $status $openButton)
        # WinUI exposes the retained severity glyph as semantic text (for
        # example, “Success” icon / “成功”图标). It is an accessibility signal,
        # not the redundant generic title removed by this behavior change.
        $messageTexts = @($visibleStatusTexts | Where-Object { $_ -notmatch $StatusIconTextPattern })
        $fileName = [IO.Path]::GetFileName($script:outputFile)
        $expectedMessages = @("Saved $fileName", "$([char]0x5DF2)$([char]0x4FDD)$([char]0x5B58) $fileName")
        $unexpectedCompletion = @($messageTexts | Where-Object { $_ -match $GenericCompletionPattern })
        if ($unexpectedCompletion.Count -gt 0) {
            throw "StatusBar still exposes a redundant completion title: '$($unexpectedCompletion -join "', '")'."
        }
        if ($messageTexts.Count -ne 1 -or $expectedMessages -notcontains $messageTexts[0]) {
            throw "StatusBar must expose only its actual saved-file message (plus icon/action controls). Visible text: '$($messageTexts -join "', '")'."
        }

        $script:screenshotPath = Join-Path $OutputDirectory "StatusOpenAction-saved-banner.png"
        Save-MainWindowScreenshot $script:screenshotPath
        $script:evidence.statusMessage = $messageTexts[0]
        $script:evidence.openButtonName = $openButton.Current.Name
        $script:evidence.openBounds = Get-RectangleObject $openBounds
        $script:evidence.closeBounds = Get-RectangleObject $closeBounds
        $script:evidence.screenshot = $script:screenshotPath
    }

    Invoke-Step "Invoke Open and prove Launcher acceptance from prompt banner closure" {
        if ($null -eq $script:saveInvokedAt) {
            throw "The Save invocation clock was not initialized."
        }
        if ($script:saveInvokedAt.ElapsedMilliseconds -gt 1200) {
            throw "Save and banner inspection took $($script:saveInvokedAt.ElapsedMilliseconds) ms; invoking Open now would overlap the three-second auto-close and make Launcher acceptance ambiguous."
        }
        $openButton = Wait-MainElement "StatusOpenButton" -Timeout 300 -Visible
        $windowsBeforeOpen = [System.Collections.Generic.HashSet[long]]::new()
        foreach ($window in [StatusOpenActionNative]::Snapshot()) {
            $windowsBeforeOpen.Add($window.Hwnd) | Out-Null
        }
        $ageAtInvoke = $script:saveInvokedAt.ElapsedMilliseconds
        if ($ageAtInvoke -gt 1200) {
            throw "Open setup extended the banner age to $ageAtInvoke ms; Launcher acceptance would be ambiguous with auto-close."
        }
        $invokeWatch = [Diagnostics.Stopwatch]::StartNew()
        Invoke-Element $openButton
        $openAfterInvoke = Find-DescendantByAutomationId (Get-MainRoot) "StatusOpenButton"
        $openDisabledOrGone = $null -eq $openAfterInvoke -or
            -not (Test-ElementVisible $openAfterInvoke) -or
            -not $openAfterInvoke.Current.IsEnabled
        if (-not $openDisabledOrGone) {
            throw "StatusOpenButton remained enabled after invocation, so the async launch was not accepted by the handler."
        }
        $closedAfter = Wait-StatusGone -Timeout 1400
        $invokeWatch.Stop()
        if ($script:saveInvokedAt.ElapsedMilliseconds -ge 2800) {
            throw "StatusBar closure occurred too close to the three-second natural expiry to prove Open caused it."
        }
        if ($null -eq (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) {
            throw "The app exited while handling StatusOpenButton."
        }

        $script:evidence.bannerAgeAtOpenInvokeMilliseconds = $ageAtInvoke
        $script:evidence.bannerClosedAfterOpenMilliseconds = $closedAfter
        $script:evidence.openInvokeToBannerCloseMilliseconds = $invokeWatch.ElapsedMilliseconds
        $script:evidence.bannerAgeAtProvenCloseMilliseconds = $script:saveInvokedAt.ElapsedMilliseconds
        $script:evidence.openDisabledOrGoneImmediately = $openDisabledOrGone
        $script:evidence.launcherAccepted = $true
        $script:evidence.foregroundWindowAfterOpen = [StatusOpenActionNative]::GetForegroundWindow().ToInt64()
        $script:evidence.matchingNewTopLevelWindowsAfterOpen = @(
            Get-NewTopLevelWindowEvidence `
                -BaselineHandles $windowsBeforeOpen `
                -ExpectedFileName ([IO.Path]::GetFileName($script:outputFile)) `
                -Timeout 1200)
    }

    Write-ResultAndExit -Status PASS -Failure $null
}
catch {
    $checks.Add([pscustomobject]@{
        name = $currentStep
        status = "FAIL"
        detail = "$_"
    })
    try {
        if ($pickerHwnd -ne [IntPtr]::Zero) {
            $evidence.pickerStillVisibleOnFailure = @(
                [StatusOpenActionNative]::Snapshot() |
                    Where-Object { $_.Hwnd -eq $pickerHwnd.ToInt64() }).Count -gt 0
        }
    }
    catch {
    }
    Write-ResultAndExit -Status FAIL -Failure "$_"
}
