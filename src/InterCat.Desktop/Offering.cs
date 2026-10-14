using Avalonia.Automation;
using Avalonia.Controls;

namespace InterCat.Desktop;

/// <summary>
/// An action offered, or why it is not (R15): the reason is its tooltip, shown on the disabled control too, and its help
/// text, so the pointer and a screen reader find it where the action is. Offered, the action says what it does, where its
/// label does not already say it. A button, a choice or a box is offered alike.
/// </summary>
internal static class Offering
{
    /// <param name="tip">
    /// What the pointer reads of the offered action where it is more than what it does - its key, a folder it takes when
    /// dropped, why the last attempt failed; null where it is what it does.
    /// </param>
    public static void Offer(Control control, string? whyNot, string? does = null, string? tip = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.IsEnabled = whyNot is null;
        ToolTip.SetShowOnDisabled(control, true);
        ToolTip.SetTip(control, whyNot ?? tip ?? does);
        AutomationProperties.SetHelpText(control, whyNot ?? does);
    }
}
