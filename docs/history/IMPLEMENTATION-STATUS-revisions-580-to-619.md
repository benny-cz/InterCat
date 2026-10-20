# InterCat implementation status — historical log, plan revisions 580 to 619

Archived on 2026-10-20, at plan revision 646, whole and in order. The current resume point is
[the implementation status](../IMPLEMENTATION-STATUS.md); revisions 500 to 579 are in
[the fourth log](IMPLEMENTATION-STATUS-revisions-500-to-579.md), revisions 230 to 499 in
[the third](IMPLEMENTATION-STATUS-revisions-230-to-499.md), revisions 104 to 229 in
[the second](IMPLEMENTATION-STATUS-revisions-104-to-229.md), and the chronology before them in
[the earliest log](IMPLEMENTATION-STATUS-through-revision-103.md).

## Slices, newest first

- **Revision 619 — a publication reads its bytes once (R11):**
  - Ranked by bytes with the table or a selection shown, each live publication read the whole session's bytes twice,
    for the ranking and for the table and card, and read the machine rung's edge traffic and interval bytes before
    re-entering the rung shown. A description now reads through the ranking's read, a ranking takes bytes a
    description read, and a restored view shows its tables once its rung and ranking are back.
  - Debug and Release each ran 2,569 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: a description reading its own bytes under a byte ranking; a restored view showing
    its tables before its rung and ranking are back; a byte ranking reading again what a description had read, or
    taking the last publication's bytes a description stated as its own; and a description left unread when a change
    of ranking cancelled the read it shared.

- **Revision 618 — a failed read lets the last publication's stand-in go (§6.4, R21):**
  - A ranking's measures from the publication before stayed once this one's read of them failed: the rows ranked by
    them, the note said "updating" for good on a capture's last publication, and an export waited and refused. The
    rows now rank by records and the note says why; an export names that ranking; nothing unread is carried on, and
    the table's edge traffic and a host's count follow the same rule.
  - Debug and Release each ran 2,568 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: the whole session's stand-in kept once its read failed, never said to have stood in,
    or the rows not ranked again without it; and the table's edge traffic or a host's count carried on after this
    publication's own read of it failed.

- **Revision 617 — a live publication keeps the table's and card's bytes until it has read its own (§6.4, R15, R21):**
  - Each publication read again what the relationship table's rows, the card, a channel's total and a mark's card
    state, and all said "reading bytes…" meanwhile, every two seconds. The last publication's bytes, edge traffic and
    host count now stand in, for the same scope, marked updating on screen and aloud, until its own arrive; a read
    that fails lets none remain.
  - Found on the way: a newer generation turned a channel chosen among a process's rows back into its process, at
    every live publication; the navigation carried keeps the row described.
  - Debug and Release each ran 2,567 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Of
    38 mutations 36 were caught: no stand-in, one standing in once a read failed, or the whole session's for an
    interval; a stand-in unmarked in a row, aloud, in the caption, the card, a channel's total, a mark's card, an
    edge's explanation or a host's count; a publication carrying none of its bytes, edge traffic or host count, or
    not those it was itself standing in, or taking none on, or not restating the table when it does; a mark's card
    calling the bytes unread meanwhile; edge traffic standing in for none, or after its read failed; a host's count
    standing in for none, or for another host; a described row not kept, described again once its process was chosen
    again, or never; and the caption not said again as the edges' traffic arrives, which a test of its own now
    catches. One, the adoption dropping the table's cached rows, survived because the restatement after it rebuilds
    them, so the reset went. The one left, the restore highlighting a described row itself, survived because the
    read the restore starts re-highlights it through the table's choice; it stays so the restore leans on no read.

- **Revision 616 — an edge to other hosts chosen in the table is its own records (§6.4, I5, R15):**
  - E on such a row listed its source's every record. A host's scope now names several addresses and the processes
    whose connections ran to them; E lists exactly the row's connections over its transport, a filter the breadcrumb
    can remove, the timeline highlights them and the card counts them.
  - Found on the way: removing a lane's mechanism, direction or end filter, or an RPC call's, at the evidence rung
    left the narrower list, since the rung compared scopes without their narrowing; it compares them whole.
  - Debug and Release each ran 2,563 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twenty-eight mutations were caught: an edge's key unsorted, with an address twice or untrimmed, allowed no
    address or one holding its separator, or naming no processes; its addresses read with its processes, its
    processes read as none, several hosts named by the first, or read by the first alone; a scope ignoring its
    processes, keeping them in its channel key, or reading processes gone without a word; several hosts named by
    their addresses in an interval's bytes, an evidence page's error or the timeline's; E listing the source's
    records, any transport's or any process's; the highlight the source's, any transport's, or not updated as the
    row is chosen; the card's heading or count the source's; and an evidence rung keeping a list narrowed by a
    mechanism, direction, end or call its filters no longer name.

