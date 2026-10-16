# InterCat implementation status — historical log, plan revisions 500 to 579

Archived on 2026-10-16, at plan revision 598, whole and in order. The current resume point is
[the implementation status](../IMPLEMENTATION-STATUS.md); revisions 230 to 499 are in
[the third log](IMPLEMENTATION-STATUS-revisions-230-to-499.md), revisions 104 to 229 in
[the second](IMPLEMENTATION-STATUS-revisions-104-to-229.md), and the chronology before them in
[the earliest log](IMPLEMENTATION-STATUS-through-revision-103.md).

## Slices, newest first

- **Revision 579 — who connected, in the relationship table (§6.4, §6.6, R15):**
  - A mark between a row's ends says what the graph's arrowheads draw: → where the source connected, ← where the
    target did, ⇄ where each did, none where no open was seen; its tooltip and the line beneath the table say it in
    the card's words.
  - A row read aloud joins its ends with "and", then says who connected, so a screen reader is never told display
    order as a direction.
  - Debug and Release each ran 2,469 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 20 mutations was caught: the direction line giving a UDP flow RPC calls' words, saying display order
    only where the target alone connected, "in all" of a single connection, every connection one end's where some
    opened unseen, or leaving out how many opened unseen; the table's mark pointing one way where each end connected
    or back at the end that connected; a row read aloud giving the target's connections to the source or saying
    something where none was seen; a row's mark or tooltip reading its ends the wrong way round, its name leaving
    out who connected, twice over, or joining its ends by "to"; the line beneath the table not saying who connected;
    an edge's card not saying it, or reading the drawn edge's counts the wrong way round; and the table showing the
    mechanism's glyph for the mark, or the evidence explanation for its tooltip.

- **Revision 578 — who opened connections to other hosts, drawn (§6.3, §6.6):**
  - An edge to Other hosts, or to a host drawn on its own, draws a head at the host where its processes connected to
    it and at the process where they accepted from it, so a process serving other machines shows at a glance; its card
    says the same, and its title joins its ends with ↔.
  - A node's label keeps clear of every arrowhead where a slot clear of them all is left, rather than hide one.
  - Debug and Release each ran 2,465 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 23 mutations was caught: an edge to other hosts counting only its last address's connections made by a
    connect, its connects as its accepts or as all its connections, or leaving out who opened its connections or how
    many it holds; its card not saying who connected, saying it the wrong way round, or titled with a one-way arrow;
    a host drawn on its own or Other hosts taken for a disc, or the graph standing a head toward a square off a
    circle inside it; a square's reach from a lane beside its centre measured to the wrong side leftward or upward,
    the farther side taken for the one it leaves by, or a reach from outside a square or toward nowhere; the graph
    keeping no head toward either end for its labels to keep clear of, or its labels placed as if none were drawn; a
    label placed over a head where another side was clear, or left unnamed where heads cover every slot; and a
    head's box missing its tip.

- **Revision 577 — who connected to whom, drawn (§6.3, §6.6):**
  - The graph draws a filled arrowhead at the process a relationship's connections were made to, at both ends where
    they went both ways, and none where the capture saw none open; the card's direction line reads the same counts
    from the drawn edge.
  - A head is 3 px wider than its stroke either side and the stroke stops under it, so a thick edge never hides it;
    the inspector's key shows it, and a frame still allocates nothing.
  - Debug and Release each ran 2,459 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 32 mutations was caught: a drawn edge counting only its last relationship's connections made from its
    source, its source's as its target's, or an RPC call as a connection, or leaving out who made its connections or
    how many it holds; the card saying display order only where only the target connected, "in all" of a single
    connection, or display order only always; the graph drawing no head at either end, a head where none was seen
    opening, or one pointing back at the process that connected; the stroke running on past a head at either end or
    under nearly all of it, an unmeasured band running on under its head, or its head as narrow as the pen it was
    given; a head narrower or squatter than its margins, kept where the edge less the disc at its near end is too
    short or with no stroke between two heads, its tip off the wrong end's disc, or its sides swapped; a disc's
    reach along a lane taken from its offset rather than the offset's square, or not a number along an edge that
    passes outside it; the triangle built again on every frame, narrower on one side, or placed narrower; the key's
    sample drawing no head; and the graph setting a head inside the disc it points at. One of them, the triangle
    built on every frame, did not build as first written and was caught as rewritten to build.

- **Revision 576 — who connected to whom (§6.3, §6.8, §7.4):**
  - A relationship's card said its direction was display order only; the capture's connects and accepts now say
    which process made each connection: "client.exe · PID 100 connected to server.exe · PID 200".
  - A process's channels lead with → or ←, and a channel's explanation and rows say the endpoint it was made from.
  - Debug and Release each ran 2,454 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 19 mutations was caught: a relation never saying which end made it, not taking a later end's accept or
    an earlier end's as the other's connect, or naming a maker where both ends claim the connection; a relationship
    counting its target's connections as its source's or every known one as its target's, or equal to one that
    counts otherwise; a channel naming no maker, or the endpoint connected to as the one connected from; a channel's
    explanation or a relationship's channel rows not saying where it was made from; a process's channel row leading
    toward a peer that connected to it, or never from one; and a relationship's card not saying who connected,
    reading a drawn edge's ends the wrong way round, saying display order only where its connections were seen
    opening, saying all of them where some went the other way, or leaving out how many opened unseen. Two of them, a
    maker known only from the later end's accept and an arrow from a peer that connected, were caught once tests
    held them.

- **Revision 575 — a port search finds who accepted connections at it (§6.7, R18):**
  - A port alone, ":3389", also finds the processes that accepted connections at that port of their own, each
    hit saying whether its connections ran to the port, arrived at it or both; an endpoint names the host's port.
  - Debug and Release each ran 2,450 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 8 mutations was caught: a port alone not finding a process at a port it accepted connections at, caught
    in the search, in the window's marks and by icat hosts --address; its hit saying accepted connections ran to the
    port, never saying to or from where both did, or taking what ran to the port for what arrived at it or the other
    way; and icat hosts --help not saying a port alone finds them.

- **Revision 574 — the window says a host's accepted connections (§6.3, §6.7, §7.1):**
  - Other hosts' list and cards, and a drawn host's card and description, say what was accepted at which port.
  - An accepted connection is named from where it came on its process's rung ("← 10.0.0.5:50000"), and each
    lifetime says connected or accepted; `icat channels --one-sided` too (`connection-list-v2`).
  - Debug and Release each ran 2,449 tests with revision 473's 93 failures and 4 skips, and nothing else failed; the
    Release run was repeated alone after the session's worker restarted during the first. Each of 14 mutations was
    caught: an accepted connection named toward where it came from, or its own end said as the one it ran from; a
    lifetime not saying how a connection opened, or saying connected or accepted as opened; a connection's opening
    lost as its process's connections were listed; its rung row led by the wrong arrow, or its own end said as where
    it ran from; Other hosts' list, its card, a drawn host's card or its description not saying what was accepted
    from the host; and icat channels --one-sided saying an accepted connection's own end as where it ran from, or
    giving its earlier contract.

- **Revision 573 — a host's connections accepted from it (§6.3, §7.4, R18, R21):**
  - A server's accepted connections listed the other host's own ports, chosen for each, as ports it ran to.
  - Each connection says whether its process connected or accepted it, from the capture's connects and accepts;
    a host counts both, with the process's own ports the accepted ones arrived at (checkpoint format 1.3,
    `other-hosts-v6`). A checkpoint before 1.3 that holds an open is derived again.
  - Debug and Release each ran 2,448 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 32 mutations was caught: an accept's cut taken for a connect's, as derived, as a later generation
    extended an end or began one; a connection's opening said the other way, said for one opened neither way, or
    lost as an extension copied it or as the connection was listed; an accepted connection's port taken at the host
    or kept among its ports, or one opened neither way counted as connected; addresses equal that differ in what was
    connected or where it was accepted; a checkpoint writing or reading an accept as a connect, reading a kind or an
    open an earlier format cannot hold, or written as format 1.2; a host's accepted connections, their ports or its
    connected ones taken from one process alone; hosts equal, or hashing alike, that differ in what was accepted; an
    accepted host said only in part, or not said to be here; a dash for none written as a count; and icat hosts
    listing no Accepted here column, for hosts or for who reached an address, no accepted count in its JSON, no dash
    for a host with no port it ran to, its note where nothing was accepted, or its earlier contract.

- **Revision 572 — a live pass over an investigation's hosts (§6.3, §8.3, §26.3):**
  - Opening the demo's server from its investigation after its client said its notice twice: both open in the
    same words, which the rail kept as a live capture's repeated detail. A session opened afresh says its own.
  - Other hosts' description said what candidate other ends are, pushing its second host below the fold at the
    window's first size; the list's note says it beneath the rows instead.
  - Debug and Release each ran 2,445 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 6 mutations was caught: a session opened afresh keeping the last one's detail when its words repeat it;
    the list's note not saying what its candidates are, or saying it for its one host as for several; Other hosts'
    description lengthened with it again, caught by its own text and by both hosts no longer fitting above the
    inspector's fold at the window's first size; and Other hosts' card not saying it.

- **Revision 571 — a session opened from its investigation names its hosts' candidate other ends (§8.3, P6):**
  - Only icat hosts --investigation named them; the window said nothing of who may be at a host.
  - A session opened from its investigation reads them in the background under its view's evidence policy.
    Other hosts' list and description, and a drawn host's card and description, name them, never as an
    established join. A later publication takes the read over, done or running; another policy reads again.
  - Debug and Release each ran 2,444 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 26 mutations was caught: a host's row, card or description not naming its candidates; Other hosts'
    description or card not saying them once, saying them for its one host as for several, or for hosts with none;
    the list or the description not restated as they arrive; a finished read a later publication took not shown with
    its first rows, or a running one never shown; a read given up shown anyway; a read that found none leaving an
    earlier one's said; its own cancellation not taken as its end; a read not handed over, taken by another
    session's view, kept under another policy or not read again under it; a read made under the first-run policy, or
    its candidates read under it whatever the view counted by; a read begun by an ended view; an unreadable
    investigation failing the read; letting a read go twice failing; a view's end not letting its read go; and the
    window not carrying the read to a later publication, nor reading it for a session opened from its investigation.
    Of these, the candidates read under the first-run policy were caught once a test held that direct evidence alone
    proposes none of the demo's correlated connections.

