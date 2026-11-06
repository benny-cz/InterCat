using System.Diagnostics;
using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.SectionProbe;

/// <summary>
/// FX-SECT-003 recorded through the product (ADR-092's third addendum): the held workload's creator makes both sections and
/// maps a view of each, and its opener opens both and holds its handles; only once it says it holds them is
/// <c>icat record</c> launched under the opt-in sections profile. Once the capture's session runs, FX-SECT-001 itself runs
/// inside it as the control, and the held opener's views and both processes' unmappings and closes follow inside it too. The
/// one session is read into a ledger of each workload's section records, each evaluated against its own truth, and deleted.
/// </summary>
internal static partial class Product
{
    public static async Task<Dictionary<string, object?>> RunHeldAsync(string icat, string workload, string output, int runs, int rounds, int bytes, int held, int duration)
    {
        static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
        var results = new List<Dictionary<string, object?>>();
        for (int run = 1; run <= runs; run++)
        {
            string folder = Path.Combine(output, "run-" + Text(run));
            string session = Path.Combine(Path.GetTempPath(), "sect-held-" + Guid.NewGuid().ToString("N")[..8]);
            string heldTruth = Path.Combine(folder, "held", "truth");
            string controlTruth = Path.Combine(folder, "control", "truth");
            Directory.CreateDirectory(folder);
            try
            {
                Dictionary<string, object?> health;
                using (Process holding = Process.Start(new ProcessStartInfo(workload,
                    ["section-share", "--truth", heldTruth, "--rounds", Text(rounds), "--view-bytes", Text(bytes), "--held-ms", Text(held)])
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }) ?? throw new InvalidOperationException("The held workload could not be started."))
                {
                    // Nothing is launched before both processes hold what the capture is never to see made.
                    string? said;
                    try
                    {
                        said = await holding.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        said = null;
                    }

                    if (said is not "held")
                    {
                        holding.Kill(entireProcessTree: true);
                        throw new InvalidOperationException("The held workload never said it held its sections.");
                    }

                    Task<string> rest = holding.StandardOutput.ReadToEndAsync();
                    using (Recording recording = await Recording.StartAsync(icat, session, duration).ConfigureAwait(false))
                    {
                        // The control: FX-SECT-001 made, used and ended inside the capture, before the held opener's hold ends.
                        using (Process control = Process.Start(new ProcessStartInfo(workload,
                            ["section-share", "--truth", controlTruth, "--rounds", Text(rounds), "--view-bytes", Text(bytes)])
                            { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("The control workload could not be started."))
                        {
                            await FinishedAsync(control).ConfigureAwait(false);
                        }

                        await FinishedAsync(holding).ConfigureAwait(false);
                        _ = await rest.ConfigureAwait(false);
                        health = await recording.FinishAsync().ConfigureAwait(false);
                    }
                }

                string heldLedger = Path.Combine(folder, "held", "records.csv");
                SectionCoverageResult heldResult = SessionLedger.Write(session, heldTruth, heldLedger, out long firstRecord);
                SectionCoverageResult controlResult = SessionLedger.Write(session, controlTruth, Path.Combine(folder, "control", "records.csv"));
                results.Add(new()
                {
                    ["run"] = run,
                    ["health"] = health,
                    ["order"] = Order(SessionLedger.Truth(heldTruth), firstRecord),
                    ["held"] = Held(heldResult, SessionLedger.Truth(heldTruth), heldLedger),
                    ["control"] = Control(controlResult),
                });
            }
            finally
            {
                if (Directory.Exists(session))
                {
                    Directory.Delete(session, recursive: true);
                }
            }
        }

        return new()
        {
            ["question"] = "Whether a capture through the product - icat record under the opt-in sections profile - joins two processes' "
                + "handles to a section it never saw made: FX-SECT-001's creator makes its two sections and maps a view of each, and "
                + "its opener opens both and holds its handles while the capture is launched, then maps and unmaps its views and both "
                + "close inside it; FX-SECT-001 itself, made and ended inside the same capture, is the control.",
            ["scenario"] = "FX-SECT-003",
            ["truth"] = new Dictionary<string, object?> { ["sections"] = 2, ["openerRounds"] = rounds, ["viewBytes"] = bytes, ["heldMilliseconds"] = held },
            ["profile"] = "sections",
            ["durationSeconds"] = duration,
            ["runs"] = results,
            ["notes"] = new[]
            {
                "Each capture is the product's own icat record process, launched once the held workload said it held its sections, "
                    + "recording into a scratch session that is deleted once both workloads' ledgers are written.",
                "A run's health is the capture's own report: what its sources lost, what its queue dropped, and what a source's gate did not admit, by reason.",
            },
        };
    }

    // When the held processes made what the capture was never to see made - both processes' handles and the creator's views -
    // beside the session's first section record and the opener's first view of a held section, mapped inside the capture.
    private static Dictionary<string, object?> Order(TruthRecord[] truth, long firstRecordUtcTicks)
    {
        DateTimeOffset made = truth.Where(static record => record.Kind is TruthEventKind.ObjectCreated or TruthEventKind.ObjectOpened
            || record is { Kind: TruthEventKind.ViewMapped, Role: "creator" }).Max(static record => record.RecordedUtc);
        DateTimeOffset used = truth.Where(static record => record is { Kind: TruthEventKind.ViewMapped, Role: "opener" }).Min(static record => record.RecordedUtc);
        DateTimeOffset closed = truth.Where(static record => record.Kind == TruthEventKind.HandleClosed).Min(static record => record.RecordedUtc);
        var first = new DateTimeOffset(firstRecordUtcTicks, TimeSpan.Zero);
        return new()
        {
            ["lastHeldHandleOrViewMadeUtc"] = made.UtcDateTime,
            ["firstSectionRecordOfTheSessionUtc"] = first.UtcDateTime,
            ["firstOpenerViewUtc"] = used.UtcDateTime,
            ["firstHeldCloseUtc"] = closed.UtcDateTime,
            ["madeBeforeTheCaptureAndUsedAndClosedInIt"] = made < first && first < used && first < closed,
        };
    }

    // What the capture made of the held sections: each close naming one, and whether its number is its alone - no other record
    // of the ledger carries it, as no creation of its handle was seen; and each section by its views - the one number the
    // records of the views the truth mapped inside the capture carry, the processes that number's records bind to, and the
    // records of the session's start rundown, a view mapped before it began, carrying that number.
    private static Dictionary<string, object?> Held(SectionCoverageResult result, TruthRecord[] truth, string ledger)
    {
        Dictionary<string, int> processes = truth.GroupBy(static record => record.Role).ToDictionary(static group => group.Key, static group => group.First().ProcessId);
        List<SectionObservation> records = SessionLedger.ReadLedger(ledger, processes);
        SectionObservation[] closes = [.. records.Where(static record => record is { IsView: false, Kind: ObservationKind.Close, Name: not null, SectionObjectNumber: not null })];
        HashSet<long> viewNumbers = [.. result.Sections.Select(static section => section.SectionNumber).OfType<long>()];
        MechanismMeasurement measured = result.Measurement;
        return new()
        {
            ["truthSections"] = measured.TruthResources,
            ["discoveredWithLifetime"] = measured.DiscoveredResourcesWithLifetime,
            ["truthOperations"] = measured.TruthOperations,
            ["observed"] = measured.TruthOperationsWithAdmittedObservation,
            ["boundToTheirSection"] = measured.TruthOperationsBoundToResourceInstance,
            ["closesNamingASection"] = closes.Length,
            ["closesNumberedAlone"] = closes.Count(close => records.Count(record => record.SectionObjectNumber == close.SectionObjectNumber) == 1),
            ["numbersOfThoseCloses"] = closes.Select(static close => close.SectionObjectNumber).Distinct().Count(),
            ["sectionsOneNumberByTheirViews"] = result.Sections.Count(static section => section.SectionNumber is not null),
            ["ofThemBoundToBothProcesses"] = result.Sections.Count(static section => section.SectionNumber is not null
                && section.MemberProcesses.SequenceEqual(section.TruthProcesses)),
            ["truthViews"] = result.Sections.Sum(static section => section.TruthViews),
            ["viewsSeen"] = result.Sections.Sum(static section => section.ViewsSeen),
            ["startRundownRecordsOnThoseNumbers"] = records.Count(record => record is { IsView: true, Kind: ObservationKind.Inventory }
                && record.SectionNumber is { } number && viewNumbers.Contains(number)),
            ["expectedMemberships"] = measured.ExpectedMemberships,
            ["resolvedMemberships"] = measured.ResolvedMemberships,
            ["falseMemberships"] = measured.FalsePeerAttributions,
            ["gaps"] = result.Sections.Select(static section => section.Gap).OfType<string>().Distinct().ToArray(),
            ["ledgerRecords"] = records.Count,
            ["tier"] = result.Assessment.Tier.ToString(),
        };
    }

    // The control made and ended inside the same capture, evaluated as FX-SECT-001 is.
    private static Dictionary<string, object?> Control(SectionCoverageResult result) => new()
    {
        ["truthSections"] = result.Measurement.TruthResources,
        ["discoveredWithLifetime"] = result.Measurement.DiscoveredResourcesWithLifetime,
        ["expectedMemberships"] = result.Measurement.ExpectedMemberships,
        ["resolvedMemberships"] = result.Measurement.ResolvedMemberships,
        ["falseMemberships"] = result.Measurement.FalsePeerAttributions,
        ["truthOperations"] = result.Measurement.TruthOperations,
        ["observed"] = result.Measurement.TruthOperationsWithAdmittedObservation,
        ["boundToTheirSection"] = result.Measurement.TruthOperationsBoundToResourceInstance,
        ["tier"] = result.Assessment.Tier.ToString(),
    };
}
