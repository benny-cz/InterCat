using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;

namespace InterCat.Desktop.Presentation;

/// <summary>
/// A picker's choices as the channel-end picker shows its own: a choice wider than the picker - a session named by a long
/// folder, or a host by a long alias - ends in an ellipsis rather than being cut mid-word, and its tooltip has it whole,
/// in the picker's box and in its list alike. What a screen reader hears is the whole choice either way.
/// </summary>
internal static class TrimmedChoices
{
    public static void Apply(ComboBox picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        picker.ItemTemplate = new FuncDataTemplate<object>((choice, _) =>
        {
            string text = choice?.ToString() ?? string.Empty;
            var shown = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(shown, text);
            return shown;
        });
    }
}
