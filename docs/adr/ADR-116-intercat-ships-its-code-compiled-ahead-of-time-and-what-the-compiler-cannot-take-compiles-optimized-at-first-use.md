# ADR-116: InterCat ships its code compiled ahead of time, and what the compiler cannot take compiles optimized at first use

- Status: accepted for M5's declared tier since revision 1089: the package's clients and broker published for x64
  Windows and compiled ahead of time, and the scale gate measuring them so (`bench/results/scale-tiers-20261119T164559Z`,
  `docs/reviews/M5-declared-performance-tier.md`); the window runs without dynamic profile-guided optimization, and the
  gate measures it so, since revision 1091 (its addendum); what the compiler leaves to the runtime is corrected in its
  second addendum (revision 1092)
- Date: 2026-11-19
- Decision owners: InterCat maintainers
- Relates to: ADR-026, ADR-113, ADR-115, plan §12, §12.1, §20.1, `installer/Build-Installer.ps1`,
  `src/InterCat.BrokerStage.targets`, `bench/README.md`

## Context

§12's budgets hold the first time a process asks a question of a session as well as afterwards: T1 promises them cold,
and T2 too. A .NET process compiles each method the first time it runs it, so a question's first answer paid for
compiling the code that answers it. At T2 a process's first evidence page took 140 to 180 ms the first time and 14 to 18
once its code was compiled (ADR-113's second addendum), and the runtime's own settings did not change that: profile-guided
optimization off, loops optimized at once, tiering off, each measured. Publishing for one runtime, the .NET SDK can
compile the assemblies ahead of time (ReadyToRun): the compiled code is kept beside the IL and used from the first call,
the runtime compiling a method again only as it grows hot. An experiment on a copy of the build brought T2's first
evidence page to 100 to 117 ms.

The compiler cannot take everything. The framework's generic code instantiated over InterCat's own value types belongs to
the framework's version bubble, which a framework-dependent application cannot compile into its own. Code doing 128-bit
arithmetic, or passing another InterCat assembly's structures by value, it leaves to the runtime, holding their calling
convention unstable across versions. A method marked to be optimized at once (`AggressiveOptimization`) it leaves by
design, and 37 of InterCat's counting loops were marked so, to escape the runtime's unoptimized first tier.

The package also staged the broker beside each client from the broker's build output. A client published for one
runtime builds its executable references for that runtime too, into a folder of its own, so the output staged was
another build's - and its assemblies, compiled at first use, replaced the client's precompiled ones of the same names.

## Decision

1. **The package's clients are published for x64 Windows (`win-x64`), every assembly compiled ahead of time**,
   framework-dependent as before. The build fetches the compiler package the SDK names
   (`Microsoft.NETCore.App.Crossgen2.win-x64` at the SDK's runtime version) the first time it runs.
2. **Each client stages the broker published as the client is** - for its runtime, compiled ahead of time when the
   client is - rather than the broker's build output, so the assemblies the two share are the same bytes. Built for a
   runtime, a client stages the broker's build output from that runtime's folder.
3. **The scale gate measures the code the package ships**: published for x64 and compiled ahead of time, its InterCat
   assemblies the same bytes as the CLI's precompiled publish from the same tree but for the version the package stamps.
   Each report counts the InterCat assemblies its run loaded and how many were precompiled, and how long the runtime spent
   compiling inside each cold answer; `ScaleTierTests` holds every run to every assembly it loaded precompiled.
4. **On a first view's path, what the compiler cannot take is rewritten where that is exact, or compiled optimized at
   its first use.** A time range's width check takes the unsigned difference of its readings, and a column's interval
   divides the span into whole and remaining parts - `floor(span * i / W) = q * i + floor(r * i / W)` - neither asking
   128-bit arithmetic, so both are compiled ahead of time; the clock conversions judging each column's coverage, 128-bit
   by nature, are marked to compile optimized at once, as the column a tick falls in already was. The 37 marked loops
   stay marked: unmarked in an experiment, the 35 the compiler could then take made a group's first timeline slower -
   106 against 88 ms at T2's median - than compiled optimized at first use.

## Consequences

- At T2 a group's first timeline took 83 ms at the median, with its count walking behind its read-ahead (ADR-113's
  third addendum), its byte lanes 76 and a process's first evidence page 103, and T2 is declared
  (`docs/reviews/M5-declared-performance-tier.md`).
- What the runtime still compiles inside a cold answer at T2 is the framework's generic code made for InterCat's types
  and the marked methods: about 6 ms of a group's first timeline, 15 of its byte lanes and 33 of a process's first
  evidence page.
- The package carries no other platform's native libraries and is half the size, 28 MB against 57, though a precompiled
  assembly is about twice its IL. It installs on x64 Windows, as the package always has.
- Tests and the development build still run code compiled at first use. A measurement made from the Release build
  rather than from the gate's publish is not comparable with the declared tier, which `bench/README.md` says.
- Building the package needs the compiler package once from the configured NuGet feed.

## Alternatives

- **A self-contained composite build**, the framework compiled with InterCat into one version bubble, which would
  compile the framework's generic code for InterCat's types too. It needs a self-contained broker package, which the
  clients' framework-dependent publish exists to avoid (ADR-115), and carries the whole framework in the package.
- **Running the query code once, in the background, when a session opens.** No compiler package is needed, but every
  open pays for it in processor time and races the first question rather than removing its cost.
- **Keeping the code compiled at first use.** T2's first evidence page and group timeline stay over budget cold.
- **Unmarking every loop marked to be optimized at once**, so the compiler takes them. Measured slower, as above.

## Reversal cost

Low. Publishing precompiled is two properties of the package's build and of the gate's publish; the broker staging's
publish is correct either way. The rewritten width check and column interval are exact, and stay so whether or not the
code is compiled ahead of time.

## Addendum: the window runs without dynamic profile-guided optimization (revision 1091)

Precompiled, a process's first evidence page took 155 ms at T3's median and 154 at T2's, alternating runs on the
machine in use. The runtime's dynamic profile-guided optimization, on by default, compiles a method that grows hot
twice - once instrumented, to learn how it runs, then optimized by what it learned - where without it the method is
compiled once, optimized. Inside a first answer, what grows hot runs instrumented for a while, slower, and is compiled
twice; the window answers each question in a burst, its first answer held to a budget. Alternating the gate's runs
with the optimization and without it, each run in a process of its own: without it a process's first evidence page
took 118 ms at T3's median of four runs where it took 155, 103 at T2's of three where it took 154, and 88 at T1's of
two where it took 107; a group's first byte lanes at T3 took 98 where they took 147, and its first timeline was
unchanged. Loops compiled optimized at their first use were measured too, and were slower: 189 ms for the evidence
page at T3, 162 for the byte lanes.

Since this revision the window's project turns dynamic profile-guided optimization off (`TieredPGO`), which its
runtime configuration carries into the package, and the scale gate's project does too, so the gate measures the
window's questions as the window runs them. Each report says whether the runtime optimized by profiles, and
`ScaleTierTests` holds every run to have run without it and both projects to turn it off. A hot method is compiled
once, optimized, without what profiles would have taught it: warm answers stay within their budgets at every tier, as
the gate measures them. The command line and the broker keep the runtime's default: their questions were not measured
without it.

Tests hold the projects and each run; 5 mutations of revision 1091 are caught, among them either project
run with the optimization and a run measured with it.

## Second addendum: what the compiler leaves, and a stored row that keeps its 128-bit addresses (revision 1092)

This record said the compiler leaves to the runtime code passing another InterCat assembly's structures by value. Its
verbose output says otherwise: such structures are compiled - methods taking a capture's identifier or a fact key among
them - and what it leaves is every method with a parameter whose type holds a 128-bit integer at any depth of its
values, a class as much as a structure, and every call passing one that it does not inline: it takes no such type as
laid out alike in every runtime version. The stored row (`ObservationRowV1`), a class holding its two IPv6 addresses
as `UInt128?`, is one, so every method passed a row is compiled at its first use, a process's first evidence page's
reading of its rows among them.

Revision 1092 tried keeping a row's addresses as their halves. The application assembly then left 124 methods to the
runtime where it leaves 196, those that only throw aside, the storage assembly 36 where 45 and the capture journal 3
where 9, and a first evidence page compiled 247 methods at first use where 260, about 3 ms less of compiling, its
reading of its rows running precompiled. The page's own time did not improve: alternating builds at T2, twelve runs
each, it took 122.5 ms at the median where revision 1091's took 103.5, the machine 16 to 68% busy, and at T3, four runs
each, 124 against 127. Why the precompiled reading was no faster was not found; with it compiled optimized at once
instead, four runs took 90 to 130 ms at T2, too few on a machine so loaded to tell apart from revision 1091's. With no
gain shown and T2's page perhaps slower, the row keeps its addresses as 128-bit numbers.

Most of what a first evidence page still compiles at first use is the framework's generic code made for the types it
is used with - sequences, dictionaries, sets and sorts of InterCat's values, the check that an enumeration's code is
defined - which a framework-dependent package cannot carry compiled.
