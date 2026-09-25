using Avalonia.Automation;
using Avalonia.Controls;

namespace InterCat.Desktop.Presentation;

/// <summary>A row a list or combo box shows, and the sentence a screen reader says for it (R15).</summary>
public interface IAccessibleRow
{
    string AccessibleName { get; }
}

/// <summary>
/// Gives every item container of a list or combo box its row's accessible name (R15). Avalonia names an item from its
/// container's automation name, else from a text block that is the whole item template, else from the row's
/// <see cref="object.ToString"/>. A name set inside a template never reaches the item, so a record row was announced as
/// its compiler-written field dump, one "Key = …" after another.
/// </summary>
public static class AccessibleItems
{
    /// <summary>Names each container as it is prepared, and again whenever it is reused for another row.</summary>
    public static void Name(ItemsControl items)
    {
        ArgumentNullException.ThrowIfNull(items);
        items.ContainerPrepared += (_, prepared) => Apply(items, prepared.Container);
    }

    private static void Apply(ItemsControl items, Control container)
    {
        object? row = items.ItemFromContainer(container) ?? (container as ContentControl)?.Content;
        if (row is IAccessibleRow accessible)
        {
            AutomationProperties.SetName(container, accessible.AccessibleName);
        }
        else
        {
            container.ClearValue(AutomationProperties.NameProperty);
        }
    }
}