- **Revision 570 — an other host's candidate other ends in its investigation (§8.3, P6, R18):**
  - Nothing said which process of another member's capture a host's connections may have reached.
  - icat hosts --investigation names, for a member session, each host's candidate other ends; a rejected join
    names none, an accepted one says so (`other-hosts-v5`).
  - Debug and Release each ran 2,438 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 15 mutations was caught: a rejected join still naming an end; a member's own connection taken for the
    other's, as the first member or the second; an accepted join not said so; a host's ends out of order; a host
    found by its connection's local endpoint; a host's name lost, or its capture's in its place; its ends said
    without their host, or without a person's acceptance; and icat hosts --investigation not refusing a session that
    is no member, nor --address beside it, listing no column of ends, a host with none said otherwise, and its JSON
    giving nothing where a host has no end.

- **Revision 569 — a live pass over the demo's hosts (§6.3, R21):**
  - The empty graph's summary named TCP's connections to other hosts and hid the UDP flow beside them; it says both.
  - Other hosts' and a host's descriptions say the same in fewer words, so the host list shows above the fold.
  - Debug and Release each ran 2,436 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 7 mutations was caught: the empty graph's summary never saying both transports, saying both where TCP's
    or UDP's relationships were left out, or counting UDP's records as its flows; Other hosts' description saying
    nothing of its list, with one host or several; and a host's not saying E lists its records.

- **Revision 568 — Other hosts' list follows a byte ranking (§6.3, R21):**
  - Under a byte ranking Other hosts still listed its hosts by records.
  - The list is read for its hosts' traffic and ranked by it, each row with both directions' bytes; while read,
    or where it cannot be, it keeps the records order and says why; its heading wraps whole.
  - Debug and Release each ran 2,436 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 14 mutations was caught: the list following a ranking that is no byte ranking, or keeping the records
    order once its bytes are read; its rows saying no bytes; its heading not naming the byte ranking, nor that the
    bytes are being read or could not be; its note not saying why, in two ways; no read started as the list changes
    or as the rail's ranking does; a read's bytes or its problem never kept; the window's list not told when they
    are; and its heading run past the inspector's edge.

- **Revision 567 — other hosts ranked by bytes (§6.3, R18, R21):**
  - Which host a machine sent the most to, or received the most from, took reading each connection in turn.
  - Each host's traffic is read over exactly the census's connections; icat hosts --rank-by bytes-sent,
    bytes-received or both lists the hosts by it, an unmeasured host never as a zero (`other-hosts-v4`).
  - Debug and Release each ran 2,433 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 20 mutations was caught: a host's connections taken without their address or their transport; its TCP
    connections counted as every connection, its UDP flows as none, its processes as its connections, or its ports
    as none; its ranking blind to an unmeasured host's place before one with none, ordered the wrong way, or not
    breaking a tie of records by connections; an unmeasured host's sends taken as measured, or its bytes as a zero;
    sent and received not summed; an interval not read; a metric hosts hold nothing of not refused; and icat hosts
    --rank-by bytes-sent read as bytes received, a ranking misnamed in its JSON, --rank-by beside --address not
    refused, a byte ranking read without bytes, a direction with no transfer called unmeasured, and a host's JSON
    transfers misread.

- **Revision 566 — the timeline's header keeps the selection's highlight whole (§6.4, R15):**
  - The caption said what the highlight marks last, and the usual widths cut it short.
  - It stands beside the caption in the highlight's ink, whole; the tooltip and screen reader read the caption whole.
  - Debug and Release each ran 2,431 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 8 mutations was caught: the selection's highlight said beside the caption without its capital, or run
    into the caption's end with no stop; the header not told when either part changed; the selection's part showing
    the whole caption, or the caption still ending in the highlight; the selection's part shown with no highlight;
    and it cut short in the smallest window. Once, run beside other window tests, the test that opens a record's
    original and content from the main window waited past its five seconds for the keyboard to come back; it passed
    alone, in three runs since and in both suites.

- **Revision 565 — a host chosen in Other hosts' list is its records (§6.3, §6.4, I5):**
  - Only a host drawn on its own could be read; the many more Other hosts lists could only be searched.
  - A host chosen in the list is highlighted, counted and listed by E as one drawn on its own is; Enter still
    finds who reached it, and the choice is kept by address while the list restates its rows.
  - Debug and Release each ran 2,431 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 10 mutations was caught: a host chosen in Other hosts' list let go while the list restates its rows, in
    two ways, or not chosen again among them; kept while another node is chosen, or when Other hosts is chosen
    again; never what the window reads, never highlighted, never read, or not headed so on screen; and the list's
    choice never the window's. A list keeps its choice by equality, so choosing the host again matters only when its
    row reads differently, as when Other hosts, opened, stands for fewer processes; the mutation dropping it was
    first missed, and a test now opens Other hosts with a host chosen.

- **Revision 564 — a host chosen in the window is its records (§6.3, §6.4, I5):**
  - A host drawn on its own, chosen, highlighted nothing, and E listed the rung's every record.
  - Chosen, it highlights the records of every connection to it; its card counts them, their connections,
    processes and bytes over the scope the rows count, from the same test of which are its; E lists them.
  - Debug and Release each ran 2,429 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 24 mutations was caught: a host's address matched by case, or its scope's channels taken whatever their
    other end; its connections read with its address untrimmed, any connection taken for one of its, or an interval
    left unread there or by the window; its TCP connections counted as every connection, its processes as its
    connections, or its bytes received as those sent; a connection's unmeasured receives said as its sends'; an
    evidence rung reached from a host read as a paired channel's, or its interval unsaid; and in the window a chosen
    host highlighting nothing, its heading or E the rung's, its card counting none of it, its records never read, in
    two ways, or not read again for a brushed interval, an interval with none unsaid, records that cannot be read
    said still to be read, their count never applied or never shown, and its card's line of what a click does gone.

- **Revision 563 — a host's records as a scope (§6.3, §6.4, R18):**
  - Nothing read a host's records: when it was reached took opening each process and connection in turn.
  - host:<address> scopes every connection to the address for evidence pages, the timeline's focus and an
    interval's bytes; icat evidence and icat timeline take --host.
  - An evidence page's closing note says what each scope's page includes, not that one-sided rows are left out.
  - Debug and Release each ran 2,424 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 17 mutations was caught: a host's key written with another prefix, or a bare prefix taken for one; its
    connections matched by an endpoint's beginning rather than its whole address, or by case; an IPv6 endpoint's
    brackets not read; the timeline's focus not reading a host, counting the connections it does not name, or
    reading no channel of a row; an evidence page not reading a host, or listing what it does not name; an end taken
    for a host's by the timeline's focus, an evidence page or an interval's bytes, whose description of a host was
    lost; and icat evidence --host reading the whole session, a host and a channel named at once not refused, and
    icat timeline --host not reading it. A guard keeping a host's focus from the persisted tiles repeated one that
    already does, so it was removed, and a reopened session's host focus is tested to count its rows.

- **Revision 562 — a coverage pass over the host and search code (R10):**
  - A collapsed group's edge to Other hosts, a host found by a search, icat hosts' help and refusals and its count
    past --limit, and the ports' hash codes had run under no test; each now runs whole and does what it says.
  - Debug and Release each ran 2,420 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 10 mutations was caught: a collapsed group's edge to Other hosts saying only that a click chooses it, or
    saying so at no rung but the machine's; a host found by a search not said so, or said so as a process is; an
    address's or a host's hash code taken from its ports' list itself; and icat hosts taking --help as an option it
    does not know, its refusal of no session in other words, a folder that is not there read as a session, and its
    count of the processes past --limit unsaid.

- **Revision 561 — an other host's address scope (§6.3, P7):**
  - Multicast groups and the broadcast address were listed and drawn as hosts, though no one host is at them.
  - Each address's standard range - multicast, broadcast, link-local or private - is said where a host is listed
    or drawn, with what it means on a host's card; icat hosts gains a Scope column (other-hosts-v3).
  - Debug and Release each ran 2,418 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 18 mutations was caught: the multicast range's first or last block misread, the broadcast address read
    from its first byte, link-local IPv4, or 172.16.0.0/12 or 192.168.0.0/16 widened, IPv6 multicast, link-local or
    unique local misread, and an IPv4-mapped address read as IPv6; the inspector's rows, Other hosts' card, a host's
    line beneath it, its card or its description without the scope; and icat hosts' column, its JSON or its
    document's name without it.

- **Revision 560 — search an endpoint or a port (§6.7, §6.3):**
  - An endpoint copied from netstat, 142.250.1.1:443, found nothing.
  - A search for an address at a port, or a port alone (:443), finds the processes that reached it there; the
    graph's marks, the lanes and icat hosts --address find by the same rule.
  - Debug and Release each ran 2,385 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 13 mutations was caught: a port alone, an IPv6 endpoint or an IPv4 endpoint not read as one; an endpoint
    naming an address it only begins, or ignoring its port; a port alone finding every address; a hit's detail
    naming the endpoint's address, dropping the port searched, or leaving out UDP flows; a host an endpoint names
    said not to be named, and a port query said to be none; and icat hosts finding by addresses alone, or saying an
    endpoint found nothing in an address's words.

- **Revision 559 — the ports at each other host (§6.3, R18):**
  - An address alone told a busy machine's connections to other hosts little apart.
  - Each host now gives the ports its connections ran to there, the lowest first: in the inspector's list, a
    host's card and description, icat hosts (other-hosts-v2) and icat overview; never a service's name.
  - Debug and Release each ran 2,384 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 15 mutations was caught: the census taking each end's own port, or carrying none; an address or a host
    compared without its ports; a host's ports not gathered from every connection to it; one port said as several,
    or three as three and none more; the inspector's rows, a host's card or its description without ports; icat
    hosts' tables or its JSON without them, or its document still named other-hosts-v1; and icat overview's table
    without them. The property test of the relation index now checks each address's ports against the generated
    connections', in a full and an incremental derivation alike.

- **Revision 558 — the inspector lists Other hosts' hosts (§6.3, §6.7):**
  - Other hosts' card names three hosts and its description named five; a busy machine's processes reach
    hundreds, which only a search or icat hosts reached.
  - Chosen, it lists every host it stands for, the busiest first and at most a hundred, saying how many more;
    Enter or a double click on one finds the processes that reached it, the keyboard staying on the list.
  - Debug and Release each ran 2,384 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 15 mutations was caught: a host node or any other cluster chosen listing Other hosts' hosts, the list
    kept from an earlier drawing, past a hundred hosts, or counting processes where one reached them; the headings
    of many or of one, the note always shown or miscounted; a host the list does not hold found, or a found host not
    searched; the list not told of a new choice or drawing; the description of one host said as of many; Enter on
    the list doing nothing or finding only a selected row; and a row read aloud as its address alone. A clause that
    gave E's keyboard to the ranked table from the list was found redundant, since the window gives it there when a
    focused list hides, and was left out; the window test holds that.

