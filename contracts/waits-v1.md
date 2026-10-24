# InterCat waits v1

Status: **implemented** on the command line (`icat waits`, revision 767) and in the window (Time inside calls…,
revision 769, §7).

This contract fixes plan M12's bottleneck views for one session's current generation: the time each process spent
inside the RPC calls it made, by interface and by the process whose server calls served them, with that server's own
time beside it where the two calls are linked; the servers callers waited on; and what no time is said for. It owns no
bytes: every span is read from the calls `operations-v1` derives and every link from its §5c, and reading waits
changes neither. ADR-067 records the decisions.

## 1. A reading

- A session, read at its current generation: its overview, its calls and their other ends, all of one generation, read
  again where a newer one was published between them, up to three times, and then refused with a reason rather than
  mixed (R7).
- An **evidence policy**, `IncludeCorrelated` by default, which admits a caller and a server by their calls' process
  bindings as the overview admits them.
- RPC's and ALPC's coverage over the session, stated beside the waits (R21), so a session that could not have recorded
  a call or its other end is not read as one that made none.

## 2. A wait

A **wait** is one caller's validated calls to one interface, served by one process or by none named:

| Field | Meaning |
|---|---|
| Caller | The process whose client calls these are, by their binding. |
| Interface | The interface their starts named; none where they named none. |
| Server | The process whose server calls served them through ALPC (`operations-v1` §5c), where the evidence policy admits it; none otherwise. |
| Calls | Its calls whose span is validated: a start and a stop paired by activity id on the caller (`operations-v1` §3, `Completed`). |
| Failed | Of them, those whose stop reported a status other than 0. |
| In calls | The sum of their spans, start to stop on the caller, in session nanoseconds; with their count, shortest, median, 95th percentile and longest, each one of them, never an interpolation. |
| Linked | Of them, those whose server call also completed, so both spans are measured. |
| Linked in calls, server ran | Over the linked calls alone: the sum of the caller's spans, and the sum of the server calls' spans. |
| Unresolved | Where no server is named: how many calls each `operations-v1` §5c reason left unresolved, and how many a server the policy does not admit served. |

Waits are listed by the time in calls, the most first, then by the most calls, then by caller name, interface and
server name, a wait with no server named after those with one.

- **A span is the caller's.** It runs from the call's start to its stop as the caller recorded them. A process can
  make several calls at once, and a call can return before its work is done, so waits add up to more than the time
  that passed, and none is said to have blocked or delayed anything; no path through calls is called critical, and no
  wait is called a deadlock.
- **The server's time is its own call's.** Where a call is linked to the server call that served it and that call
  completed, the server's time is its span; the rest of the caller's span - transport, queuing, scheduling - is not
  attributed to anything.

## 3. The servers

The servers callers waited on: each server named by some wait, over every caller - how many callers, each once however
many interfaces it called there; their calls; the time in them; and, over the linked calls alone, the time in them and
the time the server's calls took. The most time first, then the most calls, then by name.

## 4. What no time is said for

No call is timed without a validated span, and each such call is counted by why:

- **Calls whose span is not validated**, by state: open at capture end, start not observed, no activity id, or
  ambiguous (`operations-v1` §4).
- **Calls whose caller the evidence policy does not admit**, or that bind to no process.

## 5. The command line

`icat waits <directory>` lists the waits, the most time first, 20 unless `--limit` says: each with its caller,
interface in words - "svcctl (Service Control Manager)" - and its server or why none is named - "not linked: no ALPC
send on its thread during the call" - its calls, failures, time in calls, median, 95th percentile and longest, and the
server's time over the linked calls - "3.0 µs of 6.0 µs (50%)", with how many of the calls that is where not all of
them. Beneath are the servers callers waited on, what no time is said for, and what a wait is and is not.

`--process <process>` lists one process's calls - the waits it is the caller of - and the calls it served, each caller
named; a process is named by its PID where one process alone held it in the session, else by its instance id.
`--evidence-policy` takes a policy as written or in kebab form.

`--json` writes `call-waits-v1`: the session, generation and policy; RPC's and ALPC's coverage; the process, with
`--process`; how many waits there are and those listed, each with its caller and server - instance, PID and name - its
interface and documented name, calls, failures, time in calls and its distribution, linked calls, their time in calls,
the server's time, and why no server is named; the calls it served, with `--process`, or else the servers; and what no
time is said for, by state, with the calls the policy did not admit.

## 6. One generation

A reading's overview, calls and other ends are of one generation (§1).

## 7. The window

Time inside calls… in InterCat's window lists the same waits in the command line's words, read from the session's
current generation under the window's evidence policy, over all session times - a time brush does not narrow them:

- **Each wait** by its caller and interface, then who served its calls or why none is named, its calls and failures,
  its time in calls and, where some are linked, the server's own time: "1. app.exe · PID 400 → svcctl (Service Control
  Manager) · served by services.exe · PID 1960 · 2 calls, 1 failed · 6.0 µs in calls · the server ran 3.0 µs of 6.0 µs
  (50%)". The status line says how many calls the list counts, of how many callers, and the evidence policy where it
  is not the default.
- **The chosen wait** says who served its calls, how many there were, its time in calls with their median, 95th
  percentile and longest, and the server's own time, or that none is measured; beside it are the servers callers
  waited on (§3).
- **Opened with a process selected**, it lists that process's calls and the calls it served, each caller named, as
  `--process` does (§5), until the box beneath the list is unticked.
- **What no time is said for** (§4) is said beneath the list, with what a wait is and is not.
- **Select caller and server**, Enter or a double click, closes the window and makes the wait's caller and server the
  selection together, as Ctrl+click does and as a pattern's processes are (ADR-065); a wait no server is named for
  goes to its caller. A process only a newer generation holds is not selected, and InterCat's window says so. The
  window itself changes nothing.

## 8. Not defined at this version

- **Other spans**: HTTP exchanges, named pipes, ALPC messages, and a thread's waits on synchronization objects, which
  no capture here records.
- **Nesting**: a server call's own calls are their own waits; a call made inside another is not joined to it, as
  `operations-v1` §8 leaves thread nesting undefined.
- **Waits over an interval**, or across captures; a reading is of a whole generation.
