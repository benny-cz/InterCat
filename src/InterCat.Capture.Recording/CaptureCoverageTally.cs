using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Capture.Recording;

/// <summary>
/// What each descriptor delivered during one capture or replay and what became of it, for the coverage ledger
/// (`contracts/coverage-v1.md` §3). A provider admission does not know is one entry, because every record of it met one
/// policy; that keeps the ledger bounded by the plan's descriptors and the providers a source happens to deliver.
/// </summary>
/// <remarks>
/// A live capture calls it from the capture callback, so every method is bounded and allocates only the first time a
/// descriptor is seen. Past the ledger's bound, records are counted in an overflow entry and the ledger is refused once
/// the capture has ended: throwing through the callback would leave the session half-stopped.
/// </remarks>
public sealed class CaptureCoverageTally : IDeliveryObserver
{
    private readonly OwnedSessionPlan plan;
    private readonly CoverageAcquisition acquisition;
    private readonly HashSet<Guid> requested;
    private readonly Dictionary<(Guid Provider, int? EventId, int? Version), Entry> entries = [];
    private readonly Entry overflow = new();

    // A live recording snapshots the tally for each publication while the callback keeps counting, so every access
    // takes this lock. It is uncontended except for the moment of a snapshot.
    private readonly Lock gate = new();
    private HashSet<Guid>? enabled;
    private bool overflowed;
    private long queueDrops;
    private long? first;
    private long? last;

