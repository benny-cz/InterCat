# Theme verification

Theme version 1.2.0, generated 2026-09-26.

- Ink against every surface token: at least 4.5 to 1
- Fill against its plot ground: at least 3.0 to 1
- Adjacent families in normal vision: at least 18 CIE76
- Adjacent families under each simulated deficiency: at least 10 CIE76
- Adjacent families in greyscale: at least 6 CIELAB lightness
- Any two families in normal vision, neighbours or not: at least 15 CIE76
- Caution ink against every family's fill and ink in normal vision: at least 18 CIE76
- Action ink on the action fill in each state: at least 4.5 to 1; the fill against every surface: at least 3.0 to 1
- In a high-contrast mode every ink, the action ink included, clears 7.0 to 1, every fill 4.5 to 1, and the divider 3.0 to 1 against every surface
- Hatches and warning patterns are reserved for coverage and quality; no family may use one.
- The unknown grey is never reused for a supported mechanism.

## Dark

| Check | Worst measured | Required | Result |
|---|---|---|---|
| Ink contrast | 5.67 to 1 (caution on elevated) | 4.5 to 1 | met |
| Fill contrast | 3.25 to 1 (Udp fill on plot) | 3.0 to 1 | met |
| Separation, Normal | 22.3 (LegacyIpc to UnknownMechanism) | 18 | met |
| Separation, Protanopia | 17.4 (LegacyIpc to UnknownMechanism) | 10 | met |
| Separation, Deuteranopia | 23.0 (LegacyIpc to UnknownMechanism) | 10 | met |
| Separation, Tritanopia | 12.9 (Tcp to Udp) | 10 | met |
| Separation, Greyscale | 9.0 (SharedSection to OtherSocket) | 6 | met |
| Separation, AnyPair | 17.1 (Pipe to OtherSocket) | 15 | met |
| Caution from every family, Normal | 39.3 (Caution to RemoteCall ink) | 18 | met |

## Light

| Check | Worst measured | Required | Result |
|---|---|---|---|
| Ink contrast | 4.92 to 1 (Alpc ink on elevated) | 4.5 to 1 | met |
| Fill contrast | 3.55 to 1 (Alpc fill on plot) | 3.0 to 1 | met |
| Separation, Normal | 22.2 (LegacyIpc to UnknownMechanism) | 18 | met |
| Separation, Protanopia | 19.2 (LegacyIpc to UnknownMechanism) | 10 | met |
| Separation, Deuteranopia | 22.4 (LegacyIpc to UnknownMechanism) | 10 | met |
| Separation, Tritanopia | 19.3 (OtherSocket to LegacyIpc) | 10 | met |
| Separation, Greyscale | 9.3 (SharedSection to OtherSocket) | 6 | met |
| Separation, AnyPair | 17.3 (Pipe to OtherSocket) | 15 | met |
| Caution from every family, Normal | 45.5 (Caution to RemoteCall fill) | 18 | met |

## HighContrastDark

| Check | Worst measured | Required | Result |
|---|---|---|---|
| Ink contrast | 7.35 to 1 (OtherSocket ink on elevated) | 7.0 to 1 | met |
| Fill contrast | 6.20 to 1 (OtherSocket fill on plot) | 4.5 to 1 | met |
| Separation, Normal | 26.1 (LegacyIpc to UnknownMechanism) | 18 | met |
| Separation, Protanopia | 18.1 (SharedSection to OtherSocket) | 10 | met |
| Separation, Deuteranopia | 26.2 (LegacyIpc to UnknownMechanism) | 10 | met |
| Separation, Tritanopia | 25.6 (OtherSocket to LegacyIpc) | 10 | met |
| Separation, Greyscale | 14.0 (SharedSection to OtherSocket) | 6 | met |
| Separation, AnyPair | 26.2 (Pipe to OtherSocket) | 15 | met |
| Caution from every family, Normal | 46.2 (Caution to RemoteCall fill) | 18 | met |

## HighContrastLight

| Check | Worst measured | Required | Result |
|---|---|---|---|
| Ink contrast | 7.36 to 1 (RemoteCall ink on elevated) | 7.0 to 1 | met |
| Fill contrast | 5.05 to 1 (RemoteCall fill on plot) | 4.5 to 1 | met |
| Separation, Normal | 20.8 (LegacyIpc to UnknownMechanism) | 18 | met |
| Separation, Protanopia | 12.8 (RemoteCall to Alpc) | 10 | met |
| Separation, Deuteranopia | 19.2 (LegacyIpc to UnknownMechanism) | 10 | met |
| Separation, Tritanopia | 13.0 (Udp to Pipe) | 10 | met |
| Separation, Greyscale | 8.6 (Pipe to RemoteCall) | 6 | met |
| Separation, AnyPair | 21.0 (Udp to LegacyIpc) | 15 | met |
| Caution from every family, Normal | 42.5 (Caution to RemoteCall ink) | 18 | met |
