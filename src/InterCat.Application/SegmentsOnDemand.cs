using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A leased generation's named segments, each opened through the store's reader cache only when first read (P25): a
/// derivation already made of them is checked against their names, and a page of its records opens only the segments
/// that hold them.
/// </summary>
internal sealed class SegmentsOnDemand(SessionStore store, SessionManifestV1 manifest, IReadOnlyList<string> names) : IPublishedSegments
{
    private readonly SegmentReaderV1?[] opened = new SegmentReaderV1?[names.Count];

    public IReadOnlyList<string> Names { get; } = names;

    public int Count => Names.Count;

    public SegmentReaderV1 this[int index] => opened[index] ??= SessionSegments.Open(store, manifest, Names[index]);

    public IEnumerator<SegmentReaderV1> GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
