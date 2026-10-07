using System.Text;

namespace InterCat.Application.Tests;

/// <summary>An export's CSV read as a spreadsheet reads it: one line per CRLF, quoted cells unquoted.</summary>
internal static class Csv
{
    public static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A line's cells, a quoted cell's commas kept and its doubled quotes made one.</summary>
    public static string[] Cells(string line)
    {
        List<string> cells = [];
        var cell = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (quoted && character == '"' && index + 1 < line.Length && line[index + 1] == '"')
            {
                cell.Append('"');
                index++;
            }
            else if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(character);
            }
        }

        cells.Add(cell.ToString());
        return [.. cells];
    }
}
