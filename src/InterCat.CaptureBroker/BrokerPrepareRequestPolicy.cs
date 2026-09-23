using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureBroker;

/// <summary>
/// The broker's half of Prepare validation: the profile must be in this broker's installed catalog, and what each
/// profile admits (TCP focus, broader-capture consent, a content scope) is checked against it. The wire codec only
/// checks structure, so an ordinary-integrity client can speak the protocol without the capture adapter.
/// </summary>
public static class BrokerPrepareRequestPolicy
{
    /// <summary>Validates a decoded request; a failure is an <see cref="InvalidDataException"/>, as a codec refusal is.</summary>
    public static void Validate(BrokerWireRequest request)
    {
        if (request is BrokerPrepareCaptureRequest prepare)
        {
            Validate(prepare);
        }
    }

    public static void Validate(BrokerPrepareCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Quota);
        ArgumentNullException.ThrowIfNull(request.FocusedProcessIds);
        CaptureProfileDescriptor? profile = string.IsNullOrWhiteSpace(request.ProfileId)
            ? null
            : CaptureProfileCatalog.Find(request.ProfileId);
        if (profile is null || !string.Equals(profile.Id, request.ProfileId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("PrepareCapture requires a canonical catalog profile ID.");
        }

        if (profile.Kind == CaptureProfileKind.FocusedTransport)
        {
            if (request.FocusedMechanism != Mechanism.Tcp
                || (request.AllowBroaderCapture && request.FocusedProcessIds.Count == 0)
                || request.Content is not null)
            {
                throw new InvalidDataException(
                    "Focused transport requires TCP, uses broader-capture consent only with selected PIDs, and cannot carry a Content request.");
            }
        }
        else if (request.FocusedMechanism is not null
            || request.FocusedProcessIds.Count > 0
            || request.AllowBroaderCapture)
        {
            throw new InvalidDataException(
                "Mechanism, process focus and broader-capture consent apply only to Focused transport.");
        }

        if (profile.Kind == CaptureProfileKind.Content)
        {
            string? contentProblem = ContentCapturePolicyCompiler.Validate(request.Content);
            if (contentProblem is not null)
            {
                throw new InvalidDataException(contentProblem);
            }
        }
        else if (request.Content is not null)
        {
            throw new InvalidDataException("Content scope and limits apply only to the Content profile.");
        }
    }

    public static CaptureProfileRequest ToProfileRequest(BrokerPrepareCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CaptureProfileDescriptor profile = CaptureProfileCatalog.Find(request.ProfileId)
            ?? throw new InvalidOperationException($"Profile '{request.ProfileId}' is not in the local catalog.");
        return new(
            profile.Kind,
            request.RequestOriginalDiagnosticEtl,
            request.FocusedMechanism,
            request.FocusedProcessIds,
            request.AllowBroaderCapture,
            request.Content);
    }
}