- **Revision 557 — Other hosts opened kept in the investigation (§26.3, §6.3):**
  - Opening Other hosts lasted only while the window showed the session, and a host pinned on the opened graph
    came back pinned to a node not drawn.
  - A member's layout keeps hostsOpened (workspace-v21). Opened from its investigation, a session draws its first
    graph opened and says so with what else was put back; the investigation's window and icat workspace show say
    it too (workspace-resolution-v24), and the rail names it as a changed setting the default view folds.
  - Debug and Release each ran 2,380 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 19 mutations was caught: a layout keeping only Other hosts opened said to keep nothing, its words or its
    description dropped, SetLayout not storing it, a version 20 file opening Other hosts accepted, a version 20 file
    refused, the unknown version's message naming the wrong last version, the window neither writing nor reading it
    back, a session from its investigation or a newer publication drawn folded, the notice and the rail naming hosts
    a session has none of, the rail never naming it or never told, the default view leaving it opened, a change of
    it not written, the first graph not drawn opened, and icat workspace show not saying it.

- **Revision 556 — icat hosts (R18, §6.3):**
  - Nothing headless listed every other host a session reached, or which processes reached one.
  - icat hosts lists them, the busiest first, as many as --limit asks; --address lists the processes that reached
    an address by the window's search rule; --json returns other-hosts-v1.
  - Debug and Release each ran 2,377 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 7 mutations was caught: the busiest host left out of the list, the count of the rest unsaid, processes
    counted by their links, a whole address not naming itself, a query naming no address accepted, the processes
    that reached an address listed quietest first, and a limit of none accepted. The README's commands were each run
    against the demo.

- **Revision 555 — open Other hosts from the window (§6.3):**
  - Enter or a double click on Other hosts draws its busiest hosts on their own, and on it or a host folds them
    back; a newer publication keeps it opened. Hosts are named squares, reached by the keyboard after processes.
  - A host's card and description say whose connections ran to its address and which processes they are; edge
    and Other hosts cards count only what they stand for; a search marks a host by its address.
  - Debug and Release each ran 2,375 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 11 mutations was caught: Enter never folding the hosts back, the drawing never opened or never
    re-projected; Other hosts' card counting the hosts drawn on their own, an edge's card all its source's hosts, or
    a host's card every address; the processes that reached a host named quietest first; a search finding a host by
    Other hosts' addresses; hosts reached by the keyboard among the processes; a newer publication folding them; and
    Other hosts' card offering to open what is opened.

- **Revision 554 — the projection opens Other hosts into its busiest hosts (§6.3):**
  - Every connection to another host ran to one node, so the graph could not show two processes reaching one host.
  - Opened, the busiest hosts of the drawn connections are drawn on their own, named by address, each edged from the
    nodes that reached it per transport, the rest kept in Other hosts; no process moves, as many hosts are drawn as
    the budget allows, and a re-count keeps them. The window does not open it yet.
  - Debug and Release each ran 2,373 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 11 mutations was caught: Other hosts never opened; a host past the busiest opened; hosts opened past the
    budget; hosts ranked with the rest of the machine's connections; every connection drawn to Other hosts, or
    counting a record per address; a node's edges to different hosts sharing lanes; a host counting no process; a
    re-count dropping the hosts opened; a link drawn twice to one host accepted; and a host node refused for holding
    no process.

- **Revision 553 — the graph marks what a search finds (§6.7):**
  - A search marked the processes it found in the timeline's lanes only; the graph showed none of them.
  - While a search is typed, each node holding a process it finds carries a dot, filled where every process it
    holds is found and open where some are; Other hosts is marked for an address. The summary counts them, a found
    node's card and its spoken name say so, and nothing is selected.
  - Debug and Release each ran 2,372 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 12 mutations was caught: an aggregate found in part marked wholly; Other hosts never found by an
    address; marks kept from an earlier search or an earlier drawing; the summary not counting what is found; an
    aggregate's card counting all its processes as found, or a process's card silent; a process not found by an
    address it reached; the filled mark drawn in the pane's colour, the open one not drawn; and the spoken name not
    saying a node is found, or partly.

- **Revision 552 — the transport relation index over random captures (R10, relations-v1):**
  - A property test derives ninety random captures - reused PIDs, loopback, unseen local ends and other hosts,
    IPv4, IPv6 and mapped, TCP and UDP, lifecycles among transfers - and holds the census to the relationships
    and one-sided connections under every policy, each process's reach of other hosts to its one-sided
    connections by address, and every answer to the same records cut into other segments.
  - The extension and checkpoint tests compare the census, one-sided connections and reach too. Nothing needed
    fixing; a withheld relationship's record can have an admitted other end, as relations-v1 §4 intends.
  - Debug and Release each ran 2,370 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    property test alone caught 9 of 10 mutations: a census counting loopback as other hosts, a connection's own
    address or an IPv4-mapped address of another host as this machine's, ambiguous incarnations in the reach,
    records naming no endpoint or unpaired ones miscounted, relationships admitted by one end's binding, other hosts
    counted by their local address, and connections listed with their own endpoint as the remote one. The tenth, a
    process's reach taking its last connection's binding rather than the weakest, answers alike while entities-v1
    binds every transfer of one instance at one strength.

- **Revision 551 — find the processes that reached an address (§6.7, §6.3):**
  - The search found processes by name or PID only; a person holding an address had to open every process's rung.
  - An address whole, or begun up to a dot or colon, now finds the processes whose connections reached it, IPv6 in
    either case and a mapped address by its IPv4 one; a hit says what reached the matching addresses, never the
    address (P16), and a group's lanes are marked by the same rule. Other hosts and the search box say so.
  - Debug and Release each ran 2,367 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 12 mutations was caught: a number or word read as an address; an address found by any beginning rather
    than up to a dot or colon; a mapped address not found by its IPv4 one; IPv6 found in one case only; a begun
    address said beside the whole one; a hit not saying what reached it, its UDP flows uncounted or an address
    counted once per transport; the hits, the rule lanes are marked by or the lanes themselves finding no process by
    an address; and a process ranked by its name where an address it reached matched better.

- **Revision 550 — name the busiest other hosts by address (§6.3, P7):**
  - The Other hosts node said how many hosts the drawn processes reached, never which; only each process's rung
    listed its own. A host link now carries its connections address by address, its counts its addresses'.
  - The node's card names the three busiest hosts by records with what reached each, its description five, and an
    edge's card the three its source reached; a transport's line counts distinct hosts, and a process's rung names
    its own alone. icat overview lists the five busiest, and its JSON carries each host link's addresses.
  - Debug and Release each ran 2,365 tests with revision 473's 93 failures and 4 skips, and nothing else failed.
    Each of 24 mutations was caught: a process's addresses out of order by records, connections or address, IPv6
    written as IPv4, its connections miscounted per address; a link's connections not its addresses'; a host's UDP
    flows counted as TCP, its processes uncounted, the hosts out of order or ties; a transport's hosts counted once
    per process; one process's hosts saying whose; a list that fits named as the busiest; a link with no address, an
    address twice or fewer records than connections accepted; the overview dropping addresses; Other hosts naming
    the whole machine's hosts on a process's rung, an edge's card another's, or either naming too many or too few;
    and icat overview leaving out the busiest.

- **Revision 549 — the graph's compaction over random machines (§6.3, R10):**
  - A property test draws six hundred random machines - groups of every size, TCP, UDP and RPC relationships in
    either order, connections to other hosts - under random budgets, opened groups, kept processes and focused
    rungs, and holds each drawing to §6.3: every process and relationship once, every connection to other hosts
    but a context node's, no aggregate of one, even lanes, and Other hosts only folding only the less busy.
  - It found an opened group none of whose processes a snapshot holds throwing over budget; the window builds no
    such snapshot today, and the projection now finds nothing to fold there.
  - Debug and Release each ran 2,362 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    property test alone caught each of 11 mutations: edges met in the other order not parted, lanes not centred, a
    node's edges to other hosts not centred, an aggregate of one, the busiest folded, a related process or the kept
    process folded, a collapsed group's members folded again, connections drawn from where the process is not, a
    context node's connections drawn, and an opened group none of whose processes the snapshot holds throwing over
    budget, which the example test catches too.

- **Revision 548 — say how to draw RPC calls wherever a graph leaves them undrawn (§6.8, R18):**
  - Only a graph that drew nothing said that RPC calls are drawn only from a capture that collects ALPC, and how to
    record one; a busy machine's graph, relating a few processes over loopback, never pointed to RPC peers.
  - A session holding RPC calls nothing links now says so in the graph's full scope and beneath icat overview,
    whatever else the graph draws; linked calls, or none, say nothing of it.
  - Debug and Release each ran 2,357 tests with revision 473's 93 failures and 4 skips; the one other failure, a
    test of the Other hosts node expecting the graph's old words, was updated, and the Desktop tests then passed in
    both configurations. The tests caught each of 5 mutations: the guidance said of linked calls, or of a session
    with none; an empty graph's reasons leaving it out; and a drawn graph's scope and icat overview never saying it.

- **Revision 547 — the README and icat's help say what the product is now (R18):**
  - The README described milestone M1's first slice - a synthetic graph prototype, TCP-only Explore, content
    blocked; it now says what the window captures and draws, what icat reads and shares, and how to try it with no
    capture, each command run as written against the demo.
  - icat's general help omitted RPC peers from icat capture's profiles; they are now one list, which the parser's
    refusal and both helps read.
  - Debug and Release each ran 2,357 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 3 mutations: RPC peers left out of capture's profiles, the general help listing them by
    hand as before, and the refusal naming them by hand.

- **Revision 546 — fold the long tail of processes that only reach other hosts (§6.3):**
  - A capture shaped as a busy machine - sixty processes reaching other hosts, three pairs related over loopback -
    drew a star of sixty spokes that hid the relationships between processes.
  - Outside the explicit focus, past the eight busiest by their connections' records, the processes that relate to
    nothing and only reach other hosts fold into one Other hosts only node with the hub's globe, edged to Other hosts
    per transport; collapsed groups, an opened group and a kept process keep theirs, and no aggregate holds one.
  - Its cards and description say what it folds; chosen, it counts and lists its processes' records as any aggregate
    does. The same capture draws fifteen nodes beside Other hosts.
  - Debug and Release each ran 2,356 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 13 mutations: no fold however many; the busiest folded rather than the least; a process
    ranked by one connection's records rather than all of them; a related process, the kept process, a collapsed
    group's member or an opened group's member folded; the node drawn as Other processes; a budget below zero
    refused only by its slice; and the summary, the node's card, its description and its edges' card not saying what
    it folds.

