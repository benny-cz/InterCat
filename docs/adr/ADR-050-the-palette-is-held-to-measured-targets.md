# ADR-050: The palette is held to measured targets - WCAG contrast for ink and fills, CIE76 separation in normal and simulated vision - stored with the theme and held by tests

- Status: accepted; revision 722. It records the targets the theme has carried since its first palette - the adjacent
  separations - and since revision 139 (the caution and action tokens), revision 140 (high contrast and the any-pair
  distance) and revision 167 (the lifecycle family), and holds them in tests
- Date: 2026-10-22
- Relates to: §6.6, §6.8, §13.6, §21.2 item 11, R14, R20, P24, `theme/`, `src/InterCat.Desktop/Theme/`

## Context

Four meanings compete for one canvas (§6.6): which mechanism, how much, how trustworthy, and whether a period was
observed at all. Hue carries only the first. Intensity and size carry the amount, border treatment the evidence
quality, a hatch the coverage defect, and words all of them. A family's hue must therefore stay apart from every other
family's in every theme mode, for people who see colour differently, and a family's name must be legible as text in
the ranked table, the legend and the inspector.

§6.6 asked for each requirement to be enforced by a test rather than by judgement, and `theme/` has measured them since
the first palette. No record said why each target is the number it is, and nothing held the numbers themselves:
lowering a target and regenerating the report passed every test. Nor was the committed definition held to the code
except through its measured ratios, so a glyph, a label, a target or a summary row could change in the code while
`theme/` kept the old one.

## Decision

1. **Hue is a mechanism family's, and nothing else's.** Eleven families in a fixed order - TCP, UDP, pipes, RPC,
   lifecycle, ALPC, shared sections, other sockets, legacy IPC, application, unknown - each with a glyph and a label
   that are the same in every mode, so a family is named without its colour (R14). Every mechanism resolves to exactly
   one family, and every mechanism a capture source can collect to one other than unknown; the unknown grey is never a
   supported mechanism's (P24). A condition or an action - the coverage hatch, a warning's words, the live dot, the
   primary action - draws in tokens of its own, never in a family's hue. A family's tokens carry no pattern, so no
   mechanism can wear the hatch that coverage and quality own.
2. **Each family has a fill and an ink in each mode.** A fill is an area on the plot ground; an ink is text or a stroke
   on any surface. A hue that reads well as an area can fail as words, so each is authored and measured on its own.
   Four modes - dark, light, and a high-contrast form of each - carry the full token set, none derived from another.
3. **Contrast targets are WCAG 2.x's, by its relative luminance.**
   - Ink clears 4.5:1 against every surface it can land on - canvas, panel, plot and elevated - which is success
     criterion 1.4.3's minimum for text, since every family's name is read as text somewhere. Body ink, muted ink, the
     accent and the caution ink are held to it as well.
   - A fill clears 3:1 against the plot ground, 1.4.11's minimum for a graphical object a reader must perceive.
   - The primary action's ink clears 4.5:1 on its fill at rest, under the pointer and pressed; that fill clears 3:1
     against every surface, and is no family's fill or ink.
   - In a high-contrast mode every ink, the action's included, clears 7:1 (1.4.6's enhanced ratio), every fill 4.5:1,
     and the divider 3:1 against every surface, because there the divider edges panes, cards and controls. In dark and
     light the divider is the elevated tone, a quiet seam not meant to be read, and is not measured.
4. **Separation targets are CIE76 distances** - the Euclidean distance in CIELAB, from sRGB under a D65 white.
   - Adjacent families in palette order: 18 in normal vision; 10 under protanopia, deuteranopia and tritanopia, each
     simulated by Viénot, Brettel and Mollon's dichromat projection in LMS space; and 6 units of CIELAB lightness after a
     greyscale that keeps luminance.
   - Any two families: 15 in normal vision, since the legend and a busy canvas set every family beside every other,
     whatever their order.
   - The caution ink: 18 from every family's fill and ink in normal vision, so a hatch or a warning is never read as a
     mechanism. Under a simulated deficiency the hatch's pattern and the warning's words carry it.
5. **Motion is two durations** (§6.8): a selection's ring settles in 120 ms, and a level change or a layout settling
   moves in 200 ms. Nothing animates per message, and reduced motion draws each change where it ends, at once.
