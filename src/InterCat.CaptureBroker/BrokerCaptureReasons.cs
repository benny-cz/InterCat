using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureBroker;

/// <summary>What a broker capture's start and status say of why it did not start or why it stopped, in one set of words.</summary>
public static class BrokerCaptureReasons
{
    /// <summary>
    /// Why a content capture's start is refused once its session is ready: its content source was not enabled - its
    /// process exited before the filter could hold it, say - so it would keep none of what it was started for (ADR-049).
    /// Its session stops at once, so what it finalizes is the lifecycle of those few moments and no content. Null for any
    /// other capture, and for one whose content source was enabled.
    /// </summary>
    public static string? ContentNotEnabled(PreparedCapturePlan plan, CaptureStartResult start)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(start);
        if (plan.Content is not { } content)
        {
            return null;
        }

        ProviderEnablementResult? source = start.Providers.FirstOrDefault(provider =>
            string.Equals(provider.SourceId, content.SourceId, StringComparison.Ordinal));
        return source is { Enabled: true }
            ? null
            : "The content source could not be enabled: " + (source?.FailureReason?.TrimEnd('.') ?? "its provider was not enabled")
                + ". The capture stopped before keeping any content.";
    }

    /// <summary>
    /// Why a capture whose journal finalized stopped, in the words its status gives: the first limit that ended it - its
    /// callbacks undrained, its journal, a follow that stopped giving chunks up, the content it may keep, its disk reserve,
    /// another limit, or its length - and then whether it stopped releasing what its follow gave up. Null when its owner
    /// stopped it and nothing needs saying.
    /// </summary>
    public static string? Stopped(
        LiveCaptureResult result,
        bool releasesFollowed,
        long? contentLimitBytes,
        string? limitReason,
        bool userStopRequested)
    {
        ArgumentNullException.ThrowIfNull(result);
        string? reason = result.Stop is { CallbacksDrained: false }
            ? "The journal finalized, but the ETW delivery pump did not confirm callback drain."
            : result.JournalQuotaReached
                ? releasesFollowed
                    ? "The evidence reached its journal byte limit holding what the capture's follow had not given up; "
                        + "the admitted prefix was finalized."
                    : "The configured journal byte limit was reached; the admitted prefix was finalized."
                : result.FollowStalled
                    ? $"The capture's follow stopped giving chunks up, so its evidence held {LiveRecorder.MaximumHeldChunks:N0} "
                        + "chunks, as many as it may; the admitted prefix was finalized."
                : result.ContentLimitReached
                    ? $"The content it kept reached the {ByteSizeText.Of(contentLimitBytes ?? result.ContentKeptBytes)} its request "
                        + "allows; the admitted prefix was finalized."
                : result.DiskReserveReached
                    ? $"{result.DiskReserveReason} The admitted prefix was finalized."
                : limitReason ?? (!userStopRequested
                    ? "The configured maximum capture duration elapsed; evidence was finalized."
                    : null);

        // A capture that could not release what its follow gave up kept every later chunk, which is said.
        if (result.ReleaseProblem is { } problem)
        {
            string kept = $"The broker stopped releasing what the capture's follow gave up and kept every later chunk: {problem}";
            reason = reason is null ? kept : reason + " " + kept;
        }

        return reason;
    }
}
