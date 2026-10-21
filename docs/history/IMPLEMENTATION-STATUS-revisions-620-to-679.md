# InterCat implementation status — historical log, plan revisions 620 to 679

Archived on 2026-10-21, at plan revision 697, whole and in order. The current resume point is
[the implementation status](../IMPLEMENTATION-STATUS.md); revisions 580 to 619 are in
[the fifth log](IMPLEMENTATION-STATUS-revisions-580-to-619.md), revisions 500 to 579 in
[the fourth](IMPLEMENTATION-STATUS-revisions-500-to-579.md), revisions 230 to 499 in
[the third](IMPLEMENTATION-STATUS-revisions-230-to-499.md), revisions 104 to 229 in
[the second](IMPLEMENTATION-STATUS-revisions-104-to-229.md), and the chronology before them in
[the earliest log](IMPLEMENTATION-STATUS-through-revision-103.md).

## Slices, newest first

- **Revision 679 — `icat processes`' and `icat overview`'s tables say where each process accepted its connections (§5.2, R18):**
  - The Connections cell names the ports of its own each accepted at - "7 made · 8 accepted at 5432" - so a ranking by
    connections accepted reads as who served where; each table's note says so, as does the README.
  - Debug and Release each ran 2,680 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    processes and overview tests now read the ports in the server's, helper's and client's cells and each table's
    note. Six of the seven mutations were caught - either table's cell without its ports or wording them as a
    sentence does, and either note's words left out - and the seventh, a guard on connections accepted that the
    ports already imply, was removed from both tables and from the detail's field.

- **Revision 678 — a port search finds the processes that accepted connections at that port of their own (§6.7, §6.3):**
  - Whoever connected - its own machine's processes as well as other hosts - by the ports revision 677 gives each
    process: ":9222" finds chrome.exe PID 3000, its hit saying it accepted TCP connections at the port searched, and
    the same rule marks it in the graph and its lanes.
  - Debug and Release each ran 2,680 tests with revision 473's 93 failures and 4 skips, and nothing else failed; a
    new search test finds a process by a port its own machine's processes connected to it at, beside a busier one
    found by another host's connection there, by its PID as before, and not by another port, an endpoint or the
    port's digits, the hits by the rule that marks them, and a new view-model test finds and marks the server and
    the client by the ports each accepted at. Eight of the nine mutations were caught - the listening port ignored
    by the hits or by the marks, an endpoint or any port finding a listener, the hit's words, its detail or its rank
    - and the ninth, a guard the hit's other branches made redundant, was removed from the code.

- **Revision 677 — a process says at which of its own ports it accepted connections (§6.8, §7.4, R18):**
  - From the same openings its counts read, whoever connected: the card, the inspector and `icat processes --pid` say
    "19 accepted at port 9223", a program its processes' ports together, and the JSON `acceptedPorts`. A port search
    finding a process by the ports it accepted its own machine's connections at is next.
  - Debug and Release each ran 2,678 tests with revision 473's 93 failures and 4 skips, and nothing else failed; a
    new projector test accepts a connection at an IPv6 port and another host's at an IPv4 one, beside one made and
    one seen opening neither way, a new text test says a set's ports together, and the card, inspector and icat
    processes tests now name the ports. All eighteen mutations were caught, one after its test gave a set's second
    member a port of its own: the accepting end or its endpoint swapped, a connection seen opening neither way
    counted, another host's accepted connections left out, its connects counted or its remote port taken, an IPv6
    port lost to the first colon, the projector's ports dropped, a set's ports one member's, unordered or repeated,
    the counts or the card's line without them, the inspector or card left without them, and icat's field, its
    JSON's ports, or their null where no open is recorded.

- **Revision 676 — paired channels are paged busiest first (§7.1, R18):**
  - `icat channels` and the window's channel browser list the busiest first by the records at a channel's two ends,
    channels of as many records by key, one order across a generation's pages; a cursor given for the keys' order
    restarts. `icat channels` says what it is ranked by (`channel-page-v2`), and the browser's heading says the order
    and names a process's channels by the process.
  - Debug and Release each ran 2,676 tests with revision 473's 93 failures and 4 skips, and nothing else failed; new
    tests page three channels of six, two and two records whole and one at a time, from the query and from icat
    channels as text and JSON, and the channel browser's heading says the order and names a process's channels by
    the process, or by its instance where it is given no names. All ten mutations were caught: the pages in key
    order alone or the fewest first, channels of as many records against their keys' order, the command's JSON under
    the older contract, its Ranked by line or its help's words left out, and the browser's heading without the
    order, naming a process by its instance though it has a name, or by no instance where it has none.

- **Revision 675 — `icat hosts --program` lists one program's hosts, as its inspector points to (§6.3, R18):**
  - Named as `icat programs` takes one; with `--address` those of its processes that reached one; a byte ranking reads
    its connections alone, and the JSON describes the program (`other-hosts-v8`). A name two programs share is
    refused, saying how each is listed, and a command the window or icat cites puts a name a shell would split in
    double quotes.
  - Debug and Release each ran 2,674 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    hosts command's new test lists one program's hosts as text and JSON, with --address, ranked by bytes and for a
    program that reached none, and refuses a name two programs share, one none has and none at all, and a cited
    value's twelve cases say when it is quoted. All twenty-seven mutations were caught: a program's hosts or byte
    ranking read over every program, the JSON's program, path or process count wrong, either heading or the Program
    field without the program or counting every process, a note naming the session or a command without the program
    or unquoted, a shared name taken, the refusal's words, a name untrimmed or an empty one accepted, the window's
    two notes unquoted, and a value never or always quoted, an empty one or one with a space left bare, or a plus or
    a backslash quoted.

- **Revision 674 — a chosen program's inspector lists the other hosts its processes reached (§6.3, R15):**
  - As a chosen process's are listed, each host naming the busiest of its processes that reached it and how many more
    where several did; listed whole on request apart from its processes', a newer generation keeping it so.
  - Debug and Release each ran 2,661 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    tests of a process's other hosts now choose programs as well, one of two processes and one of one, in the view
    model and the window. All eleven mutations were caught: a program's processes left out, several of them said
    never or always, the heading a process's for a program, one selection's hosts kept for another, the note past a
    hundred naming the wrong command, a list asked for whole whole for every selection or kept for a process alone,
    a program taken for no selection, and the heading or the list's name not said again as the selection changes.

