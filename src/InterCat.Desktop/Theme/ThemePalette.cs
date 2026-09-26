using InterCat.Domain;

namespace InterCat.Desktop.Theme;

/// <summary>A theme mode. Both modes carry the full token set; neither is derived from the other.</summary>
public enum ThemeMode
{
    Dark = 1,
    Light = 2,
}

/// <summary>
/// One mechanism family's hue role (§6.6). Families exist so that a mechanism enum value can never invent
/// a new hue: every mechanism maps to exactly one family, and unknown maps to the reserved grey.
/// </summary>
public enum MechanismFamily
{
    Tcp = 1,
    Udp = 2,
    Pipe = 3,
    RemoteCall = 4,
    Alpc = 5,
    SharedSection = 6,
    OtherSocket = 7,
    LegacyIpc = 8,
    UnknownMechanism = 9,
}

/// <summary>
/// The fill and ink variants of one family in one mode. Fills and ink are separate questions: a hue that
/// reads as an area can fail as text, so each is authored and measured on its own (§6.6).
/// </summary>
public sealed record FamilyTokens(MechanismFamily Family, string Label, string Glyph, Srgb Fill, Srgb Ink);

/// <summary>Surface tokens ink can land on, and the accent used for selection and focus.</summary>
public sealed record SurfaceTokens(Srgb Canvas, Srgb Panel, Srgb Plot, Srgb Elevated, Srgb Accent, Srgb Ink, Srgb MutedInk)
{
    public IReadOnlyList<Srgb> All => [Canvas, Panel, Plot, Elevated];
}

/// <summary>
/// Tokens that state a condition or an action rather than a mechanism (§6.6). The caution ink strokes the coverage
/// hatch and words a warning; the action fill, in its resting, pointer-over and pressed states, carries the primary
/// action with the action ink on it. None may be a family's hue: a warning must never read as RPC, nor the primary
/// action as TCP, so each is measured like any other token.
/// </summary>
public sealed record StatusTokens(Srgb Caution, Srgb ActionFill, Srgb ActionFillHover, Srgb ActionFillPressed, Srgb ActionInk)
{
    /// <summary>Every state the action fill can be drawn in, so the ink on it is measured on each.</summary>
    public IReadOnlyList<(string Name, Srgb Fill)> ActionStates =>
        [("action fill", ActionFill), ("action fill hover", ActionFillHover), ("action fill pressed", ActionFillPressed)];
}

/// <summary>
/// The InterCat theme. Values are the contract: the thresholds below are enforced by tests, and the
/// measured results are written beside the definition so a palette change that breaks one fails a build
/// rather than a review (§6.6, §21.2 item 9).
/// </summary>
public static class ThemePalette
{
    /// <summary>Bumps when tokens, thresholds or measured results change (§24 themeVersion).</summary>
    public const string ThemeVersion = "1.1.0";

    /// <summary>Ink must clear this against every surface token it can land on.</summary>
    public const double MinimumInkContrast = 4.5;

    /// <summary>A fill must clear this against the plot ground it is drawn on.</summary>
    public const double MinimumFillContrast = 3.0;

    /// <summary>Adjacent families in palette order must clear this CIE76 distance in normal vision.</summary>
    public const double MinimumAdjacentDistance = 18.0;

    /// <summary>The same neighbours must still clear this under each simulated colour-vision deficiency.</summary>
    public const double MinimumSimulatedDistance = 10.0;

    /// <summary>And this much CIELAB lightness difference, which is what survives greyscale.</summary>
    public const double MinimumGreyscaleLightness = 6.0;

    /// <summary>
    /// The caution ink keeps this CIE76 distance from every family's fill and ink in normal vision, so a hatch or a
    /// warning is never read as a mechanism. Under a simulated deficiency the words and the hatch pattern carry it (R14).
    /// </summary>
    public const double MinimumStatusDistance = 18.0;

    /// <summary>Palette order. Adjacency is a contract because only neighbours are separation-tested.</summary>
    public static IReadOnlyList<MechanismFamily> Order { get; } =
    [
        MechanismFamily.Tcp,
        MechanismFamily.Udp,
        MechanismFamily.Pipe,
        MechanismFamily.RemoteCall,
        MechanismFamily.Alpc,
        MechanismFamily.SharedSection,
        MechanismFamily.OtherSocket,
        MechanismFamily.LegacyIpc,
        MechanismFamily.UnknownMechanism,
    ];

    public static SurfaceTokens Surfaces(ThemeMode mode) => mode == ThemeMode.Dark
        ? new(
            Canvas: Srgb.Parse("#0A1421"),
            Panel: Srgb.Parse("#0D1927"),
            Plot: Srgb.Parse("#0E1A29"),
            Elevated: Srgb.Parse("#152B3D"),
            Accent: Srgb.Parse("#7FC0FF"),
            Ink: Srgb.Parse("#E8F0F8"),
            MutedInk: Srgb.Parse("#9FB3C6"))
        : new(
            Canvas: Srgb.Parse("#FFFFFF"),
            Panel: Srgb.Parse("#F5F7FA"),
            Plot: Srgb.Parse("#FBFCFD"),
            Elevated: Srgb.Parse("#E8EDF3"),
            Accent: Srgb.Parse("#14549B"),
            Ink: Srgb.Parse("#101922"),
            MutedInk: Srgb.Parse("#455565"));

