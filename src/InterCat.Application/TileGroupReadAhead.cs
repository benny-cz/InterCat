using System.Runtime.ExceptionServices;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A group's lists read ahead of the count that walks its sections one after another (ADR-113's second addendum): each
/// section's owners' postings directory and the lists a member owns - with measures, their measures too - read on several
/// workers into the file's cache, so the count finds them kept. Since revision 1089 the workers take the sections in the
/// order the count walks them, and the count waits for a section only until its parts are kept, counting it while the
/// workers read the sections after it: reading and counting overlap, where the count had waited for every section to be
/// read before counting the first. A section no worker has taken when the count reaches it is read ahead by the count
/// itself, so the count never waits on a worker yet to start. Nothing is counted here, so the count's answer is what it
/// would be without it; a part that cannot be read is left to the count, which reads it again and refuses it as it
/// would have (I4), and anything else reading a section throws is thrown where the count reaches that section.
/// </summary>
internal sealed class TileGroupReadAhead : IDisposable
{
    private static readonly TileGroupReadAhead Nothing = new();

    // The sections read ahead, in the order the count walks them, and each one's place among them.
    private readonly TileSection[] sections;
    private readonly Dictionary<TileSection, int> places;

    // 1 once a worker or the count has taken a section to read; true once its reading has ended, however it ended.
    private readonly int[] taken;
    private readonly bool[] ended;
    private readonly object gate = new();
    private readonly Func<TileSection, uint, int, bool> member;
    private readonly bool measures;
    private readonly CancellationTokenSource? stop;
    private readonly Task[] workers;

    // The next place a worker takes, and the first place whose reading threw what no count refuses on its own.
    private int next = -1;
    private int failedAt = int.MaxValue;
    private ExceptionDispatchInfo? failure;

    private TileGroupReadAhead()
    {
        sections = [];
        places = [];
        taken = [];
        ended = [];
        member = static (_, _, _) => false;
        workers = [];
    }

    private TileGroupReadAhead(
        TileIndexFile tiles,
        IOwnedDirectory directory,
        TileSection[] sections,
        Func<TileSection, uint, int, bool> member,
        bool measures,
        CancellationToken cancellationToken)
    {
        this.sections = sections;
        places = new(sections.Length, ReferenceEqualityComparer.Instance);
        for (int place = 0; place < sections.Length; place++)
        {
            places[sections[place]] = place;
        }

        taken = new int[sections.Length];
        ended = new bool[sections.Length];
        this.member = member;
        this.measures = measures;
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // One worker fewer than the segment passes run on, the count walking on the last; one at least.
        int count = Math.Min(sections.Length, Math.Max(1, (SegmentPasses.Workers.Value ?? Environment.ProcessorCount) - 1));
        workers = new Task[count];
        for (int worker = 0; worker < count; worker++)
        {
            // Each worker stops itself, ending every section it took, which a task cancelled before it ran would not.
            workers[worker] = Task.Run(() => Work(tiles, directory), CancellationToken.None);
        }
    }

    /// <summary>
    /// Starts reading ahead what a group's count - or with <paramref name="measures"/> its measure - of
    /// <paramref name="sections"/>, walked in the order given, will read: each section's directory and the lists
    /// <paramref name="member"/> admits, where at least two of them would read from the file rather than find kept their
    /// directory or, since revision 1090, the group's lists. Otherwise nothing is read ahead, and the count reads what it
    /// needs.
    /// </summary>
    internal static TileGroupReadAhead Start(
        TileIndexFile tiles,
        IOwnedDirectory directory,
        IReadOnlyList<TileSection> sections,
        Func<TileSection, uint, int, bool> member,
        bool measures,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(member);
        cancellationToken.ThrowIfCancellationRequested();
        TileSection[] unread = [.. sections.Where(section => tiles.WouldReadGroup(section, member, measures))];
        return unread.Length < 2 ? Nothing : new(tiles, directory, unread, member, measures, cancellationToken);
    }

    /// <summary>
    /// Returns once <paramref name="section"/>'s parts are read ahead, or could not be read: at once for a section not
    /// being read ahead. Where no worker has taken the section yet, it is read ahead here, on the count's own
    /// <paramref name="counting"/>. Throws what reading it threw that the count would not refuse on its own.
    /// </summary>
    public void WaitFor(TileSection section, TileReading counting, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(counting);
        if (!places.TryGetValue(section, out int place))
        {
            return;
        }

        if (Interlocked.Exchange(ref taken[place], 1) == 0)
        {
            Read(counting, place, cancellationToken);
        }
        else
        {
            // A worker took it, and ends every section it takes, stopped or not.
            lock (gate)
            {
                while (!ended[place])
                {
                    _ = Monitor.Wait(gate);
                }
            }
        }

        lock (gate)
        {
            if (failedAt <= place)
            {
                failure!.Throw();
            }
        }
    }

    /// <summary>Stops the workers, each once the section it is reading has ended, and waits until they have.</summary>
    public void Dispose()
    {
        if (stop is null)
        {
            return;
        }

        stop.Cancel();
        try
        {
            Task.WaitAll(workers);
        }
        catch (AggregateException)
        {
            // A worker keeps what its reading threw; nothing else of it fails but closing its file, which ends with it.
        }

        stop.Dispose();
    }

    // A worker: the next section not yet taken, until none is left; a stopped worker ends each it takes unread.
    private void Work(TileIndexFile tiles, IOwnedDirectory directory)
    {
        TileReading? reading = null;
        try
        {
            for (int place = Interlocked.Increment(ref next); place < sections.Length; place = Interlocked.Increment(ref next))
            {
                if (Interlocked.Exchange(ref taken[place], 1) == 0)
                {
                    reading ??= tiles.Read(directory);
                    Read(reading, place, stop!.Token);
                }
            }
        }
        finally
        {
            reading?.Dispose();
        }
    }

    // Reads one section ahead, then says it ended, keeping what reading it threw that no count refuses on its own. A
    // reading stopped or cancelled ends with nothing more read: the count reads what it finds missing, or is cancelled.
    private void Read(TileReading reading, int place, CancellationToken cancellationToken)
    {
        ExceptionDispatchInfo? thrown = null;
        try
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                reading.ReadGroupAhead(sections[place], member, measures, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            thrown = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            lock (gate)
            {
                if (thrown is not null && place < failedAt)
                {
                    (failure, failedAt) = (thrown, place);
                }

                ended[place] = true;
                Monitor.PulseAll(gate);
            }
        }
    }
}