    public CaptureCoverageTally(OwnedSessionPlan plan, CoverageAcquisition acquisition)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(acquisition))
        {
            throw new ArgumentOutOfRangeException(nameof(acquisition));
        }

        this.plan = plan;
        this.acquisition = acquisition;

        // The providers admission knows, exactly as the admission table does: any other provider's records are omitted
        // as unrequested before a descriptor is looked at.
        requested = [.. plan.Sources.Select(source => source.ProviderGuid)];
    }

    /// <summary>Records the bounded queue could not take after the policy admitted them: a callback-queue loss.</summary>
    public long QueueDrops
    {
        get
        {
            lock (gate)
            {
                return queueDrops;
            }
        }
    }

    /// <summary>
    /// Limits what the ledger calls collected to the providers the session actually enabled. A live capture knows which
    /// enablements succeeded; a descriptor of a provider that was never enabled could not have recorded anything.
    /// </summary>
    public void RestrictToEnabledProviders(IEnumerable<Guid> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        enabled = [.. providers];
    }

    public void Delivered(in DeliveredRecord delivered)
    {
        lock (gate)
        {
            EntryFor(delivered.ProviderId, delivered.EventId, delivered.Version).Delivered++;
            first = first is { } earliest && earliest <= delivered.NativeTicks ? earliest : delivered.NativeTicks;
            last = last is { } latest && latest >= delivered.NativeTicks ? latest : delivered.NativeTicks;
        }
    }

    public void Admitted(Guid providerId, int eventId, int version, bool queued)
    {
        lock (gate)
        {
            Entry entry = EntryFor(providerId, eventId, version);
            if (queued)
            {
                entry.Admitted++;
                return;
            }

            // Lost after admission rather than admitted: it is reported as the callback queue's loss, and its delivery
            // is taken back so the descriptor's outcomes still add up to what it delivered.
            entry.Delivered--;
            queueDrops++;
        }
    }

    public void Omitted(OmissionReason reason, in DeliveredRecord delivered)
    {
        lock (gate)
        {
            Entry entry = EntryFor(delivered.ProviderId, delivered.EventId, delivered.Version);
            if (reason != OmissionReason.UnrequestedProvider && !requested.Contains(delivered.ProviderId))
            {
                // A denied event of a provider admission does not know is its own descriptor, not the provider's
                // unrequested remainder: move its delivery there so each entry keeps one policy.
                entry.Delivered--;
                entry = EntryFor(delivered.ProviderId, delivered.EventId, delivered.Version, wholeProvider: false);
                entry.Delivered++;
            }

            entry.Omitted++;
            entry.Omission = reason;
        }
    }

    public void Undecodable(UndecodableReason reason, in DeliveredRecord delivered)
    {
        lock (gate)
        {
            Entry entry = EntryFor(delivered.ProviderId, delivered.EventId, delivered.Version);
            entry.Undecodable[reason] = entry.Undecodable.GetValueOrDefault(reason) + 1;
        }
    }

    /// <summary>The one-epoch ledger of what was tallied, with the losses the source and InterCat reported.</summary>
    public CoverageLedgerV1 ToLedger(IReadOnlyList<CoverageLossV1> losses)
    {
        ArgumentNullException.ThrowIfNull(losses);
        lock (gate)
        {
            return Snapshot(losses);
        }
    }

    private CoverageLedgerV1 Snapshot(IReadOnlyList<CoverageLossV1> losses)
    {
        if (overflowed)
        {
            throw new InvalidDataException(
                $"The source delivered more than {CoverageLedgerV1.MaximumDescriptors} distinct descriptors and "
                + "providers, which is more than a coverage ledger records. It is refused rather than published "
                + "with coverage that does not add up.");
        }

        Dictionary<Guid, string> names = ProviderNames();
        CoverageLedgerV1 ledger = new()
        {
            Contract = CoverageLedgerV1.ContractName,
            Epochs =
            [
                new()
                {
                    Epoch = 1,
                    Acquisition = acquisition,
                    FirstDeliveredNativeTicks = first,
                    LastDeliveredNativeTicks = last,
                    Collected =
                    [
                        .. plan.Sources.SelectMany(source => source.Events)
                            .Where(descriptor => enabled is null || enabled.Contains(descriptor.ProviderGuid))
                            .OrderBy(descriptor => descriptor.ProviderGuid)
                            .ThenBy(descriptor => descriptor.EventId)
                            .ThenBy(descriptor => descriptor.Version)
                            .Select(descriptor => new CoverageCollectedV1
                            {
                                ProviderId = descriptor.ProviderGuid,
                                ProviderName = names.GetValueOrDefault(descriptor.ProviderGuid, descriptor.ProviderGuid.ToString("D")),
                                EventId = descriptor.EventId,
                                Version = descriptor.Version,
                                Mechanism = descriptor.Mechanism,
                            }),
                    ],
                    Deliveries =
                    [
                        .. entries
                            .Where(entry => entry.Value.Delivered > 0)
                            .OrderBy(entry => entry.Key.Provider)
                            .ThenBy(entry => entry.Key.EventId ?? -1)
                            .ThenBy(entry => entry.Key.Version ?? -1)
                            .Select(entry => new CoverageDeliveryV1
                            {
                                ProviderId = entry.Key.Provider,
                                EventId = entry.Key.EventId,
                                Version = entry.Key.Version,
                                Delivered = entry.Value.Delivered,
                                Admitted = entry.Value.Admitted,
                                Omission = entry.Value.Omitted > 0 ? entry.Value.Omission : null,
                                Omitted = entry.Value.Omitted,
                                // A copy: the live tally keeps counting after the snapshot is published.
                                Undecodable = entry.Value.Undecodable.Count > 0
                                    ? new Dictionary<UndecodableReason, long>(entry.Value.Undecodable)
                                    : null,
                            }),
                    ],
                    Losses = losses,
                },
            ],
        };
        ledger.Validate();
        return ledger;
    }

    /// <summary>
    /// Each admitted provider's name: as the capture requested it, or as the source catalog names the source whose
    /// descriptors admission compiled. An import enables nothing, so the catalog is what names its providers.
    /// </summary>
    private Dictionary<Guid, string> ProviderNames()
    {
        var names = new Dictionary<Guid, string>();
        foreach (ProviderEnablementRequest provider in plan.Providers)
        {
            names.TryAdd(provider.ProviderGuid, provider.ProviderName);
        }

        foreach (SourceAdmissionPlan source in plan.Sources)
        {
            if (WindowsSourceCatalog.Find(source.SourceId) is { } definition)
            {
                names.TryAdd(source.ProviderGuid, definition.ProviderName);
            }
        }

        return names;
    }

    private Entry EntryFor(Guid provider, int eventId, int version, bool? wholeProvider = null)
    {
        (Guid, int?, int?) key = wholeProvider ?? !requested.Contains(provider)
            ? (provider, null, null)
            : (provider, eventId, version);
        if (!entries.TryGetValue(key, out Entry? entry))
        {
            if (entries.Count >= CoverageLedgerV1.MaximumDescriptors)
            {
                overflowed = true;
                return overflow;
            }

            entry = new();
            entries[key] = entry;
        }

        return entry;
    }

    private sealed class Entry
    {
        public long Delivered { get; set; }

        public long Admitted { get; set; }

        public long Omitted { get; set; }

        public OmissionReason? Omission { get; set; }

        public Dictionary<UndecodableReason, long> Undecodable { get; } = [];
    }
}