    /// <summary>
    /// The status tokens of one mode. The action fill shares the accent's value, the application's own voice, and the
    /// action ink is the canvas it stands out from; the caution ink is a coral or vermilion no family uses.
    /// </summary>
    public static StatusTokens Status(ThemeMode mode) => mode == ThemeMode.Dark
        ? new(
            Caution: Srgb.Parse("#FF7A5C"),
            ActionFill: Srgb.Parse("#7FC0FF"),
            ActionFillHover: Srgb.Parse("#A3D3FF"),
            ActionFillPressed: Srgb.Parse("#5FAEF7"),
            ActionInk: Srgb.Parse("#0A1421"))
        : new(
            Caution: Srgb.Parse("#B42318"),
            ActionFill: Srgb.Parse("#14549B"),
            ActionFillHover: Srgb.Parse("#1D64B3"),
            ActionFillPressed: Srgb.Parse("#0E4078"),
            ActionInk: Srgb.Parse("#FFFFFF"));

    public static IReadOnlyList<FamilyTokens> Families(ThemeMode mode) =>
        mode == ThemeMode.Dark ? DarkFamilies : LightFamilies;

    public static FamilyTokens TokensFor(ThemeMode mode, MechanismFamily family)
    {
        foreach (FamilyTokens tokens in Families(mode))
        {
            if (tokens.Family == family)
            {
                return tokens;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(family));
    }

    /// <summary>
    /// Maps a mechanism to its hue family. Anything without a validated family reaches the reserved
    /// unknown grey, which no supported mechanism may reuse (§6.6).
    /// </summary>
    public static MechanismFamily FamilyOf(Mechanism mechanism) => mechanism switch
    {
        Mechanism.Tcp => MechanismFamily.Tcp,
        Mechanism.Udp => MechanismFamily.Udp,
        Mechanism.NamedPipe or Mechanism.AnonymousPipe => MechanismFamily.Pipe,
        Mechanism.Rpc or Mechanism.ComActivation => MechanismFamily.RemoteCall,
        Mechanism.Alpc => MechanismFamily.Alpc,
        Mechanism.SharedSection => MechanismFamily.SharedSection,
        Mechanism.UnixDomainSocket or Mechanism.Quic or Mechanism.RemoteFileOrSmb => MechanismFamily.OtherSocket,
        Mechanism.Synchronization or Mechanism.WindowMessage or Mechanism.Clipboard
            or Mechanism.Mailslot or Mechanism.Dde => MechanismFamily.LegacyIpc,
        _ => MechanismFamily.UnknownMechanism,
    };

    private static readonly IReadOnlyList<FamilyTokens> DarkFamilies =
    [
        new(MechanismFamily.Tcp, "TCP", "●", Srgb.Parse("#0BA2F7"), Srgb.Parse("#A7D0FF")),
        new(MechanismFamily.Udp, "UDP", "◆", Srgb.Parse("#017775"), Srgb.Parse("#01B9B5")),
        new(MechanismFamily.Pipe, "Pipes", "■", Srgb.Parse("#A083E3"), Srgb.Parse("#D7C2FF")),
        new(MechanismFamily.RemoteCall, "RPC and COM", "▲", Srgb.Parse("#9C6B00"), Srgb.Parse("#D9A85F")),
        new(MechanismFamily.Alpc, "ALPC", "◗", Srgb.Parse("#4EAA6C"), Srgb.Parse("#8EE4A6")),
        new(MechanismFamily.SharedSection, "Shared sections", "★", Srgb.Parse("#C54599"), Srgb.Parse("#F698D0")),
        new(MechanismFamily.OtherSocket, "Other sockets", "◎", Srgb.Parse("#6F8AE2"), Srgb.Parse("#B5C0FF")),
        new(MechanismFamily.LegacyIpc, "Legacy IPC", "◇", Srgb.Parse("#427389"), Srgb.Parse("#7CACC4")),
        new(MechanismFamily.UnknownMechanism, "Unknown", "?", Srgb.Parse("#868686"), Srgb.Parse("#ABABAB")),
    ];

    private static readonly IReadOnlyList<FamilyTokens> LightFamilies =
    [
        new(MechanismFamily.Tcp, "TCP", "●", Srgb.Parse("#0182C8"), Srgb.Parse("#026399")),
        new(MechanismFamily.Udp, "UDP", "◆", Srgb.Parse("#026A67"), Srgb.Parse("#00504E")),
        new(MechanismFamily.Pipe, "Pipes", "■", Srgb.Parse("#8D71CF"), Srgb.Parse("#6C53AE")),
        new(MechanismFamily.RemoteCall, "RPC and COM", "▲", Srgb.Parse("#865B00"), Srgb.Parse("#644300")),
        new(MechanismFamily.Alpc, "ALPC", "◗", Srgb.Parse("#3A975B"), Srgb.Parse("#0A753C")),
        new(MechanismFamily.SharedSection, "Shared sections", "★", Srgb.Parse("#AA2A81"), Srgb.Parse("#8A1A67")),
        new(MechanismFamily.OtherSocket, "Other sockets", "◎", Srgb.Parse("#5573C9"), Srgb.Parse("#3259AA")),
        new(MechanismFamily.LegacyIpc, "Legacy IPC", "◇", Srgb.Parse("#2A5D72"), Srgb.Parse("#0D485C")),
        new(MechanismFamily.UnknownMechanism, "Unknown", "?", Srgb.Parse("#404040"), Srgb.Parse("#393939")),
    ];
}
