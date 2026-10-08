# ADR-046: A pin keeps a session's records from a moment, within what it allows

- Status: accepted; revision 442, the window revision 443
- Date: 2026-10-08
- Relates to: §10.1, §12.1 S5, S6, §20.2, I18, IC-016, ADR-010, ADR-024, ADR-036, ADR-043, ADR-044, ADR-045,
  `contracts/store-v1.md` §8

## Context

I18 says a pinned or open evidence reference is never invalidated by retention, and §10.1 that pinning a snapshot must
either reserve disk space or materialize the selected evidence, never promising an indefinite pin within a strict
circular quota.

What existed was a lease. A pinned lease holds one generation's files, declares an allowance and is refused when it
would reserve less than it holds, but it lives only in the process that took it and for seven days at most. It keeps
files from being removed, not records from being released. Since ADR-045 a follow keeping a rolling window releases
whatever was read before the window, and nothing a person could do kept an interval they had found worth keeping: the
next release gave it up.

A release takes only a leading run of units (ADR-024, ADR-043): a middle interval cannot be released while what lies
before and after it stays. Keeping an interval therefore keeps everything after it too, and a session that keeps a
rolling window grows past it while anything is kept.

## Decision

1. **A pin keeps every record read from its moment.** A pin names a session time, and while it stands no retention
   gives up a record the session holds that was read at or after it. An interval release asked for past a pin plans to
   the pin: every record it gives up was read before the earliest pin, and it says that a pin stopped it, or that a pin
   is why it gives up nothing. A release by record number - a journal prefix or a recording's oldest chunks - cannot say
   when its records were read, so none is made while any pin stands; the interval before the pin is released by session
   time instead.
2. **It stands beside the generations.** The pins are a file of their own in the session's folder,
   `retention-pins.json`, replaced whole under `retention-pins.lock`. They are not a generation's dependency: a person
   pins while a follow writes the session, and a generation any writer but the follow's publishes fails the follow
   (ADR-024, ADR-044). Every release that could give up a pinned record holds the pins lock until it has published, and
   checks the pins under it, so a pin placed while a release planned is either honoured by that release or placed
   against the generation it published.
3. **It cannot keep what is gone.** A pin before the latest interval release's boundary is refused, naming the earliest
   moment a pin keeps from: the records before it were released, and a pin brings nothing back. It keeps what the
   session holds when placed; records an earlier release by record number gave up stay given up, their rows kept.
4. **It declares what it allows.** A pin states the most the session may hold while it stands, measured as the session's
   size is - every file its generation names - and is refused when the session already holds more. `icat pin` allows
   what the session holds now unless told more.
5. **A rolling follow stops rather than outgrow a pin.** A pin whose moment lies before what a rolling policy is due to
   release holds the policy back, and the follow says so once. Once the session holds more than such a pin allows, the
   follow stops - `icat capture` stops its capture - rather than release what the pin keeps or grow past what the person
   allowed, and says which pin stopped it and how to go on: remove it, or pin from the same moment allowing more. A pin
   within the window holds nothing back and stops nothing. A follow that keeps every record grows by its capture,
   which its own limits bound, not by a pin.
6. **Pins that cannot be read refuse.** A pins file that is damaged, of a later format or another session's refuses
   every release that could give up a pinned record and every change to the pins, rather than release what it might
   keep or write over the pins it holds; a rolling follow stops, since neither what they keep nor what they allow is
   known.
7. **Content is not held by a pin.** A pin keeps records and their rows. The content kept of their messages is
   restricted evidence a person may give up on its own (ADR-036), so a content release is made whatever pins stand, and
   `icat pin` says so where the session keeps content.

## Consequences

- A person can keep what a rolling capture found, without stopping it, for as long as the pin's allowance lasts, and a
  session never grows past what a person allowed because of a pin.
- `icat pin` lists, places and removes pins; `icat session` lists them, `icat retain` says when a pin stopped a release,
  and the follows say when one holds them past their window and when one stops them.
- Since revision 443 the window offers the same pin from its time scope's start, lists and removes the pins standing,
  and states them in its size line, in `icat pin`'s words (R18).
- Pins are not copied into a package or an investigation: a pin is a person's hold on one session's retention, and a
  package is a new session with its own.
- A pin keeps everything after its moment, since releases take leading runs. Materializing an interval into a package
  of its own, as §10.1's other branch allows, is not offered: it would copy evidence the session already holds.

## Alternatives considered

- **A pin in the manifest.** Rejected: publishing a generation beneath a running follow fails the follow, and a pin is
  most needed while a capture records.
- **A pinned lease that outlives its process.** Rejected: a lease holds files of one generation and never moves to a
  later one; a release publishes a later generation without them, and a lease would only defer their removal.
- **Let the pin lapse when its allowance runs out.** Rejected: a pin that can stop keeping its records is not one (I18).
- **Keep growing past the allowance.** Rejected: §10.1 forbids an indefinite pin within a strict quota.
- **Release around the pinned interval.** Rejected for now: a release in the middle of a recording would leave its
  chunks describing a capture with a hole in it (ADR-024).