- **Revision 615 — the relationship table says what each edge to other hosts carried (R15, R21):**
  - Its rows said their bytes were "not counted", though their connections' records measured them. Once the table
    lists them in a session whose bytes are read, each says what crossed its connections, counted once at this
    machine's end, and its explanation what it sent and received; one pass reads the reaches drawn, each kept for the
    generation. Under a brush they say their counts and bytes are the whole session's.
  - Debug and Release each ran 2,558 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twenty mutations were caught: a table's edges never read, read while it is hidden, read again though read, or
    said read with a reach unread; what crossed them counted from their sends alone, measured by their sends alone,
    or with transfers of no size stated as nothing; the split at this machine's end unsaid or swapped; under a brush
    their counts unsaid as the whole session's, or their bytes said whole when unread; the bytes unspoken; a view
    that cannot read bytes calling them unread rather than unknown; a failed read unsaid; rows left stale after a
    read, or unrestated as one starts; a chosen row lost as its rows are restated in other words, written or read;
    and, reading them, one connection taken for a reach's several, or sends of no size counted as measured.

- **Revision 614 — the table's edges to other hosts on the way down and back (R15, §3.2):**
  - From a live pass over revision 613's table on the busy machine: an edge chosen in it came back chosen at the rung
    E or Enter led to, choosing its process there; the way back from there chose nothing; each rung's table opened at
    the scroll of the rung left, a row cut in half; and its relationships stood in the order of their keys. The table
    now lets the edge go before its rows change, the way back chooses it again with its source and the keyboard on its
    row, a newer generation keeps it, each rung's table starts at its first or chosen row, and the relationships come
    most observed first.
  - Debug and Release each ran 2,553 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eighteen mutations were caught: an edge to other hosts let go on the way down without a word, after the table's
    rows changed, or not at all; the way back finding no edge, choosing its source again over the process chosen in
    an aggregate's list, or not choosing it where nothing else did; E or Enter keeping no way back to it, or Enter
    keeping it before the way down let it go; a newer generation given no edge, choosing none, choosing its source
    again, or choosing it whatever the selection; the keyboard given to a relationship's row alone; the table left
    at the scroll of the rung left; and the relationships in the snapshot's order, ties unordered, or the quietest
    first.

- **Revision 613 — the relationship table lists the graph's edges to other hosts (R15):**
  - The table, the graph's table equivalent, listed none of the edges to other hosts the graph draws since revision
    542 - most of a busy rung. Each is now a row after the relationships, naming where it ran, who opened it and what
    Enter opens; chosen, it chooses its source as a click on the edge does, and Enter opens what a double click does.
  - Debug and Release each ran 2,548 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twenty-four mutations were caught: no edge to other hosts listed, edges before the relationships, the quietest
    first; a host's port unsaid, the hosts uncounted, a sentence reading the count; who connected reversed; the
    hosts unnamed; a process's rung, or an aggregate's list, not said as what Enter opens, or the rung's own process
    said to open; a row chosen without its source, or not remembered, or taken for another edge's, or kept through a
    choice made in the graph or through another rung; Enter opening a relationship in an edge's place; the reason
    shown beside rows; the caption uncounting; a row read aloud as a relationship, or offering Enter where it opens
    nothing; the line beneath saying a relationship's; the window listing relationships alone; and one process said
    "on their own".

- **Revision 612 — a group's instances say what each did; the graph's names stand apart (§6.1, §6.3):**
  - From a live pass over the busy machine's svchost.exe and chrome.exe rungs: one program's instances were rows of
    one name, the PID cut off. Each is now named by its PID and says what it did - its relationships, and the host it
    reached where it reached one, "↔ 192.168.1.1:53" - then how it started.
  - The graph's names no longer stand against each other where there is room, edges break round their letters, and the
    search's chosen hit's name stands outside its ring.
  - Debug and Release each ran 2,543 tests with revision 473's 93 failures and 4 skips and one more: a window test
    that read a reused PID's two holders' rows in their old words, which now reads them in the new and passes in
    both. All twenty-six mutations were caught: names keeping no room, keeping it with no fallback, or falling back
    first; a chosen name allowed to crowd, or only an overlapping name stepping aside; no halo; a chosen hit's name
    kept clear of the selection's ring alone, a broken ring's not at all, or not placed again, and its leader drawn
    across the ring; one program's single instance renamed, several programs' processes renamed, rows named by
    program, or heard by PID; one relationship unsaid, the PID repeated, the brush unsaid; an accepted connection
    read as made, or its port unsaid; datagrams read as connections; a host's port unsaid; the first of several
    hosts named; an IPv6 address unbracketed; the group's rung left as it was; and the rung's instances, or their
    relationships, not read.

