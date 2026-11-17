using System.Collections.ObjectModel;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The messages a content capture from the window keeps, by their source (ADR-049, ADR-107): the HTTP messages WinINet
/// raises, or the messages applications publish through InterCat's SDK with their bytes. A person chooses one by its label,
/// and the request names its source and mechanism as `icat capture`'s --source and --mechanism do.
/// </summary>
public sealed record ContentCaptureSource(string Label, string SourceId, Mechanism Mechanism, string Explanation)
{
    /// <summary>WinINet's capture of each HTTP exchange of the processes chosen: the default.</summary>
    public static ContentCaptureSource WinInet { get; } = new(
        "HTTP messages, as WinINet raises them",
        ContentSources.WinInetCapture,
        Mechanism.Http,
        "Each request and response the processes make through WinINet - its headers and body; over HTTPS as their "
            + "plaintext, cookies and authorization included.");

    /// <summary>What the processes chosen say of the messages they publish through InterCat's SDK.</summary>
    public static ContentCaptureSource Sdk { get; } = new(
        "Messages published through InterCat's SDK",
        ContentSources.CooperativeSdk,
        Mechanism.ApplicationSdk,
        "Each message published through InterCat's SDK with its bytes, whole as the application holds them, above any "
            + "encryption of its channel; one over 32 KiB is published without them.");

    /// <summary>The sources a person may choose, the default first.</summary>
    public static ReadOnlyCollection<ContentCaptureSource> All { get; } = Array.AsReadOnly([WinInet, Sdk]);

    /// <summary>What a combo box reads as its value when this source is chosen: the label, not the record's fields.</summary>
    public override string ToString() => Label;
}
