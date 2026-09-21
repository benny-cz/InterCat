namespace InterCat.Capture.Windows;

/// <summary>
/// An explicitly diagnostic ETL-file variant for the IC-009 evidence gate. It is separate from the
/// admitted-journal authority candidate and never implies that an ETL is metadata-only.
/// </summary>
public sealed record EtlFileCapturePlan
{
    public required OwnedSessionPlan Session { get; init; }

    /// <summary>A new, non-existing .etl path. Existing evidence is never overwritten.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Operating-system buffer reservation for the file session. Set before provider enablement.</summary>
    public int BufferSizeMegabytes { get; init; } = 64;

    public string GetValidatedOutputPath()
    {
        ArgumentNullException.ThrowIfNull(Session);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputPath);
        if (BufferSizeMegabytes is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BufferSizeMegabytes),
                BufferSizeMegabytes,
                "The ETL buffer reservation must be between 1 MiB and 4 GiB.");
        }

        if (Session.MaximumDuration <= TimeSpan.Zero || Session.MaximumDuration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Session.MaximumDuration),
                Session.MaximumDuration,
                "The ETL duration limit must be greater than zero and no more than 24 hours.");
        }

        string path = Path.GetFullPath(OutputPath);
        if (!string.Equals(Path.GetExtension(path), ".etl", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The diagnostic evidence path must use the .etl extension.", nameof(OutputPath));
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"Refusing to overwrite the existing ETL evidence path '{path}'.");
        }

        return path;
    }
}
