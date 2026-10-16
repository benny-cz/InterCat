# InterCat

InterCat shows which processes on a Windows machine communicate, through which mechanism, and on what evidence. It
records a capture through ETW, keeps every admitted record in a session you can reopen and share, and draws it as a
communication graph, an activity timeline and a ranked table you can descend from the whole machine to a single source
record. Nothing is drawn that the records do not support: a relationship is drawn only where both of its ends were
seen, every mark says on hover what it rests on, and what a capture did not cover is stated rather than shown as quiet.

Where the implementation stands, and what comes next, is in [the implementation status](docs/IMPLEMENTATION-STATUS.md);
the product and engineering blueprint is [the implementation plan](docs/design/INTERCAT-IMPLEMENTATION-PLAN.md).

## What it does

**Capture (Windows).** The window's *Start exploring* records through a capture broker that Windows asks you to approve
once per start; InterCat itself runs unelevated and draws the capture as it records. *Keep* chooses what is kept:

| Keep | What it records |
|---|---|
| Every record, 10 minutes | The default Explore capture: process lifecycle, TCP and UDP over IPv4 and IPv6, and RPC calls |
| The last 10 minutes, the last hour | The same, keeping a rolling window of session time; a pin keeps a moment past it |
| RPC peers, 10 minutes | Explore with the kernel's ALPC messages, which link each RPC call to the process that served it. Opt-in: it takes one of the eight system loggers a machine can run |
| Your processes' messages | The HTTP messages your own processes send and receive through WinINet, with limits you review before it starts |

Named pipes are measured unsupported on the builds tested and are not captured. Each mechanism's measured tier on each
Windows build is in [the per-build coverage](docs/PER-BUILD-COVERAGE.md).

**The graph** relates processes by TCP connections and UDP flows whose two ends both belong to processes of the capture,
and by RPC calls linked to the calls that served them in an RPC peers capture. Each pair's relationships run side by
side, a mechanism's hue each, with an arrowhead at the process connected to where the capture saw their connections
open. Connections whose other end no record holds - most of a real machine's, to other hosts - are drawn to one
**Other hosts** node, with an arrowhead at it where a process connected out and at the process where it accepted; it
names the busiest addresses they ran to, lists them all with the ports reached at each and the connections a process
accepted from each, and, opened, draws them on their own; a host chosen, drawn or listed, highlights the records of
every connection to it for E to list. Processes that only reach other hosts are drawn beside it by program, several of
one program as one node: the busiest programs each on its own, the rest counted in **Other hosts only**; processes with
no relationship are counted in **No relationships**, and large executable groups collapse; chosen, Other hosts only or
No relationships lists the processes it counts, and Enter opens one. When it draws no relationship, the graph says why.
A search marks the processes it finds, by name, PID, an address they reached or a port they accepted connections at. The
relationship table (T) lists each relationship it draws, who connected included, for a keyboard or a screen reader.

**The timeline** counts every observed record by mechanism, process, source direction and channel end. Brush an interval
to rank within it, zoom into it, or read it on the capture machine's wall clock.

**The ranked table and the ladder** go from the machine to an executable group, a process instance, its channels, its
RPC calls or HTTP exchanges, and their source records (E), ranked by records, bytes, RPC calls, errors, durations, peers
or the connections a process made or accepted, per second if you like. Counts say what they count and over which scope,
and a chosen record opens its original journal entry.

**Investigations** hold sessions captured on several machines, aligned by their wall clocks, with the connections one
machine's capture can join to another's; a session opened from its investigation names, beside each other host it
reached, the processes another member's capture proposes at its other end, never as an established join.
*Investigation → Explore the demo* opens InterCat's generated demo, which needs no capture.

**Sharing.** A view exports as JSON or CSV. A session shares as a redacted metadata report, as a redacted session package
that reopens with pseudonymous names, addresses and ports, or byte for byte as the original evidence; a support bundle
holds no content, name or path.

F1 in the window lists every key it takes.

## Build and run

Capture needs Windows. Opening, exploring and sharing sessions works wherever .NET 10 runs.

```powershell
dotnet build InterCat.slnx
dotnet test InterCat.slnx

# The window.
dotnet run --project src/InterCat.Desktop

# icat, the command line: every command, and each one's options with <command> --help.
dotnet run --project src/InterCat.Cli -- --help
```

`icat` below is `dotnet run --project src/InterCat.Cli --`.

```powershell
# A tour with no capture: the demo's two hosts and the investigation that aligns them.
icat demo <new-folder>
icat overview <new-folder>/demo-server
icat hosts <new-folder>/demo-client --investigation <new-folder>/demo.icat-workspace
icat channels <new-folder>/demo-server
icat workspace show <new-folder>/demo.icat-workspace

# Capture through the broker (Windows asks for approval), for a minute, or with RPC peers.
icat capture <new-session-folder> --duration 60
icat capture <new-session-folder> --profile rpc-peers

# Read a session: its graph and timeline, who connected to whom, its paired channels and their records, the other
# hosts it reached, its processes and the connections each made and accepted, and its RPC calls.
icat overview <session>
icat channels <session>
icat evidence <session> --channel <key from icat channels>
icat timeline <session> --interval 0:10s
icat hosts <session> --rank-by bytes-sent
icat hosts <session> --address 10.0.0
icat evidence <session> --host 10.0.0.20
icat processes <session>
icat operations <session>

# Share it.
icat export <session> --output view.json
icat package <session> --redacted --output <new-folder>

# Read-only: what this machine's ETW sources can do, and what a profile would collect before anything starts.
icat capabilities
icat profiles explore
```

## Where the evidence lives

| Path | Contents |
|---|---|
| `contracts/` | The frozen artifact contracts: journal, store, segments, relations, operations, indexes, workspaces and more |
| `capabilities/<build>/` | Machine-readable capability reports, per build and adapter |
| `fixtures/index.json` | The fixture traceability matrix and the contract-coverage ledger |
| `fixtures/FX-*/evidence/` | Each measured fixture's curated truth log, scoped observations and result |
| `bench/results/` | Reproducible measurement runs; their counters are committed, their records are not |
| `theme/` | Theme tokens and their recorded contrast and separation measurements |
| `docs/adr/` | Architecture decisions |
| `docs/reviews/` | Scored interaction reviews with the screens they were taken from |
| `docs/PER-BUILD-COVERAGE.md` | Each mechanism's measured tier on each Windows build, from the fixture evidence |
| `docs/IMPLEMENTATION-STATUS.md` | Where implementation stands and what comes next |