- **Revision 673 — a chosen process's inspector lists the other hosts it reached (§6.3, R15):**
  - By address, the busiest first, each with its scope, what reached it, the ports, those accepted and the records;
    Enter or a double click on one finds every process that reached it. The busiest five are named, or every one on
    request, and a newer generation keeps both, with the keyboard on its host.
  - Debug and Release each ran 2,661 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    three tests more list a process's other hosts, list a hundred of a crawler's 101 and carry them into a newer
    generation, and walk them in the window. All 27 mutations were caught: every process's hosts counted as the
    chosen one's, one process's kept for another, or listed beside a row the inspector describes; a host's scope,
    ports or accepted connections unsaid; the list never whole, or whole for every process once one asked; the note
    past a hundred miscounted, or said while the busiest five are named; the button offered for five hosts or fewer,
    its words never going back to the busiest or counting past a hundred wrong; a toggle taken where none is
    offered, or one never naming the busiest again; a host the list does not hold found anyway; the rows or the
    list's name not said again; a list asked for whole not carried over, or not taken up again; a row's name without
    its Enter; the relationships' note saying what icat lists wrongly; Enter not taken, or taken on the selected row
    rather than the one with the keyboard; a double click ignored; the keyboard left on the button; and the
    keyboard's host not found again in a newer generation.

