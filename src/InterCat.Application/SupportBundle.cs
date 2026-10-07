using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>A file a session's generation names, as a support bundle lists it: its kind, its name and its size.</summary>
public sealed record SupportDependency(string Kind, string Name, long LengthBytes);

/// <summary>
/// A mechanism's rows in a session and what its capture covered of it, in the words `icat session` uses for each
/// (`CoverageStateText`): its state and the fact that decided it.
/// </summary>
public sealed record SupportMechanism(string Mechanism, long Rows, string Coverage, string Reason);

/// <summary>A session's source clock as a support bundle states it: its kind, encoding and rate, and nothing of its host.</summary>
public sealed record SupportClock(string Kind, string Encoding, long TicksPerSecond);

/// <summary>
/// The machine a support bundle was made on, as support needs it: the operating system, the architectures, the runtime and
/// how many processors; no machine, host or user name.
/// </summary>
public sealed record SupportRuntime(
    string OperatingSystem,
    string OperatingSystemArchitecture,
    string ProcessArchitecture,
    string Framework,
    int Processors);

/// <summary>
/// What a support bundle says of one session (`contracts/support-bundle-v1.md` §3), each fact chosen from an allowlist:
/// its folder's name, never its path; its generation and the files it names, with their sizes; its rows by mechanism with
/// what the capture covered of each; its coverage ledger's counters; its clock and recording; and why it could not be read,
/// when it could not. Nothing a record holds - a name, an endpoint, a command line or a payload - is among them (P16).
/// </summary>
public sealed record SupportSession
{
    /// <summary>The name of the session's folder, which stands for its path.</summary>
    public required string Folder { get; init; }

    /// <summary>Why the session could not be read in full, with its path said as its folder; null when it was.</summary>
    public required string? Problem { get; init; }

    public Guid? SessionId { get; init; }

    public long? Generation { get; init; }

    public long? PreviousGeneration { get; init; }

    public DateTimeOffset? CommittedUtc { get; init; }

    /// <summary>Whether the newest generation did not verify and the last-known-good one was read instead.</summary>
    public bool RolledBackToLastKnownGood { get; init; }

    /// <summary>How many files the folder holds that no generation names; never their names.</summary>
    public int OrphanFiles { get; init; }

    /// <summary>Whether the session is a redacted package (`redacted-session-v1`).</summary>
    public bool Redacted { get; init; }

    /// <summary>The files the generation names: journals, segments, dictionaries, ledgers and the rest.</summary>
    public IReadOnlyList<SupportDependency> Dependencies { get; init; } = [];

    /// <summary>The observation rows the generation's segments hold.</summary>
    public long Rows { get; init; }

    /// <summary>The source-field rows beside them.</summary>
    public long FieldRows { get; init; }

    /// <summary>Every mechanism, in its order, with its rows and what the capture covered of it.</summary>
    public IReadOnlyList<SupportMechanism> Mechanisms { get; init; } = [];

    /// <summary>
    /// The coverage ledger the generation publishes (`coverage-v2`): acquisitions, deliveries, omissions, undecodable
    /// records and losses, by provider and epoch. Null when it publishes none, and its coverage is then unknown.
    /// </summary>
    public CoverageLedgerV1? Ledger { get; init; }

    public SupportClock? Clock { get; init; }

    /// <summary>The capture's recording, in seconds of its clock; null when its capture recorded no stop.</summary>
    public double? RecordingSeconds { get; init; }

    /// <summary>The journal's chunks and their bytes.</summary>
    public int JournalChunks { get; init; }

    public long JournalBytes { get; init; }

    /// <summary>The kept-content files and their bytes, never a byte of what they keep (ADR-036).</summary>
    public int ContentFiles { get; init; }

    public long ContentBytes { get; init; }
}

/// <summary>A support bundle as it is written (`contracts/support-bundle-v1.md`), by `icat support` or the window.</summary>
public sealed record SupportBundleV1
{
    public required string Contract { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required string Version { get; init; }

    public required SupportRuntime Runtime { get; init; }

    /// <summary>
    /// The machine's capability report (`capability-report-v1`), which names no machine, host or user; null where the
    /// bundle was made by the window, which runs no probe and leaves it to `icat support`.
    /// </summary>
    public required CapabilityReport? Capabilities { get; init; }

    public required IReadOnlyList<SupportSession> Sessions { get; init; }

    /// <summary>What the bundle holds and what it leaves out, as its listing said before it was written.</summary>
    public required IReadOnlyList<string> Holds { get; init; }

    public required IReadOnlyList<string> LeftOut { get; init; }
}

/// <summary>
/// §20.6's support bundle: InterCat's version, the machine's runtime, and for each session named, what support needs to
/// see why a capture or a view went wrong - its files and their sizes, its rows by mechanism, its coverage and loss
/// counters, its clock and recording - with nothing a record holds (P16). Its contents are listed before it is written
/// (<see cref="Holds"/>, <see cref="LeftOut"/>).
/// </summary>
public static class SupportBundle
{
    public const string Contract = "support-bundle-v1";

    /// <summary>What every bundle leaves out, in the words its listing says them.</summary>
    public static IReadOnlyList<string> LeftOut { get; } =
    [
        "message content and payload bytes",
        "endpoint addresses and ports",
        "command lines",
        "raw records and their source fields",
        "process, executable, pipe and resource names",
        "machine, host and user names",
        "full paths, which each session's folder name stands for",
        "the identity of the source a session was made from",
    ];

    /// <summary>One serializer for every bundle, the command line's and the window's: by name, indented, nothing ignored.</summary>
    private static readonly JsonSerializerOptions Json = CreateJson();

