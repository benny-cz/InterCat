using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Domain;

namespace InterCat.CaptureBroker;

public sealed record BrokerStoreRecoveryReport(
    long Generation,
    long RecoveredSequence,
    long TruncatedTailBytes,
    long DiscardedCompactionBytes,
    string? RecoveryReason);

/// <summary>
/// What one compaction reclaimed. Frames are superseded snapshots; the records inside them are never
/// dropped, because discarding a completed request would turn its replay into a second capture.
/// </summary>
public sealed record BrokerStoreCompactionReport(
    long Generation,
    long BytesBefore,
    long BytesAfter,
    long SupersededFrames,
    int Captures,
    int Requests);

/// <summary>
/// Append-only, checksummed lifecycle store. Every frame is a complete ownership/request snapshot and
/// is flushed before the in-memory state changes. Recovery keeps the last complete valid frame and
/// truncates only the untrusted tail, so it never depends on directory-rename atomicity. The log is
/// opened through the broker-owned root rather than from a path, so the store cannot be pointed at a
/// directory whose security was never validated.
///
/// Compaction publishes the current snapshot as a new single-frame generation and replaces the live
/// log in one directory operation, so a restart sees either the whole previous log or the whole new
/// one. The temporary it writes is bounded by one frame, and the live log is untouched until the
/// replacement succeeds.
/// </summary>
public sealed class FileBrokerLifecycleStore : IBrokerLifecycleStore, IDisposable
{
    public const string FileName = "broker-ownership-v1.log";
    public const string CompactionFileName = "broker-ownership-v1.compacting";
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;
    public const long MaximumFileBytes = 64L * 1024 * 1024;
    public const int MaximumCaptures = 4096;
    public const int MaximumRequests = 16384;

    private const int FormatVersion = 2;
    private const int HeaderSize = 56;
    private const int TrailerSize = 12;
    private static readonly byte[] HeaderMagic = "ICBOWN1\0"u8.ToArray();
    private static readonly byte[] TrailerMagic = "ICBEND1\0"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The smallest bound a store will accept, so a test can force the "even one frame does not fit" path.</summary>
    private const long MinimumFileBytes = HeaderSize + TrailerSize + 1;

    private readonly Dictionary<CaptureId, BrokerCaptureOwnership> captures = [];
    private readonly Dictionary<RequestKey, BrokerStoredRequest> requests = [];
    private readonly IBrokerOwnedDirectory directory;
    private readonly long maximumFileBytes;
    private readonly Lock gate = new();
    private FileStream stream;
    private long sequence;
    private long generation;
    private bool disposed;

    public FileBrokerLifecycleStore(
        IBrokerOwnedDirectory brokerOwnedDirectory,
        long maximumFileBytes = MaximumFileBytes)
    {
        ArgumentNullException.ThrowIfNull(brokerOwnedDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, MinimumFileBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumFileBytes, MaximumFileBytes);
        directory = brokerOwnedDirectory;
        this.maximumFileBytes = maximumFileBytes;
        FilePath = Path.Combine(brokerOwnedDirectory.Path, FileName);
        stream = Open();
        try
        {
            Recovery = Recover() with
            {
                DiscardedCompactionBytes = DiscardInterruptedCompaction(),
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public string FilePath { get; }

    public BrokerStoreRecoveryReport Recovery { get; }

    /// <summary>How many times this log has been compacted. A fresh log is generation 0.</summary>
    public long Generation
    {
        get
        {
            lock (gate)
            {
                return generation;
            }
        }
    }

    /// <summary>
    /// Publishes the current snapshot as a new single-frame generation. The caller's state is
    /// unchanged either way: a failure leaves the previous log in place and the store usable.
    /// </summary>
    public BrokerStoreCompactionReport Compact()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return CompactCore();
        }
    }

    public ValueTask<BrokerStoredRequest?> FindRequestAsync(
        BrokerOwnerIdentity owner,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            requests.TryGetValue(new(owner, requestId), out BrokerStoredRequest? request);
            return ValueTask.FromResult(request);
        }
    }

