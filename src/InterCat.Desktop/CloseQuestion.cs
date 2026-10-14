using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace InterCat.Desktop;

/// <summary>
/// Something under way that closing a window stops, as the question asked before the window closes says it: a
/// recording, saved as Stop saves it, or a package or a finish, stopped partway.
/// </summary>
/// <param name="Title">The question's title when it is the only thing under way.</param>
/// <param name="Said">What is under way, and what closing does to it.</param>
/// <param name="Keep">The answer that keeps it going, and the window open, when it is the only thing under way.</param>
/// <param name="Stop">The answer that stops it and closes once it has ended, when it is the only one under way.</param>
internal sealed record CloseStop(string Title, string Said, string Keep, string Stop)
{
    /// <summary>Explore recording: the broker stops and finalizes it, and what it published stays saved.</summary>
    internal static CloseStop Recording { get; } = new("Stop Explore?",
        "Explore is recording. Closing stops it: the broker finalizes the capture, and all published evidence stays "
        + "saved.",
        "Keep recording", "Stop, save, and close");

    /// <summary>A session's package being saved to <paramref name="destination"/>; closing removes it.</summary>
    internal static CloseStop Package(string destination, bool copy) => new("Stop packaging?",
        (copy ? "An exact copy of a session" : "A redacted package of a session")
        + $" is being saved to {destination}. "
        + "Closing stops it and removes what it has written so far; the session itself is unchanged.",
        "Keep packaging", "Stop and close");

    /// <summary>An unfinished capture being finished into <paramref name="session"/>; what it derived stays.</summary>
    internal static CloseStop Finish(string session) => new("Stop finishing?",
        $"The unfinished capture is being finished into {session}. Closing stops it: what it has derived stays in the "
        + "session, and InterCat offers to finish the rest when it next opens.",
        "Keep finishing", "Stop and close");

    /// <summary>An investigation's package being saved to <paramref name="destination"/>; closing removes it.</summary>
    internal static CloseStop InvestigationPackage(string investigation, string destination) => new("Stop packaging?",
        $"{investigation} is being packaged to {destination}. Closing stops it and removes what it has written so far; "
        + "the investigation and its sessions are unchanged.",
        "Keep packaging", "Stop and close");
}

/// <summary>
/// The question asked when a window closes while something it would stop is under way; true to stop each and close once
/// each has ended. Escape, like the focused first answer, keeps them going and the window open.
/// </summary>
internal static class CloseQuestion
{
    internal static Window For(IReadOnlyList<CloseStop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentOutOfRangeException.ThrowIfZero(stops.Count);

        // One thing under way is named in the question and its answers; several are each said, and stopped together.
        CloseStop? only = stops.Count == 1 ? stops[0] : null;
        var prompt = new Window
        {
            Title = only?.Title ?? "Stop and close?", Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var keep = new Button { Content = only?.Keep ?? "Keep InterCat open" };
        var stop = new Button { Content = only?.Stop ?? "Stop all and close" };
        keep.Click += (_, _) => prompt.Close(false);
        stop.Click += (_, _) => prompt.Close(true);
        prompt.Opened += (_, _) => keep.Focus();
        prompt.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                prompt.Close(false);
                key.Handled = true;
            }
        };
        var content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        foreach (CloseStop each in stops)
        {
            content.Children.Add(new TextBlock { Text = each.Said, TextWrapping = TextWrapping.Wrap });
        }

        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Children = { keep, stop },
        });
        prompt.Content = content;
        return prompt;
    }
}
