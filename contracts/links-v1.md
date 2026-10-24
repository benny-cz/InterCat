# InterCat links v1

Status: **implemented** on the command line (`icat links`, revision 765). The window does not show links at this
version (§8).

This contract fixes plan M12's causal navigation for one session's current generation: the links its evidence records
between processes, other hosts and ends no record holds, each by one documented rule and at one strength; the links a
view follows, which are only those its evidence policy admits; and what is listed beside them and never followed. It
owns no bytes: every link is read from derivations the overview already makes (`entities-v1` §3, `operations-v1` §5c,
`relations-v1` §5), and reading links changes none of them. ADR-066 records the decisions.

## 1. A reading

- A session, read at its current generation: its overview, its processes, its relations and the other ends of its
  calls, all of one generation, read again where a newer one was published between them.
- An **evidence policy**, `IncludeCorrelated` by default. It decides which links a view follows (§3); a reading holds
  every link whatever its strength.
- Each of process lifecycle's, TCP's, RPC's and ALPC's coverage over the session, stated beside the links (R21), so a
  link the capture could not have seen is not read as one that did not happen.

## 2. Links

A link joins two ends, **from** the one that led **to** the other, by one rule:

| Kind | From | To | Rule | Strength |
|---|---|---|---|---|
| `Started` | the parent process | the child process | the child's creation or rundown record names the parent (`entities-v1` §3) | direct by the parent's start key; correlated by its PID alone where the PID's first holder held it then; candidate where a later holder of a reused PID did |
| `Called` | the process whose client calls were served | the process whose server calls served them | each client call's one ALPC message reached the server call that served it (`operations-v1` §5c), counted by interface | correlated at best, and as weak as the weaker of the two calls' bindings |
| `Connected` | the end the capture saw make a TCP connection | the end that accepted it | the connect or accept record of either end (`relations-v1` §5), counted by the port it arrived at | the relation's or the one-sided connection's strength |

- **An end.** A process of the capture; another host by its address alone - who is at it is not observed (P7); or an
  end on this machine no record holds. Only a TCP connection's other end can be a host or an unseen end.
- **One link per signature.** Links of one kind, two ends, one interface or port and one strength are one link, with
  how many times the evidence holds it: calls served, or connections.
- **A process connected to itself** led to itself; it is never also among what led to it.

## 3. One process's links

A view of one process lists:

- **Led to it**: the links the policy follows whose `to` is the process - its parent, the processes whose calls it
  served, the ends that connected to it.
- **It led to**: those whose `from` is it - its children, the processes that served its calls, the ends it connected
  to.
- **Not followed**: the links touching it at a strength the policy does not admit. Each is possible, not established;
  it is listed and never followed, and no chain is made through it.
- **Near in time**: the processes whose creation was witnessed within a second of its own, before or after, and that
  no link joins to it, followed or not, the nearest first. Nearness is never a link (P8, plan §7.4): it is listed so a
  person does not mistake it for one, and nothing follows it. A process whose creation was not witnessed is near none.

Each list orders its links by kind - `Started`, `Called`, `Connected` - then the most held first, then processes
before hosts and the end no record holds, each by name.

## 4. What links to nothing

What of a process's activity links it to nothing is counted, each by why, so a missing link is never read as none:

- **TCP connections whose opening no record shows**: neither end is known to have made them.
- **UDP flows**, which nothing opens.
- **Client calls whose other end is unresolved**, by `operations-v1` §5c's reason.
- **Calls it served that no linked client call reached**: a client may be remote, may not use ALPC, or may not be in
  the capture.
- **A parent its creation names** that no process of the capture is linked as.

## 5. A chain

A **chain** from one process to another is the shortest sequence of links the policy follows, each from the process
the previous one led to, through processes alone: another host or an unseen end is an address, never a step, so
nothing is chained through it. Among chains as short, the one whose links come first by kind and then by the instance
they lead to. A chain is as strong as its weakest link (ADR-055), and claims no more than its links: never a cause of
what the last process did.

Where none leads there, a view says so, and says whether one would through links the policy does not follow - with the
narrowest policy that follows them - and whether one leads the other way.

## 6. The command line

`icat links <directory> --process <process>` lists one process's links (§3): each with what its other end is to the
process, the other end, the evidence in words - "the child's creation record names the parent by its start key", "2
calls to svcctl (Service Control Manager), each reaching the call that served it through its one ALPC message", "1
connection to port 8080 that it made, as the capture saw it open" - and its strength; then the links not followed, the
processes near it in time, and what of it links to nothing (§4). A process is named by its PID where one process alone
held it in the session, else by its instance id; a reused PID is refused with each of its holders' ids.

`--to <process>` finds the chain from it to another process (§5), each link as a sentence - "shell.exe · PID 300
started app.exe · PID 400" - with its evidence and strength, and the chain's weakest; one to itself is refused.
`--evidence-policy` takes a policy as written or in kebab form, `include-correlated` unless it says so.

`--json` writes `evidence-links-v1`: the session, generation, rule and policy; each mechanism's coverage; the process;
its links led to it, led to and not followed, each with its kind, both ends - their kind (`Process`, `Host` or
`UnseenEnd`), instance, PID and name, or address - its strength, occurrences, interface and its documented name, port
and evidence; whether its creation was witnessed, the window of nearness in ticks and each near process with its
offset; what of it links to nothing; and, with `--to`, the chain: its end, whether one was found, its weakest strength
and its links.

## 7. One generation

A reading's overview, processes, relations and calls are of one generation: where a newer one was published between
them, they are read again, up to three times, and then refused with a reason rather than mixed (R7).

## 8. Not defined at this version

- **Links in the window.** The command line lists them (§6).
- **Links between operations**: a call to the records it caused, or a record to a record; a link here joins processes
  and ends, its evidence counted.
- **Other links**: named pipes, ALPC ports beside RPC, HTTP exchanges to the connections that carried them, an ETW
  related activity id, and thread nesting, none of which a rule here follows.
- **Links over an interval**, or across captures; a reading is of a whole generation.