- **Revision 545 — draw paired UDP flows between processes in the graph (§6.3):**
  - Two processes exchanging only datagrams showed no relationship: the overview drew TCP alone, though the relation
    index has paired UDP flows since revision 57.
  - The graph draws each pair's UDP flows as an edge of its own in UDP's hue, its channels judged by UDP's coverage;
    a brush, the channel rung and its ends, E, the channel browser, icat channels and a byte ranking read them as
    they read TCP's. A persisted overview keeps a flow's ends at minor 5, and an older one's bytes are read again for a
    session with a flow (`overview-index-v1`, `relations-v1` §5e).
  - Two edges between the same two nodes run side by side, 8 px apart, so either is hovered and chosen where drawn.
  - The census explains both transports, a flow's pairing says no record opens or closes it, and the demo's UDP
    answers name their sender first, as a UDP receive does, so its browser's flow no longer pairs with itself.
  - Debug and Release each ran 2,353 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 33 mutations: TCP's relations drawn alone; a pair's TCP and UDP merged into one edge, or
    keyed as TCP; a flow's channel named TCP, or judged by TCP's coverage over the session, a brush or a page; a
    brush, the channel pages, E or a channel's focus reading TCP alone; a page stating TCP's coverage alone; a byte
    measure, or the overview's kept bytes, without a flow's ends; an overview before minor 5 read for a session with
    a flow, or one written as minor 4; the census without UDP's reason, name, hosts or records; rows without a peer
    said of TCP alone; parallel edges on one line, two met in the other order not parted, or 4 px apart; a flow's
    pairing, its evidence scope, its card, the evidence card's two counts and its ends' words said as TCP's; and the
    demo's answers naming their own end first.

- **Revision 544 — extend RPC calls and their other ends between live generations (I14, §12.1):**
  - A live RPC peers capture, which the window draws since revision 543, paired every call and read every ALPC record
    again at each generation: 0.58 s at 80,000 calls over 640,000 records.
  - A call index keeps the records of each key a later record can still change - a call open, or a record within
    15 s of the latest - so a later generation reads only its added segments, pairs those keys again and merges the
    rest in canonical order; the other ends keep their sorted ALPC records and merge the added ones
    (`operations-v1` §3a). That generation projects in 0.18 s.
  - Both decline, for a full derivation, an earlier record of a key not kept, another file under a segment's name or
    a field published apart from its record, and equal full derivations of random chunked captures.
  - Debug and Release each ran 2,344 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 18 mutations: an added record of a key not kept read before the late window, or one read
    before what is kept of its key, extended; a source field of a call already read extended; segments told apart by
    name alone; a withdrawn call left standing; calls merged out of canonical order; an ambiguous run kept from its
    first start alone; the window's first record left out of what is kept; no late window; a start's thread misread;
    an ALPC message id of a record already read extended; the ALPC records taken by two extensions, or kept by none
    after one declined; another session's segments extended where no source field tells them apart; server starts
    not read again; sends and receives merged out of order; and calls or their other ends never extended by the
    derivation cache.

- **Revision 543 — record RPC peers from the window and icat capture (§9.4, ADR-035):**
  - The window's Keep lists RPC peers, 10 minutes: every record of ten minutes with the kernel's ALPC messages, a
    choice of its own so the card grows no taller at the smallest window; icat capture --profile rpc-peers asks
    the broker for it. The census, icat operations and a metric's reason name both where they named icat record.
  - The broker refused the profile, wanting a provider request per source where a kernel flag group has none; it
    now counts one per provider-enabled source and refuses a request for, or a process scope on, a kernel flag
    group (`broker-v1` §2).
  - A live RPC peers capture drew no RPC edge until saved, since only its ledger, published last, said it collected
    ALPC; until a ledger exists the plan it retains says so (`normalizer-plan-v1`), so its graph, a brush and a
    checkpoint have the links as it records.
  - Debug and Release each ran 2,336 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 36 mutations: a session that retains no plan read as an empty one, a plan's bound shrunk, a
    plan without its descriptors refused unexplained, a plan of another contract read or one naming none admitted,
    or a descriptor's mechanism misread; ALPC judged collected with no plan, by RPC calls, or by a plan beside a
    ledger naming none; the overview's edges or census, a brush or a checkpoint judging by the ledger alone; the
    census, a metric's reason or icat operations naming icat record; icat capture refusing rpc-peers or its refusal
    naming explore alone; the window's runner asking for the other profile, its request losing the choice, or a
    capture named as another or without its capital; the Keep choice linking no call, its note or options losing it,
    or the card's note, its start action's words or its preparing headline; and the broker counting a provider
    request for a kernel flag group, letting one name it, taking it for a provider's source, or letting its scope be
    undefined, filtered by process, given process IDs or capture outside what was asked.

- **Revision 542 — draw processes' connections to other hosts (§6.3):**
  - The relation index gives each process's connections to other hosts by transport (`relations-v1` §5d); the
    overview carries them as host links under the evidence policy, and icat overview lists who reaches other hosts.
  - The graph draws such a process on its own, edged to one square Other hosts node: a transport an edge, side by
    side, in its hue and evidence pattern at a constant thin stroke, with no traffic value and in no total; its
    summary, cards and selection say what they are, and a click or double click on an edge reaches the process.
  - The line beneath an empty graph shows only while its pane draws nothing; two keyboard tests' third process now
    sends to an unseen port on this machine, so their graphs stay as they were.
  - Debug and Release each ran 2,328 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 64 mutations: a reach of other hosts counting an end on this machine, another transport's,
    a paired one - between two of this machine's own addresses too - or one two processes hold, a host per
    connection, a record per connection, or not the weakest binding; host links admitted past the evidence policy,
    without UDP, at the wrong strength, lost to the workspace, kept for a process set aside, or keyed by anything; a
    process reaching other hosts folded as quiet, a context process's connections drawn, two transports drawn over
    each other, the node's processes or records miscounted, an edge's binding not its weakest, the drawing called
    clustered, the node or its edges weighed by bytes, a second node, one holding a process, an edge into it that is
    no host edge, a host link drawn twice or a relationship drawn as one, a link naming no process, records below
    zero, no host or more hosts than connections, the links left unchecked, or an edge's link lost; the node or its
    edges setting a scale, the node drawn smaller, or its edges thickened; a UDP flow called a TCP connection, its
    hosts not "with", one process's hosts unsaid, or UDP said first; the keyboard reaching the node first, its edges
    drawn on one line, or its label's "from"; the window's summary note, a process's or a context's line, the quiet
    node's words, the brush's caveat, an edge's gestures, the node's or an edge's card, a click or a double click on
    an edge, the node's description, its evidence heading or records, the line beneath the graph, the scope's words,
    the summary's node counts, or a cell highlighting an edge to the other hosts; and icat overview's list.

- **Revision 541 — say why the graph draws no relationship (§6.3, §6.8; reported from a live capture):**
  - A live Explore capture shaped as Windows delivers it - manifest-compiled plans, bodies read as the admitter
    reads them, a rundown, loopback TCP over IPv4 and IPv6 - relates both connections in the window's live view,
    now a test of that path; a busy machine's TCP mostly runs to other hosts, and the graph said nothing of it.
  - The relation index takes a census of each related mechanism (`relations-v1` §5c); where a graph draws none,
    its summary, a line beneath it, its node, the relationship table and icat overview say where the TCP went and
    that UDP and RPC calls without ALPC are not drawn. Drawing connections to other hosts in the graph is open.
  - A reader's lease listing failed on a file the writer staged and removed while it listed, ending a live
    follow; such a file is now left out of the listing (`store-v1` §3).
  - Debug and Release each ran 2,314 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 31 mutations: a census admitting a withheld relationship, counting a relationship from both
    its ends, taking an end two processes hold for one-sided, placing an end's other end on the wrong side of this
    machine - at its own IPv4 or IPv6 address, at another loopback address named as IPv4 or mapped into IPv6, or at
    a mapped address that is not loopback - counting a host per connection, or losing the records naming no
    endpoint; a withheld relationship not given first, one record said as all of several, a lone UDP datagram or a
    capture with no TCP left unsaid, RPC calls called unjoinable from a capture that collected ALPC, or a list's
    last item without its and; the overview's RPC count, UDP census or workspace census lost; the window's summary
    or its node's, table's or line's reason left out, or the line shown at a rung, beside relationships set aside,
    or hidden; icat overview's reason; an IPv6 address the plan compiler refuses, or TCP owned by its header,
    breaking the live view's relationships; and a listing failing on a file removed while it lists.

- **Revision 540 — icat retain's journal prefix release, run whole (S5, R18):**
  - A confirmed release the store refused - beside content kept with a single journal, or from a session read at
    its last complete generation - ended the command with an unhandled exception; what the store refuses at every
    boundary is now disclosed before it is asked, and a refusal is said in the report, exiting 1.
  - A boundary that empties the journal is never offered for confirmation; a session read at its last complete
    generation refuses every confirmed release before measuring, naming icat recover (exit 3).
  - Debug and Release each ran 2,303 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 22 mutations: content beside a single journal or a standing pin left undisclosed, or a
    release that empties the journal, keeps content or is pinned offered for confirmation or attempted; the store's
    content or pin refusal in other words; a confirmed release from a session read at its last complete generation
    measured rather than refused; that session's note left out of a journal, interval or content measurement, or
    such a measurement exiting 0; a reason alone told to add one; the report not written where asked; and a refused
    release's note, the content release's command, the pin's field or JSON, or the content beside the journal left
    unsaid.

- **Revision 539 — the refusals of pin, checkpoint, channels, exchanges and profiles, run whole (R18):**
  - Each refusal of an option's value names what it expects and the value given; icat profiles' "Invalid process
    ID" and "Unknown mechanism" are worded as every other command's, and a repeated --pid or --channel is said to be.
  - icat checkpoint says what came of it once, in its report, where it said it twice when nothing was published.
  - Debug and Release each ran 2,298 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 15 mutations: --allow-mib's, --process's or --page-size's value left unnamed; icat pin's
    hint naming the folder unquoted; --pid's, --retention's, --inspection's or --mechanism's value refused in the
    old words, or a --pid or --channel given twice; an existing report or an unknown profile refused in the old
    words; and icat checkpoint saying what came of it on standard error as well as in its report, saying it
    publishes without saying when it does not, or told it has no report.

