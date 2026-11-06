using System.ComponentModel;
using InterCat.Capture.Windows;
using InterCat.Storage;

namespace InterCat.Capture.Recording;

/// <summary>
/// Where a live capture reads which services are running and which process hosts each, as it starts and as it stops
/// (`contracts/service-inventory-v1.md`, ADR-095): the service control manager's own list on this machine, or a scripted
/// one in a test.
/// </summary>
public sealed class ServiceInventorySource
{
    /// <summary>The running services and their hosts, as one read lists them; a list that cannot be read throws.</summary>
    public required Func<IReadOnlyList<RunningService>> Read { get; init; }

    /// <summary>This machine's service control manager.</summary>
    public static ServiceInventorySource Local { get; } = new() { Read = RunningServices.Read };

    /// <summary>
    /// The inventory of <paramref name="moment"/>: the list read now, bracketed by when its read began and when it ended
    /// (§18.5). Null where the list could not be read or held - never an empty list in its place, which would say no
    /// service ran (R3) - so the capture records none, and goes on.
    /// </summary>
    public ServiceInventoryV1? Take(ServiceInventoryMoment moment, Guid captureId)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            IReadOnlyList<RunningService> services = Read();
            var inventory = new ServiceInventoryV1
            {
                Contract = ServiceInventoryV1.ContractName,
                CaptureId = captureId,
                Moment = moment,
                ReadStartedUtc = started,
                ReadEndedUtc = DateTimeOffset.UtcNow,
                Services = [.. services.Select(static service => new InventoriedServiceV1
                {
                    Name = service.Name,
                    ProcessId = service.ProcessId,
                    HostCreatedUtc = service.HostCreatedUtc,
                })],
            };
            inventory.Validate();
            return inventory;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or InvalidDataException
            or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
