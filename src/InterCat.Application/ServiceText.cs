using System.Globalization;
using InterCat.Analysis;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>How a person reads which services a process hosted: one set of words for the window and the command line (R5).</summary>
public static class ServiceText
{
    /// <summary>At most this many services are named; the rest are counted.</summary>
    public const int Named = 6;

    /// <summary>
    /// The services a process hosted, as its inspector and <c>icat processes</c> say them: named, the rest counted, with the
    /// moments the capture read them at; none where it hosted none of those read; or that the capture kept no list.
    /// </summary>
    public static string Of(IReadOnlyList<HostedService>? services, IReadOnlyList<ServiceInventoryMoment> moments)
    {
        ArgumentNullException.ThrowIfNull(moments);
        if (services is null)
        {
            return "Not recorded: this capture kept no list of the services running";
        }

        string read = Moments(moments);
        if (services.Count == 0)
        {
            return "None " + read;
        }

        string names = string.Join(", ", services.Take(Named).Select(Named_));
        return names
            + (services.Count > Named ? string.Create(CultureInfo.CurrentCulture, $" and {services.Count - Named:N0} more") : string.Empty)
            + " · " + read;

        // A service listed at one end of a capture that read both is said to be there alone: it started or stopped between.
        string Named_(HostedService service) => moments.Count < 2 || (service.AtStart && service.AtStop) ? service.Name
            : service.AtStart ? service.Name + " (at the start alone)" : service.Name + " (at the stop alone)";
    }

    // The moments a capture read its services at, as the end of a sentence.
    private static string Moments(IReadOnlyList<ServiceInventoryMoment> moments) => moments switch
    {
        [ServiceInventoryMoment.Start] => "as the capture started; its stop's list was not read",
        [ServiceInventoryMoment.Stop] => "as the capture stopped; its start's list was not read",
        _ => "as the capture started and stopped",
    };
}
