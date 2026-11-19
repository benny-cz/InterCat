using System.Runtime.Versioning;
using System.Security.Principal;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// The broker's install-time commands (ADR-115, broker-v1 §5.10), run against a real root beneath a temporary parent: an
/// installer pre-creates the root as a serving broker would, and a removal stops what a previous broker left recording,
/// refuses while a broker may be recording, and removes the root only when it holds nothing kept, or when asked.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokerInstallationTests : IDisposable
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";

    private readonly string parent = TemporaryBrokerRoot.CreateTemporaryParent();
    private readonly ScriptedEtwHost etw = new();
    private readonly StringWriter stdout = new();
    private readonly StringWriter stderr = new();

    private string RootPath => Path.Combine(parent, TemporaryBrokerRoot.RootName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(parent, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact(DisplayName = "§20.5: an install-time command takes the user's SID and, for cleanup, whether the captures go too")]
    public void AnInstallCommandIsParsed()
    {
        Assert.Equal(new BrokerInstallOptions(BrokerInstallCommand.ProvisionRoot, Sid, false),
            BrokerInstallOptions.Parse(["provision-root", "--owner-sid", Sid.ToLowerInvariant()], out string? error));
        Assert.Null(error);
        Assert.Equal(new BrokerInstallOptions(BrokerInstallCommand.Cleanup, Sid, false),
            BrokerInstallOptions.Parse(["cleanup", "--owner-sid", Sid], out _));
        Assert.Equal(new BrokerInstallOptions(BrokerInstallCommand.Cleanup, Sid, true),
            BrokerInstallOptions.Parse(["cleanup", "--remove-evidence", "--owner-sid", Sid], out _));
        Assert.False(BrokerInstallOptions.IsInstallCommand(["serve", "--owner-sid", Sid]));
        Assert.False(BrokerInstallOptions.IsInstallCommand([]));
    }

    [Theory(DisplayName = "§20.5: an install-time command line naming anything else, or missing its SID, is refused in a sentence")]
    [InlineData("--owner-sid is required.", "provision-root")]
    [InlineData("'--owner-sid' needs a value.", "provision-root", "--owner-sid")]
    [InlineData("'--owner-sid' needs a value.", "cleanup", "--owner-sid", "--remove-evidence")]
    [InlineData("Unknown argument '--remove-evidence'.", "provision-root", "--owner-sid", Sid, "--remove-evidence")]
    [InlineData("'--owner-sid' was given twice.", "cleanup", "--owner-sid", Sid, "--owner-sid", Sid)]
    [InlineData("'--remove-evidence' was given twice.", "cleanup", "--owner-sid", Sid, "--remove-evidence", "--remove-evidence")]
    [InlineData("--owner-sid is not a SID.", "cleanup", "--owner-sid", "Administrators")]
    [InlineData("Unknown argument '--root'.", "cleanup", "--root", "C:\\elsewhere", "--owner-sid", Sid)]
    public async Task AMalformedInstallCommandIsRefused(string expected, params string[] args)
    {
        Assert.Null(BrokerInstallOptions.Parse(args, out string? error));
        Assert.Equal(expected, error);

        // Through the broker's own entry point: refused before any folder is looked at.
        Assert.Equal((int)InterCatExitCode.InvalidInvocation, await BrokerProcess.RunAsync(args, stdout, stderr, CancellationToken.None));
        Assert.StartsWith(expected + Environment.NewLine + "Usage: InterCat.CaptureBroker provision-root", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(stdout.ToString());
    }

    [Fact(DisplayName = "§20.5: provision-root creates the broker's root with its final security, keeps one that has it, repairs one a trusted owner holds and refuses one anyone else holds")]
    public void TheRootIsProvisionedAsAServingBrokerWould()
    {
        Assert.Equal((0, "created"), Provision(Request(TemporaryBrokerRoot.PolicyForCurrentProcess())));
        using (WindowsBrokerRoot created = TemporaryBrokerRoot.Provision(parent, TemporaryBrokerRoot.PolicyForCurrentProcess()))
        {
            // What a serving broker then finds is exactly the declared descriptor: it neither creates nor repairs.
            Assert.Equal((false, false), (created.Report.CreatedByThisBroker, created.Report.SecurityReapplied));
        }

        Assert.Equal((0, "kept"), Provision(Request(TemporaryBrokerRoot.PolicyForCurrentProcess())));

        // A folder made with inherited security by a trusted owner is given the root's own.
        Directory.Delete(RootPath);
        Directory.CreateDirectory(RootPath);
        Assert.Equal((0, "repaired"), Provision(Request(TemporaryBrokerRoot.PolicyForCurrentProcess())));

        // One an untrusted principal owns is refused with its owner named, and left as it is: a policy trusting its owner
        // then still finds it to repair.
        Directory.Delete(RootPath);
        Directory.CreateDirectory(RootPath);
        BrokerRootSecurityPolicy foreign = TemporaryBrokerRoot.PolicyForCurrentProcess() with
        {
            TrustedOwnerSids = [BrokerRootSecurityPolicy.LocalSystemSid],
            BrokerPrincipalSids = [BrokerRootSecurityPolicy.LocalSystemSid],
        };
        stdout.GetStringBuilder().Clear();
        Assert.Equal((int)InterCatExitCode.PermissionOrCapabilityFailure, BrokerInstallation.ProvisionRoot(Request(foreign), stdout, stderr));
        string refusal = stderr.ToString();
        string owner = new DirectoryInfo(RootPath).GetAccessControl().GetOwner(typeof(SecurityIdentifier))!.Value;
        Assert.True(refusal.Contains("will not adopt or repair it", StringComparison.Ordinal)
            && refusal.Contains($"owned by {owner}", StringComparison.OrdinalIgnoreCase), refusal);
        Assert.Empty(stdout.ToString());
        Assert.Equal((0, "repaired"), Provision(Request(TemporaryBrokerRoot.PolicyForCurrentProcess())));
    }

    [Fact(DisplayName = "§20.5: cleanup on a machine with no broker root creates none")]
    public async Task CleanupWithoutARootCreatesNone()
    {
        Assert.Equal(0, await CleanupAsync());
        Assert.Equal($"root {RootPath} absent", stdout.ToString().TrimEnd());
        Assert.False(Directory.Exists(RootPath));
        Assert.Empty(etw.Reclaimed);
    }

    [Fact(DisplayName = "P14: cleanup stops the session a previous broker left recording, never another whose name resembles InterCat's, and removes a root that then holds nothing kept")]
    public async Task CleanupStopsWhatWasLeftRecordingAndRemovesAnEmptyRoot()
    {
        BrokerSessionOwnership interrupted = await LeaveRecordingAsync();
        etw.Active.Add("InterCat-b-another-tools-session");

        Assert.Equal(0, await CleanupAsync());
        Assert.Equal([interrupted.SessionName], etw.Reclaimed);
        Assert.Equal(["InterCat-b-another-tools-session"], etw.Active);
        Assert.Contains("InterruptedStartStopped", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal($"root {RootPath} removed", stdout.ToString().TrimEnd());
        Assert.False(Directory.Exists(RootPath));
    }

    [Fact(DisplayName = "R16: cleanup keeps a root holding a capture, stopping what was left recording, and removes it with its captures only when asked")]
    public async Task CleanupKeepsCapturesUnlessAsked()
    {
        BrokerSessionOwnership interrupted = await LeaveRecordingAsync(withCaptureDirectory: true);

        Assert.Equal(0, await CleanupAsync());
        Assert.Equal([interrupted.SessionName], etw.Reclaimed);
        Assert.Equal($"root {RootPath} kept with 1 capture", stdout.ToString().TrimEnd());
        Assert.True(File.Exists(Path.Combine(RootPath, FileBrokerLifecycleStore.FileName)));
        Assert.Single(Directory.GetDirectories(RootPath, "capture-*"));

        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, await CleanupAsync(removeEvidence: true));
        Assert.Equal($"root {RootPath} removed with 1 capture", stdout.ToString().TrimEnd());
        Assert.False(Directory.Exists(RootPath));
    }

    [Fact(DisplayName = "R16: cleanup refuses while another broker holds the ownership log, stopping nothing and removing nothing")]
    public async Task CleanupRefusesWhileABrokerMayBeRecording()
    {
        BrokerSessionOwnership interrupted = await LeaveRecordingAsync();
        using (WindowsBrokerRoot root = TemporaryBrokerRoot.Provision(parent, TemporaryBrokerRoot.PolicyForCurrentProcess()))
        using (new FileBrokerLifecycleStore(root))
        {
            Assert.Equal(BrokerProcess.ExitAnotherBrokerRunning, await CleanupAsync(predecessorWait: TimeSpan.FromMilliseconds(600)));
        }

        Assert.Contains("may be recording", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(stdout.ToString());
        Assert.Empty(etw.Reclaimed);
        Assert.Equal([interrupted.SessionName], etw.Active);
        Assert.True(File.Exists(Path.Combine(RootPath, FileBrokerLifecycleStore.FileName)));
    }

    [Fact(DisplayName = "R16: cleanup keeps the root, and says so in its exit, when a session a previous broker left recording cannot be proven stopped")]
    public async Task CleanupKeepsTheRootWhileASessionMayStillRun()
    {
        await LeaveRecordingAsync();

        Assert.Equal((int)InterCatExitCode.PartialResultSuccess, await CleanupAsync(reclaimer: new RefusingReclaimer()));
        Assert.Equal($"root {RootPath} kept: 1 capture not proven stopped", stdout.ToString().TrimEnd());
        Assert.Contains("could not be proven stopped; nothing was removed", stderr.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RootPath, FileBrokerLifecycleStore.FileName)));
    }

    [Fact(DisplayName = "§20.5: cleanup leaves a root whose ownership log is unreadable untouched, and says so")]
    public async Task CleanupLeavesAnUnreadableLogUntouched()
    {
        using (TemporaryBrokerRoot.Provision(parent, TemporaryBrokerRoot.PolicyForCurrentProcess()))
        {
        }

        string log = Path.Combine(RootPath, FileBrokerLifecycleStore.FileName);
        byte[] unreadable = [.. Enumerable.Range(0, 300).Select(index => (byte)(index * 7))];
        File.WriteAllBytes(log, unreadable);

        Assert.Equal(0, await CleanupAsync());
        Assert.Equal($"root {RootPath} kept: its ownership log is unreadable", stdout.ToString().TrimEnd());
        Assert.Contains("unreadable and was left untouched", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(unreadable, File.ReadAllBytes(log));
        Assert.Empty(etw.Reclaimed);
    }

    /// <summary>Records, through a root's ownership log, a start a broker began and never finished, its session left running.</summary>
    private async Task<BrokerSessionOwnership> LeaveRecordingAsync(bool withCaptureDirectory = false)
    {
        BrokerOwnerIdentity owner = WindowsBrokerTokenIdentity.ReadCurrentProcess().Owner;
        CaptureId captureId = CaptureId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var ownership = new BrokerCaptureOwnership
        {
            CaptureId = captureId,
            Owner = owner,
            Session = BrokerSessionOwnership.Create(captureId),
            PlanDigest = PreparedFocused().Digest,
            State = CaptureLifecycle.Starting,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LeaseExpiresAtUtc = now.AddMinutes(1),
            StopMilestones = BrokerStopMilestones.None,
        };
        using (WindowsBrokerRoot root = TemporaryBrokerRoot.Provision(parent, TemporaryBrokerRoot.PolicyForCurrentProcess()))
        using (var store = new FileBrokerLifecycleStore(root))
        {
            await store.SaveStartIntentAsync(
                ownership,
                new()
                {
                    Owner = owner,
                    RequestId = Guid.NewGuid(),
                    Kind = BrokerRequestKind.Start,
                    TargetKey = "interrupted-start",
                    CaptureId = captureId,
                    Completed = false,
                },
                CancellationToken.None);
            if (withCaptureDirectory)
            {
                root.OpenCaptureDirectory(captureId).Dispose();
            }
        }

        etw.Active.Add(ownership.Session.SessionName);
        return ownership.Session;
    }

    private Task<int> CleanupAsync(bool removeEvidence = false, IEtwSessionReclaimer? reclaimer = null, TimeSpan? predecessorWait = null) =>
        BrokerInstallation.CleanupAsync(
            new BrokerProcessDependencies(Request(TemporaryBrokerRoot.PolicyForCurrentProcess()), etw, reclaimer ?? etw, new NoPlans(), Runtime),
            removeEvidence,
            stdout,
            stderr,
            CancellationToken.None,
            predecessorWait);

    private (int Exit, string Outcome) Provision(BrokerRootRequest request)
    {
        stdout.GetStringBuilder().Clear();
        int exit = BrokerInstallation.ProvisionRoot(request, stdout, stderr);
        string said = stdout.ToString().TrimEnd();
        Assert.StartsWith($"root {RootPath} ", said, StringComparison.Ordinal);
        return (exit, said[$"root {RootPath} ".Length..]);
    }

    private BrokerRootRequest Request(BrokerRootSecurityPolicy policy) => new()
    {
        TrustedParentDirectory = parent,
        RootDirectoryName = TemporaryBrokerRoot.RootName,
        CapturingUserSid = TemporaryBrokerRoot.CurrentUserSid,
        Policy = policy,
    };

    /// <summary>A reclaimer that cannot stop a session, as one held by a hung or protected process would be.</summary>
    private sealed class RefusingReclaimer : IEtwSessionReclaimer
    {
        public bool StopPreviouslyOwnedSession(string sessionName, Guid ownershipToken) =>
            throw new EtwSessionException("Access is denied.");
    }

    /// <summary>Cleanup prepares no capture, so a plan source asked anything fails the test.</summary>
    private sealed class NoPlans : IBrokerCapturePlanSource
    {
        public ValueTask<CapabilityReport> ProbeAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Cleanup probes nothing.");

        public ValueTask<EffectiveCapturePlan> CompileAsync(CaptureProfileRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Cleanup compiles nothing.");
    }
}