6. **The definition is code, and `theme/` is what it writes.** `ThemePalette` is the one definition.
   `tools/InterCat.ThemeReport`, run as an explicit command (§13.6), writes `theme/tokens.json`;
   `theme/contrast-report.json`, every measurement with its target and whether it met it; and `theme/README.md`, which
   shows for each check the measurement nearest its own target. Tests measure every target in every mode, hold the
   three committed files to exactly what the tool writes, and hold the targets to the values above. The theme version
   changes whenever a token, a target or a measurement does.

## Why these numbers

- The contrast targets are the accessibility standard's own rather than values tuned to this palette, so a reviewer can
  check them against WCAG without reading this repository. A high-contrast mode exists to give more than the minimum, so
  it takes the enhanced ratio for text, and holds its fills to the ratio ordinary text needs for the same reason.
- CIE76 rather than CIEDE2000: CIE76 is a plain distance anyone can recompute in a few lines from published formulas,
  symmetric and a metric, which keeps the report reproducible by any tool. CIEDE2000 refines differences near the
  threshold of perception, about 2.3 in CIE76, and every target here is several times that. The cost is that CIE76
  overstates differences between saturated colours, blues above all, so there a target is looser than it reads; the
  glyph and the label are the redundancy that makes that acceptable.
- 18 for neighbours in normal vision is about eight just-noticeable differences, so two adjacent lanes, or a node and
  its neighbour, are told apart at a glance at a few pixels' size.
- 15 for any pair is what eleven hues keep within the lightness band the contrast targets leave them, with a margin:
  dark mode's closest pair, pipes to other sockets, is 17.1 apart, light mode's the same pair at 17.3, and the
  high-contrast forms' 21.4 and 18.4. Every pair is held, not only neighbours, because the legend sets them all side by
  side.
- 10 under a simulated dichromacy: a dichromat loses one of three colour axes, so eleven families cannot keep their
  normal-vision distances. Neighbours, the pairs most often side by side, stay apart, and the glyph and label carry
  identity the rest of the way (R14). Complete dichromacy bounds the anomalous trichromacies, which see more.
- 6 lightness units in greyscale: a greyscale print keeps only lightness, and eleven fills that each clear their
  contrast target cannot span enough lightness to hold every pair apart. Holding neighbours keeps adjacent lanes apart;
  the glyph carries the rest.
- 18 for the caution ink is the distance two neighbours keep: a warning is as unlike any family as one family is unlike
  the next. The caution ink measures at least 39 from every family's fill and ink.

## Consequences

- A palette change is made in `ThemePalette`, the files are regenerated with the tool, and the diff of `theme/` is
  reviewed as a change to the contract. A change that misses a target fails the tool's exit code and the tests; a change
  to a target fails until this ADR is revised.
- Some margins are small - light mode's greyscale step from RPC to lifecycle is 6.9 against 6, and dark mode's
  tritanopia distance from TCP to UDP 12.9 against 10 - so a new family rarely fits without moving others. It is placed
  by search over the candidates, as lifecycle was in revision 167.
- The accent and the action fill are held to contrast, not to separation: selection is carried by the outline's shape
  and the stated selection, and an action by its place and its words. The accent sits 12 from TCP's ink in dark mode and
  7.8 from other sockets' ink in light.
- The targets do not settle whether a reader with a colour-vision difference finds the canvas usable - no study has
  been run - nor what a screen reader announces, which the M0 interaction review leaves open. The tritanopia simulation
  is a single-plane projection, an approximation where Brettel's two half-planes are more exact.

## Alternatives considered

- **APCA, the contrast method drafted for WCAG 3.** Not adopted while it is a draft: a reviewer cannot check it against a
  published standard, and its targets depend on font size and weight, which the window varies.
- **CIEDE2000.** Above.
- **Holding every pair, and every simulated pair, to the neighbours' targets.** Eleven fills that each clear their
  contrast target cannot all keep them; palette order decides which pairs are neighbours, and the glyph carries the
  rest.
- **One palette for every mode.** A hue that clears 3:1 on a dark ground fails on a light one, and an ink that clears
  4.5:1 at one lightness fails at the other.
- **Tokens authored in JSON and read by the code.** The window and the tests use the tokens as typed values; a JSON
  source would need a loader whose failures become runtime ones. The JSON is written from the code instead, and held to
  it.
