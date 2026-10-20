using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace InterCat.Desktop.Presentation;

/// <summary>
/// Page Down and Page Up in every list move the keyboard a page and choose the row they come to, as Down and Up choose
/// theirs, the way a list on Windows takes them (R15): to the last or first row the list shows whole, or from that row a
/// page further. They only scrolled the rows, leaving the keyboard on a row out of sight that the next arrow scrolled back
/// to, and a screen reader heard nothing - in the ranked table at the smallest window, which shows two rows, at every
/// press.
/// </summary>
public static class ListPages
{
    private static bool registered;

    /// <summary>Takes Page Down and Page Up for every list the application shows, once.</summary>
    public static void Register()
    {
        if (registered)
        {
            return;
        }

        registered = true;
        InputElement.KeyDownEvent.AddClassHandler<ListBox>(OnKeyDown, RoutingStrategies.Tunnel);
    }

    private static void OnKeyDown(ListBox list, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.PageDown or Key.PageUp)
            || Target(list, e.Key == Key.PageDown) is not { } target)
        {
            return;
        }

        e.Handled = true;
        list.SelectedIndex = target;
        list.ScrollIntoView(target);
        list.ContainerFromIndex(target)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// The row a page down or up from the row with the keyboard comes to: the last or first row the list shows whole, where
    /// that is past it, or else as many rows further as the list shows whole, less the one kept in sight; none in a list
    /// with no rows.
    /// </summary>
    private static int? Target(ListBox list, bool down)
    {
        int count = list.ItemCount;
        if (count == 0 || list.FindDescendantOfType<ScrollViewer>() is not { } scroll)
        {
            return null;
        }

        int first = count;
        int last = -1;
        foreach (Control container in list.GetRealizedContainers())
        {
            int index = list.IndexFromContainer(container);
            if (index >= 0 && container.IsVisible && container.TranslatePoint(default, scroll) is { } top
                && top.Y >= -0.5 && top.Y + container.Bounds.Height <= scroll.Viewport.Height + 0.5)
            {
                first = Math.Min(first, index);
                last = Math.Max(last, index);
            }
        }

        int from = Current(list);
        int page = last > first ? last - first : 1;
        int target = down
            ? (last > from ? last : from + page)
            : (first < from ? first : from - page);
        return Math.Clamp(target, 0, count - 1);
    }

    /// <summary>The row of <paramref name="list"/> the keyboard is on, else its chosen row; -1 where it has neither.</summary>
    private static int Current(ListBox list) =>
        TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement() is Visual focused
        && (focused as ListBoxItem ?? focused.FindAncestorOfType<ListBoxItem>()) is { } item
        && list.IndexFromContainer(item) is >= 0 and var index
            ? index
            : list.SelectedIndex;
}