    public ValueTask<BrokerCaptureOwnership?> FindCaptureAsync(
        CaptureId captureId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            captures.TryGetValue(captureId, out BrokerCaptureOwnership? ownership);
            return ValueTask.FromResult(ownership);
        }
    }

    public ValueTask SaveStartIntentAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            var requestKey = new RequestKey(request.Owner, request.RequestId);
            if (captures.ContainsKey(ownership.CaptureId) || requests.ContainsKey(requestKey))
            {
                throw new InvalidOperationException("The start intent already exists.");
            }

            Dictionary<CaptureId, BrokerCaptureOwnership> nextCaptures = CopyCaptures();
            Dictionary<RequestKey, BrokerStoredRequest> nextRequests = CopyRequests();
            nextCaptures.Add(ownership.CaptureId, ownership);
            nextRequests.Add(requestKey, request);
            Persist(nextCaptures, nextRequests);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SaveStartCompletionAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken) =>
        SaveCompletionAsync(ownership, request, BrokerRequestKind.Start, cancellationToken);

    public ValueTask SaveStopIntentAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            var requestKey = new RequestKey(request.Owner, request.RequestId);
            if (!captures.ContainsKey(ownership.CaptureId) || requests.ContainsKey(requestKey))
            {
                throw new InvalidOperationException("The stop intent conflicts with stored state.");
            }

            Dictionary<CaptureId, BrokerCaptureOwnership> nextCaptures = CopyCaptures();
            Dictionary<RequestKey, BrokerStoredRequest> nextRequests = CopyRequests();
            nextCaptures[ownership.CaptureId] = ownership;
            nextRequests.Add(requestKey, request);
            Persist(nextCaptures, nextRequests);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SaveStopCompletionAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken) =>
        SaveCompletionAsync(ownership, request, request.Kind, cancellationToken);

    public ValueTask SaveLeaseAsync(
        BrokerCaptureOwnership ownership,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            if (!captures.ContainsKey(ownership.CaptureId))
            {
                throw new InvalidOperationException("The capture ownership record does not exist.");
            }

            Dictionary<CaptureId, BrokerCaptureOwnership> nextCaptures = CopyCaptures();
            nextCaptures[ownership.CaptureId] = ownership;
            Persist(nextCaptures, CopyRequests());
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<BrokerCaptureOwnership>> FindExpiredLeasesAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            IReadOnlyList<BrokerCaptureOwnership> expired =
            [
                .. captures.Values
                    .Where(capture =>
                        capture.LeaseExpiresAtUtc <= nowUtc
                        && capture.State is CaptureLifecycle.Starting
                            or CaptureLifecycle.Recording
                            or CaptureLifecycle.Stopping
                            or CaptureLifecycle.Finalizing)
                    .OrderBy(capture => capture.LeaseExpiresAtUtc),
            ];
            return ValueTask.FromResult(expired);
        }
    }

    public ValueTask<BrokerLifecycleSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            return ValueTask.FromResult(new BrokerLifecycleSnapshot(
                [.. captures.Values.OrderBy(capture => capture.CreatedAtUtc)],
                [.. requests.Values.OrderBy(request => request.RequestId)]));
        }
    }

    private ValueTask SaveCompletionAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        BrokerRequestKind expectedKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            var requestKey = new RequestKey(request.Owner, request.RequestId);
            if (!captures.ContainsKey(ownership.CaptureId)
                || !requests.TryGetValue(requestKey, out BrokerStoredRequest? existing)
                || existing.Kind != expectedKind
                || existing.Completed)
            {
                throw new InvalidOperationException("The operation completion has no matching pending intent.");
            }

            Dictionary<CaptureId, BrokerCaptureOwnership> nextCaptures = CopyCaptures();
            Dictionary<RequestKey, BrokerStoredRequest> nextRequests = CopyRequests();
            nextCaptures[ownership.CaptureId] = ownership;
            nextRequests[requestKey] = request;
            Persist(nextCaptures, nextRequests);
        }

        return ValueTask.CompletedTask;
    }

    private FileStream Open() =>
        directory.OpenOwnedFile(
            FileName,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.WriteThrough);

    /// <summary>
    /// Removes the temporary of a compaction that did not reach its replacement. The live log this
    /// store just recovered is the complete one by construction, so the temporary holds nothing the
    /// recovered state is missing, whether it was fully written or torn.
    /// </summary>
    private long DiscardInterruptedCompaction()
    {
        long bytes = 0;
        try
        {
            string path = Path.Combine(directory.Path, CompactionFileName);
            if (File.Exists(path))
            {
                bytes = new FileInfo(path).Length;
            }
        }
        catch (IOException)
        {
            bytes = 0;
        }

        return directory.RemoveOwnedFile(CompactionFileName) ? bytes : 0;
    }

    private static byte[] Serialize(
        Dictionary<CaptureId, BrokerCaptureOwnership> nextCaptures,
        Dictionary<RequestKey, BrokerStoredRequest> nextRequests,
        long nextSequence,
        long nextGeneration)
    {
        ValidateState(nextCaptures.Values, nextRequests.Values, nextSequence, nextGeneration);
        var document = new SnapshotDocument
        {
            FormatVersion = FormatVersion,
            Sequence = nextSequence,
            Generation = nextGeneration,
            Captures = [.. nextCaptures.Values.OrderBy(capture => capture.CaptureId.Value)],
            Requests =
            [
                .. nextRequests.Values
                    .OrderBy(request => request.Owner.UserSid, StringComparer.Ordinal)
                    .ThenBy(request => request.Owner.LogonSessionId)
                    .ThenBy(request => request.RequestId),
            ],
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        return payload.Length is < 1 or > MaximumPayloadBytes
            ? throw new IOException(
                $"Broker ownership snapshot is {payload.Length} bytes; the limit is {MaximumPayloadBytes}.")
            : payload;
    }

    private BrokerStoreCompactionReport CompactCore()
    {
        long bytesBefore = stream.Length;
        long supersededFrames = Math.Max(sequence - 1, 0);
        long nextGeneration = checked(generation + 1);
        byte[] payload = Serialize(captures, requests, 1, nextGeneration);
        long compactedLength = checked(HeaderSize + payload.Length + TrailerSize);
        if (compactedLength > maximumFileBytes)
        {
            // Refuse before touching anything: publishing a generation that is already over budget
            // would trade a named refusal for a log whose bound no longer means anything.
            throw new IOException(
                $"Broker ownership state cannot be compacted within its {maximumFileBytes}-byte bound: "
                + $"one frame carrying {captures.Count} captures and {requests.Count} requests is "
                + $"{compactedLength} bytes.");
        }

        byte[] header = BuildHeader(1, payload);
        byte[] trailer = BuildTrailer(payload.Length);

        _ = directory.RemoveOwnedFile(CompactionFileName);
        using (FileStream temporary = directory.OpenOwnedFile(
            CompactionFileName,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileOptions.WriteThrough))
        {
            temporary.Write(header);
            temporary.Write(payload);
            temporary.Write(trailer);
            temporary.Flush(flushToDisk: true);
        }

        // The live log has to be closed before it can be replaced, because it is held without shared
        // delete. Until the replacement returns, that closed file is still the complete durable one.
        stream.Dispose();
        try
        {
            directory.ReplaceOwnedFile(CompactionFileName, FileName);
        }
        catch
        {
            stream = Open();
            stream.Position = stream.Length;
            _ = directory.RemoveOwnedFile(CompactionFileName);
            throw;
        }

        stream = Open();
        stream.Position = stream.Length;
        sequence = 1;
        generation = nextGeneration;
        return new(nextGeneration, bytesBefore, stream.Length, supersededFrames, captures.Count, requests.Count);
    }

    private void Persist(
        Dictionary<CaptureId, BrokerCaptureOwnership> nextCaptures,
        Dictionary<RequestKey, BrokerStoredRequest> nextRequests)
    {
        byte[] payload = Serialize(nextCaptures, nextRequests, sequence + 1, generation);
        long frameLength = checked(HeaderSize + payload.Length + TrailerSize);
        if (stream.Length + frameLength > maximumFileBytes)
        {
            // Reclaiming the superseded frames is ordinary maintenance, not a decision the caller
            // makes: every one of them is a snapshot this one already contains.
            _ = CompactCore();
            payload = Serialize(nextCaptures, nextRequests, sequence + 1, generation);
            frameLength = checked(HeaderSize + payload.Length + TrailerSize);
            if (stream.Length + frameLength > maximumFileBytes)
            {
                throw new IOException(
                    $"Broker ownership log would exceed its {maximumFileBytes}-byte bound at "
                    + $"{stream.Length + frameLength} bytes even after compacting to one frame.");
            }
        }

        long nextSequence = checked(sequence + 1);
        byte[] header = BuildHeader(nextSequence, payload);
        byte[] trailer = BuildTrailer(payload.Length);
        long frameStart = stream.Length;
        try
        {
            stream.Position = frameStart;
            stream.Write(header);
            stream.Write(payload);
            stream.Write(trailer);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            try
            {
                stream.SetLength(frameStart);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception truncationException) when (truncationException is not OutOfMemoryException)
            {
                disposed = true;
                stream.Dispose();
            }

            throw;
        }

        captures.Clear();
        foreach ((CaptureId key, BrokerCaptureOwnership value) in nextCaptures)
        {
            captures.Add(key, value);
        }

        requests.Clear();
        foreach ((RequestKey key, BrokerStoredRequest value) in nextRequests)
        {
            requests.Add(key, value);
        }

        sequence = nextSequence;
    }

    private BrokerStoreRecoveryReport Recover()
    {
        long originalLength = stream.Length;
        long lastGoodOffset = 0;
        long recoveredSequence = 0;
        long recoveredGeneration = 0;
        string? recoveryReason = null;
        SnapshotDocument? latest = null;
        stream.Position = 0;

        while (stream.Position < originalLength)
        {
            long frameStart = stream.Position;
            long remaining = originalLength - frameStart;
            if (remaining < HeaderSize)
            {
                recoveryReason = "Ignored an incomplete ownership-frame header at the end of the log.";
                break;
            }

            byte[] header = new byte[HeaderSize];
            stream.ReadExactly(header);
            if (!header.AsSpan(0, HeaderMagic.Length).SequenceEqual(HeaderMagic))
            {
                recoveryReason = "Ignored an ownership frame with an invalid header magic.";
                break;
            }

            int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
            long frameSequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12, 8));
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20, 4));
            bool invalidHeader = version != FormatVersion
                || frameSequence != recoveredSequence + 1
                || payloadLength is < 1 or > MaximumPayloadBytes;
            if (invalidHeader)
            {
                recoveryReason = "Ignored an ownership frame with an unsupported version, sequence, or length.";
                break;
            }

            if (originalLength - stream.Position < payloadLength + TrailerSize)
            {
                recoveryReason = "Ignored an incomplete ownership-frame payload at the end of the log.";
                break;
            }

            byte[] payload = new byte[payloadLength];
            stream.ReadExactly(payload);
            byte[] trailer = new byte[TrailerSize];
            stream.ReadExactly(trailer);
            bool validTrailer = BinaryPrimitives.ReadInt32LittleEndian(trailer.AsSpan(0, 4)) == payloadLength
                && trailer.AsSpan(4, TrailerMagic.Length).SequenceEqual(TrailerMagic);
            byte[] expectedDigest = header.AsSpan(24, 32).ToArray();
            byte[] actualDigest = ComputeDigest(frameSequence, payload);
            if (!validTrailer || !CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
            {
                recoveryReason = "Ignored an ownership frame whose checksum or trailer is invalid.";
                break;
            }

            SnapshotDocument? candidate;
            try
            {
                candidate = JsonSerializer.Deserialize<SnapshotDocument>(payload, JsonOptions);
                if (candidate is null
                    || candidate.FormatVersion != FormatVersion
                    || candidate.Sequence != frameSequence)
                {
                    throw new InvalidDataException("The ownership payload identity does not match its frame.");
                }

                // One file is one generation. A frame carrying another generation is a fragment of a
                // different log that ended up here, not a later state of this one.
                if (latest is not null && candidate.Generation != recoveredGeneration)
                {
                    throw new InvalidDataException(
                        $"Frame {frameSequence} carries generation {candidate.Generation} in a log of "
                        + $"generation {recoveredGeneration}.");
                }

                ValidateState(candidate.Captures, candidate.Requests, frameSequence, candidate.Generation);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                recoveryReason = $"Ignored an invalid ownership payload: {exception.Message}";
                break;
            }

            latest = candidate;
            recoveredSequence = frameSequence;
            recoveredGeneration = candidate.Generation;
            lastGoodOffset = stream.Position;
        }

        if (originalLength > 0 && latest is null)
        {
            throw new InvalidDataException(
                recoveryReason ?? "The ownership log contains no complete valid snapshot.");
        }

        if (latest is not null)
        {
            foreach (BrokerCaptureOwnership ownership in latest.Captures)
            {
                captures.Add(ownership.CaptureId, ownership);
            }

            foreach (BrokerStoredRequest request in latest.Requests)
            {
                requests.Add(new(request.Owner, request.RequestId), request);
            }
        }

        long truncatedBytes = originalLength - lastGoodOffset;
        if (truncatedBytes > 0)
        {
            stream.SetLength(lastGoodOffset);
            stream.Flush(flushToDisk: true);
        }

        stream.Position = stream.Length;
        sequence = recoveredSequence;
        generation = recoveredGeneration;
        return new(recoveredGeneration, recoveredSequence, truncatedBytes, 0, recoveryReason);
    }

    private static void ValidateState(
        IEnumerable<BrokerCaptureOwnership> captureValues,
        IEnumerable<BrokerStoredRequest> requestValues,
        long expectedSequence,
        long expectedGeneration)
    {
        if (expectedSequence <= 0)
        {
            throw new InvalidDataException("The ownership sequence must be positive.");
        }

        if (expectedGeneration < 0)
        {
            throw new InvalidDataException("The ownership generation is never negative.");
        }

        BrokerCaptureOwnership[] captureArray = [.. captureValues];
        BrokerStoredRequest[] requestArray = [.. requestValues];
        if (captureArray.Length > MaximumCaptures || requestArray.Length > MaximumRequests)
        {
            throw new InvalidDataException("The ownership snapshot exceeds its capture or request count bound.");
        }

        if (captureArray.Select(item => item.CaptureId).Distinct().Count() != captureArray.Length)
        {
            throw new InvalidDataException("The ownership snapshot contains a duplicate capture ID.");
        }

        foreach (BrokerCaptureOwnership ownership in captureArray)
        {
            bool validDigest = ownership.PlanDigest is not null
                && ownership.PlanDigest.Length == 71
                && ownership.PlanDigest.StartsWith("sha256:", StringComparison.Ordinal)
                && ownership.PlanDigest.AsSpan(7).ToString().All(Uri.IsHexDigit);
            if (ownership.CaptureId.Value == Guid.Empty
                || !ValidOwner(ownership.Owner)
                || ownership.Session is null
                || !ownership.Session.IsValid
                || !validDigest
                || !Enum.IsDefined(ownership.State)
                || ownership.CreatedAtUtc == default
                || ownership.UpdatedAtUtc < ownership.CreatedAtUtc
                || ownership.LeaseExpiresAtUtc < ownership.CreatedAtUtc
                || ownership.StopMilestones is null
                || (ownership.FailureReason?.Length ?? 0) > 512)
            {
                throw new InvalidDataException(
                    $"Capture '{ownership.CaptureId}' has an invalid ownership record.");
            }
        }

        var requestKeys = new HashSet<RequestKey>();
        HashSet<CaptureId> captureIds = [.. captureArray.Select(item => item.CaptureId)];
        foreach (BrokerStoredRequest request in requestArray)
        {
            var key = new RequestKey(request.Owner, request.RequestId);
            bool outcomeValid = request.Completed
                ? request.Kind == BrokerRequestKind.Start
                    ? request.StartOutcome is not null && request.StopOutcome is null
                    : request.StopOutcome is not null && request.StartOutcome is null
                : request.StartOutcome is null && request.StopOutcome is null;
            if (!requestKeys.Add(key)
                || !ValidOwner(request.Owner)
                || request.RequestId == Guid.Empty
                || !Enum.IsDefined(request.Kind)
                || string.IsNullOrWhiteSpace(request.TargetKey)
                || request.TargetKey.Length > 128
                || request.TargetKey.Any(char.IsControl)
                || !captureIds.Contains(request.CaptureId)
                || !outcomeValid)
            {
                throw new InvalidDataException(
                    $"Request '{request.RequestId}' has an invalid ownership/idempotency record.");
            }
        }
    }

    private static bool ValidOwner(BrokerOwnerIdentity owner) =>
        !string.IsNullOrWhiteSpace(owner.UserSid)
        && owner.UserSid.Length <= 184
        && owner.UserSid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
        && owner.UserSid.All(character =>
            char.IsAsciiDigit(character) || character is 'S' or 's' or '-')
        && owner.LogonSessionId != 0;

    private static byte[] BuildHeader(long frameSequence, byte[] payload)
    {
        byte[] header = new byte[HeaderSize];
        HeaderMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12, 8), frameSequence);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), payload.Length);
        ComputeDigest(frameSequence, payload).CopyTo(header, 24);
        return header;
    }

    private static byte[] BuildTrailer(int payloadLength)
    {
        byte[] trailer = new byte[TrailerSize];
        BinaryPrimitives.WriteInt32LittleEndian(trailer.AsSpan(0, 4), payloadLength);
        TrailerMagic.CopyTo(trailer, 4);
        return trailer;
    }

    private static byte[] ComputeDigest(long frameSequence, byte[] payload)
    {
        byte[] preimage = new byte[16 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(preimage.AsSpan(0, 4), FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(preimage.AsSpan(4, 8), frameSequence);
        BinaryPrimitives.WriteInt32LittleEndian(preimage.AsSpan(12, 4), payload.Length);
        payload.CopyTo(preimage, 16);
        return SHA256.HashData(preimage);
    }

    private Dictionary<CaptureId, BrokerCaptureOwnership> CopyCaptures() => new(captures);

    private Dictionary<RequestKey, BrokerStoredRequest> CopyRequests() => new(requests);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stream.Dispose();
        }
    }

    private readonly record struct RequestKey(BrokerOwnerIdentity Owner, Guid RequestId);

    private sealed record SnapshotDocument
    {
        public required int FormatVersion { get; init; }
        public required long Sequence { get; init; }
        public required long Generation { get; init; }
        public required IReadOnlyList<BrokerCaptureOwnership> Captures { get; init; }
        public required IReadOnlyList<BrokerStoredRequest> Requests { get; init; }
    }
}
