using Avalonia.Automation;
using Avalonia.Controls;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// What an action of a window says on itself (R15): why it is not offered - its tooltip, shown on the disabled button too,
/// and its help text, the same words - or, offered, what it does where its label does not already say it.
/// </summary>
internal static class Offers
{
    /// <summary>Why <paramref name="button"/> is not offered, as its tooltip and its help text both say it.</summary>
    public static string? WhyNot(Button button)
    {
        Assert.False(button.IsEnabled);
        Assert.True(ToolTip.GetShowOnDisabled(button));
        Assert.Equal(ToolTip.GetTip(button) as string, AutomationProperties.GetHelpText(button));
        return AutomationProperties.GetHelpText(button);
    }

    /// <summary>What <paramref name="button"/>, offered, says it does, as its tooltip and help text; null where its label says it.</summary>
    public static string? Offered(Button button)
    {
        Assert.True(button.IsEnabled);
        Assert.Equal(ToolTip.GetTip(button) as string, AutomationProperties.GetHelpText(button));
        return AutomationProperties.GetHelpText(button);
    }

    /// <summary>
    /// Everything <paramref name="button"/> says from now on, in order, each with whether it is offered then: a state that
    /// lasts only while a read is under way is heard whether or not a test's dispatcher runs before the read completes.
    /// </summary>
    public static List<(bool Offered, string? Said)> Heard(Button button)
    {
        var heard = new List<(bool Offered, string? Said)>();
        button.PropertyChanged += (_, change) =>
        {
            if (change.Property == AutomationProperties.HelpTextProperty)
            {
                heard.Add((button.IsEnabled, AutomationProperties.GetHelpText(button)));
            }
        };
        return heard;
    }
}