- **Revision 611 — the way back to a chosen relationship chooses it again (§3.2, R15):**
  - E on a chosen relationship, or Enter or a double click opening it, then Esc or the breadcrumb back came to its
    rung with it no longer chosen. The way back to the rung that way began at now chooses it there again, and gives
    the keyboard to its row where the relationship table is shown. Each rung keeps its own way back, and a newer
    generation keeps them.
  - Debug and Release each ran 2,537 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twelve mutations were caught: a newer generation given no way back to a relationship, or keeping none; a way
    down from a rung keeping that rung's way back; E keeping none, or Enter for a channel or for linked calls; an
    opened relationship's way back kept for the machine rung whatever rung it was opened at; a way back taken at a
    rung below its own, or at another rung at its depth; a way back to a rung replaced kept; no keyboard asked for;
    and the window not giving the relationship's row the keyboard.

- **Revision 610 — the inspector says who connected for a chosen relationship (§6.3, §6.8, R4):**
  - From a live pass over the busy machine's relationships: chosen, a relationship was titled "A ↔ B" whoever
    connected, and its subtitle was eight lines of channel key. It is now titled with its row's mark, "A → B", its
    subtitle says who connected, and how it was derived is the inspector's explanation further down.
  - Found and next: Esc after E on a chosen relationship comes back with nothing chosen.
  - Debug and Release each ran 2,532 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    nine mutations were caught: a relationship named with "↔" whoever connected, or with no mark where none opened;
    the inspector's title not that name; its subtitle without who connected, or its derivation again; the timeline's
    highlight named without the mark; the inspector's explanation not the relationship's, or headed as a pairing;
    and the explanation kept after the choice was let go.

- **Revision 609 — the graph rings the hit chosen among a search's hits (§6.7, R15):**
  - From revision 607's live pass: arrowing through the busy machine's 31 hits for svchost moved nothing in the graph.
    The nodes of the hit chosen - a process's own, a group's members, a channel's two ends - are now ringed in the
    search's ink, whole on a node that is only the hit's processes and broken on one holding them among others, and
    the ring follows the arrows.
  - Debug and Release each ran 2,531 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eleven mutations were caught: the whole ring, or the broken one, never drawn; the two swapped; a ring kept for a
    hit no longer chosen; a process's, a group's, or a channel's second end left out; the whole ring drawn broken or
    the broken one whole; and the ring unsaid to a screen reader, or a broken one said as whole. The two that never
    draw a ring did not build at first, since the analyzers ask that a member reading no instance data be static;
    written to read it, both were caught.

- **Revision 608 — a bare number offers its port search (§6.7, R15):**
  - From revision 607's live pass: 5353, which six of the busy machine's svchost.exe instances sent to, found nothing,
    since a bare number is a PID and a port is typed after a colon. A number whose port search finds something now
    offers it among its hits, after a process whose PID it is, else first; Enter searches the port in the box, and
    Enter again opens its first hit.
  - Debug and Release each ran 2,529 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    twelve mutations were caught: the port never offered, or offered for a number whose port finds nothing; offered
    first ahead of the process whose PID the number is, or after any process at all; the number read untrimmed;
    going to it opening a rung, or giving the keyboard to the ranked table, or leaving the caret where it was; the
    summary saying only that nothing matched; and the row read aloud as a hit to open, counting records, or named a
    result.

