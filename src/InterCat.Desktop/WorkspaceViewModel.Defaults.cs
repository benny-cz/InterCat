using System.Globalization;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// §6.8's defaults: every first-run default is correct without a choice, and a setting a person changed is shown as
/// changed, with one command that restores them all. The settings are those that change what a number counts or how it
/// reads - the ranking and whether per second, the lanes' scale, the grouping and the evidence policy - in the words an
/// investigation uses when it keeps them (R5).
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>
    /// The settings that differ from their first-run defaults, each as an investigation that keeps it says it: "its rows
    /// ranked by bytes sent per second", "its processes grouped by terminal session". Per second is named only for a
    /// ranking with a rate, since no other reads it. None in the tour, which no person set.
    /// </summary>
    public IReadOnlyList<string> ChangedSettings => !realOverview
        ? []
        : WorkspaceLayout.Parts(0, 0, Grouping, rankBy, perSecond && RankingMetrics.IsAdditive(rankBy), EvidencePolicy,
            scalesEachLane, CultureInfo.CurrentCulture, CollectorsSetAside);

    /// <summary>Whether any setting differs from its default, so the rail names it and offers the default view back.</summary>
    public bool DiffersFromDefaults => ChangedSettings.Count > 0;

    /// <summary>
    /// The rail's line while a setting differs from its default, short enough to sit beside the command that restores
    /// them, since the rail's height is its ranked rows': "2 settings changed". Each setting's own control shows its value,
    /// and <see cref="ChangedSettingsText"/> names them all.
    /// </summary>
    public string ChangedSettingsSummary => DiffersFromDefaults
        ? CountText.Of(ChangedSettings.Count, "setting") + " changed"
        : string.Empty;

    /// <summary>
    /// Every setting that differs from its default, for the line's tooltip and a screen reader: "Not the default view: its
    /// rows ranked by bytes sent per second and its records counted with candidates."
    /// </summary>
    public string ChangedSettingsText => DiffersFromDefaults
        ? "Not the default view: " + WorkspaceLayout.Series(ChangedSettings) + "."
        : string.Empty;

    /// <summary>Says the settings changed, after the ranking, its rate or the lanes' scale did.</summary>
    private void RaiseSettingsChanged()
    {
        OnPropertyChanged(nameof(ChangedSettings));
        OnPropertyChanged(nameof(DiffersFromDefaults));
        OnPropertyChanged(nameof(ChangedSettingsSummary));
        OnPropertyChanged(nameof(ChangedSettingsText));
    }
}
