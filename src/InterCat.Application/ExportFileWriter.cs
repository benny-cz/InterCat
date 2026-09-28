using System.Text;

namespace InterCat.Application;

/// <summary>Publishes a complete export beside its destination, never a partially written final file.</summary>
public static class ExportFileWriter
{
    /// <summary>Publishes <paramref name="content"/> as UTF-8 text.</summary>
    public static Task WriteAsync(string destination, string content, bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return PublishAsync(destination, overwrite,
            staged => File.WriteAllTextAsync(staged, content, new UTF8Encoding(false), cancellationToken));
    }

    /// <summary>Publishes <paramref name="content"/> as the bytes it is, such as content a person chose to save (§11.2).</summary>
    public static Task WriteAsync(string destination, ReadOnlyMemory<byte> content, bool overwrite,
        CancellationToken cancellationToken = default) =>
        PublishAsync(destination, overwrite, staged => File.WriteAllBytesAsync(staged, content, cancellationToken));

    private static async Task PublishAsync(string destination, bool overwrite, Func<string, Task> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string folder = Path.GetDirectoryName(Path.GetFullPath(destination)) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(folder);
        string staged = Path.Combine(folder, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.partial");
        try
        {
            await write(staged).ConfigureAwait(false);
            File.Move(staged, destination, overwrite);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }
}
