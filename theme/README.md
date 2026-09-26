# Theme verification

Theme version 1.1.0, generated 2026-09-26.

- Ink against every surface token: at least 4.5 to 1
- Fill against its plot ground: at least 3.0 to 1
- Adjacent families in normal vision: at least 18 CIE76
- Adjacent families under each simulated deficiency: at least 10 CIE76
- Adjacent families in greyscale: at least 6 CIELAB lightness
- Caution ink against every family's fill and ink in normal vision: at least 18 CIE76
- Action ink on the action fill in each state: at least 4.5 to 1; the fill against every surface: at least 3.0 to 1
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
| Caution from every family, Normal | 45.5 (Caution to RemoteCall fill) | 18 | met |