- **Revision 538 — icat rederive and icat content run whole (R18, R20, §3.7):**
  - icat rederive refuses a session read at its last complete generation before replaying it, naming `icat
    recover`; a check of one says the generation it checked; a session with no saved plan is refused without a
    progress line, in words that promise no migration.
  - Every command names an option given twice as given twice, not as unknown.
  - icat content's text view, saves, whole parts and what there is not to show are tested; a range past the kept
    bytes names them, in the window's content dialog as well.
  - Debug and Release each ran 2,275 tests with revision 473's 93 failures and 4 skips, and one more: a test still
    held the no-plan explanation's words "legacy session". Its assertion now holds the new ones, and the journal
    tests passed in both configurations again, but for their 3 known Windows-only ticket tests. The tests caught
    each of 12 mutations: an option or a flag the command line took forgotten, so one given twice reads as unknown,
    or a repeated one never named; a range's or a part's buffers checked at one end only; --rows-per-segment's value
    unnamed; a session read at its last complete generation replayed rather than refused, or its check said to
    re-derive it; a progress line said for a replay or a check that never begins; and the no-plan explanation or
    refusal in their old words.

- **Revision 537 — a coverage pass over the command line's untested paths, each run whole (R5, R18, R21):**
  - icat operations lists a process's calls with each call's other end named as its group's is, and says of each
    side it lists how many calls found their other end, never "0 of the 0 client calls".
  - icat session states a segment's byte totals in icat metric's words, not TransportObserved and SendSide; icat
    metric names a record's accounting in words and counts one declared contribution in the singular.
  - Tests now run the call listing and its refusals, a session's kept content, and icat metric's process focus,
    matrix and canonical request.
  - Debug and Release each ran 2,264 tests with revision 473's 93 failures and 4 skips, and nothing else failed. The
    tests caught each of 13 mutations: a call's other end named "PID · 1960", or by its executable without its PID;
    the server calls listed never counted, or a side with no call listed counted; none said as 0, or a lone call
    agreeing in the plural; the two sides joined otherwise; a metric's declared contributions counted in the plural,
    or a record's accounting named by its identifier; and a segment's byte domain or accounting named by its
    identifier, its bytes written as Bytes, or its contributions uncounted.

- **Revision 536 — icat recover and icat staging run whole, as a person runs them (§20.1, R18):**
  - A publication or release refused on a session whose newest generation does not verify says so in plain words
    and names `icat recover "<folder>"`; icat session names it too, and names icat staging with the folder.
  - icat recover's review says what readers read, what is refused until the repair and what it keeps; after a
    repair it no longer promises a cleanup nothing performs.
  - icat staging's review lists every file, what it is and whether a cleanup removes it - the ownership markers a
    confirmation removed unnamed included - and each refusal names the review that supplies what it lacks.
  - Debug and Release each ran 2,253 tests with revision 473's 93 failures and 4 skips, and one more: a test still
    held recover's old "No repair is needed or performed" sentence. Its assertion now holds the new one, and the
    CLI's 96 tests passed in both configurations again. The tests caught each of 29 mutations: either half of a
    confirmation accepted alone, by recover or staging; a review that loses what readers read, or claims to keep the
    bytes of a pointer that is gone; a repair that claims a backup it did not make or is silent about the files it
    kept; those files counted without their noun or as no bytes; a generation that fails called a damaged pointer;
    icat session or a storage refusal naming recover or staging without the folder; a cleanup counting fewer files
    than it removes, or a review leaving a marker unlisted; a cleanup saying it keeps what it kept; every row listed
    past twelve, or the rest uncounted; files removed stated in a review; a writer's file or an unmarked one unsaid
    or said as plural; nothing staged said as a cleanup that removes nothing; the confirmation printed unindented;
    five files removed said as none; and a cleanup headed as a review.

- **Revision 535 — icat verify re-evaluates a curated fixture's run and says what it found (I14, R18, R21):**
  - Runs rebuilt from each of the four curated transport fixtures verify to the coverage they were published with;
    only the IPv6 fields added since, each zero, differ.
  - Its report stated only the tier, so every UDP run - whose topology criteria cannot be measured - exited partial
    unexplained; it now states the coverage as icat measure does, every gap included, from one report both use,
    with each criterion named in words.
  - Its JSON names its contract, fixture-verification-v1.
  - Debug and Release each ran 2,248 tests with revision 473's 93 failures and 4 skips. The tests caught each of 12
    mutations: a criterion named by its identifier in the domain's words, in a gap's sentence or in the report's
    table; a single false attribution that were recorded; the report's gaps left out, its tier named by its
    enumeration, a criterion counted against its own numerator, or its observed operations counted as all of them; a
    verification naming another contract, leaving its coverage out, naming its fixture without its run, or counting
    an unrelated observation it did not exclude.

- **Revision 534 — icat counts one in the singular (R5):**
  - Run on the smallest sessions - one record, one segment, chunks of one record, an investigation of one session -
    icat said "1 records", "First 1 of 1 observations", "1 observations in 1 segment", a re-derivation's "1
    observations, 1 fields", "1 source fields", a release's "2 to 2 records", "1 of 1 sessions" and "PID(s) 4242".
  - Each now counts in the window's words (`CountText`), and so do the last "(s)" hedges: icat profiles' compile
    note, a profile's descriptors and processes, icat record's losses, and the broker's recovery, maintenance and
    interrupted-capture messages.
  - Debug and Release each ran 2,241 tests with revision 473's 93 failures and 4 skips, and one more: a profile test
    that still asserted the hedged PID(s), corrected, after which the command line's 85 tests passed in both. The
    tests caught each of 14 mutations: a session's extent or newest chunk counting 1 records, or listing its only
    observation as the first 1 of 1; a metric's read or answer counting 1 observations; a re-derivation counting its
    source-field segments from its observation segments; a package's rows, redacted or original, counting 1 source
    fields; a compaction counting 1 source fields; a journal release naming its one boundary as a range, or saying
    the boundaries run from it when only one is allowed; an investigation's copies counting 1 sessions; a join
    refused saying there is 0 candidates; and a profile naming one process as PIDs.

- **Revision 533 — icat follow finishes an unfinished capture in the card's words (§3.1, R5, R18):**
  - `icat follow <session-dir>` had never run in a test. An unfinalized capture finished whole was reported as
    "Following stopped … run this again to continue", which was then refused; it is now a partial recording
    derived, saying what can never be recovered.
  - Unreadable evidence said "The session keeps the 0 chunk(s)" of a session holding none, with a partial result's
    exit code; it now says nothing had been saved and exits as corrupted input, and otherwise keeps "the 1
    published chunk", then gives the reason. A session that did not exist was said to hold everything.
  - The card and icat share their follow words (`FollowText`): a capture's first publication reads "1 published
    chunk", not "1 published chunks", and so do the live preview's card and icat capture's progress.
  - Debug and Release each ran 2,235 tests with revision 473's 93 failures and 4 skips. The tests caught each of 19
    mutations: a follow's count of none said in the singular and of one in the plural, its chunks not called
    published, or its progress counting the evidence's chunks as derived or as records; the session's finality read
    inverted; a finish saying a session that does not exist holds everything, or that a finalized capture ended
    unfinalized; unreadable evidence said of a session holding nothing as keeping its chunks, exiting as a partial
    result where nothing was saved, keeping one chunk more than it does, or giving its reason in parentheses; a
    finish's progress counting only chunks; an unfinalized capture finished whole still said to have stopped and
    asking to be run again, or one still recording called partial for good; the capture card counting its
    generations as chunks; a live preview's card saying 1 published chunk are, or 1 previewed record fall; the
    card's finishing text losing its progress; and a follower refused for a released first chunk saying its first 1
    chunks.

- **Revision 532 — icat raw says a record as the window does (§3.7, R5, R18):**
  - icat raw printed a record's provider as a bare identity and its codes as enumeration names (Qpc,
    ApprovedMetadata/Retained); it now lists the window's facts in the window's words, each length counted, from
    one set of lines the window and the command share.
  - A redacted package's record is called its synthetic entry by both, found or not, with the package's own reason
    when its entry is missing.
  - icat raw now runs in tests on real sessions - hidden and revealed bytes, a preview stopped at 256 bytes, a
    released journal batch, a redacted package - where no test had run it on one.
  - Debug and Release each ran 2,228 tests with revision 473's 93 failures and 4 skips. The tests caught each of 20
    mutations: a provider named from the wrong identity; a clock's encoding or a body's classification printed as an
    enumeration's name; a length of one counted as 1 bytes; a record outside the retained journal listed without its
    raw identity; a record's content left out of its facts; an extended item's retained length read from its
    original; a preview's last row dropped; a code's words begun with a space; a CPU cycle counter called unknown;
    icat raw listing only a record's first fact, calling a record synthetic because it names no admission policy,
    saying a preview stopped when it had not, or saying no body bytes were kept of a package's entry rather than an
    original's; the query calling an original record synthetic, calling a package's missing entry an original
    record's, or giving it an original's reason; and the window not presenting a package's record as synthetic,
    listing a fact without its colon, or leaving out why no body bytes were kept.

- **Revision 531 — the main window's untested paths, run whole (§3.1, R15, R21):**
  - A capture that ends before it shows anything gives back the session shown before it - its records, actions
    and name - as revision 130 promised; a capture that fails after saying it records is said to have failed after
    that, and one that saved nothing says so, never pointing to a session below.
  - A new investigation that cannot be made says why on the card and opens no window; a theme that cannot be kept
    says so, and where, in place of where choices are kept.
  - O on a call, a record or a saved session double-clicked, the graph's buttons, a support bundle that cannot be
    written, an original copy opened here, the investigation's Open this column and Esc, and a content capture
    through the broker's review now run in tests as a person runs them.
  - Debug and Release each ran 2,222 tests with revision 473's 93 failures and 4 skips. The tests caught each of 28
    mutations: a new investigation that could not be made opening a window anyway, or its card not saying none was
    made; one already there no longer opened as it is, a window whose file had gone not reading it again, and a new
    one not said to be new; a theme not kept still said to be kept, or not saying where it could not be; the session
    shown before a capture not given back when the capture said it could not run or when it failed, never
    remembered, given back over a view the capture recorded or published, or given back without its path or its
    session; a failed capture always pointing to a session below, or said before what it said last; the content
    chooser listing the machine's processes rather than the window's, or its review closing without asking the
    broker; a support bundle that could not be written said nothing; an original copy not opened here; O opening
    only a call whose other end is unknown, and a double click on a record not opening it; Pin node laying the graph
    out again and Re-layout doing nothing; a saved session double-clicked not opening, and a capture's session not
    listed once it ends; Esc not closing the investigation window, and Open this column opening nothing.

