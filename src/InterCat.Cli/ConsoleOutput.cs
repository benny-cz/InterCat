using System.Text;

namespace InterCat.Cli;

/// <summary>
/// Makes what icat writes survive where it is written. A console on a legacy code page (852 in Czech, 437 in the United
/// States) cannot encode the ellipsis, arrows, middle dots and group separators icat's text uses, and they came out as
/// full stops, question marks or nothing; redirected output was in that code page too, where a pipe or a file expects
/// UTF-8. So a console is written in UTF-16, which .NET hands to the console whole and which changes no code page, and
/// redirected output is written as UTF-8 without a byte-order mark.
/// </summary>
internal static class ConsoleOutput
{
    public static void Install()
    {
        if (!Console.IsOutputRedirected || !Console.IsErrorRedirected)
        {
            // For a console, .NET writes UTF-16 text with the console's own Unicode API and sets no code page.
            Console.OutputEncoding = Encoding.Unicode;
        }

        if (Console.IsOutputRedirected)
        {
            Console.SetOut(Utf8(Console.OpenStandardOutput()));
        }

        if (Console.IsErrorRedirected)
        {
            Console.SetError(Utf8(Console.OpenStandardError()));
        }
    }

    private static TextWriter Utf8(Stream stream) =>
        TextWriter.Synchronized(new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true });
}
