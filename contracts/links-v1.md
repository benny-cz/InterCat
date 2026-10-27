# InterCat links v1

Status: **implemented** on the command line (`icat links`, revision 765) and in the window (Follow its links…,
revision 766, §8), over an interval of a session since revision 797 (§1).

This contract fixes plan M12's causal navigation for one session's current generation, or what began in an interval of
it: the links its evidence records between processes, other hosts and ends no record holds, each by one documented
rule and at one strength; the links a view follows, which are only those its evidence policy admits; and what is
listed beside them and never followed. It owns no bytes: every link is read from derivations the overview already
makes (`entities-v1` §3, `operations-v1` §5c, `relations-v1` §5), and reading links changes none of them. ADR-066
records the decisions.

## 1. A reading

- A session, read at its current generation: its overview, its processes, its relations and the other ends of its
  calls, all of one generation, read again where a newer one was published between them.
- An **evidence policy**, `IncludeCorrelated` by default. It decides which links a view follows (§3); a reading holds
  every link whatever its strength.
- An **interval**, optionally, in presentation ticks (since revision 797): the links are then made of what began in
  it, and so is what links to nothing (§4): a start where the child's creation was witnessed, a call where the
  client's started or, where no start was recorded, stopped, and a connection or flow where its first record was
  raised. A start no record witnessed began before the capture, so no interval holds it. A server call a client
  reached began with that client's call, wherever the server's began; one no client reached, at its own start. What
  began outside the interval is counted apart, for each process it touches (§3), and nothing else of it is read.
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
- **Outside the interval**, where one was asked for (§1): how many of the starts, calls, connections and flows it took
  part in began outside it, a served call counted for both its caller and its server, so none of them is listed and
  none is counted as linking to nothing. Its links, what links to nothing and what began outside add up to what the
  whole session holds of it.
- **Near in time**: the processes whose creation was witnessed within a second of its own, before or after, and that
  no link joins to it, followed or not, the nearest first. Nearness is never a link (P8, plan §7.4): it is listed so a
  person does not mistake it for one, and nothing follows it. A process whose creation was not witnessed is near none.
  Nearness reads every witnessed creation, over whatever interval the links were read.

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
what the last process did. Over an interval, a chain runs through the links that began in it alone.

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

`--interval <start:end>` reads the links over an interval (§1, since revision 797), each bound a count of
100-nanosecond session ticks or a time with its unit, as every command that takes an interval reads one. The answer
then states the interval and how many of the starts, calls, connections and flows the process took part in began
outside it, and a chain is sought through the interval's links alone, its absence said of the interval. An interval
whose end is not after its start is refused.

`--json` writes `evidence-links-v1`: the session, generation, rule and policy; additively since revision 797, the
interval read, as its start and end ticks or null; each mechanism's coverage; the process, and, additively, how many
of the starts, calls, connections and flows it took part in began outside the interval; its links led to it, led to
and not followed, each with its kind, both ends - their kind (`Process`, `Host` or `UnseenEnd`), instance, PID and
name, or address - its strength, occurrences, interface and its documented name, port and evidence; whether its
creation was witnessed, the window of nearness in ticks and each near process with its offset; what of it links to
nothing; and, with `--to`, the chain: its end, whether one was found, its weakest strength and its links.

## 7. One generation

A reading's overview, processes, relations and calls are of one generation: where a newer one was published between
them, they are read again, up to three times, and then refused with a reason rather than mixed (R7).

## 8. The window

Follow its links…, among the actions beside the graph, is offered for the selected process at any rung but its source
records. It reads the session's current generation under the window's evidence policy, over the window's time scope
(plan §6.4) where it has one since revision 797, said on the window's clock above the lists and kept for every process
followed to, and lists, in the command line's words (R18):

- **Led to it** and **It led to**, side by side: each link by what its other end is to the process and that end, then
  its evidence and strength; where one holds none, it says so in its place.
- **Not followed**, where links touching the process are at a strength the policy does not admit, saying so.
- **Near in time**, as text and never as a link anything follows, and **Not linked** where something of it is.
- **About it**: the generation read and, where it is newer than the window shows, which generation the window shows;
  the strengths the policy follows; how many links led to it and it led to; each mechanism's coverage; and, over an
  interval, how much of it began outside it.

Enter, a double click or Follow follows the chosen link to the process at its other end, either way along it, and
centres the list on that process, the path walked said above it; Back or Alt+Left returns a link at a time. A link to
an address, another host or an end no record holds, and a link the policy does not follow are never followed, each
saying why. Select this process closes the list and makes the process it is centred on the selection, as a click on
its node does, and the graph then has the keyboard; a process the window does not hold is not selected, and the window
says so. Escape and Close choose nothing.

## 9. Not defined at this version

- **Links between operations**: a call to the records it caused, or a record to a record; a link here joins processes
  and ends, its evidence counted.
- **Other links**: named pipes, ALPC ports beside RPC, HTTP exchanges to the connections that carried them, an ETW
  related activity id, and thread nesting, none of which a rule here follows.
- **Links across captures**: a reading is of one generation, whole or over one interval of it (§1).
