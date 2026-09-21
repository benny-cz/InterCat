namespace InterCat.Capture.Windows;

/// <summary>Why a record was not admitted. Each reason is counted separately and never folded (section 20.6).</summary>
public enum OmissionReason
{
    /// <summary>The record came from a provider this capture did not request.</summary>
    UnrequestedProvider = 1,

    /// <summary>The descriptor is not in the profile's allowlist.</summary>
    DescriptorNotAdmitted = 2,

    /// <summary>The descriptor is explicitly denied, for example a payload-producing debug event (P28).</summary>
    DescriptorDenied = 3,
}

/// <summary>Why an admitted descriptor could not be read. A guessed layout is never used instead (section 18.3).</summary>
public enum UndecodableReason
{
    /// <summary>The body is shorter than the admitted slots require.</summary>
    BodyShorterThanSchema = 1,

    /// <summary>The descriptor arrived at a version the saved schema does not cover.</summary>
    UnknownDescriptorVersion = 2,

    /// <summary>Reading the body raised an error inside the bounded admission read.</summary>
    AdmissionReadFailed = 3,

    /// <summary>The record arrived with a pointer width the compiled offsets were not built for.</summary>
    PointerWidthMismatch = 4,
}

/// <summary>An immutable reading of the health ledger (section 20.6).</summary>
public sealed record CaptureHealthSnapshot
{
    public required long AdmittedRecords { get; init; }
    public required long ObservedRecords { get; init; }
    public required long PolicyOmissionsUnrequestedProvider { get; init; }
    public required long PolicyOmissionsDescriptorNotAdmitted { get; init; }
    public required long PolicyOmissionsDescriptorDenied { get; init; }
    public required long UndecodableBodyShorterThanSchema { get; init; }
    public required long UndecodableUnknownDescriptorVersion { get; init; }
    public required long UndecodableAdmissionReadFailed { get; init; }

    /// <summary>Records whose pointer width did not match the compiled offsets (section 18.3).</summary>
    public required long UndecodablePointerWidthMismatch { get; init; }

    /// <summary>Loss the ETW session itself reported. It bounds loss to a polling interval, not to exact records.</summary>
    public required long ProviderReportedEventLoss { get; init; }

    /// <summary>Buffer loss the real-time consumer reported, kept apart from provider-reported loss.</summary>
    public required long ConsumerReportedBufferLoss { get; init; }

    /// <summary>Records InterCat dropped because its own bounded queue was full (R8).</summary>
    public required long ApplicationDrops { get; init; }

    /// <summary>Records whose timestamp was implausible and was quarantined with its raw evidence (section 18.3).</summary>
    public required long QuarantinedTimestamps { get; init; }

    public required int QueueDepth { get; init; }
    public required int QueueCapacity { get; init; }

    /// <summary>
    /// True when nothing was lost, dropped or quarantined. A trace with zero reported loss is still not
    /// proof of universal coverage (section 13.3).
    /// </summary>
    public bool IsLossFree =>
        ProviderReportedEventLoss == 0
        && ConsumerReportedBufferLoss == 0
        && ApplicationDrops == 0
        && QuarantinedTimestamps == 0;
}

/// <summary>
/// The live health ledger. Counters are independent quantities: no total adds overlapping counters,
/// and no counter is inferred from another (section 9.3, section 20.6).
/// </summary>
public sealed class CaptureHealthLedger
{
    private long admitted;
    private long observed;
    private long omittedUnrequested;
    private long omittedNotAdmitted;
    private long omittedDenied;
    private long undecodableShort;
    private long undecodableVersion;
    private long undecodableRead;
    private long undecodablePointerWidth;
    private long applicationDrops;
    private long quarantinedTimestamps;
    private long providerLoss;
    private long consumerBufferLoss;

    public void RecordObserved() => Interlocked.Increment(ref observed);

    public void RecordAdmitted() => Interlocked.Increment(ref admitted);

    public void RecordApplicationDrop() => Interlocked.Increment(ref applicationDrops);

    public void RecordQuarantinedTimestamp() => Interlocked.Increment(ref quarantinedTimestamps);

    public void RecordOmission(OmissionReason reason)
    {
        switch (reason)
        {
            case OmissionReason.UnrequestedProvider:
                Interlocked.Increment(ref omittedUnrequested);
                break;
            case OmissionReason.DescriptorNotAdmitted:
                Interlocked.Increment(ref omittedNotAdmitted);
                break;
            case OmissionReason.DescriptorDenied:
                Interlocked.Increment(ref omittedDenied);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason));
        }
    }

    public void RecordUndecodable(UndecodableReason reason)
    {
        switch (reason)
        {
            case UndecodableReason.BodyShorterThanSchema:
                Interlocked.Increment(ref undecodableShort);
                break;
            case UndecodableReason.UnknownDescriptorVersion:
                Interlocked.Increment(ref undecodableVersion);
                break;
            case UndecodableReason.AdmissionReadFailed:
                Interlocked.Increment(ref undecodableRead);
                break;
            case UndecodableReason.PointerWidthMismatch:
                Interlocked.Increment(ref undecodablePointerWidth);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason));
        }
    }

    /// <summary>Records source-reported loss. Both quantities are monotonic and are stored, not added.</summary>
    public void RecordSourceLoss(long providerReportedEventLoss, long consumerReportedBufferLoss)
    {
        Interlocked.Exchange(ref providerLoss, Math.Max(Interlocked.Read(ref providerLoss), providerReportedEventLoss));
        Interlocked.Exchange(
            ref consumerBufferLoss,
            Math.Max(Interlocked.Read(ref consumerBufferLoss), consumerReportedBufferLoss));
    }

    public CaptureHealthSnapshot Read(int queueDepth, int queueCapacity) => new()
    {
        AdmittedRecords = Interlocked.Read(ref admitted),
        ObservedRecords = Interlocked.Read(ref observed),
        PolicyOmissionsUnrequestedProvider = Interlocked.Read(ref omittedUnrequested),
        PolicyOmissionsDescriptorNotAdmitted = Interlocked.Read(ref omittedNotAdmitted),
        PolicyOmissionsDescriptorDenied = Interlocked.Read(ref omittedDenied),
        UndecodableBodyShorterThanSchema = Interlocked.Read(ref undecodableShort),
        UndecodableUnknownDescriptorVersion = Interlocked.Read(ref undecodableVersion),
        UndecodableAdmissionReadFailed = Interlocked.Read(ref undecodableRead),
        UndecodablePointerWidthMismatch = Interlocked.Read(ref undecodablePointerWidth),
        ProviderReportedEventLoss = Interlocked.Read(ref providerLoss),
        ConsumerReportedBufferLoss = Interlocked.Read(ref consumerBufferLoss),
        ApplicationDrops = Interlocked.Read(ref applicationDrops),
        QuarantinedTimestamps = Interlocked.Read(ref quarantinedTimestamps),
        QueueDepth = queueDepth,
        QueueCapacity = queueCapacity,
    };
}