- **Revision 607 — a typed search keeps its first hit chosen; a cell outlasts a taller timeline (§6.2, §6.7):**
  - From a live pass searching the busy machine: typed letter by letter, a search left no hit chosen at every second
    letter, so Enter opened nothing and Down reached no hit. The first hit now stays chosen at every letter.
  - The graph's summary said what a search found, or what a chosen cell highlights, at the end of a line a busy
    machine's census fills, past its ellipsis; it now says it first.
  - A process lane's cell chosen as a group's rung opened lost its explanation and highlight once the lanes' rows made
    the timeline taller and the view was counted again; a view is now counted again only when its width changes. A
    window test had failed on this about one run in eight under load, before this revision too.
  - §19.4 now says a newer generation's layout keeps every node seen anchored, as revision 605 built it.
  - Debug and Release each ran 2,526 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    ten mutations were caught: the hits list's letting go of its hit taken as the person's, the list marked as
    restating its hits never, or its choice read back as kept while they were restated; a view counted again for its
    height alone, or not for its width alone; the graph's summary saying its marks last, in either of its two forms
    or in both, uncapitalized, or a chosen cell's before a search's. A view not counted again for its width alone
    went unseen at first, since the test's wider window also wrapped the timeline's header and made it taller; the
    test now widens a window wide enough already, its height checked unchanged. The cell tests then passed 30 runs
    in a row in Release with three of four cores busy, where they had failed 4 of 30 before the fix.

- **Revision 606 — the inspector scrolls no further than a row needs (R15):**
  - The whole window suite ran five times in Release with three of four cores busy, and nothing failed.
  - A row given the keyboard back after a newer generation, or reached by arrows past the inspector's foot, scrolled
    the inspector two or three times as far as it needed; it now scrolls until the row shows whole, and no further.
  - An export test looked for a PID as any run of its digits, which a run's random hex identity can hold, and failed
    once; it and a like test of `icat hosts` now match the number whole.
  - Debug and Release each ran 2,524 tests with revision 473's 93 failures and 4 skips, and nothing else failed,
    after the export test's fix; before it, Debug had failed that test once, as said. All three mutations were
    caught: the row not laid out before it takes the keyboard, and the host list's or the process list's own
    bringing of a focused row into view kept.

- **Revision 605 — a recording's graph holds still while it grows (§6.3, §6.8):**
  - From a live pass recording the busy machine in chunks: each publication moved nodes the person had seen by up to
    most of the pane, as if a group had been opened. A newer generation's layouts are now settled until the view is
    back, so no node seen moves more than a nudge at a publication, however many join; the choices made between
    publications all stayed chosen.
  - Debug and Release each ran 2,522 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    six mutations were caught: a settled layout easing its anchors anyway; the settled request not reaching the
    layout, or dropped by the scheduler; a newer generation never asking for one, or not asking; and every later
    layout settled.

- **Revision 604 — a newer generation keeps a chosen relationship and a set chosen together (R7, §6.7):**
  - While recording, each publication let go a relationship chosen in the graph or its table, and processes chosen
    with Ctrl+click or Ctrl+Space. Both are now chosen again, what the generation no longer holds said once, and the
    keyboard returns to the relationship's row.
  - Debug and Release each ran 2,520 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    ten mutations were caught: the relationship, or the set, not carried; the relationship not chosen again, in the
    workspace and in the window; a relationship gone not said; the set not chosen again, or chosen beside the ranked
    group's row it began from; one process gone said as several; all gone said as some; and one gone not said. The
    set beside a ranked row went unseen at first, and a test of a set begun from one now holds it.

- **Revision 603 — a newer generation keeps a list's choice, and the keyboard on it (R7, §6.3):**
  - While recording, each publication kept the aggregate or Other hosts chosen but let the process or host chosen in
    its list go, and the way back to the list, and gave the keyboard to the ranked table. It now keeps them, says once
    when the list no longer holds the one chosen, and gives the keyboard back to its row.
  - Debug and Release each ran 2,517 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    ten mutations were caught: the process chosen in a list, the host chosen in Other hosts' list, or the way back
    to a list not carried; the choice, or the way back, not restored; a process, or a host, the list no longer holds
    not said; one still listed said gone; and the window forgetting, or not asking, which list a row had the
    keyboard in.

- **Revision 602 — every way back to an aggregate's list comes to its row (§3.2, §6.3):**
  - From a live pass over the busy machine: E on a process chosen in an aggregate's list, then Esc, comes back to its
    row, and so does forward and back again; a way down from that rung or above it is a way of its own, and the crumb
    of the rung shown chooses nothing. An opened group's Other members comes back the same way.
  - Other members whose processes have no relationship says so once, not again as "no relationship".
  - Debug and Release each ran 2,513 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    eleven mutations were caught: E on a listed process recording no way back, in the workspace and in the window; a
    way down from the list's rung keeping it; a way down below it letting it go, and E there dropping it; the way
    back taken once; a climb past it letting it go; the crumb of the rung shown choosing it again; Other members
    with no relationship saying so twice; its least active said without their relationships; and some without a
    relationship said as only its least active.