- **Revision 672 — a newer generation keeps the keyboard on the relationship or pair it was on (R7, R15):**
  - While a recording publishes, the keyboard on a chosen process's relationship or a chosen program's pair stays on
    the row for the same process or program wherever the newer counts list it, rather than going to the list's first
    row (a probe's finding).
  - Debug and Release each ran 2,658 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    one test more publishes two generations under the keyboard, the first moving the relationship it is on and the
    second the pair. All six mutations were caught, three of them on a second run, as first written they did not
    build: the row the keyboard was on not looked for, or not remembered, and a relationship or a pair not matched
    at all, or taken for the list's first row.

- **Revision 671 — a hub's relationships and a busy program's pairs are listed whole on request (§6.3, R15):**
  - A button under each of the inspector's lists offers every relationship or pair, the busiest first, and then the
    busiest five again, the keyboard going to the first row it adds; past a hundred, a note names the icat command
    that lists them all. A recording's next generation keeps a list asked for whole.
  - Debug and Release each ran 2,657 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    two tests more list a hub's 101 relationships and pairs a hundred at a time, carried into a newer generation,
    and press the buttons in the window. All 29 mutations were caught: either list never listed whole, or whole for
    every process or program once one asked; the note past a hundred miscounted, or said while the busiest five are
    named; either button's words never going back to the busiest, or counting past a hundred wrong; a toggle taken
    where none is offered, or one never naming the busiest again; the rows or the button's words not said again once
    toggled, or the button not shown as its process or program is chosen; a list asked for whole not carried over,
    or not taken up again; and the keyboard left on the button, or put on the list's first row rather than the first
    it added.

- **Revision 670 — a newer generation keeps the way back along a walk (R7, §3.2, §6.3):**
  - A recording's next publication carries the hops Esc comes back along after Enter on a relationship or a pair of
    programs; a hop from a process or program the session no longer holds is let go, with the hops before it.
  - Debug and Release each ran 2,655 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    two tests more carry a walk of relationships and one of programs into a newer generation, the second in the
    window as a recording publishes it. All seven mutations were caught: either walk's hops not carried over, or not
    taken up again - the programs' caught in the window too - and either walk coming back along a hop from a process
    or program the session no longer holds.

- **Revision 669 — Enter on a chosen program's pair of programs chooses the program at its other end (§6.3, §6.7, R15):**
  - The inspector's relationships by program take the keyboard as a process's relationships do: Enter or a double
    click on a pair chooses the program at its other end, with its ranked row; the program's own pair goes nowhere;
    Esc comes back along the walk, the keyboard on the pair left by.
  - Esc letting the choice go with the keyboard in an inspector list gives the keyboard to the ranked table rather
    than to nothing, choosing no row (a live pass finding).
  - Debug and Release each ran 2,653 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    one test more checks that Esc comes back along a chosen program's pairs only on the rung each hop was made at.
    All 22 mutations were caught: the other end taken as the pair's second program, no pair said to be the program's
    own, its own pair chosen anyway, the ranked row left unchosen going or coming back, no hop kept or one never let
    go, the earliest hop taken for the latest, the selection or the rung left unchecked, only the latest hop
    forgotten where all are, the program come back to returned for the one left, the list's name not said again
    under a brush, a row's name without its Enter, Enter not taken, the keyboard not kept on the list, the selected
    row taken for the one with the keyboard, a double click ignored, Esc climbing the ladder anyway, the keyboard
    given back to the first pair or the first relationship rather than the one left by, and the keyboard left on
    nothing once Esc lets the choice go.

- **Revision 668 — Esc comes back along a walk of who connected to whom (§6.3, §6.7, R15):**
  - After Enter on a relationship chose the process at its other end, Esc chooses again the process it was chosen
    from, the keyboard on the relationship left by, before it climbs the ladder; a hop the selection or the ladder has
    since moved away from is forgotten.
  - Debug and Release each ran 2,652 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eight mutations were caught: no hop kept, the selection or the rung left unchecked, the earliest hop taken for
    the latest, the process come back to returned for the one left, only the latest hop forgotten where all are, Esc
    climbing the ladder anyway, and the keyboard not given back to the relationship left by.

- **Revision 667 — Enter on a chosen process's relationship chooses the process at its other end (§6.3, §6.7, R15):**
  - The inspector's relationships list is one the keyboard reaches a row at a time, each row read aloud whole; Enter
    or a double click on one chooses the process at its other end, whose own relationships the list then holds, the
    keyboard on them, so who connected to whom is walked a key at a time.
  - Debug and Release each ran 2,652 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    seven mutations were caught: a process's connection to itself chosen, the other end taken from the chosen
    process's side, Enter offered on a connection to itself, the list's name or binding without what Enter does, the
    keyboard not kept on the list, and Enter there unheard. A mutation of what a choice returns was left out as
    equivalent: choosing a listed process always takes.

- **Revision 666 — `icat overview`'s busiest processes say the connections each made and accepted (§5.2, R18):**
  - Its first table said what each process mostly did, nothing of who connected. Where the session records opens, a
    column says "41 made · 34 accepted" or "none", as `icat processes` counts them, and a note points to `icat
    processes --pid` for with whom and to `--rank-by connections-made` or `connections-accepted`.
  - Debug and Release each ran 2,652 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: the column shown where the session records no open, or never, none said as zeros,
    made and accepted swapped, and the note said only where no open is recorded.

- **Revision 665 — a chosen process's inspector names the processes it talked with (§6.3, §6.8):**
  - Chosen, a process said how many connections it made and accepted but not with whom. The inspector now lists its
    relationships, the busiest first, each by the process at its other end and marked from its side (← connected to
    it, → it connected, ⇄ both), with what it made and accepted, mechanism and records; of RPC calls, those it made or
    served.
  - Debug and Release each ran 2,651 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    sixteen mutations were caught: made counted from the other side, the quietest first, ties by key, the marks
    dropped, calls read as connections, calls served said made, who connected said the wrong way round, the rest
    uncounted, four named, the heading or the list left unraised, "(itself)" unsaid, and the window's list hidden or
    named without who connected; and on a second run the list shown under a connection described at the process's
    rung, which the first run missed until the test chose one there, and the brush's heading inverted, in place of a
    mutation that did not build.

- **Revision 664 — `icat processes` says what its list is ranked by, and ranks by `--rank-by` (§5.2, R18):**
  - The list was ordered by bytes without saying so. It now states its ranking, and `--rank-by` takes records,
    bytes-sent, bytes-received, bytes-sent-and-received, connections-made or connections-accepted, as `icat export`
    and `icat hosts` name them; a session that records no open ranks nothing by connections. `icat`'s help for `export
    --rank-by` names the connection rankings too.
  - Debug and Release each ran 2,650 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    fourteen mutations were caught: the ranking ignored, ties under either connection ranking or under bytes
    received going to the records or the bytes, ties under records reversed, bytes sent read as received,
    connections-made read as accepted, a ranking of one PID's detail or by connections where no open is recorded
    allowed, the field unsaid, said in the detail or in the other ranking's words, the JSON naming the default, and
    the help naming no connection ranking for an export.

- **Revision 663 — `icat processes --pid` says who connected to whom, end by end (§5.2, §7.4, R18):**
  - A process's detail counted the connections it made and accepted, but not with whom. Each process it exchanged data
    with now counts the connections it made to it and accepted from it, adding up to its own: itself named as itself,
    an end it sent nothing to listed, and the ends no record holds named "not in the capture (remote or unobserved)";
    the JSON counts them, null where the session records no open.
  - Debug and Release each ran 2,649 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Sixteen of seventeen mutations were caught: a connection it made counted as accepted, those it accepted
    uncounted, one to itself counted once, those whose other end no record holds dropped or taken from every
    process, an end it sent nothing to left out, "(itself)" said of the other ends, the column shown where nothing
    was seen open, "-" explained where no row has one, the old label kept, counts given or read where the session
    records no open, and the policy's admission or the seen opening dropped from either kind of connection's count.
    The seventeenth, counting a one-sided UDP flow's opening, is equivalent: no UDP descriptor the capture admits
    connects or accepts, so a flow's opening is never seen.

- **Revision 662 — `icat processes --pid` points to a process's RPC calls (§7.4, R18):**
  - An RPC caller's detail said only that it made no transport record. It now states its RPC call records and that
    `icat operations --pid` lists each call with the process at its other end; the JSON counts them (`rpcRecords`).
  - Debug and Release each ran 2,648 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    three mutations were caught: RPC records counted as ALPC's, the RPC line never said, and said for a process that
    made no call.

- **Revision 661 — an RPC relationship says which process called which (§6.3, §7.4):**
  - The overview's RPC links paired two processes in id order and dropped the caller, so an RPC edge read "display
    order only". Each link total now counts each end's calls (overview index minor 6), and the edge's arrowhead, card,
    table row and spoken name, `icat overview` and `icat programs` name the caller first: "caller.exe · PID 400 called
    services.exe · PID 1960". An older overview's links are followed again rather than used.
  - Debug and Release each ran 2,647 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    fifteen mutations were caught: the server counted as caller, an edge forgetting its callers or swapping its
    ends, the second's calls written as the first's, the callers left unread, a link of no call or of more calls
    than records accepted, minor 5 written, the caller named second, all of its calls uncounted, calls said as
    connections, a row read aloud as TCP, the overview's note without calls, and links kept without callers used, or
    any count taken as one.

- **Revision 660 — a process a search finds says what tells it apart (§6.7):**
  - Searching svchost listed fifty hits reading "Process instance of svchost.exe". Each now says its relationships and
    the connections it made and accepted, its program only where its name does not say it.
  - Debug and Release each ran 2,646 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    nine mutations were caught: a program said always or never, a detail begun in lower case, a process connected to
    itself counted twice, a relationship's target uncounted, connections counted where no open is recorded, both
    counts said as made, accepted ones unsaid, and what reached an address unsaid.

- **Revision 659 — an edge's card is titled by who connected, as its row is (§6.3, §6.8):**
  - Hovering an edge titled its card with its ends in display order joined by ↔, while the inspector named the chosen
    relationship maker first. Cards now name the maker first with → or ⇄, and ↔ only where none was seen open, edges
    to the other hosts too.
  - Debug and Release each ran 2,645 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: a maker at the edge's target named second, both ways titled as one, an arrow where
    none was seen open, and an edge's or an edge to the other hosts' card titled in display order again.

- **Revision 658 — `icat programs` lists every pair of programs, or one program's (§6.3, R18):**
  - The overview names the five busiest pairs and the window a chosen program's; nothing listed the rest. `icat
    programs <directory> [--program <name>] [--limit <n>] [--json]` does, the program named as listed, by its file
    name with or without its extension, or by its path; a file name two programs share is refused, naming both.
  - Debug and Release each ran 2,644 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    fourteen mutations were caught: no program matched by its name as listed, by its file name, without its
    extension or by its path, a pair matched by its first program alone, a row too many in the table or the JSON,
    the more line said at the limit, a program's own processes unsaid or itself counted among the others, the first
    of two named programs taken, the overview's pointer dropped, and the command neither dispatched nor in icat's
    help.

- **Revision 657 — a chosen program names the programs its processes talked with (§6.3, §6.8):**
  - On the heavy machine, choosing svchost.exe said its processes made 62 connections and accepted 55, not with whom.
    The inspector now lists its five busiest pairs of programs, maker first, each over its relationships and records -
    svchost.exe → svchost.exe, svchost.exe → lsass.exe, chrome.exe → svchost.exe and on - and how many more, the whole
    session's under a brush.
  - Debug and Release each ran 2,641 tests with revision 473's 93 failures and 4 skips, and nothing else failed, the
    cut-off text test now measuring the new section with a program chosen. All nine mutations were caught: session
    groups named as programs, a pair matched by its first program alone, four pairs named, one more program left
    unsaid, the heading's whole session said without a brush, the list's and the heading's changes unheard, arrows
    read aloud, and the section shown with nothing chosen. A tenth, the heading never saying whole session, did not
    build; its replacement, saying it only without a brush, was caught.

- **Revision 656 — `icat overview` lists the busiest pairs of programs (§6.3, R18):**
  - On the heavy machine four of its five busiest relationships were chrome.exe's own processes. It now also sums them
    by the two programs at their ends, maker first, with their relationships, processes, mechanisms and records -
    svchost.exe → svchost.exe, chrome.exe → chrome.exe, msedge.exe → msedge.exe, Code.exe → node.exe, svchost.exe →
    lsass.exe - wherever two programs share more than one relationship.
  - Debug and Release each ran 2,640 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    nine mutations were caught: the maker not named first, no tie broken by name, a program's own connections split
    as two ways, fewer relationships ranked first, a target's process uncounted, a connection counted in drawn
    order, both ways read as one, the table listed where each pair shares one relationship, and only its first row
    listed.

- **Revision 655 — the investigation's notes and candidate joins say why they list none (§8.3):**
  - A fresh investigation's Notes tab and, before a search, its Candidate joins tab showed blank boxes. Each now says
    in its place what it will hold and how: "No note yet. Add a note… writes one…", and what Find candidate joins
    lists there.
  - Debug and Release each ran 2,637 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All three mutations were caught: the notes list's
    sentence never hidden or never shown, and the candidate list's kept after a search.

- **Revision 654 — the inspector's headings wrap (§6.8):**
  - With a group chosen, "How its processes are grouped" was cut to "…are groupec" at the inspector's edge: the
    headings that read the selection did not wrap. They do now, and the minimum-window cut-off test checks each rung
    with its row chosen, where those headings show, as well as opened.
  - Debug and Release each ran 2,637 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. The test of cut-off text failed before the headings
    wrapped, on "How its processes are grouped", and its one mutation, that heading unwrapped again, was caught.

- **Revision 653 — Other hosts' list and card name who reached each host (§6.3, R5):**
  - The inspector listed the heavy machine's 97 hosts each "from 1 process". Each host listed or named on the card now
    says who reached it as `icat hosts` does, "5 UDP flows from msedge.exe · PID 3400 and 4 more", from one helper
    both read; one drawn process's hosts still name none.
  - Debug and Release each ran 2,637 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All six mutations were caught: a process that reached
    a host over two transports counted twice, the quietest named first, how many more left out, the list's rows and
    the card's lines counting the processes again, and one drawn process's hosts naming it.

- **Revision 652 — at a process's rung a peer of its own program is named by its PID (§7.4):**
  - At chrome.exe's busiest process, 34 of whose 36 peers are chrome.exe's, the rail cut every row before the PID:
    each read "← chrome.exe · Pl…". Such a peer now reads "← PID 3028", as the graph names it at that rung; another
    program's keeps its whole name, and every peer is read aloud whole.
  - Debug and Release each ran 2,637 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All three mutations were caught: a same-program peer
    named whole, every peer named by its PID, and a peer read aloud by its PID alone.

- **Revision 651 — the relationship table names each relationship's maker first (§6.8, R5):**
  - On the heavy machine one program's processes each connecting to its browser process read two ways row by row,
    "chrome.exe · PID 3000 ← chrome.exe · PID 3028" above "chrome.exe · PID 3024 → chrome.exe · PID 3000", as their
    ids sorted. A relationship only one end made connections of now names that end first, its mark pointing on, as
    `icat overview` names it, in its row, the inspector's title and the timeline's highlight.
  - Debug and Release each ran 2,636 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All four mutations were caught: the maker never named
    first, a relationship made each way turned about too, the mark left pointing back, and the names left in the
    display order.

- **Revision 650 — a paired channel is named by its two processes and which connected to which (§7.1, R5):**
  - `icat channels` printed each channel as its key and endpoints alone, and the window's channel list joined its two
    processes by "↔" whichever made it. Both now read "msedge.exe · PID 3448 → msedge.exe · PID 3400 · TCP · :50113 →
    :9223 on 127.0.0.1", or "and" where neither was seen opening it; a page names its channels' processes itself
    (`processNames`), and the command line puts each key on a line of its own.
  - Debug and Release each ran 2,636 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All fourteen mutations were caught: the maker never
    led, nor any channel read as made, its endpoints or names left in the name's order, the arrow between endpoints
    dropped, an unknown end called unlisted, the transport left out, a reused PID's holder numbered as the first or
    not at all, a page naming no end or only the first, and the key or the page's names left out of icat channels
    and the window's list.

- **Revision 649 — `icat processes --pid` lists the other hosts an instance reached (§6.3, R18):**
  - Its Exchanged with table folded every end with no process of the capture into one row: chrome.exe's 673,817 B to
    41 hosts read as one "unresolved" end. Beneath it the instance's other hosts are now listed by address, as `icat
    hosts` lists them, over its own connections: connections, ports, those accepted, records and bytes each way, the
    most bytes first, twenty and how many more; the JSON gives every one (`otherHosts`).
  - Debug and Release each ran 2,634 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All eleven mutations were caught: the hosts not
    listed or not read, another process's connections counted, the hosts ranked by records, every host listed or the
    rest not counted, the accepted connections, ports, scope or sent bytes left out or misread, and the note on who
    reached an address pointing where it did.

- **Revision 648 — the command line names who reached each other host (§6.3, R18):**
  - A live pass with `icat` over the heavy machine's session: `icat hosts` counted the processes that reached each of
    its 97 hosts and named none. Each host now names the process whose connections there hold the most records and how
    many more reached it - "msedge.exe · PID 3400 and 4 more" - in `icat hosts` and `icat overview` alike, and the
    JSON lists them all (`other-hosts-v7`).
  - `icat hosts --rank-by` listed a host a process accepted a connection from with the host's own port among its Ports
    and no Accepted here count: a host read for its bytes is now counted as the census counts it.
  - Debug and Release each ran 2,633 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All fifteen mutations were caught: a host read for
    its bytes counting an accepted connection's port among its ports, or not counting its connections made, its
    connections accepted or their ports, or taking those ports at the host's end, in the traffic's test and in icat
    hosts; a process that reached a host over two transports counted twice, the quietest named first, how many more
    left out or miscounted, the connections counting the processes again in icat hosts and icat overview, the JSON
    listing none, and the Processes column left blank in either.

- **Revision 647 — a graph names each program it draws before it names one twice (§6.3):**
  - At the default window the heavy machine named Code.exe and node.exe eight times each and left lsass.exe,
    services.exe and six more programs unnamed. A process whose name an aggregate or a busier process bears is now
    named after every process first to bear its own: 16 of the 19 programs are named, where 11 were.
  - Debug and Release each ran 2,631 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All four mutations were caught: processes ranked
    whatever their names repeat, an aggregate's name not counting, repeats ranked first, and repeats never named.

- **Revision 646 — an opened group's own processes are named by their PIDs (§6.3):**
  - Opened on the heavy machine, chrome.exe's every name read "chrome.exe", its rung's title already, where its table
    names each process "PID 3012". An opened group's own processes are now named by their PIDs, at its rung and a
    process's below it, with no second line repeating it; its peers keep their programs' names.
  - Debug and Release each ran 2,630 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint on every rung still allocating nothing of its own. All four mutations were caught: an opened group's
    processes named by their program again, every process named by its PID, a PID's second line under a name that is
    its PID, and the halo laid round the program's name under a PID's letters.

- **Revision 645 — Other hosts is named before the aggregates round it (§6.3):**
  - A live pass over a heavy machine shaped as one is - system hubs, two browsers, an editor and its servers, a
    database and its users, 255 processes - opened in 0.16 s and drew 72 nodes with no disc over another. At the
    smallest window Other hosts went unnamed, the aggregates round it having taken each slot first; it is now named
    before them, and every aggregate still is.
  - Debug and Release each ran 2,629 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    three mutations were caught: Other hosts ranked with the aggregates again, processes named before aggregates,
    and processes left unranked.

- **Revision 644 — an edge passing beneath a name breaks round its letters' corners too (§6.3):**
  - At 1920 × 1080 an edge two pixels above "chrome.exe" showed through the halo by nearly half at a letter's corner,
    where one halo copy alone covers the diagonal pixel. The four diagonal copies are laid twice, and the fleck shows
    a fifth of the ink; the window test checks the letters at 1920 × 1080 and 1456 × 939.
  - Debug and Release each ran 2,628 tests with revision 473's 93 failures and 4 skips, and nothing else failed, a
    repaint still allocating nothing of its own. Both mutations were caught: the corners laid once, or the sides
    laid twice in their place, leave the edge showing through beside a letter at 1920 × 1080.

- **Revision 643 — the graph's display budget is what the design pane holds (§6.3):**
  - On wide machines of 80 programs related at random, 200 nodes left up to 235 pairs of discs overlapping at the
    design pane, by up to 25 px, a disc's centre under another's; at 100 a few touched by at most 5 px and none hid
    another. The default budget is now 100 nodes: a busy machine past it folds more groups, each counted and opened by
    Enter, and the tables and search still list every process.
  - Debug and Release each ran 2,628 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Both mutations were caught: the budget back at 200 nodes, or at 120, leaves a wide machine's disc over another's
    centre at the design pane.

- **Revision 642 — a lone component is drawn about the pane's middle (§19.4):**
  - The seeding grid gave a lone component two columns on the wide pane and started it in the first, and grown at
    angles its hashes fix it leaned further; relaxation's weak pull across never brought it back, so dense drawings
    sat with their middles a quarter to a third of the way across. A short row of the grid is now centred and a
    component, once grown, is set back across on its cell's middle: the same drawings sit within 0.02 of the middle.
    The busy machine is named whole at 1456 × 939, and two window tests of its names check each rule where its premise
    holds; an edge's ink showing through a name's halo at 1920 × 1080, found on the way, is left for its own revision.
  - Debug and Release each ran 2,627 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: a short row's components started at its left or at its right; a component left
    leaning, or set back by its rightmost node rather than its middle; and revision 641's spread grown from the
    drawing's left edge rather than its middle, which a centred drawing now shows. Two mutations of a first draft
    went unnoticed, and the code they named went with them: no more columns than components, which a centred short
    row already gives a lone one, and a component set back down as well as across, which relaxation's pull down
    already does.

- **Revision 641 — a dense graph's discs stand apart (§6.3, §19.4):**
  - A live pass over a wide machine, 253 processes of 80 programs, found its machine rung drawn in the left half of
    the pane with 846 pairs of discs overlapping, some out of the pointer's reach, and an opened group's 61 nodes
    overlapping in 23. The layout now parts what relaxation leaves too near: the drawing widens about its middle as
    far as its discs need room, then each pair still too near is pushed apart. The group's discs all stand clear, and
    up to 100 nodes none overlap; at 199, more than the pane holds, 212 pairs still do.
  - Debug and Release each ran 2,625 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Thirteen of sixteen mutations were caught: no separation, or one parting the first frame; a pin giving way; a
    held node giving way wholly or not at all, ignoring a new drawing's freedom, or spread wholly; no spread, or one
    without room to spare; no pushes, or only ten rounds; pushes to touching rather than the clearance, or moving
    one disc of a pair. Three went unnoticed, none changing what the tests hold: a spread half a square wider; one
    grown from the drawing's left edge rather than its middle, the same while the seeding leaves a lone component
    against that edge, the pass's other finding; and a spread tried where no two discs are too near, where it found
    room enough on every drawing measured.

- **Revision 640 — a range of the investigation's time states both ends alike (§8.2, §8.4):**
  - A live pass over the investigation window at its smallest found its pages sound, and a lane saying its records ran
    "from 0.0001 s to 0.000103 s", each end to its own precision, as icat workspace timeline's interval did. A range
    now states both ends to the finer end's precision, at least milliseconds.
  - Debug and Release each ran 2,621 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    six mutations were caught: a range stated to its start's precision or its end's alone, or below milliseconds;
    and a lane's sentence, icat workspace timeline's interval or a saved view's confirmation stating its ends apart
    again.

- **Revision 639 — the breadcrumb up lands where Esc does (§3.2, §6.7):**
  - A crumb up chose no row, and left the deeper rungs' process selected at the machine rung. It now chooses the row
    the rung was left with - the row the way down went through - once the rung lists it, after an aggregate's list or
    a relationship a way down began at, which are chosen again first.
  - Debug and Release each ran 2,613 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    test of forward history across a publication now expects nothing said of the process a crumb's view had kept
    selected, the crumb having chosen its group. All six mutations were caught: a crumb up choosing no row, or
    choosing it before an aggregate's list or a relationship a way down began at, or over either; and either way
    back taken but not said to have been.

- **Revision 638 — every rung comes back with its row, by Esc and by forward alike (§3.2, §6.7):**
  - Forward to a rung other than E chose no row, and Esc back from E taken from a row a process rung reads after it is
    shown - a connection to another host - came back to none. A rung now records the row it is left with, the row a
    way down goes through included, and forward and the way back both choose it again, once the rung lists it where it
    reads its rows later.
  - Debug and Release each ran 2,612 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eight mutations were caught: forward choosing no row on a rung other than E, or none it reads later, or one not
    described as it was; a way down recording no row, or the row chosen rather than the one it goes through; Esc not
    waiting for a row read later; a rung left before reading its rows losing the row it was still to choose; and the
    ladder recording no row.

- **Revision 637 — Page Down and Page Up move the keyboard through a list; a path wraps between its folders (§6.7, §6.8, R15):**
  - A live pass at the smallest window found Page Down and Page Up only scrolling a list's rows, the keyboard left on
    a row out of sight, and the inspector wrapping svchost.exe's path with "C:" alone on a line. Every list now pages
    as a list on Windows does, choosing the row it comes to; a path wraps after a backslash, never after its drive's
    colon.
  - Debug and Release each ran 2,610 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twelve mutations were caught: the lists' paging not taken, choosing nothing, leaving the keyboard or the row out
    of sight, going a page past the last or first row shown, paging a whole page, or taking a modifier's key; and a
    drive's colon not joined to its backslash, no break after a backslash - in the text and in the window - or the
    inspector not breaking its path at all.

- **Revision 636 — at the smallest window, crowded graph names keep a word's space (§6.3):**
  - A live pass over the busy machine at 1080 × 700 in the light and high-contrast themes found two graph names
    standing edge to edge at the machine rung, reading as one word - "svchost.exems-teams.exe". A name standing nearer
    another than names keep where there is room now still keeps a word's space across and a pixel down from it, or
    goes unnamed.
  - Debug and Release each ran 2,602 tests with revision 473's 93 failures and 4 skips, and nothing else failed;
    revision 612's test of a pane too narrow for two names side by side now expects the second unnamed. All six
    mutations were caught: crowded names keeping no room, none across or none down - the window's own check among
    them - and the slots a name may take, or must, nearer the names than they keep, only clear of them.

- **Revision 635 — forward along a way down and back again comes where the first way came (§3.2, §6.7):**
  - Esc gave the keyboard back to a record opened from a cell's list only once, and Alt+Right to E chose no record.
    The way back now holds until a way down of its own, as the other ways back do, and forward to E chooses the record
    chosen there when it was left; a record opened and left before E listed it is no longer chosen by a later E.
  - Debug and Release each ran 2,600 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    ten mutations were caught: the way back spent when taken, kept for another rung at its depth, or that rung not
    checked; forward to E choosing no record; an ascent or the breadcrumb leaving E without recording its record;
    the record E was still to choose not recorded, or outliving E; and the ladder recording no record, or recording
    only one already recorded.

- **Revision 634 — the way back from a moment the interval table lists gives the keyboard back to its row (§3.2, R15):**
  - A live pass stepping and resizing by keyboard on the busy machine found that Enter on an interval-table row, which
    lists its moment's records in E, came back by Esc with the keyboard on the ranked table. The way back - Esc, the
    breadcrumb, or forward and back - now gives it to the row again where the tables are shown and the row is still
    the analysis interval's.
  - Debug and Release each ran 2,600 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twenty mutations were caught: Enter or E in the interval table listing the moment as E from elsewhere does; the
    window not answering the ask, or not giving the row the keyboard; the way down not kept; Esc or the breadcrumb
    not asking; the tables hidden on the way back asking; the way back spent when taken, kept for another rung at
    its depth, or that rung not checked; hidden tables keeping the ask; the rows listed again not offering it,
    offering it for another row, or asking again; a navigation or another interval keeping the ask; a way down of
    its own keeping the way back; and a newer generation not carrying or not restoring it.

- **Revision 633 — a cell a step or the interval table chose stays the cell when the timeline cuts its columns afresh (§6.8):**
  - A step or an interval-table row chooses a column explained as a cell of the lane it follows, and a resize or zoom
    turned it into a time range. Each rung now keeps the cell the analysis interval is, as its lane drew it last,
    however it was chosen, while that lane and that interval stand.
  - Debug and Release each ran 2,598 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    six mutations were caught: no cell kept before the timeline is counted afresh, or when a click chooses one; a
    kept cell standing for another lane or another interval; the recount a navigation asks for keeping one; and a
    way down letting the rung it left forget its own.

- **Revision 632 — live pass over timeline cells, their columns and the ways back on the busy machine (§6.8, §3.2, §6.4):**
  - A wider or narrower window cuts the timeline's columns afresh, and a clicked cell held as revision 629 meant. Back
    from E taken from a group's process-lane cell, the way back no longer chooses that process's row in the cell's
    place; a kept cell's column reads plainly; and E with nothing selected no longer shows an evidence line saying so.
  - Debug and Release each ran 2,597 tests. Release had revision 473's 93 failures and 4 skips and nothing else;
    Debug had one more, the journal allowance test, whose follow stalled once under the suite's load and did not
    again in 21 runs alone, with its assembly or under full CPU load, so its assertion now says why a follow stalls.
    All eight mutations were caught: the way back choosing the lane's process's row, E from a cell left unmarked or
    marked when a selection led, a kept cell's column worded as before or without saying the timeline no longer
    draws it, and the evidence line always shown, its change unsaid, or unbound.

- **Revision 631 — the way back from a record opened from a cell's list gives the keyboard back to it (§3.2, §6.4, R15):**
  - Enter on a record listed beside a cell opened E with the keyboard on it, and Esc came back with the keyboard on
    the ranked table. The way back, by Esc or the breadcrumb, now gives it to that record once the cell's records are
    listed again, so the next is a key away; a newer generation keeps the way back, and another interval, a navigation
    or a way down of its own lets it go.
  - Debug and Release each ran 2,596 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    thirteen mutations were caught: no way back kept from the list; none taken by Esc or the breadcrumb; one kept
    past a way down of its own, a navigation or another interval; one given up before the records are wanted or
    while they are read, or never offered once they are; a newer generation not given it or not restoring it; and
    the window neither focusing the record nor choosing it.

- **Revision 630 — the way back to a clicked timeline cell chooses it again (§3.2, §6.8):**
  - Esc or the breadcrumb back from E, a listed record or Enter brought back the cell's interval but not the cell: the
    inspector explained another lane's cell or none, and E lost the lane's filter. A rung left with a cell chosen now
    keeps the way back to it - Esc, the breadcrumb and forward choose it again - taken when used and kept by a newer
    generation. Revision 629's kept cell now stands in only while its lane is drawn, which also keeps E from failing
    on a channel end's cell before the rung's lanes are counted again.
  - Debug and Release each ran 2,594 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    seventeen mutations were caught: no way back kept on a way down, an ascent, the breadcrumb or forward; none
    taken by Esc, the breadcrumb, the crumb of the rung shown or forward; one taken at another rung at its depth, or
    left to be taken again; one kept past a way down of its own; the cell chosen again unsaid, or its timeline name
    unsaid; a newer generation not given the ways back, not restoring them, or keeping none for the cell it
    restored; and a kept cell standing in while its lane is not drawn.

- **Revision 629 — a clicked timeline cell stays the cell when the timeline cuts its columns afresh (§6.8, §6.4):**
  - A wider or narrower window, or another zoom, counts the view in other columns; a clicked cell none of them is lost
    its explanation, was titled a time range, listed no records, and E lost its lane's filter. The cell is now kept as
    drawn when chosen until another interval, lane or rung is chosen, and explained in the columns it was chosen in.
  - Debug and Release each ran 2,590 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: no kept cell to fall back on, none kept when chosen, one kept once another interval
    was chosen, one kept while the timeline draws its column, and the kept cell described in the columns drawn now.

- **Revision 628 — the way back from Enter on an aggregate's process chooses its edge to other hosts again (§3.2, R15):**
  - E on a process chosen in an aggregate's list kept the way back to the list and to the aggregate's edge chosen in
    the table; Enter on it kept only the list's, so Esc back to the machine left the table's row unchosen. Enter now
    keeps both, as E does.
  - Debug and Release each ran 2,589 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Both mutations were caught: Enter keeping no way back to the edge's row, and taking it at the process's rung it
    opened rather than where it began.

- **Revision 627 — live pass over exporting from the window; the Rank by box names its metric whole (§6.4, R15):**
  - Exports at the machine, group, process and evidence rungs of the busy machine held what each rung listed, and the
    card said how many rows or records and whether complete. The Rank by box, beside its label, had 69 pixels for a
    name: "Bytes sent and received" read "Bytes sent …", and Connections made and accepted, or RPC calls made and
    served, read alike. Its label and basis now stand above it and it takes the rail's width, so every name shows
    whole at the smallest window; Group by follows, the boxes still in line.
  - Debug and Release each ran 2,589 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    window's 454 tests passed with the rail's new rows, the smallest window's ranked list still keeping two rows and
    the start of a third. Both meaningful mutations were caught: the Rank by box held to its old width, and the
    Group by box no longer spanning the rail; a third, a margin that left the basis above the box, changed nothing
    and went.

- **Revision 626 — a newer generation keeps a channel's rung whole (§6.4, R7):**
  - An RPC channel's calls and a process's HTTP exchanges were listed afresh by each publication, so the list went
    empty every couple of seconds until read, the chosen call coming back only then. The last publication's rows for
    the same channel and scope now stay listed until this one's first page replaces them, the chosen one chosen at
    once; another scope's rows are never handed on, and a failed read drops them with the reason.
  - Debug and Release each ran 2,588 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    18 mutations were caught: a publication carrying no channel list or taking none on; carrying no calls or no
    exchanges, or another scope's while that scope's are read, or none of an earlier publication's still unread;
    taking on no calls or no exchanges, appending its own first page to them, not saying the rows changed, not
    marking them as taken on, or listing them with no channel summary.

- **Revision 625 — an RPC channel's and HTTP exchanges' rung export their calls and exchanges (§6.4, R18):**
  - Both rungs list calls or exchanges a page at a time but exported nothing, saying "This rung lists no row", and
    `icat export --at` could not reach them. Their export now holds every call or exchange of the scope, read in one
    pass up to the evidence limit, each with its session time and how it went, culture-invariant; `icat export --at
    <channel key>` reaches the rung and writes the same file, `--limit` bounds the calls or exchanges too, and an
    export it cut short says so. A rung with none listed yet, none in scope or an unreadable read says which.
  - Debug and Release each ran 2,587 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    40 mutations were caught: the window exporting a channel's rung as ranked rows, offering or writing an export of
    calls it could not read, not saying what its limit left out, ignoring that limit or the brushed interval; the
    export offered with none listed or read, or for neither rung; its tip calling calls exchanges, saying none are
    in scope before they are read, or silent on a failed read; icat export not descending to the channel, writing
    its ranked rows, ignoring --limit or the interval, not saying what the limit left out, or refusing --limit
    without --evidence; the builder listing no HTTP rung, its reads always complete, its counts unagreed or all
    calls, its context never incomplete or silent; a call's duration where it has none, its procedure, missing
    status, failure, other end, records or start time; an exchange's unrecorded end or its parts; and the one-pass
    reads ignoring their limit or interval.

- **Revision 624 — a process's rung exports every row it lists (§6.4, R18):**
  - Its export held the paired channels alone: none of the connections to other hosts, RPC channels or HTTP exchanges
    listed beside them, and a process whose rows are all connections said it had nothing to export. The window and
    `icat export` now build the same rows through one builder (`ProcessRungRows`) and write them in the rung's order,
    byte for byte and culture-invariant; an export waits for rows still being read, and refuses, saying why, when they
    could not be read. Open: an RPC channel's or HTTP exchanges' rung still exports nothing (revision 625).
  - Debug and Release each ran 2,582 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    39 mutations were caught, one after a test added for it: the window's export holding the channels alone, or none
    of the connections, RPC channels or HTTP exchanges; the connections, or the RPC and HTTP rows, unranked by
    bytes; the words in the reader's culture, here and in icat export's; the ranking named by the channels alone; an
    export not waiting for each kind of row it reads, or giving up at once; one written or offered, or its tip
    silent, when a kind could not be read; icat export reading its rows over the whole session in an interval, none
    of its RPC or HTTP rows, no datagram flow, unranked, or naming its ranking by the channels; and the builder's
    known bytes, datagram wording, ranking, RPC side and other end, HTTP wording and order.

- **Revision 623 — a process's rung counts and ranks its connections to other hosts by bytes (§6.1, R21):**
  - A live pass over a recording's newer generations found the byte ranking's note at a process's rung summing its
    paired channels alone: "800 B on 2 sends" beside a 1.4 KB connection, and "No sends in scope" for a process whose
    only row, a connection to another host, sent 200 B. It now counts every row the rung ranks and is restated once
    the connections are read. "Bytes sent and received" now ranks connections too, rather than the rung falling back
    to records under byte figures; "1 send" replaces "1 sends", and no zero is stated where nothing measured one.
    Open: the rung's export holds its channels alone (revision 624).
  - Debug and Release each ran 2,575 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    17 mutations were caught: the note leaving the connections out; a connection unranked by bytes sent and
    received, ranked where nothing measured, by its sends alone, or with no transfer unmeasured; "1 sends", "with no
    size" and "none with a size" swapped either way, "0 B on 0 measured sends" or "no record" stated where nothing
    measured, "1 measured sends"; the note or its detail not restated once the connections are read; and the
    definition not naming them.

- **Revision 622 — a newer generation keeps a process's rung whole (§6.4, R7):**
  - A process's connections, RPC channels and HTTP exchanges were read afresh by each publication, so its rows to
    other hosts vanished every couple of seconds until read, and the one chosen among them was lost, the inspector
    going back to the process. The last publication's rows for the same process and scope are now listed until this
    one's are read, and a row chosen there - or a call on its channel's rung - is chosen again once listed, described
    as it was.
  - Debug and Release each ran 2,574 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Of
    13 mutations 12 were caught: a publication carrying none of its rung's rows, or taking on none of its
    connections, RPC channels or HTTP exchanges; a restored row never chosen once listed, or chosen only at a
    process's rung and not as a call on its channel's; the row described where its process had been chosen again
    over it; and the row chosen after a choice made meanwhile - the selection cleared, another row or process
    chosen, a node chosen in the graph - or on a rung entered again. The one left, the adoption saying the rows
    changed itself, survived because the ranking restated after it says so, so it went.

- **Revision 621 — a live publication keeps Other hosts' list in its byte ranking (§6.3, §6.4):**
  - Under a byte ranking the list ranked its hosts by records until each publication had read their bytes, so its rows
    moved to records order and back every couple of seconds. The last publication's bytes now rank it, for the same
    byte ranking, said to be updating in the heading and aloud per row, until this one's are in; a failed read lets
    them go.
  - Debug and Release each ran 2,571 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    ten mutations were caught: no host traffic standing in, or one for another byte ranking; a stand-in unmarked in
    the heading, its rows or aloud; a publication carrying none of the traffic, or not that it was itself standing
    in, taking none on, or not saying its list changed when it does; and the last one's carried on after this
    publication's own read failed.

- **Revision 620 — a live publication keeps the interval table's bytes until it has read its own (§6.4, R15):**
  - Each publication read every listed interval's bytes again, its rows saying "reading bytes…" meanwhile. The
    timeline's columns keep their places as a live extent grows, so the bytes the last publication read for the same
    records over exactly an interval now stand in, marked updating on screen and aloud; a new interval or another
    lane's rows are read afresh, and a failed read lets none remain.
  - Debug and Release each ran 2,570 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eleven mutations were caught: no interval's bytes standing in, one standing in with no read under way, or for
    another lane's records; a stand-in unmarked in its row, aloud or in the caption; a publication carrying none of
    the interval bytes, or not those it was itself standing in, taking none on, or not restating its rows when it
    does; and the last one's carried on after this publication's own read failed.
