using System.Globalization;
using InterCat.Application;

namespace InterCat.Desktop.Presentation;

/// <summary>A bounded search hit in the left rail; endpoint values are intentionally absent from its snippet.</summary>
public sealed record SearchRow(SearchHit Hit) : IAccessibleRow
{
    public static SearchRow Of(SearchHit hit) => new(hit);

    public string Kind => Hit.Kind switch
    {
        SearchHitKind.Group => "GROUP",
        SearchHitKind.Process => "PROCESS",
        SearchHitKind.Channel => "CHANNEL",
        SearchHitKind.Moment => "TIME",
        _ => "RESULT",
    };

    public string Label => Hit.Label;

    public string Detail => Hit.Detail;

    /// <summary>
    /// The records the hit holds, in the right number; nothing for a moment, which is a place to go rather than a match.
    /// </summary>
    public string Observations => Hit.Kind == SearchHitKind.Moment ? string.Empty : Spoken.Count(Hit.ObservationCount, "record");

    /// <summary>
    /// The hit as it reads aloud: its kind as a word rather than the eyebrow's capitals, which some voices spell out,
    /// and its record count in the right number - or, for a moment, where Enter goes.
    /// </summary>
    public string AccessibleName => Hit.Kind == SearchHitKind.Moment
        ? $"{SpokenKind}, {Label}, {Detail}. Press Enter to go there."
        : $"{SpokenKind}, {Label}, {Detail}, {Observations}. Press Enter to open.";

    private string SpokenKind => Hit.Kind switch
    {
        SearchHitKind.Group => "Group",
        SearchHitKind.Process => "Process",
        SearchHitKind.Channel => "Channel",
        SearchHitKind.Moment => "Time",
        _ => "Result",
    };
}