- **Revision 601 — the way back from a listed process returns to its list (§3.2, R15):**
  - Esc or the breadcrumb back to the rung a process was opened from in an aggregate's list chooses the aggregate, the
    process in its list and the keyboard on its row, where the way down began, rather than the process's group in the
    ranked table.
  - P26's window test waits for the timeline's own count of the view before reading the interval table it compares; a
    count arriving after the read had failed it once.
  - Debug and Release each ran 2,509 tests with revision 473's 93 failures and 4 skips. Debug also failed P26's
    live-preview window test once; a delayed timeline count reproduced that failure exactly, and the fixed test
    passed with the delay and then 40 runs in a row under load. Nothing else failed. All nine mutations were caught:
    the origin not recorded; the way back by Esc, and by the breadcrumb, not resuming it; a search's descent keeping
    it; it resumed twice; it resumed a rung too deep; the keyboard not asked to the row; the ask uncounted; and the
    process not chosen again. Removing the ask's count alone went unseen at first, since the window reads the
    notice; the workspace test now checks the count.

- **Revision 600 — an aggregate's process row says which connections it accepted (§6.3):**
  - "3 records · 1 TCP connection to 1 host · accepted here at port 445", as Other hosts' list says it of a host, so a
    process that only accepted connections no longer reads as connecting out.
  - Debug and Release each ran 2,508 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    five mutations were caught: a row not saying what was accepted; all accepted said as a count; UDP flows counted
    among what could be accepted; the ports unordered; and nothing accepted said as all accepted.

- **Revision 599 — the keyboard follows an aggregate into its list (§6.3, R15):**
  - Enter or a double click on No relationships, Other hosts only, Other processes or Other members in the graph
    chooses it and gives its process list the keyboard, where Enter opens one; it opened nothing before, so a keyboard
    reached the list only by F6. The node's card and the keys window say so.
  - Debug and Release each ran 2,508 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    six mutations were caught: an aggregate opening nothing again; one chosen again when already chosen, letting its
    chosen process go; the keyboard not asked for; the window not following the ask; the card not saying it; and a
    focus's context node listed and opened.

- **Revision 598 — a busy graph names more of its nodes where its pane has room (§6.3):**
  - Up to 40 names, where 24 were, each placed only where it fits; a process's PID line keeps its threshold of 24
    nodes. The busy machine is named whole at 1920 × 1080 and 27 of 30 at 1456 × 939, where six went unnamed whatever
    the room.
  - Debug and Release each ran 2,506 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    three mutations were caught, one once a test drew a small graph: the budget back at 24 names, the PID line
    following the names' budget again, and no PID line at all.

- **Revision 597 — the other intermittent window tests, stressed (R13, R15):**
  - With three of four cores busy, the HTTP lane test failed none of 120 runs. The path tests' failures were Escape
    pressed in a dialog before it had the keyboard, which this headless platform gives to the button that opened it;
    the tests press Escape once the dialog has the keyboard, as a person does, their waits allow 15 s and name what
    they wait for, and none of 120 runs failed. No product code changed.
  - Debug and Release each ran 2,504 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    change is to tests alone, so the stress runs are its evidence: under the same load the corrected path test
    failed none of 120 runs, where it had failed 4 and 5 of 60.

- **Revision 596 — a zoom of the merged time left before it is read is never drawn (R13):**
  - Every zoom's reading was drawn in turn, one already left included; drawn between Home and Enter, it made Enter act
    on a column of a view no longer asked for, which is why the merged-time window test failed now and then. A reading
    for a zoom no longer asked for is not drawn. With three of the machine's four cores kept busy, the test failed 6
    of 120 runs without it, each at that wait, and none of 120 with it.
  - Debug and Release each ran 2,504 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Both mutations were caught: a reading for a zoom already left drawn, and a drawing not counted.

- **Revision 595 — a process chosen in an aggregate's list is its own records (§6.4, §6.7):**
  - Chosen by a click or the arrow keys, it is what the timeline highlights, the card counts as the selected process
    and E lists; a node chosen in the graph lets it go, and a brush keeps it chosen as the list restates.
  - The list control let its choice go while its rows were restated, though the choice read back as kept, so the list
    showed none chosen while the card counted one; read during a restatement, the choice is now none.
  - Debug ran 2,503 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Release ran the
    same, but for the merged-time window test known to fail now and then - Enter on its first column did not open
    alpha's session within 15 s - which passed five reruns. All ten mutations were caught: the aggregate's records
    highlighted, headed, counted or listed by E with a process chosen; the choice kept when a node is chosen in the
    graph; the list's restatement written back as no choice, or the choice read back during it; the choice not said
    again after it; the highlight not following a choice; and the card's heading not said again.

