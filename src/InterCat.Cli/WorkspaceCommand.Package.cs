using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>An investigation packaged with its sessions, or measured for it (`contracts/workspace-v14.md` §8).</summary>
internal sealed record WorkspacePackageDocument
{
    public required string Contract { get; init; }

    /// <summary>Whether the package was written; false for a measurement.</summary>
    public required bool Performed { get; init; }

    public required string Source { get; init; }
    public required string? Directory { get; init; }

    /// <summary>The package's own investigation file, which opens it; null for a measurement.</summary>
    public required string? Workspace { get; init; }

    public required IReadOnlyList<WorkspacePackageMemberDocument> Members { get; init; }
    public required long Rows { get; init; }
    public required int Files { get; init; }
    public required long Bytes { get; init; }

    /// <summary>Whether any copy is a session as it was recorded rather than itself a redacted package.</summary>
    public required bool Unredacted { get; init; }

    /// <summary>What the package holds and exposes, a paragraph each, as the Desktop states it before saving.</summary>
    public required IReadOnlyList<string> Disclosure { get; init; }

    public required string Warning { get; init; }
    public required string? Verification { get; init; }
}

internal sealed record WorkspacePackageMemberDocument
{
    public required Guid SessionId { get; init; }

    /// <summary>Where the investigation found the session.</summary>
    public required string FullPath { get; init; }

    public required bool Copied { get; init; }

    /// <summary>The copy's path, relative to the package's investigation file; null when it is not copied.</summary>
    public required string? PackagedPath { get; init; }

    /// <summary>The generation its copy holds, or would; null when it is not copied.</summary>
    public required long? Generation { get; init; }

    public required long Rows { get; init; }
    public required int Files { get; init; }
    public required long Bytes { get; init; }
    public required bool Redacted { get; init; }

    /// <summary>How it resolves in the package once written, or where it was found for a measurement.</summary>
    public required WorkspaceMemberState State { get; init; }

    /// <summary>Why it is not copied, or not present at its selected generation; null when neither.</summary>
    public required string? Note { get; init; }
}

internal static partial class WorkspaceCommand
{
    public const string PackageContract = "workspace-package-v1";

    /// <summary>
    /// `icat workspace package`: §8.4's export. It copies each chosen session that is where the investigation last found it
    /// as an original evidence package, beside a copy of the investigation's file that names each copy relative to itself,
    /// and publishes the folder only after every copy and the investigation reopened as they should. `--check` measures it.
    /// </summary>
    private static InterCatExitCode Package(
        string path,
        string? output,
        List<string> only,
        bool check,
        bool json,
        CancellationToken cancellationToken)
    {
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
        Guid[]? chosen = only.Count == 0
            ? null
            : [.. only.Select(text => InvestigationWorkspace.MemberNamed(workspace, text).SessionId).Distinct()];
        WorkspacePackageDocument document;
        if (check)
        {
            document = Packaged(InvestigationPackage.Select(InvestigationPackage.Preview(path, cancellationToken), chosen), null);
        }
        else
        {
            InvestigationPackageResult result = InvestigationPackage.Create(path, output!, chosen, new PackageProgress(), cancellationToken);
            document = Packaged(result.Source, result);
        }

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            RenderPackage(document);
        }

