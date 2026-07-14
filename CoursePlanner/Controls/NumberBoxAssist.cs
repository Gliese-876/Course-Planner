using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CoursePlanner.Controls;

public static class NumberBoxAssist
{
    public static readonly DependencyProperty TextAlignmentProperty =
        DependencyProperty.RegisterAttached(
            "TextAlignment",
            typeof(TextAlignment),
            typeof(NumberBoxAssist),
            new PropertyMetadata(TextAlignment.DetectFromContent, OnTextAlignmentChanged));

    public static TextAlignment GetTextAlignment(NumberBox element) =>
        (TextAlignment)element.GetValue(TextAlignmentProperty);

    public static void SetTextAlignment(NumberBox element, TextAlignment value) =>
        element.SetValue(TextAlignmentProperty, value);

    private static void OnTextAlignmentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not NumberBox numberBox)
            return;

        numberBox.Loaded -= NumberBox_Loaded;
        numberBox.Loaded += NumberBox_Loaded;
        numberBox.GotFocus -= NumberBox_GotFocus;
        numberBox.GotFocus += NumberBox_GotFocus;
        ApplyTextAlignment(numberBox);
    }

    private static void NumberBox_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is NumberBox numberBox)
            ApplyTextAlignment(numberBox);
    }

    private static void NumberBox_GotFocus(object sender, RoutedEventArgs args)
    {
        if (sender is not NumberBox numberBox)
            return;

        ApplyTextAlignment(numberBox);
        numberBox.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.High,
            () =>
            {
                if (numberBox.IsLoaded)
                    ApplyTextAlignment(numberBox);
            });
    }

    private static void ApplyTextAlignment(NumberBox numberBox)
    {
        numberBox.ApplyTemplate();
        var inputBox = FindDescendant<TextBox>(numberBox, "InputBox")
                       ?? FindDescendant<TextBox>(numberBox);
        if (inputBox is null)
            return;

        inputBox.TextAlignment = GetTextAlignment(numberBox);
        inputBox.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        inputBox.VerticalContentAlignment = VerticalAlignment.Center;

        if (GetTextAlignment(numberBox) == TextAlignment.Center &&
            FindDescendant<FrameworkElement>(inputBox, "DeleteButton") is { } deleteButton)
        {
            // NumberBox reserves a 40-DIP column for the TextBox clear button
            // while editing. A short centered value would otherwise shift left
            // as soon as focus enters the control.
            deleteButton.MinWidth = 0;
            deleteButton.MaxWidth = 0;
            deleteButton.Width = 0;
            deleteButton.Margin = new Thickness(0);
            deleteButton.IsHitTestVisible = false;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, string? name = null) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match &&
                (name is null || match is FrameworkElement element && element.Name == name))
            {
                return match;
            }

            var nested = FindDescendant<T>(child, name);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}
