using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

public sealed partial class ProcessActivityIndex
{
    /// <summary>The published files these counts were made from, or null when one had no published identity.</summary>
    internal IReadOnlyCollection<StoreDependency>? FilesRead => read;

    /// <summary>
    /// Writes the counts in canonical order (`contracts/derivation-checkpoint-v1.md` §3, <c>activity</c>): the rule they
    /// were counted under, records with no owner, each PID's span and unbound records, then each instance's records per
    /// mechanism.
    /// </summary>
    internal void WriteState(IndexFileWriter writer)
    {
        writer.Str8(CountRule);
        writer.I64(RecordsWithoutOwner);
        KeyValuePair<int, PidSpan>[] spans = [.. pids];
        Array.Sort(spans, static (left, right) => left.Key.CompareTo(right.Key));
        writer.Count(spans.Length);
        foreach ((int processId, PidSpan span) in spans)
        {
            writer.I32(processId);
            writer.I64(span.First);
            writer.I64(span.Last);
            writer.I64(span.Unbound);
        }

        (int Instance, Mechanism Mechanism, long Direct, long Bound)[] counts =
        [
            .. instances.SelectMany(entry => entry.Value.Direct.Keys.Union(entry.Value.Bound.Keys).Select(mechanism => (
                entry.Key,
                mechanism,
                entry.Value.Direct.GetValueOrDefault(mechanism),
                entry.Value.Bound.GetValueOrDefault(mechanism))))
                .Where(entry => entry.Item3 + entry.Item4 > 0),
        ];
        Array.Sort(counts, static (left, right) =>
        {
            int order = left.Instance.CompareTo(right.Instance);
            return order != 0 ? order : left.Mechanism.CompareTo(right.Mechanism);
        });
        writer.Count(counts.Length);
        foreach ((int instance, Mechanism mechanism, long direct, long bound) in counts)
        {
            writer.I32(instance);
            writer.U16((ushort)mechanism);
            writer.I64(direct);
            writer.I64(bound);
        }
    }

    /// <summary>
    /// Rebuilds counts a checkpoint holds over <paramref name="processes"/>, whose positions they name.
    /// <paramref name="earlierRule"/> names the rule of <see cref="ProcessInstanceIndex.EarlierBindingRules"/> the
    /// checkpoint was derived under, whose instances and counts are this rule's only when every record named its owner:
    /// that rule bound fewer of those that did not, and this one binds some of them.
    /// </summary>
    internal static ProcessActivityIndex ReadState(
        IndexFileReader reader,
        ProcessInstanceIndex processes,
        HashSet<StoreDependency> read,
        string? earlierRule)
    {
        string rule = reader.Str8();
        if (!string.Equals(rule, CountRule, StringComparison.Ordinal))
        {
            throw reader.Invalid($"its process counts were made under {rule}, and this build counts under {CountRule}.");
        }

        long withoutOwner = reader.I64();
        if (withoutOwner < 0)
        {
            throw reader.Invalid("it counts a negative number of records without an owner.");
        }

        if (earlierRule is not null && withoutOwner > 0)
        {
            throw reader.Invalid(
                $"it was derived under {earlierRule}, which left {withoutOwner:N0} "
                + $"{(withoutOwner == 1 ? "record" : "records")} naming no owner unbound, and "
                + $"{ProcessInstanceIndex.BindingRule} binds an RPC or HTTP record among them to the process that raised it.");
        }

        int count = reader.Count(4 + 8 + 8 + 8);
        var pids = new Dictionary<int, PidSpan>(count);
        int? previous = null;
        for (int index = 0; index < count; index++)
        {
            int processId = reader.I32();
            var span = new PidSpan { First = reader.I64(), Last = reader.I64(), Unbound = reader.I64() };
            if ((previous is { } before && before >= processId) || span.First > span.Last || span.Unbound < 0)
            {
                throw reader.Invalid("a PID's readings or unbound records contradict each other, or are out of order.");
            }

            pids.Add(processId, span);
            previous = processId;
        }

        count = reader.Count(4 + 2 + 8 + 8);
        var instances = new Dictionary<int, InstanceActivity>();
        (int Instance, int Mechanism) last = (-1, -1);
        for (int index = 0; index < count; index++)
        {
            int instance = reader.I32();
            var mechanism = (Mechanism)reader.U16();
            long direct = reader.I64();
            long bound = reader.I64();
            if (instance < 0 || instance >= processes.Instances.Count || !Enum.IsDefined(mechanism)
                || direct < 0 || bound < 0 || direct + bound <= 0
                || (instance, (int)mechanism).CompareTo(last) <= 0)
            {
                throw reader.Invalid("an instance's records name no instance, an undefined mechanism, or are out of order.");
            }

            if (!instances.TryGetValue(instance, out InstanceActivity? activity))
            {
                activity = new();
                instances[instance] = activity;
            }

            if (direct > 0)
            {
                activity.Direct[mechanism] = direct;
            }

            if (bound > 0)
            {
                activity.Bound[mechanism] = bound;
            }

            last = (instance, (int)mechanism);
        }

        return new(processes, instances, pids, withoutOwner, read);
    }
}
