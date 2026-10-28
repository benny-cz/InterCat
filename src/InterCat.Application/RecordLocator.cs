using System.Globalization;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The observation segment a record's locator names (§3.7), as a person copies it from an evidence page: a segment the
/// generation does not name, or a row the segment does not hold, is a mistake in the locator and is refused as one, never
/// read as damage to the session.
/// </summary>
internal static class RecordLocator
{
    /// <summary>The segment <paramref name="segmentName"/> names in <paramref name="manifest"/>, holding <paramref name="segmentRow"/>.</summary>
    public static SegmentReaderV1 Segment(SessionStore store, SessionManifestV1 manifest, string segmentName, int segmentRow)
    {
        if (!SessionSegments.Names(manifest).Contains(segmentName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(string.Create(CultureInfo.CurrentCulture,
                $"Generation {manifest.Generation:N0} names no observation segment '{segmentName}'. Take the record's locator from icat evidence."),
                nameof(segmentName));
        }

        SegmentReaderV1 segment = SessionSegments.Open(store, manifest, segmentName);
        if (segmentRow >= segment.RowCount)
        {
            throw new ArgumentException(string.Create(CultureInfo.CurrentCulture,
                $"Segment '{segmentName}' holds rows 0 to {segment.RowCount - 1:N0}; row {segmentRow:N0} is not one of them."),
                nameof(segmentRow));
        }

        return segment;
    }
}
