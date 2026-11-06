# ADR-096: A service container is the instance hosting listed services, and any other process keeps its executable's group

- Status: accepted for M6's process enrichment; metrics and `icat metric` group by service container (revision 999)
- Date: 2026-11-06
- Decision owners: InterCat maintainers
- Relates to: ADR-095, plan M6, §3.2, §6.3, `metrics-v1` §6, `query-identity-v1` §4, R3, P6

## Context

§6.3 groups a session's processes by executable, service container, host or user session, and §3.2's group rung lays one
lane per process group - executable, service container, session. `metrics-v1` §6 had listed a grouping by service
container as not derived. Since ADR-095 every live capture keeps the services the service control manager lists running as
it starts and stops, each bound to the process instance hosting it by its PID and creation time (`service-binding-v1`).
Many services share a host: on the measured machine 129 services ran in 118 processes (FX-SVC-002), most of them
`svchost.exe`, which a grouping by executable gathers into one group, so the traffic of a hundred hosts is one row. A
service list stays metadata of its host: an IPC operation of a shared host is never attributed to one of its services
without evidence naming it (ADR-095, decision 4).

## Decision

1. **A service container is a process instance that hosted a service its capture's lists name**, bound by
   `service-binding-v1`. Grouped by service container, its records are one group of their own, keyed by the instance and
   named by its executable and the services it hosted - never a group for one service, whose share of its host's records
   no record measures.
2. **A process that hosted none of the services listed keeps its executable's group**, formed as a grouping by executable
   forms it, so a ranking reads each service host apart and every other program as before. A process whose full image path
   was not witnessed stays `ExecutableUnknown`. The groups still partition the total: every record belongs to one container,
   one executable or one stated reason.
3. **A process a listed service may have run in, though the binding cannot tell, is unattributed by a reason of its own,
   `ServiceHostUnknown`**: a service bound to no instance whose PID is the process's, where the service's host's creation
   time, or the process's own, was not read. A service whose host's creation time was read and is another's - an earlier
   holder of the PID - says nothing of this process. It is never placed in a container by its PID alone, and never taken
   for one hosting none (R3).
4. **A capture that kept no list of its services is not grouped by service container** - an import, a redacted package, a
   capture made before its services were read: `GroupingNotDerived`, never every process taken for one hosting none.
5. **An answer grouped so names both entity rules in its identity**: its `entityRevision` is the process binding rule and the
   service binding rule, joined by a colon (`query-identity-v1` §4), so a new service binding rule changes its identity.
   Every such answer was unavailable before, so no earlier identity changes.
6. **The binding is the analysis layer's**, since a metric groups by it; `service-binding-v1` is unchanged, and the window
   and the command line say a process's services in the same words as before.

## Consequences

- `metrics-v1` §6 derives `ServiceContainer`: a total, a count of peers or channels, and a count or duration of calls group
  so, and `icat metric --group-by service-container` names each container by its host's PID and image and its services.
- Three tests hold totals, peers, channels and calls grouped so, and the command's words; 29 mutations of the path are
  caught.
- The group rung's lanes and the window's grouping by service container, and a saved layout keeping it, are the next
  slices'.