- **Revision 530 — the capture card's own actions say why they wait (R15):**
  - Stop and save, Start exploring, Open saved session, the unfinished capture's action and Forget, and Explore
    the demo say why they wait, on themselves and as their help text, in the words their keys or a click say.
  - The redacted package prompt says why its whole session and Choose a folder wait for a session too large to
    package whole.
  - Debug and Release each ran 2,216 tests with revision 473's 93 failures and 4 skips. The tests caught each of 25
    mutations: Start exploring's and Open saved session's reasons dropped, or kept only while a session opens or
    only while a capture is finished, Start's help, Open's fuller hover and the hover saying why the last capture
    could not run; the unfinished capture's action offered once asked to stop or while a session opens, and Forget's
    reason inverted or its hover lost; the reasons not restated once stopping is asked for, as finishing starts, as
    a session starts or ends opening, or as the Keep choice changes; Stop and save's reason once pressed and while
    the capture is saved, and its help; Explore the demo's reason on the card and in the menu, and its help from the
    first frame; the redacted package prompt's reasons for its whole session and Choose a folder; and an offered
    action's tooltip saying only its help.

- **Revision 529 — an investigation dialog's action not offered says why (R15):**
  - The saved views', translations', one host's, package's, align, compare and note dialogs, and the pin dialog's
    Keep records, say why an action or a choice is not offered, on the disabled control and as its help text, and
    what it does once offered.
  - A dialog opened on an investigation that no longer reads says so on its lists' actions, not that they hold none.
  - Debug and Release each ran 2,215 tests with revision 473's 93 failures and 4 skips. The tests caught each of 39
    mutations, one once State translation's explanation was read before a first statement: the saved views' Save
    reason with nothing shown and its help, their reasons once read, with no view saved, chosen or readable and once
    readable again, and for a view saved on another time reference; the translations' explanation, State
    translation's and Withdraw's reasons while a change is written and after, and Withdraw's with none stated,
    chosen or readable and once readable again; One host's reasons while the word is recorded, with no other host,
    with the hosts one already or not, and its help; the package's reasons for a session that cannot be copied, for
    none chosen or none copyable, and its help; the session to align to when it is the only one, and its help;
    Align's and Compare's reasons while they work and after; where a note is pinned while it is not to be, and Add
    note's reason while it writes, after, and its help; and Keep records' before a reason is written and with one of
    spaces alone.

- **Revision 528 — a dialog's action not offered says why (R15):**
  - The record window's Reveal, the content window's Copy as hex, Save bytes, Decode and part toggle, the channel
    browser's Show and Next, and the content capture's Review with the broker say why they are not offered, on the
    disabled button and as their help text, from the window's first frame.
  - A channel page that could not be read no longer says no channel is in scope; a failed reveal of kept bytes
    offers itself again while Copy and Save say why they cannot.
  - Debug and Release each ran 2,215 tests with revision 473's 93 failures and 4 skips. The tests caught each of 36
    mutations, one once written so that it builds: a reason hidden while its action is disabled, an offered action's
    help dropped or set to its reason; Reveal's reason before the record is read, while it is read, with no body or
    original retained, once revealed and when not verified; the channel browser's before its first page, while a
    page is read, with a page not read or older than the window's, with no channel in scope or chosen, and on the
    last page; the content window's before and while its content and bytes are read, with no content kept, no byte
    of the message or none in it, without consent, before its bytes are shown and when they could not be read, a
    failed reveal offered again, Copy and Save offered with what they do, Save refused for a part not whole, Decode
    while decoding and after, and the part toggle offered; the review's reason and help; and the investigation
    window's explanations of its offered actions.

- **Revision 527 — a main-window action not offered says why (R15):**
  - Show source records, Browse paired channels and both Share actions had reasons that never showed while they
    were disabled; each now shows its reason on the disabled button and says it as its help text.
  - Go to parent, the children action, Pin node, Re-layout graph and the record action say why when not offered,
    and what they do when they are; a window with no session says so from its first frame.
  - Debug and Release each ran 2,213 tests with revision 473's 93 failures and 4 skips. The tests caught each of 13
    mutations, one once the first frame was read before the window shows: an offered action's help set to its reason
    or a reason's to what it does, a tooltip apart from its help, the first frame left unexplained, Show source
    records' reason hidden while disabled, Re-layout graph's tooltip dropped or its reason without a session, Pin
    node's reason inverted, the record action offered with no record chosen, Keep records' and Share redacted
    session's help, and Go to parent's and the children action's reasons inverted.

- **Revision 526 — a session of an investigation is called a session wherever it is read (§8.4, R5):**
  - The investigation's refusals, its file problems and the reasons its window's rows give said member, the
    file's word for a session, where the window lists sessions; they now say session, by its leading digits.
  - icat workspace show heads its field and its hosts' table Sessions; its caveats, its help and icat's own help
    say session throughout.
  - Debug and Release each ran 2,212 tests with revision 473's 93 failures and 4 skips. The tests caught each of 25
    mutations, two once placing a session not in the investigation and aligning one to its own clock were tested: a
    member named again in what a session's row says to do, another session's folder, a relink, an addition and a
    capture added twice, a host none of its sessions was on, placing, aligning and packaging a session not in it,
    its time across sessions, a time reference not one of them, an alignment to another than the reference, a host
    confirmation, icat's field, hosts' column, caveats, help and overview.

- **Revision 525 — an investigation action not offered says why (R15):**
  - Eighteen buttons of the investigation window were disabled without a word; each now says why as its tooltip
    on the disabled button and its help text, and what it does once offered where its name does not.
  - Enter or a double click on a session not where it was last found said nothing; it now says to relink it.
  - Debug and Release each ran 2,212 tests with revision 473's 93 failures and 4 skips. The test caught each of 19
    mutations: a reason not shown on a disabled button, an explanation dropped, a tooltip or help text set apart
    from the other, an unreadable investigation called one still being read, Open offered for a session not where it
    was last found and Enter saying nothing for it, the time reference's align and withdraw reasons, one host
    offered among one host's sessions, a decision offered again or a missing one offered to withdraw, a note about
    the whole investigation offered to show, no session or note said as none chosen, the zooms at the whole
    investigation, the zoom with no session placed, Compare instants with no session, and Open this column with its
    column chosen.

- **Revision 524 — a redacted package's record is synthetic from the window's first word (§11.3, R15, R21):**
  - The record window is told what a package's rows open: its title, heading, status, accessible names and
    failures say synthetic before a byte is read, and Reveal is not offered; a row the package holds no entry for
    says the package is not as it was saved.
  - The inspector's record button is named by its label, its explanation its help text, synthetic for a package.
  - An investigation with no session says so, never that every session is where it was last found; Package says
    when the file names none, or could not be measured.
  - Tests run a record whose journal a release gave up, a package's journal gone while it is open, and the
    investigation's package states.
  - Debug and Release each ran 2,211 tests with revision 473's 93 failures and 4 skips; Release also failed once the
    call lane's density test, which hovered a column the lane could count again as the window settled. That test and
    its HTTP twin now wait for the view's own count and point again at a lane counted again, and the UI tests then
    passed whole in both configurations. The tests caught each of 20 mutations: the record window's synthetic title,
    heading, disclosure, status, accessible names, unavailable and failure wording and Reveal, the main window's
    flag and the record button's name and help text, and the investigation's empty status and nothing-to-package
    words.

- **Revision 523 — an investigation is called one wherever it is read (§8.4, R5):**
  - Its refusals and file problems, and icat's help and output, said workspace where the window and the rest of
    icat said investigation; they now say investigation, and workspace names only the command, the file's
    extension and the contract.
  - The main window says what it shows is unchanged, not the current workspace; a refusal of icat raw that sent a
    person to the current workspace says to restart from icat evidence.
  - Debug and Release each ran 2,207 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 18
    mutations: the investigation called a workspace in its members, its time, its time reference, a leading part
    naming two sessions, a file made over another, a file kept inside a session, a file not found, a file that does
    not read as one and its time across members; icat's heading, what new and an empty show say, its help, its
    reference instant, an instant with no place and its caveat; and the main window's dropped and unchosen paths
    saying the current workspace.

- **Revision 522 — what an investigation says, as its window says it (§8.4, R5):**
  - The workspace named sessions, captures, hosts and notes by all 32 hex digits, which neither the window's rows
    nor icat show; every message now names each by its first eight, and an alignment others rest on names whose
    alignment each sentence means.
  - A failed action in the investigation's window or a dialog says first what it did not do, then why; a file
    gone from under the window is said to be gone, never with the command line's advice to make one.
  - Sessions that could not be added no longer read the investigation again, whose failure hid that they were not.
  - Debug and Release each ran 2,206 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 18
    mutations: identities named by all 32 digits, everywhere or in a relink's refusal; the one-session and
    several-session sentences swapped, and the dependent's alignment unnamed; a gone file said in the command line's
    words; a removed note, a withdrawn alignment or a relink said without what was not done; a join's decisions
    swapped; nothing added read again, or not waiting for a reading under way; the aligned session unnamed; a host
    decision unprefixed; a note's adding and rewording swapped, and a translation's stating and withdrawing; a
    view's removal unnamed; and a comparison, and the card's unkept sentence, in the command line's words.

- **Revision 521 — the search's hits by keyboard (§6.7, R15):**
  - No test walked the hits by keyboard, and Up on the first did nothing: a person who went into them had to
    clear the search to go on typing it. Up there now gives the search box the keyboard back, its words kept.
  - The keys sheet lists the hits' keys - Up or Down, Enter, Esc - under a place of their own.
  - Debug and Release each ran 2,202 tests, with revision 473's 93 failures and 4 skips; Release once failed a live
    pin test besides. Its recording fixture publishes on a timer, which can put the first record in a chunk of its
    own, wholly before the pin, and the step then rightly releases it: the split the command line's two pin tests
    were fixed for earlier. The test now publishes the first record alone on purpose and expects its release, never
    one past the pin, and passed 16 runs in a row, 8 in each build. The tests caught each of 5 mutations: Up on the
    first hit keeping the keyboard there, Up on the second leaving the hits, the search's words lost on the way
    back, the caret left where it was, and the hits' keys missing from the sheet.

