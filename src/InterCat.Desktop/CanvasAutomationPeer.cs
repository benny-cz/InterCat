using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace InterCat.Desktop;

/// <summary>
/// A drawn pane as UI Automation sees it (R15, §6.5): a focusable custom control with a role word of its own, help text
/// that says how it is used from the keyboard and which table lists what it draws, and a status stating what it draws
/// now. Avalonia's default peer gave a canvas no control type at all, which screen readers skip or read as unknown.
/// </summary>
internal sealed class CanvasAutomationPeer(Control owner, string role, string help, Func<string?> status)
    : ControlAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

    protected override string GetLocalizedControlTypeCore() => role;

    protected override string GetHelpTextCore() => help;

    protected override string? GetItemStatusCore() => status();

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    /// <summary>
    /// Renames a drawn pane, and tells a screen reader, which says a focused element's new name once told of it: a
    /// control's peer does not report the change of its own accord. With no peer, nothing is listening.
    /// </summary>
    internal static void Rename(Control owner, string spoken)
    {
        string? said = AutomationProperties.GetName(owner);
        if (string.Equals(said, spoken, StringComparison.Ordinal))
        {
            return;
        }

        AutomationProperties.SetName(owner, spoken);
        FromElement(owner)?.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, said, spoken);
    }
}
