using System.Globalization;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Remote;

namespace InterCat.Cli;

/// <summary>
/// An agent's preview of a controller's capture request (`remote-agent-v1` §4.10): its focus and content options read and
/// checked as `icat profiles` reads them, in its words, then compiled from this host's own provider schemas - starting
/// nothing and enabling no provider - and whether this host records it as asked: an agent records only the metadata
/// profiles that take no focus, and keeps no content.
/// </summary>
internal sealed class CompilerCapturePreview(Func<CaptureProfileRequest, EffectiveCapturePlan>? compile = null) : IRemoteCapturePreview
{
    private readonly Func<CaptureProfileRequest, EffectiveCapturePlan> compile = compile ?? ProfilesCommand.Compile;

    public RemotePreviewed Preview(RemotePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CaptureProfileCatalog.Find(request.Profile) is not { } selected)
        {
            return new(null, null, false, $"'{request.Profile}' is no capture profile; icat profiles lists every one.");
        }

        bool keepsContent = selected.Kind == CaptureProfileKind.Content;
        ProfileRequestOptions? options = ProfileRequestOptions.From(new ProfileOptionTexts(
            request.Mechanism,
            [.. request.ProcessIds.Select(processId => processId.ToString(CultureInfo.InvariantCulture))],
            request.AllowBroaderCapture,
            request.ContentSource,
            request.Channels,
            request.MaximumRecordBytes?.ToString(CultureInfo.InvariantCulture),
            request.MaximumSessionBytes?.ToString(CultureInfo.InvariantCulture),
            request.Retention,
            request.Inspection), out string? unread);
        if (options is null)
        {
            return new(selected.Id, null, keepsContent, unread);
        }

        CaptureProfileRequest? compiling = options.RequestFor(selected, diagnosticEtl: false, out string? refusal, out _);
        if (compiling is null)
        {
            return new(selected.Id, null, keepsContent, refusal);
        }

        EffectiveCapturePlan plan = compile(compiling);
        string? notRecorded = !BrokerCaptureDriver.Profiles.Contains(selected.Id, StringComparer.Ordinal)
            ? keepsContent
                ? "A content capture is not recorded remotely: its host's broker keeps content only of its own client's processes."
                : $"A {selected.Id} capture is not recorded remotely: an agent records {string.Join(", ", BrokerCaptureDriver.Profiles)}."
            : !plan.CanStart
                ? "Its host cannot start it as asked: its preview says why."
                : null;
        return new(selected.Id, ProfilesCommand.PreviewJson(plan), keepsContent, notRecorded);
    }
}
