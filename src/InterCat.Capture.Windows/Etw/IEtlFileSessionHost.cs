namespace InterCat.Capture.Windows;

/// <summary>A file-mode ETW session created and owned by this capture attempt.</summary>
public interface IOwnedEtlFileSession : IDisposable
{
    string SessionName { get; }

    string OutputPath { get; }

    ProviderEnablementResult Enable(ProviderEnablementRequest request);

    bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason);

    long ReadEventsLost();

    void StopSession();
}

/// <summary>
/// Creates exclusive file-mode sessions. This boundary keeps ownership, cleanup and overwrite refusal
/// testable without ETW or elevation.
/// </summary>
public interface IEtlFileSessionHost
{
    bool? IsElevated { get; }

    IReadOnlyList<string> ListActiveSessionNames();

    IOwnedEtlFileSession CreateExclusive(EtlFileCapturePlan plan, string validatedOutputPath);
}
