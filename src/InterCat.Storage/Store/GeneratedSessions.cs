namespace InterCat.Storage;

/// <summary>
/// Sessions InterCat generates rather than captures or imports: its demo's (`contracts/demo-investigation-v1.md` §3).
/// Their generations are published under a source identity of their own, which a reader checks to say so. Their rows are
/// written as they are, beside a journal of records made to match them, so nothing re-derives one.
/// </summary>
public static class GeneratedSessions
{
    /// <summary>The source identity every demo session is published under.</summary>
    public const string DemoSourceIdentity = "intercat-demo-v1";

    /// <summary>Whether a generation was published as the demo's, by the source identity it names.</summary>
    public static bool IsDemo(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return string.Equals(manifest.SourceIdentity, DemoSourceIdentity, StringComparison.Ordinal);
    }
}
