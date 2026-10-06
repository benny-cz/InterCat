using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Desktop;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// A prompt answers the keyboard as every other InterCat prompt does (§6.8): its safe answer has focus as it opens, and
/// Escape gives that answer.
/// </summary>
public sealed class PromptKeyboardTests
{
    [AvaloniaFact(DisplayName = "§6.8: the question asked on closing while Explore records keeps recording on Escape, as its focused first answer does")]
    public async Task TheClosingQuestionKeepsRecordingOnEscape() =>
        await AssertEscapeGivesTheSafeAnswer(MainWindow.StopExplorePrompt(), "Keep recording");

    [AvaloniaFact(DisplayName = "§6.8: the redacted report's question chooses no file on Escape, as its focused Cancel does")]
    public async Task TheRedactedReportsQuestionCancelsOnEscape() =>
        await AssertEscapeGivesTheSafeAnswer(MainWindow.RedactedSharePrompt(), "Cancel");

    private static async Task AssertEscapeGivesTheSafeAnswer(Window prompt, string safe)
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        try
        {
            Task<bool> answered = prompt.ShowDialog<bool>(owner);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(safe, Assert.IsType<Button>(prompt.FocusManager?.GetFocusedElement()).Content);
            prompt.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(answered.IsCompleted);
            Assert.False(await answered);
        }
        finally
        {
            owner.Close();
        }
    }
}
