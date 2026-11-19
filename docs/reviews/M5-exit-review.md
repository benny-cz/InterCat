# M5 exit review — scale, reliability and release

Date: 2026-11-19, at plan revision 1087
Build reviewed: `main` at revision 1086 (`cf227d28`), on Windows 11 26H2 x64, build 10.0.26320.0 (Candidate), the reference machine
Method: each item of M5's exit gate and of its implement list (§14) is checked against evidence already in the
repository - reviews, bench results, fixtures and named tests. An item is met only by evidence that names it; what the
evidence does not reach is said not to be reached, and is not called met.

## The exit gate

| Gate item | Verdict | Evidence |
|---|---|---|
| Correctness suite | Met | `docs/reviews/M5-correctness-suite-review.md`: all sixteen of §13.2's properties covered since revision 1077, each by the tests the review names |
| Declared performance tier | Met: T1, and T2 since revision 1089 | `docs/reviews/M5-declared-performance-tier.md` (revision 1079, and revision 1089's): every §12 budget cold and warm at the median of three runs on the reference machine. T2 at revision 1087 held every warm budget and S1 and S2, but not every cold one; since revision 1089, with InterCat's code compiled ahead of time as the package ships it, it holds every one. T3 holds every warm budget and S1 and S2, and since revision 1090 a group's first timeline cold, but not its first byte lanes, 4 ms over, so it is not declared; T4 is M13's |
| Compatibility matrix | Met for the builds measured; six listed builds owe the corpus | `docs/PER-BUILD-COVERAGE.md`: 10.0.26220.0-x64 (Primary) and 10.0.26320.0-x64 (Candidate) each carry every mechanism's tier from their own runs (revision 1082). `icat bench` names the corpus run on those two and every other build of §1.3's matrix as owing it - 26200, 26100 on x64 and ARM64, 22631, Windows Server 2025 and 2022 - and no build borrows another's tier (P27, `BuildBacklogTests`, `MeasuredCoverageTests`) |
| Crash, disk and loss tests | Met | Every §13.3 case has a test since revision 1078: a start cancelled while enabling, a console close, logoff or shutdown held until the broker finalizes, a declined elevation (revision 1067); loss injected at the source, the consumer and decoding, each said at its own layer (revision 1068); a slow disk and CPU contention through a whole recording (revision 1078); a broker's restart recovery (the R16 tests) |
| Accessible keyboard workflow | Met | A test walks the investigation by keyboard alone and `F6` moves between the window's panes (revision 472); every §6.6 meaning has its redundant channel (R14, revision 1077) |
| Content-export review | Met | `docs/reviews/M5-content-export-review.md` (revision 787): every path by which kept content leaves InterCat, each asked for, consented and saying what it holds, and a test that fails when source reads content without its row |
| Installation, uninstallation and capture cleanup checks | Met on the development machine; the removal asking for captures by its tests alone | `bench/results/installer-checks-20261119T105837Z` (revision 1086, `InstallerChecksTests`, ADR-115's addendum): installed signed with its root carrying exactly the production descriptor, a capture through the installed broker, an update keeping what was recorded, a removal refused (1603) while a capture recorded and that capture finishing undisturbed, and removals leaving no folder, path entry, menu entry, broker process or trace session. The removal asking for the captures was not run there, the machine's root holding its owner's captures; it rests on `BrokerInstallationTests` against real roots and on the package's own tables |
| Scale tiers T1 to T3, S1 to S7 | Met | `bench/results/scale-tiers-*` (revisions 1063 to 1066, `ScaleTierTests`): S1, a T3 reopen at 1.03 times T1's against the 2 allowed; S2, the steady working set alike at every tier; S3, every question the gate asks within its budget warm at every tier and cold at T1; S4, the persisted overview (ADR-054); S5, growth stated before it hurts (revisions 393, 439 and 450); S6, retention keeping every reference (revisions 434 and 442); S7, a preview never ranked, exported or stepped through as exact (P26) |
| A reproducible demo investigation | Met | `icat demo` (revision 395, `contracts/demo-investigation-v1.md`): two hosts, several transports and explicit unsupported rows, generated and saying so; the window opens it (revision 396) |

## The implement list

| Item | State |
|---|---|
| Adaptive indices and tiles | The tile index (`contracts/tile-index-v1.md`) and its postings, from which a focus counts its members and an evidence page reads its rows (ADR-113, ADR-114); the operation index |
| The complete §12.1 overview pyramid | Persisted with each generation and never recomputed at open (`contracts/overview-index-v1.md`, ADR-054) |
| Bounded graph layout | A graph past the display budget is refused rather than partly drawn, a cancelled layout publishes nothing, and a late one never replaces a newer graph (R8 and R7, `GraphLayoutTests`, `GraphProjectionTests`, `GraphLayoutSchedulerTests`) |
| Cache and queue budgets | §26.2's: the segment readers' cache holds its budget while a query reads (revisions 876 to 884); the acquisition queue drops at its bound, counted and published (R8, revision 1078) |
| Rolling retention with pin semantics | A follow keeping a window (ADR-045), an interval release keeping its boundary checkpoint (ADR-043), and a pin holding every release (ADR-046) |
| Accessibility polish | The keyboard workflow, the panes' `F6`, and redundant channels for every meaning (R14, R15) |
| Signed installer and binaries | One per-machine Windows Installer package (ADR-115, `installer/`, revision 1084), InterCat's binaries and the package Authenticode-signed - by a self-signed test certificate, untimestamped. A release certificate, and timestamping, are this item's open gap |
| Update compatibility | An update installs over an earlier version, keeping the root and what it recorded, and is refused rather than ending a capture that records (revisions 1083, 1085 and 1086); a package's client and broker are always one version, and a broker states its own in its Hello (broker-v1) |
| Support diagnostics | `icat support` writes the support bundle (`support-bundle-v1`, revision 391) and the window saves it (revision 392), holding nothing a record holds (P16) |

## Verdict

M5's exit gate is met for its measured scope. What it does not reach is stated where it applies: the package is signed
by a test certificate, so a release still needs a release certificate and timestamping; the fixture corpus has run on
two of the matrix's eight builds, so the other six claim no tier until it runs on them; the removal that also removes
the captures was proven by the broker's tests and the package's tables, not by Windows Installer running it on a
machine set aside for it; and T2 and T3 were measured but not declared, being over budget cold. Each of the first three
needs a machine or a certificate this repository does not have, not a change to it; since revision 1089 T2 is declared,
and since revision 1090 T3 misses only a group's first byte lanes, by 4 ms at the median.

M4's last gate item, a known two-host exchange from separately captured traces, likewise waits for a second host; the
machine the installation checks were meant for would serve both.