- **Revision 520 — what an investigation could not keep, said once (§8.4, §11.3, §26.3):**
  - A change a session's investigation could not keep added a sentence beside the session's status each time,
    so dragging the split grew the card a sentence at a time, and the sentence stayed once a change was kept.
  - Now it is said once, however often changes fail, and taken back once one is kept. The coverage pass that
    found it also found a package stopped by its own button, or one that could not be made, run by no test; both
    are now, and save nothing, leave no unsaved folder, and say which.
  - It also found revision 519 had made a files check refused by a package's hold say no earlier generation was
    complete. A package asked for now waits for the check, saying so; a check that finds changed files while
    something holds the session leaves it as it is.
  - Debug and Release each ran 2,201 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 21
    mutations: what the investigation could not keep said again at each change, never taken back once its layout or
    its panes are kept, left unsaid for either, or the one said in place of the other; Cancel packaging doing
    nothing for a redacted package, an exact copy or an investigation's package; a cancel or a failure left unsaid,
    for a package, a copy or an investigation's package; the card's headline not put back after a cancel; the button
    left offering the cancel; a redacted package or an exact copy asked for before the files check, or its wait
    unsaid; a second ask waiting beside the first; and a check reopening a session a package holds.

- **Revision 519 — what holds the session shown says so (§3.1, §8.4):**
  - While a package was being saved, Open saved session and Ctrl+O did nothing and said nothing; Start
    exploring started a capture whose card the package then wrote over; an investigation's Open said only that
    its session could not be opened.
  - One rule now names what holds the session shown - a capture, an open, a package, a finish - and every way
    to replace it waits and says why, on the card and in an investigation's status line. Start exploring waits
    for a package, as Open saved session did.
  - Debug and Release each ran 2,195 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 17
    mutations: Start exploring, Open saved session or an open going ahead while the session shown is held, or
    waiting unsaid; Ctrl+R silent while a capture runs; a capture, an open, a package or a finish not holding the
    session shown; a capture being saved said as one under way; a start refused in the words of an open, in either
    of two ways; and an investigation's Open or its column giving no reason, or saying the session opened.

- **Revision 518 — closing over work under way (§3.1, §8.4, §11.3):**
  - Closed while a package was being saved or an unfinished capture finished, the window went at once: the
    package was cancelled as InterCat ended, which could leave its unsaved folder in the folder chosen, and the
    finish stopped unsaid. Only a recording was asked about, and no test had closed the window during one.
  - One question now names all that closing would stop - a recording, a package and where it goes, a finish, an
    investigation's package - and keeps them going on Esc. Stopped, the window says so and closes once each has
    ended, no unsaved folder left; a package begun meanwhile is stopped too, and an investigation's window asks
    the same about its own package.
  - Debug and Release each ran 2,190 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 27
    mutations: a recording, a package, a finish or an investigation's package not asked about; the answer ignored;
    the stopping, or a recording's saving, unsaid; a recording, a package or a finish not stopped, or not waited
    for; an investigation's package not stopped, or one begun meanwhile left running; an exact copy's, a redacted
    package's, a finish's or an investigation's package's work not kept for the close; an investigation's window
    closing without asking, without waiting, without stopping its package or without saying so; and several things
    named as one, only the first said, Escape stopping them, or the keyboard placed on the answer that stops them.
    One, an investigation's package not kept, was first caught only by the run's time limit, and promptly when run
    again; every wait these tests make for a window's work is now bounded, so work that never ends fails a test
    rather than hangs it.

- **Revision 517 — the session's folder from the card, and Enter on a button (§3.1, §6.7, R15):**
  - The card's line naming the session shown was text a person could neither copy nor open. Open folder beside
    it opens the session's folder in the file manager, and the line's menu copies the path whole; a folder gone,
    or one the system would not open, says so.
  - With a row chosen in the ranked table, Enter on any button descended into the row instead of pressing it. A
    button the keyboard is on now takes its own Enter, and a list of choices opens on it; the ladder tests'
    helper now really gives the ranked table the keyboard.
  - Debug and Release each ran 2,182 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 11
    mutations: Enter on a button, or on a list of choices, descending into the chosen row; a folder gone since the
    session was opened opened anyway; the system's refusal, a failure, or the line saying the folder was not opened
    left unsaid; the path not copied, or its copy unsaid; and the button, or either item of the line's menu, doing
    nothing. Opening the list of choices on Enter by the window's own hand survived: a list opens on its own Enter
    once the window lets the key through, so only that is written.