- **Revision 594 — an aggregate chosen lists the processes it counts (§6.3, §6.7):**
  - No relationships, Other hosts only, Other processes and Other members list their processes in the inspector, the
    busiest first and at most 100, each with its records, relationships and connections to other hosts; Enter or a
    double click on one opens its rung. On the busy machine No relationships' twenty-two are read there, where the
    ranked table, ranking executables, needed their groups opened.
  - Debug and Release each ran 2,502 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    fifteen mutations were caught, one once its test watched the list restated after an interval's counts arrive
    rather than when it is chosen: Other hosts only or Other processes listing nothing; a hundred and one rows; the
    quietest first; names ordered by case; records counted over the whole session under a brush; a process's one
    relationship unsaid, or its relationships counted at one end; its connections to other hosts unsaid, or unmarked
    as the whole session's; the note counting every process; a process the list does not hold opened; the list not
    restated when the scope or the choice changes; and Enter on the list left to the graph.

- **Revision 593 — a program's processes that only reach other hosts take one place (§6.3):**
  - The eight places beside Other hosts go to programs, ranked by their processes' records together: several of one
    are its group's node, one is itself, and one place left over is drawn rather than folded. The busy machine's eight
    places had gone to Teams' four processes, two of svchost.exe's, one of chrome.exe's and ntoskrnl.exe; now
    chrome.exe's eleven, svchost.exe's nine and Teams' four are three nodes, and nothing is folded.
  - A node's card says "no relationship" rather than "0 relationships"; under the node budget a group's node that
    holds its processes that only reach other hosts takes its other members, each counted as a node saved.
  - Debug and Release each ran 2,497 tests with revision 473's 93 failures and 4 skips, and nothing else failed. All
    fifteen mutations were caught: places by process rather than by program, here and in the property; a place
    ranked by its busiest process or by its size; ties broken the other way; a place too few drawn; a place counted
    in Other hosts only rather than drawn as its group's node; a place skipped by the fold; one place left over
    folded, or one place more drawn, here and in the property; an opened group's processes placed; a group's node
    refused its one remaining member, or credited a node less for it; and "0 relationships" said again.

- **Revision 592 — the ranked list's rows take only their room (§6.1, R15):**
  - A row is 62 px, its two lines and touch target with 4 px of padding, not the theme's 21 px: six rows whole at 1456
    × 939 where there were five, and two and most of a third at 1080 × 700 with a saved session shown.
  - Debug and Release each ran 2,494 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    one mutation, the theme's padding restored, was caught: a row of 75 px, and five rows whole at 1456 by 939.

- **Revision 591 — a connection ranking's figure names its side (§6.1):**
  - "13 made" or "8 accepted" rather than "13 connections", so at 1080 × 700 a row's name is shown whole beside it.
  - Still open from the same pass: with a saved session open, its card leaves the ranked list two rows at 1080 × 700.
  - Debug and Release each ran 2,492 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Both mutations were caught: the figure naming the other side, and the figure saying connections again, which
    leaves the name 72 of the 82 px it needs at 1080 by 700.

- **Revision 590 — two intermittent window tests (tests only):**
  - The dense HTTP lane's pixel check reads the columns of the count the frame drew, since the lane is counted again
    as the window settles; the investigation window's waits allow 15 s and name what they waited for when one times
    out.
  - Debug and Release each ran 2,491 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    merged-time test passed 20 runs alone and 12 runs of its class before the change, and the lane test's columns,
    probed idle, never moved after the first read: its settling matters only when a count lands late, as in a full
    parallel run. No mutation run: no product code changed.

- **Revision 589 — names stay where they stand when a node is chosen or the keyboard moves (§6.3, §6.7, R15):**
  - The names drawn with nothing chosen are placed first, as that frame places them; a chosen or focused node's name
    takes a free slot, or else its preferred one, where unchosen names step aside rather than move. This closes the
    item revision 588 left open.
  - Debug and Release each ran 2,491 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 16 mutations of the placement, made a function of its own, was caught: a chosen name inside its ring, or
    taking a held slot before a free one, or leaving the name it overlaps, or treating unchosen names as chosen
    ones; the keyboard's node not always named, or not named at all; a name already drawn not kept as chosen, or
    placed again though it stands beyond its ring, or kept twice; the budget never spent, or ranks ignored; a name
    and its second line said alone, or one alone said with it; a chosen node's leader from its rim; and the view not
    saying whose its names are, or not telling the placement what is chosen. An earlier run of 8 mutations against
    the drawing alone left 5 uncaught, which is why the placement was made testable on its own.

