# InterCat communication patterns v1

Status: **implemented** on the command line (`icat patterns`, revision 763) and in the window (Recurring patterns…,
revision 764, §7), over an interval of a session since revision 795 (§1).

This contract fixes plan M12's recurring communication patterns - its explainable pattern groups - for one session's
current generation, or what began in an interval of it: every TCP connection the capture saw open, every UDP flow and
every RPC call, each grouped by a structural signature, with who took part in each role and how far through the
session, or the interval, it recurred. It owns no bytes: every occurrence is read from the generation's published
segments through the derivations the overview already makes (`relations-v1`, `operations-v1`, `entities-v1`), and
reading patterns changes none of them. ADR-064 records the decisions.

## 1. A request

- A session, read at its current generation: its overview, its connections and flows and its calls, all of one
  generation, read again where a newer one was published between them.
- An **evidence policy**, `IncludeCorrelated` by default, which admits a relation by its strength and a call by its
  process binding as the overview admits them; what it does not admit is counted apart (§4).
- An **interval**, optionally, in presentation ticks (since revision 795): the patterns are then made of the
  connections, flows and calls that began in it, a connection or flow where its first record was raised and a call at
  its start, or at its stop where no start was recorded. One that began outside it is counted apart (§4) and nothing
  else of it is read, so none is counted twice; one that began before it and ran on into it began outside it. Its
  tenths are then the interval's (§3).

## 2. Signatures

An occurrence is grouped by its **signature**: its mechanism, its service port or RPC interface and procedure, and
where its server end is. A signature names no process, address, port a client chose for itself, or time, so a pattern
groups what recurs and its participants say who took part (P6: a grouping, never an identity).

| Mechanism | Occurrence | Signature | Client | Server |
|---|---|---|---|---|
| TCP | a connection the capture saw open at one end alone: between two processes of the capture, or one-sided, whose other end no record holds | `tcp:here:<port>` where a process of this machine accepted it or a connection ran to an end on it no record holds, at that end's port; `tcp:elsewhere:<port>` where this machine's process connected to another host, at the host's port | the end that connected | the end that accepted, or the end connected to |
| UDP | a flow, between two processes or one-sided | `udp:here:<port>` or `udp:elsewhere:<port>`, by its **service port** and where the end at it is | the end not at the service port | the end at the service port |
| RPC | a call whose start named its interface | `rpc:<interface>:<procedure>`, the procedure `none` where its start recorded none | a process that made a client call | a process that served a call |

- **Service port.** Of a UDP flow's two ports, the one outside the dynamic range 49152-65535 (RFC 6335), the lower
  where both are; nothing opens a UDP flow, so its roles are told by port alone.
- **An end.** A process of the capture; another host, by its address alone - who is at it is not observed (P7); or an
  end on this machine no record holds - loopback, or the holder's own address - all of a pattern's counted together.
- **Roles.** A TCP connection's roles are where the capture saw it open: a connect at one end, or an accept at the
  other. An RPC call's are its side: the process that raised a client call made it, and the process that raised a
  server call served one.

## 3. A pattern

A pattern is one signature with the occurrences that share it:

| Field | Meaning |
|---|---|
| Occurrences | TCP connections, UDP flows, or the RPC calls its clients made. |
| Served | Of RPC, the calls the capture recorded served; none otherwise. |
| Weight | Its occurrences, or of RPC the more of its calls made and served: what it is ranked by. |
| Clients, servers | Each end in that role, with how many occurrences it took part in, the most first, then processes before hosts and the end no record holds, each in ordinal order. |
| First, last | When its first and last occurrences began, in presentation ticks: a connection or flow by its first record, a call by its start. |
| Tenths | In how many of ten equal parts of the session an occurrence began: of the interval where one was asked for (§1), else of its recording where its capture recorded a stop, else of the span of its records; one before or after it in the nearest part. |
| Recurring | Whether its weight is more than one. |

Patterns are ranked by weight, then by signature in ordinal order. A pattern is explained by its mechanism: what its
signature is made of, what is left out of it and how its roles were told.

## 4. Counted apart

No occurrence is dropped silently: what no pattern holds is counted, each by why.

- **TCP connections the capture saw open at neither end, or both**: no end has a known role.
- **UDP flows between two dynamic ports**: they have no service port.
- **RPC calls no recorded start named an interface for**: their start was not recorded, or named none.
- **What the evidence policy did not admit**: relations by their strength, calls by their process's binding.
- **What began outside the interval**, where one was asked for (§1): counted there alone, before its evidence policy
  or roles are read, so every connection, flow and call is in a pattern, counted apart for one reason, or outside the
  interval, never two of them.

