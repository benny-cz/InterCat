# ADR-114: An evidence page reads only the rows its scope's postings place

- Status: accepted for M5's scale tiers (§12.1 S3, and T1's budgets cold); an owner's or a channel's evidence page reads
  the rows the tile index's postings place since revision 1066
- Date: 2026-11-18
- Decision owners: InterCat maintainers
- Relates to: ADR-054, ADR-113, plan §12, §12.1 S3, `contracts/tile-index-v1.md` §4, `bench/results/scale-tiers-20261118T142911Z`, I4, I14, R22, S3

## Context

Revision 1065 brought every query the scale gate measures within its §12 budget at T1 to T3, warm. Cold, a process's
first evidence page still took 306 ms at T1, which promises every budget cold as well as warm, against 150, and a
channel's 191. A page merges its session's segments in canonical order and decides each row by its scope: to list 100
records of one of the scale gate's 400 processes, which take turns record by record, it visited some 33,000 rows, and
before deciding the first it bound all 250,000 of its segment's rows to their owners. A probe written for the
investigation, and removed, found the cold page's time in that binding, in the rows it visited, and in code running for
the first time, which runs unoptimized until the runtime has seen enough of it and made each visit dear. A process with
few records spread across a session fared worse: its first page opened and bound every segment until it had found a
hundred records, a scan of the whole session that S3 rules out. The postings revision 1064 keeps hold what such a page
needs: the reading of each of an owner's and a channel's records, section by section.

## Decision

1. **An owner's or a channel's page reads the rows its postings place**: where the generation's tile index posts its
   owners and channels and describes each segment under the derivation the page binds with, a page of a process, a
   group, a paired channel, a one-sided connection or a host takes, for each segment it reaches, the readings its
   section posts for the scope - every list of its owners' keys, whatever their binding's strength, or of its channels -
   and reads only the rows holding those readings, found by a binary search of the segment's session times, which never
   go back where its section posts. A reading two rows share brings both.
2. **A segment holding none of the scope's records is not opened.**
3. **The page's filters still decide each row** - its policy, time scope, narrowing and each row's binding - so its pages
   are exactly those reading every row lists, and a cursor holds across the two.
4. **A row read alone is bound alone**: its owner, channel and end, exactly as its segment's rows bound together are, so
   a page reading a hundred rows binds a hundred; a page of an RPC or HTTP operation's rows binds its rows so too.
5. **Every other row is read as before**: a section posting nothing - an index of an earlier minor, a segment whose
   readings go back, a live session's chunks - or holding an untimed record, which no list posts, has its segment's rows
   read; postings that cannot be read, or that place a reading none of their segment's rows holds, refuse the index,
   said once, and the segments after them are read whole (I4); an index stating another derivation is not read (R22).

## Consequences

- Measured at T1 to T3 (`bench/results/scale-tiers-20261118T142911Z`, three runs of each tier in processes of their own, the machine 12 to
  36% busy before them): a process's first evidence page took 129 ms cold at T1, at the median of the runs,
  from 352, and a channel's 13, from 222, within §12's 150 ms, so every query the gate measures answers
  within its budget cold at T1, as T1 promises, which `ScaleTierTests` holds; at T2 and T3, which promise their budgets
  warm, a process's took 191 and 169 ms cold. Warm, the ten pages took 20, 21 and 41 ms for processes and
  29, 34 and 47 for channels at T1's, T2's and T3's 95th percentile, from 92, 99 and 121 and 74, 82 and 41.
- A first page visits about a hundred rows where it visited every row up to its hundredth record, and a process with
  few records opens only the segments holding them.
- What a cold page still holds is its first segment opened, every column of its first row read, since a record is read
  whole, and the code it runs for the first time.
- `contracts/tile-index-v1.md` §4 says how a page reads postings; their format is unchanged.
