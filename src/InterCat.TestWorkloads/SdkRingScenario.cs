using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Sdk;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one FX-SDK-001 run: the ring's readers, its two epochs' messages, and its slots.</summary>
internal sealed record SdkRingOptions
{
    public const string ScenarioId = "FX-SDK-001";

    /// <summary>The name every process gives the ring's channel as it opens it.</summary>
    public const string ChannelName = "fx-sdk-ring";

    /// <summary>The most readers the ring's header holds a cursor for.</summary>
    public const int MaximumReaders = 8;

    public required string TruthDirectory { get; init; }

    /// <summary>The ring's mapping name, which the coordinator chooses and gives each process; null for the coordinator.</summary>
    public string? Name { get; init; }

    /// <summary>The identity every process names the ring's channel by, which the coordinator chooses.</summary>
    public Guid Channel { get; init; }

    public int Readers { get; init; } = 3;

    /// <summary>How many messages the producer publishes at the channel's first epoch.</summary>
    public int Messages { get; init; } = 120;

    /// <summary>How many it publishes at the second, once it made the channel again: its sequence starts at 1 once more.</summary>
    public int RestartMessages { get; init; } = 40;

    /// <summary>How many slots the ring holds: a slot is written again only once every reader consumed what it held.</summary>
    public int Slots { get; init; } = 16;

    /// <summary>The seed of each message's length.</summary>
    public int Seed { get; init; } = 20_261_107;

    /// <summary>A reader's number, from 1; 0 for the coordinator and the producer.</summary>
    public int Reader { get; init; }
}

