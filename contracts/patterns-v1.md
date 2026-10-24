# InterCat communication patterns v1

Status: **implemented** on the command line (`icat patterns`, revision 763). The window does not show patterns at this
version (§7).

This contract fixes plan M12's recurring communication patterns - its explainable pattern groups - for one session's
current generation: every TCP connection the capture saw open, every UDP flow and every RPC call, each grouped by a
structural signature, with who took part in each role and how far through the session it recurred. It owns no bytes:
every occurrence is read from the generation's published segments through the derivations the overview already makes
(`relations-v1`, `operations-v1`, `entities-v1`), and reading patterns changes none of them. ADR-064 records the
decisions.

## 1. A request

- A session, read at its current generation: its overview, its connections and flows and its calls, all of one
  generation, read again where a newer one was published between them.
- An **evidence policy**, `IncludeCorrelated` by default, which admits a relation by its strength and a call by its
  process binding as the overview admits them; what it does not admit is counted apart (§4).

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
| Tenths | In how many of ten equal parts of the session an occurrence began: of its recording where its capture recorded a stop, else of the span of its records; one before or after it in the nearest part. |
| Recurring | Whether its weight is more than one. |

Patterns are ranked by weight, then by signature in ordinal order. A pattern is explained by its mechanism: what its
signature is made of, what is left out of it and how its roles were told.

## 4. Counted apart

No occurrence is dropped silently: what no pattern holds is counted, each by why.

- **TCP connections the capture saw open at neither end, or both**: no end has a known role.
- **UDP flows between two dynamic ports**: they have no service port.
- **RPC calls no recorded start named an interface for**: their start was not recorded, or named none.
- **What the evidence policy did not admit**: relations by their strength, calls by their process's binding.

Absence is never stated as none: each mechanism's coverage over the session is stated beside the patterns (R21), so a
mechanism the capture did not collect is not read as one nothing used.

## 5. The command line

`icat patterns <directory>` lists the recurring patterns, the heaviest first, 20 unless `--limit` says: each with its
rank, its signature in words - "TCP at port 8080 here", "TCP to port 443 at other hosts", "UDP to port 53 at other
hosts", "RPC calls to svcctl (Service Control Manager), procedure 7" - how many times it occurred, its clients and
servers counted - processes and the programs they ran, as the overview groups them by executable, hosts, and ends no
record holds - and its tenths. It states the session's span, each mechanism's coverage, how many patterns recur and
how many occurred once, and what no pattern holds. `--all` lists those that occurred once too.

`--pattern <rank>` shows one: its signature as a key, what it is made of, when it occurred, and every participant in
each role with its occurrences, a process named as the overview names it and with the program it ran. A rank past
those listed is refused, as are `--pattern` beside `--limit`, a limit below one and a policy not named.

`--json` writes `communication-patterns-v1`: the session, its generation and policy; its span and the ten parts; each
mechanism's coverage; how many patterns there are, recur and occurred once, and whether those listed include them;
what is counted apart (§4); and each pattern listed with its rank, key, mechanism, place, port, interface and its
documented name, procedure, signature in words, explanation, occurrences, served, first and last ticks, tenths, and
its clients and servers - each with its kind (`Process`, `Host` or `UnseenEnd`), instance, PID, name and program, or
address, and its occurrences.

## 6. One generation

A reading's overview, connections, flows and calls are of one generation: where a newer one was published between
them, they are read again, up to three times, and then refused with a reason rather than mixed (R7).

## 7. Not defined at this version

- **Patterns in the window.** The command line lists them (§5). - **Other mechanisms' patterns**: HTTP exchanges,
named pipes and ALPC ports, whose structural signatures need their own normalization (plan M12's source-specific
normalization). - **Patterns over an interval**, or across captures; a reading is of a whole generation. -
**Periodicity.** Tenths say how far through the session a pattern recurred, never at what interval: no period is
inferred from the times its occurrences began.