- **Revision 588 — a name set away from its node is tied to it (§6.3):**
  - From a live pass over a busy synthetic machine, where names in the outer rings read as their neighbours': a name
    takes a slot nearer its own node than any other where one is left, before standing over an arrowhead, and one more
    than 10 px from its node's rim, or nearer another node, is tied to it by a hairline from the rim.
  - Still open from the same pass: choosing a node can move the names around it.
  - Debug and Release each ran 2,486 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Of
    14 mutations 13 were caught: a name taking a slot nearer another node, or one over an arrowhead before one
    nearer another, or its gap measured from its node's centre; a name hiding a node; no leader for a name far off,
    or near but nearer another, through the decision a drawn name takes or alone; a leader for a name over its node,
    from the centre rather than the rim, or to the wrong side of a box above; and the window keeping no leader or no
    owner of a name. The one left, the window's call deciding a near leader without its neighbours, survived because
    the busy machine leaves no name near its node yet nearer another, as names now avoid such slots; the decision's
    own test catches the same change.

- **Revision 587 — the inspector states the selection's connections made and accepted (§6.8, R15):**
  - A Connections fact beneath the selected process's or group's summary, as its card counts them, where a keyboard
    and a screen reader reach it: none the capture saw open, not recorded where the session records no open (R21), and
    the whole session's under a brush.
  - Debug and Release each ran 2,484 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Of
    12 mutations 11 were caught: the snapshot never saying an open is recorded; a process counting no member, or a
    group only its first, or no group stated; a session with no open recorded counted as none; a selection with none
    saying nothing; the brush not said; either side's counts swapped; the screen reader not told what the line is,
    or the line shown with nothing chosen; and the inspector not told when the selection changes. The one left, the
    inspector told again once a brush is counted, survived because the chosen row the brush keeps re-tells it; it
    stays for a selection no row holds.

- **Revision 586 — icat processes says the connections each instance made and accepted (§5.2, §6.8, R18):**
  - A Connections column ("3 made · 12 accepted", "none"), a Connections line under --pid and connectionsMade and
    connectionsAccepted in --json: the overview's own counts, as the window's card says them; not recorded, and null,
    where the session records no connection's open (R21).
  - Debug and Release each ran 2,482 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 11 mutations was caught: a session whose opens are all paired, or all to other hosts, taken for one that
    records none; the overview never saying any open is recorded; the document's made taken from what was accepted,
    or its accepted given where no open is recorded; the note on connections given where none is recorded; the table
    without its column, a count of none written as two zeros, or its two sides swapped; and --pid saying none where
    no open is recorded, or the counts where there are none.

- **Revision 585 — one count of connections made and accepted (§5.2, §6.3, §6.8, R18):**
  - A process's card, the ranking by connections made or accepted and its export read one count from the relation
    index: a paired connection made by its connecting end and accepted by the other, as the capture saw it open at
    either end, a process connected to itself both; a one-sided one by its holder's own open. The ranking counted only
    a process's own connect and accept records, so a server whose accepts were lost ranked as accepting none.
  - A card now also counts a process's connections to itself and to local ports no process of the capture holds, which
    the graph does not draw; a brush takes a connection whose first record is within it.
  - Debug ran 2,481 tests with revision 473's 93 failures and 4 skips, and nothing else failed. Release failed one
    more, an HTTP exchange lane's pixel check, which passed in each of 6 reruns and draws nothing this change
    touches. Each of 22 mutations was caught: a connection seen open neither way, or bound as the evidence policy
    withholds, counted for an end, paired or one-sided; one taken under a brush that it opened outside, at either
    bound; the ends' parts swapped, or the maker or the accepter left out; a one-sided open read the other way; a
    process's later connection replacing its earlier; the card without what each process made or accepted, or
    counting what every policy admits; the ranking counting the whole session under a brush, or under the default
    policy only, or its TCP records not counted, or counted for every mechanism but lifecycle; and a set counting
    every process, or its accepts from what they made.

