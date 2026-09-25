using System.Globalization;
using InterCat.Application;

namespace InterCat.Desktop.Presentation;

/// <summary>A bounded search hit in the left rail; endpoint values are intentionally absent from its snippet.</summary>
public sealed record SearchRow(SearchHit Hit)
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

    public string AccessibleName => $"{Kind}, {Label}, {Detail}, {Observations}. Press Enter to open.";
}
