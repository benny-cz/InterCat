using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using InterCat.Desktop;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.8: a text field's placeholder too long for it ends in an ellipsis, never cut at the field's edge. Cut, the rail's
/// search box said "Search names or PID (Ctrl+I", naming a shortcut that is not its own.
/// </summary>
public sealed class PlaceholderTests
{
    [AvaloniaFact(DisplayName = "§6.8: a field's placeholder too long for it ends in an ellipsis and is whole in its tooltip, and one that fits is drawn whole")]
    public void APlaceholderTooLongEndsInAnEllipsis()
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            // The rail's search box at the rail's narrowest and widest: its placeholder ends in an ellipsis exactly when the
            // text is wider than the field, whatever font draws it.
            Grid grid = window.GetControl<Grid>("WindowGrid");
            TextBox search = window.GetControl<TextBox>("SearchBox");
            Assert.Equal("Search names or PID (Ctrl+F)", search.Watermark);

            // Cut short, the placeholder is whole in the field's tooltip, and its shortcut is the field's accelerator.
            Assert.Equal(search.Watermark, ToolTip.GetTip(search));
            Assert.Equal("Ctrl+F", Avalonia.Automation.AutomationProperties.GetAcceleratorKey(search));
            foreach (double rail in new[] { 220.0, 560.0 })
            {
                grid.ColumnDefinitions[0].Width = new GridLength(rail);
                Settle(window);
                TextBlock placeholder = Placeholder(search);
                bool fits = NaturalWidth(placeholder) <= placeholder.Bounds.Width + 0.5;
                Assert.Equal(!fits, placeholder.TextLayout.TextLines[0].HasCollapsed);
            }

            // A placeholder far wider than its field ends in an ellipsis in any window: it is the application's rule.
            var narrow = new TextBox { Watermark = "A placeholder far wider than the field it is written in", Width = 120 };
            var other = new Window { Width = 400, Height = 200, Content = narrow };
            other.Show();
            try
            {
                Settle(other);
                Assert.True(Placeholder(narrow).TextLayout.TextLines[0].HasCollapsed);
            }
            finally
            {
                other.Close();
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static TextBlock Placeholder(TextBox field) =>
        field.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PART_Watermark");

    /// <summary>How wide a text block's text is drawn with nothing to hold it.</summary>
    private static double NaturalWidth(TextBlock text)
    {
        var free = new TextBlock
        {
            Text = text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontWeight = text.FontWeight,
            FontStyle = text.FontStyle,
            Padding = text.Padding,
        };
        free.Measure(Size.Infinity);
        return free.DesiredSize.Width;
    }

    private static void Settle(Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        _ = window.CaptureRenderedFrame();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