/// <summary>
/// FX-SDK-001: a shared-memory ring one producer writes and three readers read, each process saying what it publishes and
/// consumes through InterCat's cooperative SDK as an application would (ADR-102). The producer makes the ring - a section of
/// the session's namespace under a name of the run's own - and opens its channel at epoch 1; once every reader has opened
/// the ring, it publishes each message into the next slot, its length seeded, and says so; a slot is written again only once
/// every reader consumed what it held. Each reader opens the channel at the epoch the producer opened and consumes every
/// message of it in sequence, saying so as its own numbered reader. Then the producer makes the channel again - a restart, so
/// its sequence starts at 1 once more at epoch 2 - and at that epoch's end publishes one message no reader reads, which stays
/// open. Each process logs every message it published or consumed by its channel, epoch, sequence and reader, with its
/// length. None reads InterCat state.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SdkRingScenario
{
    /// <summary>A slot's bytes: its message's sequence, its length, and the message.</summary>
    private const int SlotBytes = 4_096;

    /// <summary>What the header holds before the slots.</summary>
    private const int HeaderBytes = 256;

    // The header, in eight-byte words: the epoch the producer opened, the last message it published there - the epoch in
    // the high half and the sequence in the low - that epoch's message count, whether the ring is done, each reader's
    // attachment, and the last message each reader consumed, epoch and sequence alike.
    private const int EpochWord = 0;
    private const int PublishedWord = 1;
    private const int CountWord = 2;
    private const int DoneWord = 3;
    private const int AttachedWords = 4;
    private const int CursorWords = 12;

    /// <summary>The ring's epochs: its first opening, and the opening that makes it again.</summary>
    private const int Epochs = 2;

    /// <summary>How long any process waits for another before the run is failed.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// The coordinator: chooses the ring's name and its channel's identity, starts the producer, then each reader once the
    /// ring is made, lets the producer begin once every reader has opened it, and writes the run's parameters.
    /// </summary>
    public static async Task<int> RunCoordinatorAsync(SdkRingOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");
        SdkRingOptions run = options with
        {
            Name = $@"Local\InterCat-{SdkRingOptions.ScenarioId}-{Guid.NewGuid():N}",
            Channel = Guid.NewGuid(),
        };
        using Process producer = StartChild(executable, "sdk-ring-producer", run);
        if (await producer.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "created")
        {
            await producer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The producer exited before it made the ring.").ConfigureAwait(false);
            return 1;
        }

        var readers = new List<Process>();
        try
        {
            for (int reader = 1; reader <= run.Readers; reader++)
            {
                Process started = StartChild(executable, "sdk-ring-reader", run with { Reader = reader });
                readers.Add(started);
                if (await started.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "attached")
                {
                    producer.Kill(entireProcessTree: true);
                    await Console.Error.WriteLineAsync($"Reader {reader} exited before it opened the ring.").ConfigureAwait(false);
                    return 1;
                }
            }

            await producer.StandardInput.WriteLineAsync("go").ConfigureAwait(false);
            await producer.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll([producer.WaitForExitAsync(cancellationToken), .. readers.Select(reader => reader.WaitForExitAsync(cancellationToken))])
                .ConfigureAwait(false);

            var summary = new
            {
                scenarioId = SdkRingOptions.ScenarioId,
                channel = run.Channel,
                channelName = SdkRingOptions.ChannelName,
                name = run.Name,
                run.Readers,
                run.Messages,
                run.RestartMessages,
                run.Slots,
                run.Seed,
                epochs = Epochs,
                producerExitCode = producer.ExitCode,
                readerExitCodes = readers.Select(reader => reader.ExitCode).ToArray(),
            };
            await File.WriteAllTextAsync(Path.Combine(run.TruthDirectory, "scenario.json"),
                JsonSerializer.Serialize(summary, SummaryOptions), cancellationToken).ConfigureAwait(false);
            return producer.ExitCode != 0 || readers.Any(reader => reader.ExitCode != 0) ? 1 : 0;
        }
        finally
        {
            foreach (Process reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>The producer: makes the ring, and once told every reader has opened it, publishes each epoch's messages.</summary>
    public static async Task<int> RunProducerAsync(SdkRingOptions options, CancellationToken cancellationToken)
    {
        string name = options.Name ?? throw new InvalidOperationException("The producer takes the ring's name from --name.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-producer.jsonl"), SdkRingOptions.ScenarioId, "producer");
        truth.Write(TruthEventKind.ProcessStarted);
        using MemoryMappedFile ring = MemoryMappedFile.CreateNew(name, HeaderBytes + ((long)options.Slots * SlotBytes));
        using MemoryMappedViewAccessor view = ring.CreateViewAccessor();
        truth.Write(TruthEventKind.ObjectCreated, resourceName: name, status: "Section");
        await Console.Out.WriteLineAsync("created").ConfigureAwait(false);
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false) != "go")
        {
            return 1;
        }

        return Produce(options, view, truth) ? 0 : 1;
    }

    /// <summary>A reader: opens the ring, says it has, and consumes every message of each epoch the producer opens.</summary>
    public static async Task<int> RunReaderAsync(SdkRingOptions options, CancellationToken cancellationToken)
    {
        string name = options.Name ?? throw new InvalidOperationException("A reader takes the ring's name from --name.");
        if (options.Reader is < 1 or > SdkRingOptions.MaximumReaders)
        {
            throw new InvalidOperationException($"A reader is numbered from 1 to {SdkRingOptions.MaximumReaders}.");
        }

        string role = string.Create(CultureInfo.InvariantCulture, $"reader-{options.Reader}");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, $"truth-{role}.jsonl"), SdkRingOptions.ScenarioId, role);
        truth.Write(TruthEventKind.ProcessStarted);
        using MemoryMappedFile ring = MemoryMappedFile.OpenExisting(name);
        using MemoryMappedViewAccessor view = ring.CreateViewAccessor();
        truth.Write(TruthEventKind.ObjectOpened, resourceName: name, status: "Section");
        Attach(view, options.Reader);
        await Console.Out.WriteLineAsync("attached").ConfigureAwait(false);
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Consume(options, view, truth) ? 0 : 1;
    }

    private static unsafe bool Produce(SdkRingOptions options, MemoryMappedViewAccessor view, TruthLog truth)
    {
        byte* ring = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ring);
        try
        {
            ring += view.PointerOffset;
            long* header = (long*)ring;
            var random = new Random(options.Seed);
            for (long epoch = 1; epoch <= Epochs; epoch++)
            {
                long count = epoch == 1 ? options.Messages : options.RestartMessages;

                // The count is written before the epoch, so a reader that sees the epoch reads its count.
                Volatile.Write(ref header[CountWord], count);
                Volatile.Write(ref header[PublishedWord], epoch << 32);
                Volatile.Write(ref header[EpochWord], epoch);
                using InterCatChannel channel = InterCatChannel.Open(options.Channel, SdkRingOptions.ChannelName, InterCatChannelKind.SharedMemory, epoch);
                for (long sequence = 1; sequence <= count; sequence++)
                {
                    // A slot is written again only once every reader consumed what it held.
                    if (sequence > options.Slots && !WaitConsumed(header, options.Readers, (epoch << 32) | (sequence - options.Slots)))
                    {
                        return false;
                    }

                    Publish(ring, header, options, channel, truth, epoch, sequence, random.Next(64, SlotBytes - 16 + 1), read: true);
                }

                // The epoch ends once every reader consumed all of it.
                if (!WaitConsumed(header, options.Readers, (epoch << 32) | count))
                {
                    return false;
                }

                // At the last epoch, one more message no reader reads: its count stays as it was, so the message stays open.
                if (epoch == Epochs)
                {
                    Publish(ring, header, options, channel, truth, epoch, count + 1, random.Next(64, SlotBytes - 16 + 1), read: false);
                }
            }

            Volatile.Write(ref header[DoneWord], 1);
            return true;
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static unsafe void Publish(byte* ring, long* header, SdkRingOptions options, InterCatChannel channel, TruthLog truth, long epoch,
        long sequence, int length, bool read)
    {
        byte* slot = ring + HeaderBytes + ((sequence - 1) % options.Slots * SlotBytes);
        *(long*)slot = sequence;
        *(int*)(slot + 8) = length;
        new Span<byte>(slot + 16, length).Fill(unchecked((byte)sequence));

        // The message is committed - where its readers can take it - once its sequence is published, after its bytes.
        Volatile.Write(ref header[PublishedWord], (epoch << 32) | sequence);
        channel.Published(sequence, length, copiedLength: length);
        truth.Write(TruthEventKind.MessageSent, declaredBytes: length, completedBytes: length, resourceName: SdkRingOptions.ChannelName,
            status: read ? null : "unread", message: new(options.Channel, epoch, sequence, null));
    }

    private static unsafe void Attach(MemoryMappedViewAccessor view, int reader)
    {
        byte* ring = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ring);
        try
        {
            long* header = (long*)(ring + view.PointerOffset);
            Volatile.Write(ref header[AttachedWords + reader - 1], 1);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static unsafe bool Consume(SdkRingOptions options, MemoryMappedViewAccessor view, TruthLog truth)
    {
        byte* ring = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ring);
        try
        {
            ring += view.PointerOffset;
            long* header = (long*)ring;
            long epoch = 0;
            while (true)
            {
                // The next epoch the producer opens, or the end of the ring.
                long next;
                var waited = Stopwatch.StartNew();
                var spinner = default(SpinWait);
                while ((next = Volatile.Read(ref header[EpochWord])) <= epoch)
                {
                    if (Volatile.Read(ref header[DoneWord]) == 1)
                    {
                        return true;
                    }

                    if (waited.Elapsed > Patience)
                    {
                        return false;
                    }

                    spinner.SpinOnce();
                }

                epoch = next;
                long count = Volatile.Read(ref header[CountWord]);
                using InterCatChannel channel = InterCatChannel.Open(options.Channel, SdkRingOptions.ChannelName, InterCatChannelKind.SharedMemory, epoch);
                for (long sequence = 1; sequence <= count; sequence++)
                {
                    waited.Restart();
                    while (Volatile.Read(ref header[PublishedWord]) < ((epoch << 32) | sequence))
                    {
                        if (waited.Elapsed > Patience)
                        {
                            return false;
                        }

                        spinner.SpinOnce();
                    }

                    byte* slot = ring + HeaderBytes + ((sequence - 1) % options.Slots * SlotBytes);
                    int length = *(int*)(slot + 8);

                    // A slot holding another message than the one published was written again before this reader read it.
                    if (*(long*)slot != sequence)
                    {
                        return false;
                    }

                    channel.Consumed(sequence, length, consumer: options.Reader);
                    truth.Write(TruthEventKind.MessageReceived, declaredBytes: length, resourceName: SdkRingOptions.ChannelName,
                        message: new(options.Channel, epoch, sequence, options.Reader));
                    Volatile.Write(ref header[CursorWords + options.Reader - 1], (epoch << 32) | sequence);
                }
            }
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    /// <summary>Waits until every reader consumed at least <paramref name="reached"/>, epoch and sequence alike.</summary>
    private static unsafe bool WaitConsumed(long* header, int readers, long reached)
    {
        var waited = Stopwatch.StartNew();
        var spinner = default(SpinWait);
        for (int reader = 0; reader < readers; reader++)
        {
            while (Volatile.Read(ref header[CursorWords + reader]) < reached)
            {
                if (waited.Elapsed > Patience)
                {
                    return false;
                }

                spinner.SpinOnce();
            }
        }

        return true;
    }

    private static Process StartChild(string executable, string verb, SdkRingOptions options)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in new[]
        {
            verb, "--truth", options.TruthDirectory, "--name", options.Name!, "--channel", options.Channel.ToString(),
            "--readers", options.Readers.ToString(CultureInfo.InvariantCulture),
            "--messages", options.Messages.ToString(CultureInfo.InvariantCulture),
            "--restart-messages", options.RestartMessages.ToString(CultureInfo.InvariantCulture),
            "--slots", options.Slots.ToString(CultureInfo.InvariantCulture),
            "--seed", options.Seed.ToString(CultureInfo.InvariantCulture),
            "--reader", options.Reader.ToString(CultureInfo.InvariantCulture),
        })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }
}
