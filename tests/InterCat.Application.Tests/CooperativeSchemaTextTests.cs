using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// What of the SDK's schema never reached a capture (ADR-111): a process announcing a minor later than this InterCat knows,
/// whose added events no capture of it admits, or the events of a minor it knows that the capture's retained plan did not
/// admit - content's aside, which a capture keeping none leaves out by choice - said alike for icat messages and the window.
/// </summary>
public sealed class CooperativeSchemaTextTests
{
    private const string Later = "native.exe · 200 announced the SDK's schema 1.3, later than the 1.2 this InterCat knows: what it added "
        + "never reached the capture, and is counted nowhere here.";

    private const string NoBatches = "client.exe · 100 announced the SDK's schema 1.1, but the capture admitted none of the batches minor 1 "
        + "added: they never reached it, and are counted nowhere here.";

    [Fact(DisplayName = "R21: a process announcing a later minor than InterCat knows is said to have raised what never reached the capture, and so is one whose known minor's events the capture's plan did not admit - content's aside - each from the records and the retained plan, never assumed")]
    public void WhatNeverReachedTheCaptureIsSaid()
    {
        // A capture of InterCat's first minor admitted the SDK's events 1 to 7: the .NET process's batches never reached it,
        // and nothing the native process's later minor added could.
        Assert.Equal([NoBatches, NoBatches.Replace("client.exe · 100", "viewer.exe · 300", StringComparison.Ordinal).Replace("1.1,", "1.2,",
            StringComparison.Ordinal), Later], Read(Plan(1, 2, 3, 4, 5, 6, 7), out IReadOnlySet<ushort>? first).Unreached);
        Assert.Equal(Enumerable.Range(1, 7).Select(id => (ushort)id), first!.Order());

        // A capture admitting the batches missed only what the later minor added; content, which a capture keeping none leaves
        // out by choice, is never said missed of the viewer speaking this InterCat's minor 2.
        Assert.Equal([Later], Read(Plan(1, 2, 3, 4, 5, 6, 7, 8, 9), out _).Unreached);

        // A generation retaining no plan says nothing of a known minor's events, only of the later minor's.
        CooperativeMessageReading unplanned = Read(null, out IReadOnlySet<ushort>? none);
        Assert.Null(none);
        Assert.Equal([Later], unplanned.Unreached);
        Assert.Equal([(100, (int?)1, "dotnet-1.1"), (200, (int?)3, "native-1.3"), (300, (int?)2, "dotnet-1.2")],
            unplanned.Announcements.Select(announcement => (announcement.ProcessId, announcement.Minor, announcement.Library)));
    }

    /// <summary>The .NET process announcing minor 1 and the native one minor 3, under <paramref name="plan"/>; read as icat messages reads them.</summary>
    private static CooperativeMessageReading Read(byte[]? plan, out IReadOnlySet<ushort>? admitted)
    {
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\native.exe" },
            Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\viewer.exe" },
        };
        var fields = new List<SourceFieldRowV1>();
        ulong ordinal = 10;
        foreach ((long ticks, int process, string library, long minor) in new[]
            { (10L, 100, "dotnet-1.1", 1L), (20L, 200, "native-1.3", 3L), (30L, 300, "dotnet-1.2", 2L) })
        {
            ObservationRowV1 row = SdkRecord(ticks, CooperativeSdk.SdkStarted, process, ++ordinal, null) with
            {
                Kind = ObservationKind.Inventory,
                ResourceName = library,
            };
            rows.Add(row);
            fields.Add(Field(row, SourceField.SdkSchemaMinor, minor));
        }

        using var session = new TemporarySession(Guid.NewGuid());
        _ = Publish(session.Store, rows, fields: fields, plan: plan);
        session.Store.ReleaseSegmentReaders();
        admitted = CooperativeSchemaText.AdmittedEvents(session.Store.Root, session.Store.Current!);
        CooperativeMessageReading reading = CooperativeMessageText.Read(session.Store);
        session.Store.ReleaseSegmentReaders();
        return reading;
    }

    /// <summary>A normalizer plan admitting the SDK's events <paramref name="events"/>, as much of it as says so.</summary>
    private static byte[] Plan(params ushort[] events) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        contract = "normalizer-plan-v1",
        descriptors = events.Select(id => new { providerGuid = CooperativeSdk.ProviderId, eventId = id, mechanism = (int)Mechanism.ApplicationSdk }),
    });
}