        // A session left out by the person's own choice is no shortfall; one that could not be copied is.
        return chosen is not null || document.Members.All(member => member.Copied)
            ? InterCatExitCode.Success
            : InterCatExitCode.PartialResultSuccess;
    }

    private static WorkspacePackageDocument Packaged(InvestigationPackagePreview preview, InvestigationPackageResult? result) => new()
    {
        Contract = PackageContract,
        Performed = result is not null,
        Source = preview.WorkspacePath,
        Directory = result?.Directory,
        Workspace = result?.WorkspacePath,
        Members = [.. preview.Members.Select((member, index) =>
        {
            InvestigationPackageMember? written = result?.Members[index];
            return new WorkspacePackageMemberDocument
            {
                SessionId = member.SessionId,
                FullPath = member.Resolution.FullPath,
                Copied = member.Copy is not null,
                PackagedPath = written?.PackagedPath,
                Generation = written?.Generation ?? member.Copy?.Generation,
                Rows = member.Copy?.Rows ?? 0,
                Files = written?.PackagedPath is null ? member.Copy?.Files.Count ?? 0 : written.Files,
                Bytes = written?.PackagedPath is null ? member.Copy?.Bytes ?? 0 : written.Bytes,
                Redacted = member.Copy?.Redacted ?? false,
                State = written?.State ?? member.Resolution.State,
                Note = written is null ? member.LeftOut ?? member.Resolution.Reason : written.Note,
            };
        })],
        Rows = preview.Rows,
        Files = preview.Files,
        Bytes = preview.Bytes,
        Unredacted = preview.Unredacted,
        Disclosure = InvestigationPackage.Disclosure(preview, CultureInfo.CurrentCulture),
        Warning = InvestigationPackage.WarningFor(preview),
        Verification = result is null ? null
            : "Each file was checked against its generation's digest as it was copied, and each copy reopened and hashed as a "
                + "recipient would; then the investigation was reopened from its own file, every copy found beside it as the "
                + "session it is.",
    };

    private static void RenderPackage(WorkspacePackageDocument document)
    {
        ConsoleUi.Heading(!document.Performed ? "Investigation package, measured only"
            : document.Unredacted ? "Investigation package, unredacted" : "Investigation package of redacted packages");
        ConsoleUi.Field("Investigation", document.Source);
        if (document.Directory is { } directory) ConsoleUi.Field("Written to", directory);
        int copied = document.Members.Count(member => member.Copied);
        // Records, files and size are the disclosure's to say, in the one unit the Desktop says them in.
        ConsoleUi.Field("Copies", string.Create(CultureInfo.CurrentCulture, $"{copied:N0} of {document.Members.Count:N0} sessions"));
        ConsoleUi.Line();
        ConsoleUi.Table(
            ["Session", "Found in", "Copy", "Generation", document.Performed ? "In the package" : "State", "Records"],
            [.. document.Members.Select(member => (IReadOnlyList<string>)
            [
                Short(member.SessionId),
                Path.GetFileName(Path.TrimEndingDirectorySeparator(member.FullPath)),
                member.Copied ? member.PackagedPath?.Replace('/', Path.DirectorySeparatorChar) ?? "to copy" : "not copied",
                member.Generation is { } generation ? ConsoleUi.Count(generation) : "-",
                member.State.ToString().ToLowerInvariant(),
                member.Copied ? ConsoleUi.Count(member.Rows) : "-",
            ])]);
        foreach (WorkspacePackageMemberDocument member in document.Members.Where(member => member.Note is not null))
        {
            ConsoleUi.Note($"{Short(member.SessionId)}: {member.Note}");
        }

        ConsoleUi.Line();

        // The first paragraph says what saving does, which the heading and fields have said, and the warning comes last.
        foreach (string paragraph in document.Disclosure.Skip(document.Members.Any(member => member.Copied) ? 1 : 0)
            .Where(paragraph => paragraph != document.Warning))
        {
            ConsoleUi.Note(paragraph);
        }

        if (document.Verification is { } verification) ConsoleUi.Field("Verified", verification);
        ConsoleUi.Warn(document.Warning);
        ConsoleUi.Note(document.Workspace is { } opened
            ? $"icat workspace show \"{opened}\""
            : "This was a measurement; nothing was written. Run without --check, with --output, to write the package.");
    }

    /// <summary>Reports which copy is being made, its stage, and every tenth of all the copies' bytes, to stderr.</summary>
    private sealed class PackageProgress : IProgress<InvestigationPackageProgress>
    {
        private (int Session, OriginalPackageStage Stage)? step;
        private long tenth = -1;

        public void Report(InvestigationPackageProgress value)
        {
            long now = value.Total <= 0 ? 10 : value.Done * 10 / value.Total;
            if (step == (value.Session, value.Stage) && now == tenth) return;
            step = (value.Session, value.Stage);
            tenth = now;
            string what = value.Stage == OriginalPackageStage.Copying ? "Copying and checking" : "Reopening and verifying";
            ConsoleUi.Progress(string.Create(CultureInfo.CurrentCulture,
                $"{what} session {value.Session:N0} of {value.Sessions:N0}… {now * 10}% of all the copies' bytes"));
        }
    }
}