- **Revision 516 — the window opens where it closed (§26.3):**
  - InterCat's window opened at its first size wherever the system put it, each launch, however a person had
    placed it.
  - Where it closes is kept in the settings file (app-settings-v1's `window` key). It opens there, as large and
    maximized if it was, inside the screen that still shows its title bar and no larger than it, or as it first
    did when none does. It is written as the window closes, never over a file that cannot be read or another
    version wrote.
  - Debug and Release each ran 2,179 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 22
    mutations: the place unread, a place not read left unsaid, the key reported as unknown, or one with another
    member, no width or a negative height taken; the place written over a file that cannot be read or another
    version wrote, or dropping the file's other keys; a window opened where no screen shows its title bar, left off
    its screen, larger than it or smaller than it can be, or measured without the screen's scale; the place not
    applied, not maximized, kept while maximized, not followed as the window moves or is resized, or not kept as it
    closes or that it was maximized.

- **Revision 515 — sessions dropped on an investigation's window (§8.4):**
  - The window took nothing dragged onto it. Sessions' folders dropped on it now join the investigation as the
    folders Add sessions chooses do, several at once; an investigation file opens in a window of its own; anything
    else is not added, which the window says.
  - An addition, relink, alignment or note that finished while the investigation was being read was never read
    again: its words were lost and the rows could miss it. A reading asked for meanwhile now waits, then reads.
  - Debug and Release each ran 2,163 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 9
    mutations: a refresh asked for during a reading skipped again; the window taking no drop, or taking text; a
    folder dropped not added, or a file added as one; an investigation file not opened; what was not added unsaid,
    dropped from the status, or unsaid when only files were dropped.

- **Revision 514 — a session or an investigation dropped on the window (§3.1):**
  - A session folder named on the command line or dropped on the program opened, but the open window took
    nothing dragged onto it.
  - It now takes files and folders, never text, while a saved session could be opened: a folder opens as Open
    saved session opens the one chosen, an investigation file in a window of its own. Anything else, or several
    things at once, opens nothing and the card says why. Open saved session's tooltip says a folder can be dropped.
  - Debug and Release each ran 2,161 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 12
    mutations: the window taking no drop; a drop taken during a capture, or text taken; a drop opened during a
    capture; several things opening the first, or said as one; a folder or an investigation not opened, or any file
    opened as an investigation; the card's refusal or headline unsaid; the drop and Ctrl+O not waiting while Open
    saved session is hidden.

- **Revision 513 — the keys that act anywhere, from the search box (§6.7, R15):**
  - The keys sheet says Ctrl+R, Ctrl+E, Ctrl+O, F5, and Alt+Left or Alt+Right act anywhere, but the window gave
    every key pressed in the search box to the box: after a search, Ctrl+E exported nothing and said nothing, and
    Alt+Left did not go back a level.
  - None of them types anything, so each now acts from the search box too, the three commands only while their
    buttons are offered; the search keeps its words and the keyboard, and a letter typed there stays text.
    §6.7's table now lists Ctrl+R, Ctrl+O and F11, and says which keys act from the search box.
  - Debug and Release each ran 2,159 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 12
    mutations: the keys that act anywhere acting nowhere, or not from the search box; Ctrl+R, Ctrl+E or Ctrl+O
    dropped, or acting without Ctrl; Ctrl+O acting while a capture hides Open saved session; F5, Alt+Left or
    Alt+Right dropped; Left or Right acting without Alt.

- **Revision 512 — a dialog the system could not show (§6.1):**
  - No flow behind a file or folder choice caught a picker that failed, so a shell that refused one, or no
    service to show it, ended InterCat with every window it had open.
  - The seam every choice goes through asks inside a guard: a picker that fails chooses nothing, as one declined
    does, and the main, investigation and content windows each say why where they say what an action did. A
    picker whose starting folder cannot be found still shows.
  - Debug and Release each ran 2,157 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 10
    mutations: a save, open or folder picker asked outside the guard; a failure chosen as nothing but never said;
    the main window's headline or detail, the investigation's or the content window's words unsaid; where a picker
    starts asked outside its guard, or asked of the platform past the seam.

- **Revision 511 — `Ctrl+O` opens a saved session (R15):**
  - Open saved session had no key, though `Ctrl+O` is where a person reaches to open anything; Start exploring
    has `Ctrl+R` and Export this view `Ctrl+E`.
  - `Ctrl+O` now does what the button does, only while the button is offered. The keys sheet lists it, and the
    button says it on hover and to a screen reader: in its words it wrapped at the rail's narrowest, and the
    smallest window's ranked list lost the start of its third row.
  - Debug and Release each ran 2,157 tests, with revision 473's 93 failures and 4 skips. The first run caught the
    key in the button's words wrapping at the rail's narrowest; the tests then caught each of 5 mutations: Ctrl+O
    unhandled, or acting while a capture runs; the keys sheet without it; and the button's hover or its screen
    reader's word without it.

- **Revision 510 — the main window named by what it shows (R15):**
  - The window was "InterCat · IPC Explorer" whatever it showed, so switching between windows listed InterCat
    windows a person could not tell apart, and a screen reader said no more as one came to the front.
  - It is now named by the session's folder, and while a capture runs by what it does - starting, recording or
    saving, with the folder once it has one - as the investigation's window is named by its file.
  - Debug and Release each ran 2,156 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 7
    mutations: the window left unnamed by an update, or still named by a session forgotten; a capture starting,
    recording with or without its folder, or saving named only as a session; and a folder given with a separator at
    its end named by nothing.

- **Revision 509 — the session folder read again as the window comes back into use (§3.1):**
  - The saved sessions and an unfinished capture's card were read when the window took its folder and after a
    capture: a session saved or deleted elsewhere, or a capture finished or forgotten in another window, stayed as
    first read until a row or the card was pressed.
  - Each time the window comes back into use it reads the folder again: the saved sessions while it shows them,
    and the card. An unchanged list is left as it is; a changed one keeps the keyboard on its row while that is
    still listed, else on the newest, and gives it where the window does once none is.
  - Debug and Release each ran 2,155 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 9
    mutations: the folder not read again on coming back, or read on the window's opening; the list read while a
    session is shown, or the card not read; an unchanged list replaced; and the keyboard not kept, kept on the
    newest rather than its own row, or not placed on its row at all. Holding the window's keyboard rule while the
    list is read again, and placing the keyboard once none is listed, survived: the rebuilt list keeps the
    keyboard's row container, and a row gone gives the keyboard where the window gives what a control leaves, so
    neither is written.

- **Revision 508 — an unfinished capture changed since its card offered it (§3.1 step 6):**
  - A capture whose evidence went since was offered Finish again and again, each refused as a "session
    directory" that does not exist; one forgotten in another window was refused as being finished there; and Open
    what was saved, its session gone, was refused while the card kept offering it.
  - A finish that does not go reads the card again: the reason stands on the card while it offers the capture as
    it did, a card that offers it otherwise says what changed, and the status says why once it no longer offers
    it, never on another capture's card. Evidence not there is named as the capture's, a ticket gone as finished
    or forgotten elsewhere, and neither refusal writes anything (live-follow-v1 §4).
  - Open what was saved asks for the session the card described: one deleted, emptied or written over since is
    said gone in the card's words, and the card goes once nothing of the capture is left.
  - Debug and Release each ran 2,153 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 15
    mutations: what was saved opened as whatever session its folder holds, its session gone left unsaid, a session
    written over said only gone, every refusal said as gone, the card not read again after an open, or a capture
    opened still offered; a finish's card not read again; its reason unsaid once the card goes, pinned on another
    capture's card, unsaid on an unchanged card, or said on a changed one; a ticket gone said as another window
    finishing, evidence not there named as a session, the session a card described left unread, and a session
    written over worded as gone.

- **Revision 507 — a saved session gone from its folder since the rail listed it (§3.1):**
  - A row stayed as the rail read it while its folder changed: one written over by another session opened that
    session under the row's name, one deleted was refused by its folder's path, one emptied as a folder no session
    was ever published in, and each stayed listed.
  - A row now opens only the session it was read from. A folder that no longer holds it says so in the row's
    words, adding that another session is there when one is, and the list is read again, the keyboard on its
    newest row; files that are there but cannot be read say why, and their row stays.
  - Debug and Release each ran 2,147 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 11
    mutations: a row opened as whatever session its folder holds, or read without its session; a session gone or
    written over left unsaid, or unsaid while the list is read again; every refusal said as gone; another session
    there unnamed, or named where none is; the list not read again; the keyboard left where its row went; and the
    open's refusal not returned. A twelfth, the opener's result reported false, survived because nothing read it,
    and the opener now returns none.

- **Revision 506 — the capture card at the rail's narrowest, in every phase (§6.8):**
  - Read at both rail widths before a capture, once one could not start, starting, recording, stopping and saved,
    with processes named by long image paths, chosen and down every rung: at the narrowest rail "Start exploring
    (Ctrl+R)" lost its shortcut before a capture and once one was saved, and "Retry exploring (Ctrl+R)" once one
    could not start.
  - Every button's words now wrap where their place is narrower than they are; the long names already wrapped or
    ended in an ellipsis their tooltips complete.
  - Debug and Release each ran 2,146 tests, with revision 473's 93 failures and 4 skips. The tests caught a button's
    words kept from wrapping. Wrapping only the primary action's survived: in no state a test reaches does another
    button's label outgrow its place, and the rule holds for every button so that none is cut off when one does.

- **Revision 505 — an investigation's merged time zoomed while its alignments change (§8.2):**
  - The window kept its zoom whatever changed: with every alignment withdrawn no session was placed and the zoom
    buttons still acted; with another session's clock made the investigation's time, the range was read on that
    clock, an unrelated stretch that held nothing; and the whole a zoom is kept within was read only while
    unzoomed, so Zoom out stopped at the investigation as it had been.
  - Each read of the merged time states the whole investigation's time and whose clock it is. A zoom is let go
    once that clock is another's or none, or no placed session meets it, and the window says why after what the
    change did; one still meeting a session stays, and zooms within the investigation as it is now.
  - Debug and Release each ran 2,145 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 9
    mutations: a zoom kept across a change of clock, or where no placed session meets it; the whole kept only while
    unzoomed, or read as the interval shown; a change of clock said as no session meeting the zoom, a lost clock
    said at all, the reason unsaid, or said in place of what the change did; and the time reference left unread.

- **Revision 504 — a rolling capture's publications under a person's navigation (§6.7, ADR-045, R21):**
  - The timeline kept its zoom across every view it was given: a release that gave up the time in view left it
    drawing time the session no longer held while the ranking counted the whole session, and a session shown
    afresh opened at the previous one's zoom, often a range wholly outside its own time.
  - A zoom carried to a newer generation stays inside the time the session keeps - moved, at its span, to the
    earliest time kept, or the whole session where wider - and is said; another session, a capture's first view
    and a session opened again open whole.
  - What a release took was said twice and in the store's words. Each change is now said once: the rung gone by its
    breadcrumb and where the view went, the chosen lane's mechanism, the selected process, and the analysis
    interval cut or cleared.
  - The live preview's card said exact records come about every 2 s; it says the capture's own interval - 5 s for
    one keeping its last hour - and none it was not told.
  - Debug and Release each ran 2,143 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 21
    mutations: a zoom left in time a release gave up, or never whole again; a session shown afresh, another or the
    same, kept at the zoom before; the view's move unsaid, said of a whole view or with no release, or said as the
    wrong move; the selected process said twice, or only beside a rung gone; the rung gone unsaid, in the window and
    the view model, or said gone back to the wrong rung; a lane's mechanism, or the analysis interval cut or
    cleared, unsaid; the rung's own time said beside the interval again; and the live preview's card stating 2 s, or
    no rate where the capture said 5 s, its interval lost in any of three places. Three survived at first, where the
    test's second session was too short to hold the first's zoom, so keeping a zoom inside a session fitted it
    anyway; a longer one caught them, and showed that the capture's waiting view needed no reset of its own, which
    is gone.

- **Revision 503 — a next page that cannot be read (§3.2, R21):**
  - E's records, an RPC channel's calls and an HTTP channel's exchanges, a first page listed and the next
    unreadable: the rail and the inspector said all of them were unavailable while the first page stayed listed,
    why was said nowhere, and Load more was gone.
  - A next page that cannot be read now leaves the rows listed; the rail says it could not be read and the
    inspector why, and M asks for it again. A scope's first page that cannot be read no longer leaves the previous
    scope's rows on screen.
  - Ctrl+E with nothing to export says why, as the button's tip does, telling records that could not be read from
    records still being read and from a scope holding none.
  - Debug and Release each ran 2,138 tests, with revision 473's 93 failures and 4 skips. The tests caught 18 of 19
    mutations: for E's records, a channel's calls and its exchanges, a next page that failed said as all unavailable,
    not asked for again, left unsaid, still said once read, or said with the first page's sentence before its reason;
    the inspector without why; Ctrl+E without why; and the tip saying records not read, or none in scope, are still to
    come. The previous scope's calls left on screen when a new scope's first page failed survived: no test can have a
    scope counted while its calls cannot be read.

- **Revision 502 — an investigation's member changed under its window (§8.4, ADR-038, R22):**
  - Another session written into a member's folder, or the folder emptied, after the investigation read it: Open and
    a column of the merged time opened the folder as it stood - InterCat's window showed the other session - and the
    investigation said it had opened its member.
  - A member is now opened by its identity: InterCat's window refuses another session or none, in revision 501's
    words, and keeps what it showed; the investigation reads its sessions again and says why, as the row says it.
  - Another session at a member's path, and a folder holding no session, now end with relinking the member where it
    is now; the latter no longer says that no session was published there. A screen reader says the step once.
  - Debug and Release each ran 2,134 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 11
    mutations: an emptied member's folder said as never published, another session's or a moved member's reason without
    its step, the step said twice or never; a member opened, or its column, as whatever its folder holds, or kept as the
    session shown by its path alone, or its store opened by path; and a member that did not open said without reading
    its folder again, or without why.

- **Revision 501 — a session's folder written again or emptied under the window (§20.1, R21):**
  - Every read said the session identity had changed after its store was opened; an emptied folder, that no
    session was ever published there. A lease now says the folder holds another session, and to open it again,
    or that its session's files were moved or deleted.
  - The registry handed a reader of one session the store of whatever the folder held: Keep records offered the
    other session's pins and placed its pin there, Share original session would have packaged it, and counting
    candidates did nothing without a word. It now refuses, in the same words, and every read of the shown session
    asks for it by its identity; the window's own checks, in three wordings no read reached, are gone.
  - A choice of evidence policy that cannot be counted says why on the capture card; a zoom whose own count cannot
    be read says the overview's columns stand in, and why.
  - Debug and Release each ran 2,132 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 20
    mutations: a lease saying the old words, or that no session was published where one was read - by its manifest, its
    guard or its folder gone - or a store that never read one saying it is gone; the registry handing out a store of
    another session, kept or opened afresh, saying another session for none, or a removed folder raw; the evidence
    source asking for no session; a zoom's failure unsaid, kept after a count, or dropped from either caption; the
    highlight repeating the zoom's reason; the package's preview or copy, or the channel browser, reading any session; a
    policy choice's failure unsaid; and an original record's refusal in other words.

- **Revision 500 — a read that fails says so, never that there is nothing (R21, §6.7):**
  - With a shown session's files damaged, a process's rung said it had no channel where its RPC calls, HTTP
    exchanges or connections could not be read. It now says which could not be read and why, once for each reason,
    in the reason it is empty and both summaries, and promises nothing one step away.
  - A selection whose highlight could not be counted left the caption saying it was still counting; it now says it
    could not be, with why unless the caption already says it.
  - Ctrl+click and Ctrl+Space on a channel's, a call's or a record's row, which join no selection, are left to the
    list, which chooses the row; they were taken and did nothing.
  - Debug and Release each ran 2,126 tests, with revision 473's 93 failures and 4 skips. The tests caught each of 12
    mutations: a failed read said as nothing to list; the RPC calls', HTTP exchanges' or connections' failure left
    unsaid; each failure said apart where one reason failed them all; the failure unsaid in the rail or the inspector;
    the highlight still counting, its reason repeated or dropped; and Ctrl+click or Ctrl+Space taken on a row that joins
    no selection.

- Revisions 230–499 are in [the third historical log](history/IMPLEMENTATION-STATUS-revisions-230-to-499.md), whole and in order.
- Revisions 104–229 are in [the historical log](history/IMPLEMENTATION-STATUS-revisions-104-to-229.md), whole and in order.