- **Revision 584 — a process's card counts the connections it made and accepted (§6.3, §6.8):**
  - "Connections: 3 made · 12 accepted, as the capture saw them open", beneath where its connections ran: its
    relationships by the end that connected, its connections to other hosts by its own connect or accept; a group's
    members' together, one between two of them made and accepted alike. None seen opening says nothing; the rest of
    the machine's card leaves its processes' to their own rungs.
  - Both that line and the one saying where the connections ran count the whole session, and under a brush now say so.
    The ranking counts a process's own connect and accept records, so the two differ where only one end of a paired
    connection was seen opening.
  - Debug and Release each ran 2,479 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 16 mutations was caught: a relationship's connections counted from the wrong end, made or accepted by
    its source alone, or once where both its ends are members; a host's accepts counted as connects, or left out, or
    every process's hosts counted for one; the line left out where only accepts were seen, or its two counts
    swapped; the rest of the machine's card counting its processes', the card counting none, or setting the line
    above where the connections ran; and a brushed card not saying either line is the whole session's.

- **Revision 583 — rank by connections made or accepted (§5.2, §6.1, R18):**
  - Rank by offers Connections made and Connections accepted: each process's own TCP connects or accepts in scope, a
    group's its members' together, so the processes that serve others rank first; unmeasured, never zero, where the
    session records no open.
  - icat export --rank-by connections-made and connections-accepted export the same rows; icat metric names neither
    yet.
  - Debug and Release each ran 2,477 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 29 mutations was caught: the reader counting no connect, no accept or no TCP record, every mechanism's
    records but lifecycle's, or the whole session under a brush; taking a session whose opens are all paired, or all
    to other hosts, for one that records none; a ranking of connections made reading the accepts, a session without
    opens measured rather than unmeasured, a process with no TCP record counted as none, or one in such a session as
    known; a group's count recorded where a member's is not, or holding only its last member's accepts; the ladder
    or the workspace leaving the counts out; the family taken for peers, connections accepted called made, or the
    export naming one ranking for the other or ranking by records; the selector without Connections accepted, or its
    measures never found, read or applied to the rows; the note saying the other side, or no TCP where no open is
    recorded, and the detail leaving the side out; a row's figure saying no TCP where no open is recorded, or read
    aloud with the other side; and icat export taking connections-accepted for connections-made.

- **Revision 582 — a lone arrowhead holds on a shorter edge (§6.3):**
  - An edge carrying one head needs room for it alone, not for a head at each end: a dense graph of 450 edges drew 205
    of its 375 heads rather than 153, repainting in about 6 to 7 ms with them, under a frame's 16.7 ms.
  - Debug and Release each ran 2,471 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 4 mutations was caught: a lone head still needing room for a head at each end, or no stroke behind it;
    and the graph taking every edge for one that carries a lone head, or for one that carries two.

- **Revision 581 — icat overview says who connected (§6.8, R18):**
  - It lists the five busiest relationships, maker first - "client.exe · PID 100 → server.exe · PID 200", ⇄ both ways,
    "and" where none was seen opening - and its caveat no longer calls every edge's direction display order only.
  - Its other-hosts tables say each process's and each host's accepted connections, as `icat hosts` does, rather than
    calling every process one reaching other hosts.
  - Debug and Release each ran 2,471 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 13 mutations was caught: a relationship named target first where its source connected, source first
    where its target did, with one arrow where each end connected, or with an arrow where none was seen opening; the
    quietest relationships listed first, or the note on the arrows left out; the accepted column left out, counting
    the connections made rather than accepted, or naming none of the ports they arrived at; the hosts' accepted
    column left out, or a host's ports left blank rather than marked none; the other-hosts line calling every
    process one reaching them; and the caveat calling every edge's direction display order only.

- **Revision 580 — the demo shows who connected (§14):**
  - The demo records each TCP connection's open as Kernel-Network does - a connect at the end that made it, an accept
    at the end that took it - so its graph, cards, table and `icat hosts` show the client's processes connecting out,
    the server's api and files accepting, and api connecting to db.
  - The graph's summary counts processes "with connections to other hosts" rather than saying they connect to them.
  - Debug and Release each ran 2,469 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 11 mutations was caught: the demo leaving out the browser's or sync's connect, api's or files' accept,
    or api's connect to db or db's accept of it; recording an accept as a connect, under the connect's event, or
    naming an open's ends the wrong way round; leaving the accept out of its descriptors; and the graph's summary
    saying its processes connect to other hosts.