Absence is never stated as none: each mechanism's coverage over the session is stated beside the patterns (R21), so a
mechanism the capture did not collect is not read as one nothing used.

## 5. The command line

`icat patterns <directory>` lists the recurring patterns, the heaviest first, 20 unless `--limit` says: each with its
rank, its signature in words - "TCP at port 8080 here", "TCP to port 443 at other hosts", "UDP to port 53 at other
hosts", "RPC calls to svcctl (Service Control Manager), procedure 7" - how many times it occurred, its clients and
servers counted - processes and the programs they ran, as the overview groups them by executable, hosts, and ends no
record holds - and its tenths. It states the session's span, each mechanism's coverage, how many patterns recur and
how many occurred once, and what no pattern holds. `--all` lists those that occurred once too. A session holding no
pattern says so: none at all, or none the evidence policy admits, the policy named as `--evidence-policy` takes it.

`--interval <start:end>` reads the patterns over an interval (§1, since revision 795), each bound a count of
100-nanosecond session ticks or a time with its unit, as every command that takes an interval reads one. The list then
states the interval, spreads the tenths over it and counts what began outside it with what no pattern holds; an
interval holding none says so of the interval, and a rank past those it holds is refused in its words. An interval
whose end is not after its start is refused.

`--pattern <rank>` shows one: its signature as a key, what it is made of, when it occurred, and every participant in
each role with its occurrences, a process named as the overview names it and with the program it ran. A rank past
those listed is refused, as are `--pattern` beside `--limit`, a limit below one and a policy not named.

`--json` writes `communication-patterns-v1`: the session, its generation and policy; additively since revision 795,
the interval read, as its start and end ticks or null, and how many connections, flows and calls began outside it; its
span and the ten parts; each mechanism's coverage; how many patterns there are, recur and occurred once, and whether
those listed include them; what is counted apart (§4); and each pattern listed with its rank, key, mechanism, place,
port, interface and its documented name, procedure, signature in words, explanation, occurrences, served, first and
last ticks, tenths, and its clients and servers - each with its kind (`Process`, `Host` or `UnseenEnd`), instance,
PID, name and program, or address, and its occurrences.

## 6. One generation

A reading's overview, connections, flows and calls are of one generation: where a newer one was published between
them, they are read again, up to three times, and then refused with a reason rather than mixed (R7).

## 7. The window

Recurring patterns…, among the actions beside the graph, is offered for a session at any rung but its source records.
It reads the session's current generation under the window's evidence policy, over the window's time scope (plan §6.4)
where it has one, and says it in the command line's words (R18):

- **The scope.** Above the list, what it reads over (since revision 795): the window's time scope, the brushed
  interval or the visible range while the timeline is zoomed, said on the window's clock, its tenths then the
  interval's and what began outside it counted with what no pattern holds; or all session times. Before it, the list
  read the whole session whatever the scope.
- **The list.** The recurring patterns, the heaviest first, each by its rank and its signature in words, then how many
  times it occurred, its clients and servers counted and its tenths. *Include those that occurred once* lists those
  after them, the chosen pattern staying chosen.
- **The chosen pattern.** What its signature is made of, when it occurred, its signature as a key, and every end in
  its role, its clients first, each with its occurrences in its role's unit: a process named as the overview names it,
  with the program it ran where its own name does not say it, or that no record witnessed its executable; another host
  by its address, with who is at it said to be unobserved.
- **About the list.** The generation read and, where it is newer than the window shows, that a process only it holds
  is not selected there; how many patterns recur and occurred once; the evidence policy, where it is not the default;
  each mechanism's coverage; what no pattern holds (§4); and, where the list holds none, why.
- **Select its processes**, or Enter or a double click on a pattern, closes the list and makes the pattern's
  processes, of either role and each once, the selection together, as Ctrl+click makes one (plan §6.7); a pattern of
  one process goes to it, as a click does. The graph then has the keyboard, and Enter lists their records. A process
  the window does not hold is left out; where it holds none of them, the window says so and the selection stays as it
  was. Escape and Close choose nothing.

The window selects a pattern's processes rather than marking edges: an edge the graph draws stands for every port and
interface between its two ends, so marking it would claim more than the pattern holds (ADR-065).

## 8. Not defined at this version

- **Other mechanisms' patterns**: HTTP exchanges, named pipes and ALPC ports, whose structural signatures need their
  own normalization (plan M12's source-specific normalization).
- **Patterns across captures**: a reading is of one generation, whole or over one interval of it (§1).
- **Periodicity.** Tenths say how far through the session, or the interval read, a pattern recurred, never how often:
  no period is inferred from the times its occurrences began.
