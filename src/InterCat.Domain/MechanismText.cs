namespace InterCat.Domain;

/// <summary>
/// A mechanism as InterCat names it to a person, one mapping for every layer (R5): in a lane, a row or a column, and
/// within a sentence, where a lifecycle is the process's or the thread's rather than the lane's one word.
/// </summary>
public static class MechanismText
{
    /// <summary>A mechanism as a lane or a row names it: "TCP", "Process", "Named pipe".</summary>
    public static string Name(Mechanism mechanism) => mechanism switch
    {
        Mechanism.ProcessLifecycle => "Process",
        Mechanism.ThreadLifecycle => "Thread",
        Mechanism.Tcp => "TCP",
        Mechanism.Udp => "UDP",
        Mechanism.UnixDomainSocket => "Unix socket",
        Mechanism.NamedPipe => "Named pipe",
        Mechanism.AnonymousPipe => "Anonymous pipe",
        Mechanism.Rpc => "RPC",
        Mechanism.Alpc => "ALPC",
        Mechanism.SharedSection => "Shared section",
        Mechanism.ComActivation => "COM activation",
        Mechanism.WindowMessage => "Window message",
        Mechanism.RemoteFileOrSmb => "Remote file",
        Mechanism.Quic => "QUIC",
        Mechanism.Dde => "DDE",
        Mechanism.ApplicationSdk => "Application SDK",
        Mechanism.Http => "HTTP",
        Mechanism.UnknownMechanism => "Unknown mechanism",
        _ => mechanism.ToString(),
    };

    /// <summary>
    /// A mechanism as a sentence names it: "TCP", "named pipe", and "process lifecycle" where a lane says "Process". A
    /// word keeps its capital only where it is a name or an abbreviation: "Unix socket", "COM activation".
    /// </summary>
    public static string InSentence(Mechanism mechanism) => mechanism switch
    {
        Mechanism.ProcessLifecycle => "process lifecycle",
        Mechanism.ThreadLifecycle => "thread lifecycle",
        Mechanism.NamedPipe => "named pipe",
        Mechanism.AnonymousPipe => "anonymous pipe",
        Mechanism.SharedSection => "shared section",
        Mechanism.Synchronization => "synchronization",
        Mechanism.WindowMessage => "window message",
        Mechanism.Clipboard => "clipboard",
        Mechanism.Mailslot => "mailslot",
        Mechanism.RemoteFileOrSmb => "remote file",
        Mechanism.ApplicationSdk => "application SDK",
        Mechanism.Instrumented => "instrumented",
        Mechanism.UnknownMechanism => "unknown mechanism",
        _ => Name(mechanism),
    };
}
