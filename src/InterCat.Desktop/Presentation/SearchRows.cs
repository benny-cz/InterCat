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
        _ => "RESULT",
    };

    public string Label => Hit.Label;

    public string Detail => Hit.Detail;

    public string Observations => string.Create(CultureInfo.CurrentCulture, $"{Hit.ObservationCount:N0} records");

    /// <summary>
    /// The hit as it reads aloud: its kind as a word rather than the eyebrow's capitals, which some voices spell out,
    /// and its record count in the right number.
    /// </summary>
    public string AccessibleName =>
        $"{SpokenKind}, {Label}, {Detail}, {Spoken.Count(Hit.ObservationCount, "record")}. Press Enter to open.";

    private string SpokenKind => Hit.Kind switch
    {
        SearchHitKind.Group => "Group",
        SearchHitKind.Process => "Process",
        SearchHitKind.Channel => "Channel",
        _ => "Result",
    };
}
