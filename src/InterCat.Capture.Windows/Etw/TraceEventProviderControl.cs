using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.Capture.Windows;

/// <summary>Configures providers identically for live delivery and ETL-file acquisition.</summary>
internal static class TraceEventProviderControl
{
    public static ProviderEnablementResult Enable(TraceEventSession session, ProviderEnablementRequest request, ProcessHolds holds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(holds);

        try
        {
            var options = new TraceEventProviderOptions { StacksEnabled = request.RequestCallStacks };
            if (request.EventIdsToEnable.Count > 0)
            {
                options.EventIDsToEnable = [.. request.EventIdsToEnable];
            }
            else if (request.EventIdsToDisable.Count > 0)
            {
                options.EventIDsToDisable = [.. request.EventIdsToDisable];
            }

            if (request.ProcessIdsToInclude.Count > 0)
            {
                // The filter holds process IDs, so each process it names is held open while the session lives: then none of
                // its IDs can pass to another process and bring that process's records into scope (R22, ADR-037).
                if (!holds.TryHold(request.ProcessIdsToInclude, out string? refusal))
                {
                    return new(request.SourceId, false,
                        $"its process filter could not be held to the processes it names: {refusal}. The provider was not enabled.");
                }

                options.ProcessIDFilter = [.. request.ProcessIdsToInclude];
            }

            session.EnableProvider(
                request.ProviderGuid,
                (TraceEventLevel)request.Level,
                request.MatchAnyKeyword,
                options);
            return new(request.SourceId, true, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(request.SourceId, false, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    public static bool TryRequestCaptureState(
        TraceEventSession session,
        ProviderEnablementRequest request,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            session.CaptureState(request.ProviderGuid, request.MatchAnyKeyword, 0, null);
            failureReason = null;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failureReason = exception.Message;
            return false;
        }
    }
}
