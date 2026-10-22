using System.Reflection;
using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Microsoft.Diagnostics.Tracing.Session;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// A capture's configuration is fixed when it starts (P13, §9.3): its session is configured as it is created, its
/// providers are enabled and their state asked for while it starts, and nothing it runs afterwards enables, disables or
/// re-enables a provider or reconfigures its session. That is why the one coverage epoch a capture's ledger states can
/// speak for the whole capture (`contracts/coverage-v2.md` §2). The calls are found in the IL of everything the broker
/// and the recorders are built from, so a change that reconfigured a running capture would name itself here.
/// </summary>
public sealed class CaptureConfigurationTests
{
    [Fact(DisplayName = "P13: a capture's providers are enabled and its session configured only as it starts, and nothing disables a provider")]
    public void ACaptureIsConfiguredOnlyAsItStarts()
    {
        Assembly windows = typeof(OwnedCaptureSession).Assembly;
        List<(string Caller, MethodBase Target)> calls = [.. CallbackReach.Calls(
            windows,
            typeof(CaptureCoverageTally).Assembly,
            typeof(BrokerConnectionDispatcher).Assembly,
            typeof(BrokerWireFrame).Assembly,
            typeof(LiveSessionRecorder).Assembly,
            typeof(SessionStore).Assembly,
            typeof(CaptureId).Assembly)];
        Type control = windows.GetType("InterCat.Capture.Windows.TraceEventProviderControl", throwOnError: true)!;

        string[] CallersOf(Type type, Func<string, bool> named) =>
        [
            .. calls.Where(call => call.Target.DeclaringType == type && named(call.Target.Name))
                .Select(call => call.Caller)
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];

        // TraceEvent enables a provider in one place and kernel flags in another, and nothing turns either off.
        Assert.Equal(
            ["InterCat.Capture.Windows.TraceEventProviderControl.Enable"],
            CallersOf(typeof(TraceEventSession), name => name == "EnableProvider"));
        Assert.Equal(
            ["InterCat.Capture.Windows.TraceEventSessionHost+TraceEventOwnedSession.EnableKernelFlags"],
            CallersOf(typeof(TraceEventSession), name => name == "EnableKernelProvider"));
        Assert.Empty(CallersOf(typeof(TraceEventSession), name => name.StartsWith("Disable", StringComparison.Ordinal)));

        // A session's settings are set as it is created, or as a dead capture's session is reclaimed to be stopped.
        Assert.Equal(
            [
                "InterCat.Capture.Windows.TraceEventEtlFileSessionHost.CreateExclusive",
                "InterCat.Capture.Windows.TraceEventSessionHost.CreateExclusive",
                "InterCat.Capture.Windows.TraceEventSessionHost.StopPreviouslyOwnedSession",
            ],
            CallersOf(typeof(TraceEventSession), name => name.StartsWith("set_", StringComparison.Ordinal)));

        // Enabling a provider and asking for its state are reached only through an owned session ...
        Assert.Equal(
            [
                "InterCat.Capture.Windows.TraceEventEtlFileSessionHost+TraceEventOwnedEtlFileSession.Enable",
                "InterCat.Capture.Windows.TraceEventSessionHost+TraceEventOwnedSession.Enable",
            ],
            CallersOf(control, name => name == "Enable"));
        Assert.Equal(
            ["InterCat.Capture.Windows.TraceEventProviderControl.TryRequestCaptureState"],
            CallersOf(typeof(TraceEventSession), name => name == "CaptureState"));

        // ... and an owned session does either only while its capture starts.
        Assert.Equal(
            ["InterCat.Capture.Windows.OwnedCaptureSession.StartAsync"],
            CallersOf(typeof(IOwnedEtwSession), name => name is "Enable" or "EnableKernelFlags" or "TryRequestCaptureState"));
        Assert.Equal(
            ["InterCat.Capture.Windows.OwnedEtlFileCapture.StartAsync"],
            CallersOf(typeof(IOwnedEtlFileSession), name => name is "Enable" or "TryRequestCaptureState"));
    }
}
