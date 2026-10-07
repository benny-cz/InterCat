namespace InterCat.Storage;

/// <summary>
/// A generation's segments in their order, each opened only when it is first read, whose published names are known
/// without opening one. A derivation made from them is checked against them by name alone, so a query that reads its
/// derivation opens no segment, and one that reads some of their records opens only the segments holding them (P25).
/// </summary>
public interface IPublishedSegments : IReadOnlyList<SegmentReaderV1>
{
    /// <summary>Each segment's published name, in order, read without opening it.</summary>
    IReadOnlyList<string> Names { get; }
}
