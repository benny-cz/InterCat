using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// A session's resource instances (`contracts/resources-v1.md`, ADR-097): each named synchronization object, each section
/// by its handles and by its views, never joined, and each AF_UNIX socket, from its first record to its last, with its type,
/// its name split into its namespace and its leaf, the processes holding it and the traffic it carried - and the namespaces
/// they lie in, to browse by. A resource whose mechanism records transfers and which carried none was idle, listed apart
/// with --idle; a section whose views are an image's is a module's, listed only with --images.
/// </summary>
internal static class ResourcesCommand
{
    /// <summary>The document --json writes: the namespaces and the resources listed.</summary>
    internal const string Contract = "resources-v1";

    private const int DefaultLimit = 50;

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? mechanismText = command.TakeOption("--mechanism");
        string? namespaceText = command.TakeOption("--namespace");
        string? limitText = command.TakeOption("--limit");
        bool idle = command.TryTakeFlag("--idle");
        bool images = command.TryTakeFlag("--images");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown)
        {
            ConsoleUi.Failure(directory is null
                ? "A session directory is required: icat resources <directory>."
                : command.Refusal(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        Mechanism? mechanism = mechanismText switch
        {
            null => null,
            "synchronization" => Mechanism.Synchronization,
            "section" => Mechanism.SharedSection,
            "socket" => Mechanism.UnixDomainSocket,
            _ => Mechanism.UnknownMechanism,
        };
        if (mechanism == Mechanism.UnknownMechanism)
        {
            ConsoleUi.Failure(CommandLine.Expects("--mechanism", mechanismText, ["synchronization", "section", "socket"]));
            return InterCatExitCode.InvalidInvocation;
        }

        string? within = namespaceText?.Trim().TrimEnd('\\');
        if (namespaceText is not null && string.IsNullOrEmpty(within))
        {
            ConsoleUi.Failure("--namespace expects an object directory, such as \\Sessions\\1\\BaseNamedObjects; it was blank.");
            return InterCatExitCode.InvalidInvocation;
        }

        int limit = DefaultLimit;
        if (limitText is not null
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
        {
            ConsoleUi.Failure($"--limit expects a number of resources of 1 or more; '{limitText}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
        SessionResources resources;
        try
        {
            resources = SessionResources.Of(store, cancellationToken: cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        // A namespace holds the resources named in it or beneath it, compared as the object manager compares names; an
        // unnamed resource lies in none. An image's section is listed only when asked, and those the rest admit are counted.
        bool Admitted(ResourceInstance instance) => (mechanism is null || instance.Mechanism == mechanism)
            && (within is null || instance.Name is { } name && In(name, within))
            && (!idle || instance.Traffic is { Idle: true });
        ResourceInstance[] kept = [.. resources.Instances.Where(instance => Admitted(instance) && (images || !instance.Image))];
        int imagesLeft = images ? 0 : resources.Instances.Count(instance => instance.Image && Admitted(instance));
        (string Namespace, int Resources)[] namespaces =
        [
            .. kept.Where(instance => instance.Name is not null)
                .GroupBy(instance => ResourceNames.NamespaceOf(instance.Name!) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(group => (group.Key, group.Count()))
                .OrderByDescending(entry => entry.Item2)
                .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase),
        ];
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract,
                SessionPath = path,
                Rule = ResourceInstanceIndex.Rule,
                BindingRule = ProcessInstanceIndex.BindingRule,
                resources.Generation,
                Mechanism = mechanismText,
                Namespace = within,
                Idle = idle,
                Images = images,
                ImagesNotListed = imagesLeft,
                Resources = kept.Length,
                Unnamed = kept.Count(instance => instance.Name is null),
                Namespaces = namespaces.Select(entry => new { Namespace = entry.Namespace.Length == 0 ? null : entry.Namespace, entry.Resources }),
                Listed = kept.Take(limit).Select(instance => new
                {
                    Mechanism = instance.Mechanism.ToString(),
                    Layer = instance.Layer.ToString(),
                    instance.Number,
                    instance.ByViews,
                    instance.Image,
                    instance.Views,
                    instance.LargestView,
                    Type = instance.TypeName,
                    instance.Names,
                    Namespace = instance.Name is { } name ? ResourceNames.NamespaceOf(name) : null,
                    Leaf = instance.Name is { } leaf ? ResourceNames.LeafOf(leaf) : null,
                    FirstNativeTicks = instance.FirstNativeTicks,
                    FirstSeconds = SessionText.Seconds(resources.Clock, instance.FirstNativeTicks),
                    LastNativeTicks = instance.LastNativeTicks,
                    LastSeconds = SessionText.Seconds(resources.Clock, instance.LastNativeTicks),
                    instance.FirstHandleMade,
                    instance.LastHandleClosed,
                    instance.HeldAtStop,
                    Holders = instance.Holders.Select(holder => new
                    {
                        InstanceId = holder.Process.ToString(),
                        ProcessId = Instance(resources, holder.Process).ProcessId,
                        Name = Instance(resources, holder.Process).ImageName,
                        holder.HandlesMade,
                        holder.HandlesDuplicated,
                        holder.HandlesClosed,
                        holder.HeldAtStop,
                        holder.ViewsMapped,
                        holder.ViewsUnmapped,
                        holder.ViewsListed,
                    }),
                    Traffic = instance.Traffic is { } traffic
                        ? new
                        {
                            traffic.Sends,
                            traffic.Receives,
                            traffic.BytesSent,
                            traffic.BytesReceived,
                            traffic.UnmeasuredTransfers,
                            traffic.Idle,
                        }
                        : null,
                    Unbound = instance.Unbound.ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
                    instance.Records,
                }),
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Resources");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Rule", $"{ResourceInstanceIndex.Rule}, records bound by {ProcessInstanceIndex.BindingRule}");
        if (within is not null)
        {
            ConsoleUi.Field("Namespace", within + ", and beneath it");
        }

        if (idle)
        {
            ConsoleUi.Field("Kept", "the resources whose mechanism records transfers and which carried none: idle");
        }

        if (kept.Length == 0)
        {
            ConsoleUi.Note(within is not null
                ? $"No resource of this session is named in {within} or beneath it."
                : idle
                ? "No resource of this session that could carry traffic was idle: each carried a transfer the capture saw, or "
                    + "none of its mechanisms records one."
                : imagesLeft > 0
                ? (mechanism is null ? "Every resource" : "Every section") + " of this session is an image's section, a module's "
                    + "views rather than memory a process shared, and is listed only with --images: "
                    + CountText.Of(imagesLeft, "image section") + "."
                : "No record of this session numbers a named synchronization object, a section or an AF_UNIX socket: a capture "
                    + "collects them only under a profile admitting their sources, so none listed is not none on the machine.");
            return InterCatExitCode.Success;
        }

        int objects = kept.Count(instance => instance.Mechanism == Mechanism.Synchronization);
        int sections = kept.Count(instance => instance.Mechanism == Mechanism.SharedSection && !instance.ByViews);
        int mapped = kept.Count(instance => instance.ByViews && !instance.Image);
        int imaged = kept.Count(instance => instance.Image);
        int sockets = kept.Count(instance => instance.Mechanism == Mechanism.UnixDomainSocket);
        ConsoleUi.Field("Resources", string.Join(", ", new[]
        {
            objects > 0 ? CountText.Of(objects, "synchronization object") : null,
            sections > 0 ? CountText.Of(sections, "section") : null,
            mapped > 0 ? CountText.Of(mapped, "mapped section") : null,
            imaged > 0 ? CountText.Of(imaged, "image section") : null,
            sockets > 0 ? CountText.Of(sockets, "Unix socket") : null,
        }.OfType<string>()) + "; " + CountText.Of(kept.Length - kept.Count(instance => instance.Name is null), "named", "named"));
        if (imagesLeft > 0)
        {
            ConsoleUi.Field("Images", CountText.Of(imagesLeft, "image section") + " not listed: a module's views, not memory a "
                + "process shared; --images lists them");
        }
        int carried = kept.Count(instance => instance.Traffic is { Idle: false });
        int idled = kept.Count(instance => instance.Traffic is { Idle: true });
        int unrecorded = kept.Count(instance => instance.Traffic is null);
        ConsoleUi.Field("Traffic", string.Join(", ", new[]
        {
            carried > 0 ? CountText.Of(carried, "carried traffic", "carried traffic") : null,
            idled > 0 ? CountText.Of(idled, "idle", "idle") : null,
            unrecorded > 0 ? CountText.Of(unrecorded, "of a mechanism recording none", "of mechanisms recording none") : null,
        }.OfType<string>()));
        if (namespaces.Length > 0 && within is null)
        {
            ConsoleUi.Table(
                ["Namespace", "Resources"],
                [.. namespaces.Select(entry => (IReadOnlyList<string>)
                    [entry.Namespace.Length == 0 ? "(a name in no directory)" : entry.Namespace, ConsoleUi.Count(entry.Resources)])]);
        }

        ConsoleUi.Table(
            ["Resource", "Type", "Namespace", "Name", "Held by", "First seen", "Last seen", "Traffic", "Records"],
            [.. kept.Take(limit).Select(instance => (IReadOnlyList<string>)
            [
                Kind(instance) + " " + instance.Number.ToString(CultureInfo.InvariantCulture),
                instance.TypeName ?? (instance.Mechanism == Mechanism.UnixDomainSocket || instance.ByViews ? "-" : "not named"),
                instance.Name is { } name ? ResourceNames.NamespaceOf(name) ?? "-" : "-",
                (instance.Name is { } leaf ? ResourceNames.LeafOf(leaf) + (instance.Names.Count > 1
                    ? string.Create(CultureInfo.CurrentCulture, $" (and {instance.Names.Count - 1:N0} more names)")
                    : string.Empty) : "unnamed") + Mapped(instance),
                HeldBy(resources, instance),
                Seconds(resources, instance.FirstNativeTicks) + (instance.FirstHandleMade ? string.Empty : ", made before"),
                Seconds(resources, instance.LastNativeTicks) + (instance.HeldAtStop ? ", held at the stop"
                    : instance.ByViews ? instance.LastHandleClosed ? ", unmapped" : ", its unmapping unseen"
                    : instance.LastHandleClosed ? ", closed" : ", its close unseen"),
                Traffic(instance.Traffic),
                ConsoleUi.Count(instance.Records),
            ])]);
        if (kept.Length > limit)
        {
            ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture,
                $"{kept.Length - limit:N0} more; --limit {kept.Length:N0} lists every one."));
        }

        ConsoleUi.Note("Each resource is the number the capture gave one object from the first handle it saw made to the close "
            + "of the last, never its kernel address, which a later object reuses; its name is the one its closes and the "
            + "stop's rundown carry, and a resource no record named is unnamed. A synchronization object's and a section's "
            + "records are handles, not traffic: none carries a signal, a wait or a byte through shared memory. A Unix "
            + "socket's sends and receives are its traffic, and one that carried none was idle; --idle lists those alone.");
        if (kept.Any(instance => instance.ByViews))
        {
            ConsoleUi.Note("A mapped section is a section found by its views - the number its views carry - never joined to the "
                + "number its handles carry, so one section can be listed twice, once by each; a view's size is the extent it "
                + "mapped, never bytes moved through it.");
        }

        return InterCatExitCode.Success;
    }

    // A section by its views in a cell: how many views of it were seen, and the largest extent one mapped.
    private static string Mapped(ResourceInstance instance) => !instance.ByViews ? string.Empty
        : " · " + CountText.Of(instance.Views, "view") + (instance.LargestView is { } largest ? " up to " + ConsoleUi.Size(largest) : string.Empty);

    // A resource's traffic in a cell: its bytes each way and its transfers that measured none; idle where it carried none;
    // and none recorded where its mechanism records no transfer, never idle (R3).
    private static string Traffic(ResourceTraffic? traffic) => traffic switch
    {
        null => "none recorded",
        { Idle: true } => "idle",
        _ => ConsoleUi.Bytes(traffic.BytesSent) + " sent, " + ConsoleUi.Bytes(traffic.BytesReceived) + " received"
            + (traffic.UnmeasuredTransfers > 0 ? " · " + CountText.Of(traffic.UnmeasuredTransfers, "transfer") + " unmeasured" : string.Empty),
    };

    /// <summary>Whether <paramref name="name"/> lies in the object directory <paramref name="within"/> or beneath it.</summary>
    private static bool In(string name, string within) =>
        ResourceNames.NamespaceOf(name) is { } directory
        && (string.Equals(directory, within, StringComparison.OrdinalIgnoreCase)
            || directory.StartsWith(within + "\\", StringComparison.OrdinalIgnoreCase));

    private static string Kind(ResourceInstance instance) => instance.Mechanism switch
    {
        Mechanism.SharedSection when instance.Image => "Image section",
        Mechanism.SharedSection when instance.ByViews => "Mapped section",
        Mechanism.SharedSection => "Section",
        Mechanism.UnixDomainSocket => "Unix socket",
        _ => "Synchronization object",
    };

    private static ProcessInstance Instance(SessionResources resources, ProcessInstanceId id) => resources.Processes.Instances[resources.Processes.PositionOf(id)];

    // The processes holding a resource, as the overview names them, the first three and the rest counted; and what bound to none.
    private static string HeldBy(SessionResources resources, ResourceInstance instance)
    {
        string[] named = [.. instance.Holders.Select(holder =>
        {
            ProcessInstance process = Instance(resources, holder.Process);
            return ProcessNode.NameWithPidOf(process, resources.Processes.InstancesOf(process.ProcessId));
        })];
        string held = named.Length == 0 ? "no process"
            : string.Join(", ", named.Take(3)) + (named.Length > 3
                ? string.Create(CultureInfo.CurrentCulture, $" and {named.Length - 3:N0} more") : string.Empty);
        long unbound = instance.Unbound.Values.Sum();
        return unbound > 0 ? held + " · " + CountText.Of(unbound, "record") + " bound to none" : held;
    }

    private static string Seconds(SessionResources resources, long nativeTicks) =>
        SessionText.Seconds(resources.Clock, nativeTicks) is { } seconds ? seconds + " s"
            : string.Create(CultureInfo.CurrentCulture, $"{nativeTicks:N0} ticks");

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat resources <session-directory> [--mechanism synchronization|section|socket]");
        ConsoleUi.Line("               [--namespace <directory>] [--idle] [--images] [--limit <n>] [--json]");
        ConsoleUi.Line("  The session's resources: each named synchronization object, each section by its handles and by");
        ConsoleUi.Line("  its views - never joined - and each AF_UNIX socket, from its first record to its last, with its");
        ConsoleUi.Line("  views, its type, its name split into the object directory it lies in and its leaf, the processes");
        ConsoleUi.Line("  holding it and the traffic it carried; and how many lie in each directory. --namespace keeps");
        ConsoleUi.Line("  those named in one directory or beneath it, such as \\Sessions\\1\\BaseNamedObjects; --mechanism");
        ConsoleUi.Line("  one mechanism's; --idle those whose mechanism records transfers and which carried none. A");
        ConsoleUi.Line("  synchronization object's and a section's records are handles and views, not traffic. --images");
        ConsoleUi.Line("  lists the sections whose views are an image's too, a module's rather than memory shared. --limit");
        ConsoleUi.Line("  lists that many, 50 unless it says so; --json writes a document.");
    }
}
