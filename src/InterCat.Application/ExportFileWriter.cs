using System.Text;

namespace InterCat.Application;

/// <summary>Publishes a complete UTF-8 export beside its destination, never a partially written final file.</summary>
public static class ExportFileWriter
{
    public static async Task WriteAsync(string destination, string content, bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(content);
        string folder = Path.GetDirectoryName(Path.GetFullPath(destination)) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(folder);
        string staged = Path.Combine(folder, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.partial");
        try
        {
            await File.WriteAllTextAsync(staged, content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(staged, destination, overwrite);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }
}