    /// <summary>
    /// The bundle of <paramref name="sessions"/>, with <paramref name="capabilities"/> when the maker ran the probe: what
    /// <see cref="Holds"/> listed, made now.
    /// </summary>
    public static SupportBundleV1 Make(IReadOnlyList<string> sessions, CapabilityReport? capabilities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return new()
        {
            Contract = Contract,
            CreatedUtc = DateTimeOffset.UtcNow,
            Version = ProductVersion,
            Runtime = Runtime(),
            Capabilities = capabilities,
            Sessions = [.. sessions.Select(session => DescribeSession(session, cancellationToken))],
            Holds = Holds(sessions.Count, capabilities is not null),
            LeftOut = LeftOut,
        };
    }

    /// <summary>The name a bundle's file is offered under, as the redacted report's is: no session's name is in it.</summary>
    public const string SuggestedName = "intercat-support-bundle.json";

    /// <summary>The bundle as its file holds it.</summary>
    public static string Serialize(SupportBundleV1 bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.Serialize(bundle, Json);
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>InterCat's version, as its build stamped it.</summary>
    public static string ProductVersion { get; } =
        typeof(SupportBundle).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SupportBundle).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The machine this runs on, as a bundle states it.</summary>
    public static SupportRuntime Runtime() => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        Environment.ProcessorCount);

    /// <summary>
    /// What a bundle of <paramref name="sessions"/> holds, in the words its listing says them, before it is written.
    /// </summary>
    public static IReadOnlyList<string> Holds(int sessions, bool capabilities) =>
    [
        "InterCat's version, and this machine's operating system, architecture, runtime and processor count",
        .. capabilities ? new[] { "this machine's capability report: which sources it could capture, and why not" } : [],
        .. sessions == 0 ? [] : new[]
        {
            CountText.Of(sessions, "session") + ": "
                + (sessions == 1 ? "its folder's name, generation and files" : "each one's folder name, generation and files")
                + " with their sizes, rows by mechanism and coverage, coverage ledger and loss counters, clock and recording",
        },
    ];

    /// <summary>
    /// What a bundle says of the session at <paramref name="path"/>. A session that cannot be read is said to be, with why,
    /// rather than refused: a bundle is made when something is wrong.
    /// </summary>
    public static SupportSession DescribeSession(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string folder = Path.GetFileName(full);
        SessionStore? store = null;
        try
        {
            store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
            using EvidenceLease? lease = store.Current is null ? null : store.AcquireLease();
            if (lease?.Manifest is not { } manifest)
            {
                return new()
                {
                    Folder = folder,
                    Problem = SessionStore.NoGeneration,
                    RolledBackToLastKnownGood = store.Recovery.RolledBackToLastKnownGood,
                    OrphanFiles = store.Recovery.OrphanFiles.Count,
                };
            }

            var rows = new SortedDictionary<Mechanism, long>();
            long rowCount = 0;
            foreach (string name in SessionSegments.Names(manifest))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SegmentReaderV1 segment = SessionSegments.Open(store, manifest, name);
                SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
                for (int row = 0; row < segment.RowCount; row++)
                {
                    var mechanism = (Mechanism)mechanisms.UnsignedAt(row)!.Value;
                    rows[mechanism] = rows.GetValueOrDefault(mechanism) + 1;
                }

                rowCount += segment.RowCount;
            }

            long fieldRows = 0;
            foreach (string name in SessionSegments.FieldNames(manifest))
            {
                fieldRows += SessionSegments.Open(store, manifest, name).RowCount;
            }

            CoverageLedgerV1? ledger = SessionSegments.CoverageLedger(store.Root, manifest);
            SourceClockDescriptor? clock = SessionSegments.SourceClock(store.Root, manifest);
            TimeRange? recording = clock is { } recorded ? SessionRecording.NativeInterval(store, manifest, recorded, cancellationToken) : null;
            return new()
            {
                Folder = folder,
                Problem = null,
                SessionId = manifest.SessionId,
                Generation = manifest.Generation,
                PreviousGeneration = manifest.PreviousGeneration,
                CommittedUtc = manifest.CommittedUtc,
                RolledBackToLastKnownGood = store.Recovery.RolledBackToLastKnownGood,
                OrphanFiles = store.Recovery.OrphanFiles.Count,
                Redacted = SessionRedaction.Read(store.Root, manifest) is not null,
                Dependencies = [.. manifest.Dependencies.Select(dependency =>
                    new SupportDependency(dependency.Kind.ToString(), dependency.Name, dependency.LengthBytes))],
                Rows = rowCount,
                FieldRows = fieldRows,
                // Every mechanism, as `icat session` states them: one with no rows and unknown coverage is not a quiet one (R21).
                Mechanisms =
                [
                    .. SessionCoverage.ByMechanism(ledger).Select(entry => new SupportMechanism(MechanismText.Name(entry.Mechanism),
                        rows.GetValueOrDefault(entry.Mechanism), CoverageStateText.Value(entry.State), entry.Reason)),
                ],
                Ledger = ledger,
                Clock = clock is { } source ? new(source.Kind.ToString(), source.Encoding.ToString(), source.TicksPerSecond) : null,
                RecordingSeconds = recording is { } interval && clock is { } rate
                    ? interval.SpanTicks / (double)rate.TicksPerSecond : null,
                JournalChunks = manifest.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal),
                JournalBytes = manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
                    .Sum(dependency => dependency.LengthBytes),
                ContentFiles = manifest.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Content),
                ContentBytes = manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Content)
                    .Sum(dependency => dependency.LengthBytes),
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            // A reader names the file it could not read by its path; the bundle names the session by its folder.
            return new()
            {
                Folder = folder,
                Problem = exception.Message.Replace(full, folder, StringComparison.OrdinalIgnoreCase),
            };
        }
        finally
        {
            store?.ReleaseSegmentReaders();
        }
    }
}
