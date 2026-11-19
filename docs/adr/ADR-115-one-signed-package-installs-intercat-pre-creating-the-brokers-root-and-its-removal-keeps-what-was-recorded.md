# ADR-115: One signed package installs InterCat per machine, pre-creating the broker's root, and its removal keeps what was recorded

- Status: accepted for M5's installer; the broker's install-time commands since revision 1083, the package, built
  from `installer/` and test-signed, since revision 1084, Restart Manager kept from closing a recording broker since
  revision 1085, its checks on a second machine to follow
- Date: 2026-11-19
- Decision owners: InterCat maintainers
- Relates to: ADR-007, ADR-027, ADR-052, plan §9.2, §20.5, §25, M5, `contracts/broker-v1.md` §5.4, §5.5 and §5.10,
  `src/InterCat.BrokerStage.targets`

## Context

The broker writes only beneath its root, `%ProgramData%\InterCat`, created with its protected descriptor and
high-integrity label by the creating call (§9.2, broker-v1 §5.4). Until now only a serving broker created it, on the
first live capture. `%ProgramData%`'s default ACL lets any user create an entry there first, and a broker that finds the
root owned by an untrusted principal refuses rather than adopting it, so on a machine nothing installed a user could
squat the name before the first capture and every capture would then be refused; §20.5 asks installation to pre-create
the root. M5's exit gate asks for a signed installer and for installation, update, uninstallation and capture cleanup
checks, and §25 for a release whose installation, update, uninstall and capture cleanup leave no orphaned session,
driver, service or evidence the user did not ask to keep.

The Desktop and the CLI each stage the broker's whole output beside themselves (revision 88), and each finds its broker
there. Built together, the files the two clients share are byte for byte the same - 31 of the Desktop's 86 and the CLI's
36, none differing - so one folder can hold both clients and the one broker they launch.

## Decision

1. **One per-machine Windows Installer package** installs the Desktop, the CLI and the broker into one folder under
   `Program Files`, framework-dependent on .NET 10 as the clients are published, with a Start menu entry for the Desktop
   and the folder on the system path for the CLI. It is authored in WiX and built from the clients' framework-dependent
   publish, refusing a build whose two clients would put different bytes under one name.
2. **Installing pre-creates the broker's root.** A deferred custom action running as Local System starts the installed
   broker as `provision-root --owner-sid <the installing user>`, which provisions the root exactly as a serving broker's
   first step does (broker-v1 §5.4): created with its final descriptor, an existing one adopted only from a trusted owner
   and its drifted security re-applied, a squatted one refused with its owner named. A refusal fails the installation,
   which rolls back; a root the installation created and then rolled back is removed if it holds nothing.
3. **Removing InterCat stops nothing recording and leaves nothing running.** Before its files are removed, the package
   starts the broker as `cleanup --owner-sid <the removing user>`. While another broker holds the ownership log a capture
   may be recording, so cleanup refuses (exit 6) and the removal - or the update whose removal of the old version it is -
   fails and rolls back, the capture untouched: the user stops it, then retries (§20.5's deferred update). Otherwise it
   runs the recovery a starting broker runs, stopping every session a previous broker left recording, and refuses
   (exit 1) if any session it owns was not proven stopped. Restart Manager, which Windows Installer runs at
   InstallValidate and which in a silent removal closes the processes holding files in use - a recording broker among
   them, before cleanup could refuse - may find InterCat's files in use but never shuts a process down
   (`MSIRESTARTMANAGERCONTROL=DisableShutdown`, since revision 1085).
4. **Removal keeps what was recorded unless asked.** A root holding no capture directory and nothing but the ownership
   log is removed with the log; a root holding captures is kept whole, captures, log and all, and said to be. Removed
   with the property `INTERCAT_REMOVE_EVIDENCE=1`, outside an update, cleanup runs as `cleanup --remove-evidence` and
   removes the root with every capture in it. Sessions a user derived in their own folders are never touched.
5. **Every binary InterCat builds, and the package, carry an Authenticode signature.** Until a release certificate
   exists they are signed with a self-signed test certificate made for the purpose, trusted only on the machine the
   checks run on and never on the development machine, and not timestamped; the M5 review states both as the release
   gap they are.
6. **The checks run on a second machine**: install, the root's security read back, a capture through the installed
   broker, an update over it, a removal refused while a capture records, a removal keeping recorded captures and one
   asked to remove them, each followed by a look for anything left - files, the path entry, a broker process, an
   `InterCat-` trace session. The development machine's production root is never provisioned by them.

## Consequences

- A user can no longer squat the root on an installed machine: the name is taken, with its final security, before any
  user can act, and a squat that happened before installation fails the installation instead of the first capture.
- An installed machine's root is readable by the user who installed, until a capture by another user re-applies it for
  them, as a serving broker always has.
- Uninstalling while a capture records fails rather than ending the capture; the message Windows Installer shows is its
  own, and the installer's log carries the broker's sentence.
- A file still in use when InterCat is removed or updated - the window left open - is replaced at the next restart,
  never freed by closing what holds it.
- Captures outlive InterCat's removal unless the remover asks otherwise, in a folder only an administrator can delete;
  removing them is never a default.
- A machine without the .NET 10 Desktop Runtime cannot run what is installed until the runtime is added; the package is
  framework-dependent because the clients are (revision 88), and a self-contained package waits for a self-contained
  broker.

## Alternatives

- **An archive or per-user installation.** Neither can pre-create a root in `%ProgramData%` with an Administrators owner
  and a High label without elevation, so neither answers §20.5.
- **MSIX.** Its container virtualizes `%ProgramData%` writes and does not run a custom action as Local System, so it
  cannot pre-create the root the broker validates; a desktop bridge for an elevated ETW broker adds restrictions this
  release need not take on.
- **Removing every capture on uninstall.** Evidence is what a user recorded on purpose; deleting it because the tool was
  removed would lose it without asking, which §25 forbids as surely as leaving debris.
- **Killing a running broker on removal or update.** It would end a recording the user started without their consent,
  which §20.5 rules out; refusing defers the change to a moment the user chooses.

## Reversal cost

Moderate. The package is a build artifact and the broker's two install-time commands are additive; a later package
format could call them as this one does. What would not reverse is a removal's handling of captures already performed
on a user's machine, which is why removal keeps them by default.
