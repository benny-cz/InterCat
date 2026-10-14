using Avalonia.Automation;
using Avalonia.Controls;

namespace InterCat.Desktop;

/// <summary>
/// An action offered, or why it is not (R15): the reason is its tooltip, shown on the disabled button too, and its help
/// text, so the pointer and a screen reader find it where the action is. Offered, the action says what it does, where its
/// label does not already say it.
/// </summary>
internal static class Offering
{
    public static void Offer(Button button, string? whyNot, string? does = null)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.IsEnabled = whyNot is null;
        string? said = whyNot ?? does;
        ToolTip.SetShowOnDisabled(button, true);
        ToolTip.SetTip(button, said);
        AutomationProperties.SetHelpText(button, said);
    }
}
