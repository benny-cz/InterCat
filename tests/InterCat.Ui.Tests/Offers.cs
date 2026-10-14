using Avalonia.Automation;
using Avalonia.Controls;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// What an action of a window says on itself (R15): why it is not offered - its tooltip, shown on the disabled control too,
/// and its help text, the same words - or, offered, what it does where its label does not already say it.
/// </summary>
internal static class Offers
{
    /// <summary>Why <paramref name="control"/> is not offered, as its tooltip and its help text both say it.</summary>
    public static string? WhyNot(Control control)
    {
        Assert.False(control.IsEnabled);
        Assert.True(ToolTip.GetShowOnDisabled(control));
        Assert.Equal(ToolTip.GetTip(control) as string, AutomationProperties.GetHelpText(control));
        return AutomationProperties.GetHelpText(control);
    }

    /// <summary>What <paramref name="control"/>, offered, says it does, as its tooltip and help text; null where its label says it.</summary>
    public static string? Offered(Control control)
    {
        Assert.True(control.IsEnabled);
        Assert.Equal(ToolTip.GetTip(control) as string, AutomationProperties.GetHelpText(control));
        return AutomationProperties.GetHelpText(control);
    }

    /// <summary>
    /// Everything <paramref name="control"/> says from now on, in order, each with whether it is offered then: a state that
    /// lasts only while a read is under way is heard whether or not a test's dispatcher runs before the read completes.
    /// </summary>
    public static List<(bool Offered, string? Said)> Heard(Control control)
    {
        var heard = new List<(bool Offered, string? Said)>();
        control.PropertyChanged += (_, change) =>
        {
            if (change.Property == AutomationProperties.HelpTextProperty)
            {
                heard.Add((control.IsEnabled, AutomationProperties.GetHelpText(control)));
            }
        };
        return heard;
    }
}
