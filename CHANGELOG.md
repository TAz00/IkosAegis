# Changelog

All notable changes to IkosAegis are recorded here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning is [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Removed — the legacy PIN encryption path is gone

`Encryption` had already stopped encrypting anything; it survived only to decrypt blobs written
by builds that did, so a craft locked under an older version could still be opened. That
migration path is now deleted along with the class, its tests, and the `System.Security`
reference that DPAPI needed.

**Nothing to migrate.** The encrypting builds were never published — no release exists that
wrote a blob, so keeping a reader for one was carrying a compatibility burden against zero
saves. It cost more than it looked: every path that touched a PIN also had to distinguish
*cannot read this* from *no PIN set*, and conflating those two unlocks somebody else's craft.
Deleting the unreadable case deletes the conflation with it, which is why `ReconcileStoredState`,
`ToggleLockEvent` and `Engage` each lost a guard rather than gaining one.

PIN storage is unchanged — plain text, shareable, exactly as before.

### Added — a refused docking approach now says so

The docking guard logged nothing, deliberately: it runs every frame a port is in range, and a
locked craft parked beside a station would have filled the log.

The reasoning was right and the conclusion was wrong. It made **a refused dock and a dock nobody
attempted look identical**, so a two-machine test session that should have settled whether the
block works produced no evidence either way. Now throttled to one line per port every 15
seconds, which buys the visibility without the spam — the same answer the recovery guards
already use.

## [0.2.0] - 2026-08-20

### Fixed — the LMP bridge threw on startup and never connected

The bridge shipped dead. `Could not connect the Luna Multiplayer bridge -> NullReferenceException`
on every launch, so nothing was ever sent or received.

**Subscribing a `static` method to any KSP `EventData` throws.** `EventData.Add` wraps the
delegate in an `EvtDelegate` whose constructor does `evt.Target.GetType().Name` with no null
check, and `Delegate.Target` is null for a static method. The handlers were `private static`,
so the subscription threw inside KSP's own constructor before it attached. Nothing to do with
LMP, load order, or a race — it would have thrown on `GameEvents` just the same.

- Handlers are now instance methods on a held `Handlers` object.
- **The give-up rule was wrong too.** One exception set `_events = null` and disabled the bridge
  for the whole session. Now bounded at 5 attempts, and the log says which attempt and whether
  it is still retrying. (It would not have rescued this particular bug, which threw every time —
  it is there so a genuine transient during load does not cost the feature.)

This is the same mistake that was fixed once already in `AegisAddon`, where static `GameEvents`
handlers had to become instance methods. All five of those are still instance methods; this file
was the only regression.

### Fixed — exception logs had no stack trace

`AegisLog.Exception` printed the stack only at `Debug` level, so the bug above reached a report
as one line: `NullReferenceException: Object reference not set to an instance of an object`.
True, and useless — finding the cause needed a disassembly of a KSP type. Stack traces now log
at error level alongside the message. Nothing here is on a hot path.


### Fixed — another player's lock did not reach you, so you could dock to their locked craft

Two players in the same scene: one engages the lock, the other docks to them anyway. The lock
was never wrong — **it never arrived.**

Luna Multiplayer moves part state as the savegame's `ProtoVessel`, and a
`[KSPField(isPersistant = true)]` only reaches that snapshot when the game *saves*. Between
saves, `isLocked` existed in one client's memory and nowhere else.

Worse, it failed asymmetrically, which is why it looked so strange: LMP applies an incoming
field change to the `ProtoPartModuleSnapshot` and **nothing else** — it never writes the live
`PartModule`, and nothing inside LMP subscribes to the `...FieldProcessed` events it raises
afterwards. `VesselIsLocked` prefers loaded modules and only falls back to the ProtoVessel when
there are none, so the lock held in the tracking station (nothing loaded, proto read) and did
not hold in a shared flight scene (modules loaded, and they said "open").

- **New `LmpBridge`** wires up both halves. Outgoing: fires
  `PartModuleEvent.onPartModuleBool/StringFieldChanged` from `AegisAddon.SyncVessel`, the one
  choke point every state change passes through. Incoming: subscribes to the matching
  `...FieldProcessed` events and writes the value into the live module, which LMP does not do.
- **Announced per module, not per vessel.** LMP addresses a change by `part.flightID` plus
  module name, so one message per craft would update one pod and leave the others disagreeing.
- **Bound by reflection**, so IkosAegis still builds and runs with no LMP installed — the only
  compile-time type involved is KSP's own `EventData`, which keeps the handlers strongly typed.
  Binding is retried each frame until it succeeds, because LMP creates those event objects in
  its own `Awake` and two `[KSPAddon]`s have no ordering guarantee.
- A re-entrancy flag stops an applied remote value being announced straight back, which would
  have two clients trading the same value forever.

`pinLength` is deliberately not synced: it is `isPersistant = false` and comes from the part
config, so it is already identical everywhere.

> LMP's *declarative* route — XML in `GameData/LunaMultiplayer/PartSync/` — cannot do this job.
> Its transpiler compares field values at method entry and exit and only instruments
> Update-family methods, `[KSPAction]`s and `guiActive` `[KSPEvent]`s. **Our unlock happens in
> the keypad's callback**, after `ToggleLockEvent` has returned, so locking would have synced
> and unlocking would not — worse than no sync at all.

### Fixed — misleading indentation in the stand-down path

Three statements were indented as though they were inside a braceless `if`; only the first
actually was. The behaviour was correct and the layout said otherwise, which is how the next
reader introduces a real bug.


### Added — "clear launch site" was a free bypass

`LaunchSiteFacility.ClearSite` is the fourth way a craft leaves a save, and the one that does
not look like recovery. Park something on or near a pad, press launch, and KSP offers to clear
the site; it then walks `LaunchSiteClear.GetObstructingVessels()` and calls
`ShipConstruction.RecoverVesselFromFlight` on each one directly — no Recover button, no
tracking station, no marker, and no dialog naming the craft.

Unguarded, that was the cheapest attack in the game: park a rover beside someone else's locked
craft, press launch, clear them both away.

- It is now guarded like every other exit, with the same PIN prompt and the same 45-second
  grant.
- **The launch-site exemption still applies per vessel**, so a craft genuinely sitting *on* the
  pad or runway clears without a prompt — that exemption exists precisely so the mod can never
  leave an immovable obstruction on a launch site. Only a locked craft parked *near* the site,
  which `landedAt` does not call a launch site, is guarded.
- Refuses on the first locked obstruction rather than gathering them all: the keypad can only
  ask one PIN at a time and a grant is per vessel, so a site blocked by two locked craft takes
  two rounds.

### Fixed — the guards could not be told apart from not running

A KSC-scene recovery of a locked rover went through and the log said **nothing at all** — no
refusal, no exemption, no line. That is not one bug, it is an inability to see: "the Harmony
prefix never attached" and "the craft was not locked" produced byte-identical logs, and no
amount of reading could separate them.

- **Startup now lists what Harmony actually attached to** (`Guarded methods: …`), and *warns*
  when the list is empty. A prefix that fails to bind throws nothing and warns about nothing,
  so until now nothing in a log could rule it out.
- **`MayProceed` logs every branch, including the boring one.** "Allowed, it is not locked" is
  now a line, with where the answer came from — live modules, the saved ProtoVessel, or neither.
  These run on a button press, so the cost is a handful of lines per session.
- `AegisAddon.DescribeLockSource` reports that provenance. "Not locked" had three very different
  causes that all read the same: no Aegis module on the craft, modules present and open, or
  nothing readable at all. Only the third is a bug.

The general lesson, and the third time this project has paid for it: **a guard that is silent
when it works and silent when it never ran cannot be debugged from a log.**


### Fixed — locked docking ports stayed dead in the save, forever

Docking to a locked craft was refused correctly. Then, once the craft was unlocked, **docking
never worked again** — not after unlocking, not after re-locking and unlocking, not in a new
scene.

**The block was written into the savegame.** Ports were disabled by pushing the port's
`KerbalFSM` into its `st_disabled` state — the game's own mechanism, which is why it looked
right. But `ModuleDockingNode` saves that state by hand:

```
OnSave:  node.AddValue("state", fsm.currentStateName);
OnLoad:  state = node.GetValue("state");   // then lateFSMStart
```

Note it is **not** a `[KSPField(isPersistant = true)]`, so listing the module's persistent
fields says it is not saved. It is. So the port came back disabled after any unload — while
the in-memory record of "we disabled this" did not survive, being cleared on every scene
change. From then on the port was stuck: the disable path skips a port that is already
disabled, so it was never re-recorded, and unlocking never re-enabled it.

- **Docking is now blocked at runtime and writes nothing.** A Harmony postfix on
  `ModuleDockingNode.FindNodeApproaches` returns null while either vessel is locked, so the
  FSM's acquire transition simply finds nothing to dock with. Checked in both directions,
  because docking is symmetric.
- **Existing saves are repaired.** On load, a port that is disabled and *not*
  `ShieldedFromAirstream` is re-enabled once, and says so in the log. A fairing- or bay-
  shielded port is left alone, because that is the game disabling it for a real reason.
- The old code also logged `Re-enabled N docking port(s)` unconditionally while the method
  doing the work silently gave up on a destroyed node — so the one line that would have
  revealed this was reporting success it had not achieved. Same fault as two other log lines
  fixed this week.

**The rule this is really about:** *a transient condition must not be expressed through
durable state.* "This craft is locked right now" is a runtime fact; the FSM state outlives the
session. The same reasoning retired machine-bound PIN encryption a day earlier.


### Removed — per-machine PIN encryption

**A PIN can be shared again.** PINs are stored in plain text and work on any machine, for
anybody you give the code to.

The encryption added a few days earlier hid the PIN in a synced multiplayer save, and in doing
so broke the thing a PIN is mostly *for*: you could not hand a crewmate the code so they could
fly your craft, because their client could not decrypt the stored value to compare against. A
code that only works on the machine that set it is not a PIN, it is a per-machine binding
wearing a keypad.

Worth recording what was actually traded away, because it is less than it appears: **a
three-digit code cannot be protected at rest by anything that must also verify on somebody
else's machine.** A salted hash falls to a thousand offline guesses. At this length the
secrecy was largely notional; the shareability is real. If secrecy is ever wanted back, it
needs a much longer minimum PIN and a slow KDF — not a return to machine-bound keys.

- **Existing encrypted locks migrate automatically.** The owner's next load decrypts the blob
  once and stores it in plain text; other clients leave it alone until then, because guessing
  is impossible and clearing it would unlock somebody else's craft. A regression test covers
  that round trip, since breaking it would strand a craft with a code nobody can type.
- `Encryption` survives as a **legacy decryptor only**. Nothing in the mod encrypts a PIN; a
  new call to `Protect` re-introduces the pitfall above.
- The startup log now states the storage plainly every run, so a server owner cannot be
  surprised by it.

### Fixed — terminating with the correct PIN soft-locked the tracking station

Entering the right code and pressing Terminate again did not delete the vessel, and left the
tracking station unusable: the scene stayed blocked and the *Leave* button greyed out, with no
exception and nothing in the log to explain it.

**One button press passes through two guarded methods.** Terminating goes
`BtnOnClick_DeleteSelectedVessel` → KSP's confirmation dialog → `OnVesselDeleteConfirm`, and
both were patched. The PIN grant was single-use, so the button spent it, the confirm step then
found none, refused, and skipped the original — which is where
`SpaceTracking.OnDialogDismiss()` lives. The dialog was never dismissed, so the modal stayed
logically open.

Two changes, because there were two faults:

- **The grant is checked, not consumed.** Its window is already bounded by 45 seconds and
  cleared on every scene change, so single-use bought nothing the clock did not already
  provide and cost the ability to finish a two-step interaction. *A one-shot token is only
  safe when exactly one thing consumes it, and a UI flow is not one thing.*
- **A refusal at the confirmation now dismisses the dialog itself.** Skipping a stock method
  means skipping its teardown; the same rule that ruled out replacing `FlightEVA.Spawn`
  applies here. *A refusal the host UI cannot express is not a refusal, it is a broken screen.*

The warning it logged was also wrong, and worth noting because it sent the diagnosis the wrong
way for a moment: it read *"the button prefix did not run, which means something else drove
the dialog"*, when the log two seconds earlier showed the button prefix running and allowing
the termination. It had assumed the only way to arrive unauthorised was an external driver.

### Fixed — Luna Multiplayer: a locked craft was neither locked nor safe

Three separate defects, found by another player flying and then deleting a locked vessel.
Worth stating plainly: **LMP synced the mod's state perfectly.** The server's copy of the
vessel carries `isLocked = True` and the encrypted `pinCode`. Every one of these was our
enforcement failing on a client that already had the correct state.

- **The control lock silently stopped existing, and the mod never noticed.**
  `ControlLock.Acquire` began `if (HeldKeys.Contains(key)) return true;` — trusting our own
  bookkeeping instead of the game. `InputLockManager` is a global stack anyone may clear, and
  LMP clears it from a method named `DeleteAllTheControlLocksSoTheSpaceCentreBugGoesAway()`,
  plus a second `ClearControlLocks()` in its KSC-marker patch. After either fired our set
  still said "held", the per-frame reconcile short-circuited, and the lock was never
  re-applied: **the craft flew normally while the mod reported it locked.**

  `Acquire` now reads the lock back from `InputLockManager` on every call and re-applies when
  it is missing, logging when it had to. The verification already existed — it was just
  skipped on every frame after the first, which is precisely when it mattered.

- **An input lock cannot stop a control state written in code, and LMP writes one.**
  `InputLockManager` blocks the *player's input*. `Vessel.OnFlyByWire` is a callback list KSP
  invokes while building the control state, after input is read, and LMP's
  `VesselFlightStateSystem` uses it to apply the controlling player's interpolated
  `mainThrottle`, pitch, roll and yaw to remote vessels. The lock and the write never meet —
  which is why the throttle came up on a craft nobody had unlocked.

  New `ControlNeutraliser` attaches to every loaded locked vessel and zeroes its control state
  each frame, re-registering itself if anything joins the callback list after it. Not an
  LMP-specific fix: MechJeb, kOS and every autopilot drive vessels the same way, so this
  closes the general hole where "locked" meant "locked against a human at this keyboard".

- **A locked vessel could be terminated from another player's tracking station.** Recovery was
  guarded and *termination was not*, which is a hole big enough to drive a craft through:
  someone who cannot recover a locked vessel could simply delete it, which is worse. Confirmed
  in the server's own audit log — `removed by player Ikos (Terminated)`.

  Now guarded **exactly like recovery**: the same keypad prompt, the same 45-second grant, and
  the same launch-site exemption. Both are ways of taking a craft away from its owner, so both
  ask the same question, and a PIN proved once covers whichever of the two is pressed next.

  A first attempt refused termination outright, reasoning that there is nothing to recover
  afterwards so there is nothing to authorise. That was wrong in a way worth recording: it made
  the two doors out of a locked craft behave differently for no reason a player could see, and
  it left *the owner* unable to delete their own vessel. Knowing the code is the entire test;
  what you then do with your own craft is your business.

  > For anyone patching near this: the method is `BtnOnClick_DeleteSelectedVessel` with a
  > capital **C**, where its neighbour is `BtnOnclick_RecoverSelectedVessel` with a small one.
  > Harmony matches by exact name, so a patch written from the pattern of the button beside it
  > silently never applies.

**Not a bug:** the greyed-out Recover button in the tracking station is LMP's doing. Its
`SpaceTracking_SetVessel` patch locks Recover whenever `!vessel.IsRecoverable` — not landed or
splashed on Kerbin — so our prefix never got the chance to prompt. Terminate was left unlocked
in that same branch, which is how the two behaviours were seen together.

### Tooling
- **Automatic screenshots.** `Scripts\Watch-Screenshots.ps1` tails `KSP.log` in the
  background and captures the game window when a configured marker appears — the mod already
  logs a line at exactly the moment worth photographing, so the shot list is a config file
  rather than an afternoon with a finger over PrintScreen.

  Each shot is taken **once**: a manifest beside the images records what has been captured, so
  the watcher can be left running across every future deploy without re-taking, and without
  overwriting a good screenshot with a worse one from a half-built test craft. Deleting a PNG
  re-arms that shot. `-ListOnly` prints what is outstanding and the exact in-game actions
  needed to trigger each, which a script cannot perform itself.

- **Two log lines added because a screenshot needed them**, and both were worth having anyway:
  refusing to lock a craft with no PIN was previously only a `ScreenMessage`, and the
  docking-ports-disabled path logged its *success* at `Debug` — meaning a protective feature
  working correctly was invisible at the default log level, which is not a state a user's log
  should be able to reach.

### Added
- **Nothing can dock to a locked craft.** Every `ModuleDockingNode` on a locked vessel is put
  into KSP's own `st_disabled` FSM state — the state the game itself uses for a port inside a
  closed fairing — so ports stop acquiring and the other craft finds nothing to attach to.
  Docking was the third door into a locked vessel: two hard-attached craft are one vessel,
  with shared resources, crew-transfer routes and staging.

  Uses the stock state machine (`fsm.RunEvent(on_disable)`, reported by the stock
  `IsDisabled`) rather than a Harmony patch, so the port reads as disabled to every other mod
  and there is nothing to keep in step with a KSP update. `RunEvent` on a state with no such
  transition is a **silent no-op**, so the result is read back and a failure is logged rather
  than leaving an unguarded port with no symptom.

  Only ports this mod disabled are ever re-enabled — the same "only ever flip one way"
  discipline as the boarding switch, so a port disabled by a fairing or another mod is left
  exactly as found.

### Fixed
- **`Publish.ps1` no longer closes a running KSP without being asked.** `-NoLaunch` stops the
  script *starting* the game and says nothing about stopping one — a distinction that cost a
  live test session. Deploying genuinely does require the file lock to go, so the fix is not
  to skip the kill but to make it a decision: the script now refuses with the running pids
  and start times, and `-Force` opts in.

### Security
- **The PIN is now encrypted at rest, with a key belonging to the machine and user account
  that set it.** Windows DPAPI (`CurrentUser` scope), with a portable AES fallback on Linux
  and macOS whose key is derived from machine and user identity via PBKDF2. Adapted from
  KSPRedeem's `Encryption`, which stores OAuth credentials the same way.

  The reason is **Luna Multiplayer**: the savegame is synchronised to every player, so a
  plaintext `pinCode` in a vessel's persistent fields is a code every other player can read
  out of their own copy of the save. Encrypted, the blob syncs to everyone and is useful to
  nobody but its owner — they cannot read it, cannot unlock the craft even knowing the
  digits, and cannot recover it.

- **Base64url, not base64.** A `ConfigNode` treats `//` as a comment and truncates the rest
  of the line, and plain base64 produces `//` in roughly one blob in ten — which would
  silently cut a PIN short on save and leave a craft nobody could ever unlock. KSPRedeem lost
  days to exactly that; the lesson is applied here from the start, and a unit test runs 400
  blobs asserting no `//` and no `+` ever appears.

- **"Cannot decrypt" is never treated as "no PIN".** This is the dangerous conflation in the
  feature: every other player in an LMP session sees a blob they cannot open, and a client
  that read that as *unconfigured* would unlock somebody else's craft or clear their code.
  Every path that acts on a missing PIN now checks ownership first — locking, unlocking,
  changing the code, the recovery guard, and `ReconcileStoredState`, whose whole job is
  "repair a craft that could never be opened" and which now changes nothing at all on a craft
  it does not own.

- **Legacy plaintext PINs are not silently upgraded.** They keep working and are warned about
  once per load; re-setting the PIN stores it encrypted. Auto-upgrading on load would mean the
  first client to load a shared craft in a multiplayer session claims it and locks out the
  real owner.

### Known cost of the above
- **A PIN only works on the machine and user account that set it** — including your own second
  computer, and including after a Windows reinstall. The mod cannot distinguish those from
  another player. The only rescue is editing `isLocked = False` in the `.sfs` or `.craft`; the
  PIN itself is no longer readable. This is documented prominently in the README rather than
  buried, because it is a real way to lose access to a craft.

### Packaging
- **Ready for CKAN.** `Package.ps1` builds Release and assembles
  `dist/IkosAegis-<version>.zip` as `GameData/IkosAegis/…`, with `LICENSE`, the KSP-AVC
  `.version` file, the ModuleManager patch and a short `ReadMe.txt` pointing at GitHub. The
  README itself is not shipped — installed into `GameData` it would be a page of broken
  image links.
- **The packaging script refuses to ship an inconsistent release.** It checks that the
  version agrees across `AssemblyInfo.cs`, `IkosAegis.version` and the `%aegisVersion`
  ModuleManager beacon; that the `.netkan` parses; that the CKAN identifier, the `GameData`
  folder name and the `:FOR[]` token are one string; that both hard dependencies are
  declared; and that no `.pdb`, `0Harmony.dll` or `ModuleManager*.dll` has crept into the
  staging tree. Both failure paths were deliberately triggered to confirm they fire.
- **`IkosAegis.netkan` reviewed against the CKAN spec.** `$vref` is now bare
  `#/ckan/ksp-avc` — exactly one `.version` file is installed and more than one is an error,
  so naming a path only creates something that can drift. `install` gained a filter for
  `.pdb`/`Thumbs.db`/`.DS_Store`; the metadata is where build leftovers get excluded.
  No `ksp_version` is declared, since the `.version` file is the single source for the
  compatibility range.
- **Harmony is no longer committed to the repo.** The build references
  `GameData/000_Harmony/0Harmony.dll` from the dev install — the same path the `Harmony2`
  package writes — so a developer with the dependency installed needs no extra step and no
  third-party binary is tracked.

### Added
- **`depends` on Harmony2 and ModuleManager**, and an install checker for people who install
  by hand. Missing ModuleManager raises a `PopupDialog`, because that failure is the mod
  loading perfectly and doing nothing at all; missing Harmony gets a log line, because only
  recovery blocking is lost.
- **A locked vessel cannot be recovered.** All three routes are covered — the flight scene's
  Recover button, the tracking station, and the Space Centre vessel markers. Each refuses and
  opens the keypad; the correct PIN **authorises the next attempt** (45 s, dropped on scene
  change) rather than recovering the craft itself, so all three UI flows share one code path
  and nothing can double-recover.
- **A launch-site exemption**, so the mod can never soft-lock a save. A craft parked on a
  **launch pad or a runway** is always recoverable, prompt-free. `Landed` is explicitly *not*
  the test — a locked lander on the Mun is landed. Any of three things counts:
  `situation == PRELAUNCH`; landed at one of `PSystemSetup.SpaceCenterFacilityLaunchSites`
  (the stock pad and runway, each tagged with a VAB or SPH editor, which is what excludes the
  VAB, R&D and Mission Control); or landed at a site `PSystemSetup.IsLaunchSite` recognises,
  covering Making History and mod-added sites without naming them.

  **`IsLaunchSite` alone was not enough, and the first implementation was wrong because of
  it.** `PSystemSetup` keeps *facilities* and *launch sites* in two separate lists —
  `IsFacilityOrLaunchSite` is literally `IsFacility(…) || IsLaunchSite(…)` — and the stock
  KSC pad and runway live in the **facility** list. So a craft that flew and landed back on
  the pad was never exempt; only `PRELAUNCH` was carrying that case, and the runway was not
  covered at all.

  Site names are read from the game, never hardcoded, because KSP is not consistent about
  them: one save holds both `LaunchPad` and `KSC_LaunchPad_Platform` for the same slab of
  concrete — the facility name and the PQS collider name. All four name fields per facility
  are compared, and the list the game reported is written to `KSP.log` once at startup so a
  miss is diagnosable instead of mysterious.
- **Lock state is now readable for unloaded vessels**, via `ProtoVessel`. Everything else in
  the mod works from live `PartModule`s, which exist only inside physics range — so the
  tracking station, where nothing is loaded, would have reported every vessel in the save as
  unlocked and passed every recovery. Persistent `[KSPField]`s are read straight out of each
  `ProtoPartModuleSnapshot.moduleValues`. Live modules still win where both exist, since the
  proto is only rewritten on save.

### Dependencies
- **Harmony is now required, for the recovery block only.** This reverses an explicit
  "deliberately not built: Harmony" decision in PLAN.md, and the reason is that recovery has
  no stock veto and no single choke point: `AltimeterSliderButtons.recoverVessel` fires an
  event that is a notification rather than a request, `SpaceTracking.OnRecoverConfirm` does
  the whole job inline and raises no request event at all, and
  `KSCVesselMarkers.RecoverVessel` calls `ShipConstruction.RecoverVesselFromFlight` directly.
  `Vessel.IsRecoverable` is computed and has no setter. Prefix patches are the only way to
  refuse.

  All Harmony types are confined to `RecoveryGuard.cs` and installation is wrapped in a
  try/catch, so a missing `0Harmony.dll` costs the recovery block and nothing else. The DLL
  is **not** redistributed — the CKAN metadata depends on `Harmony2`, which installs the one
  shared `GameData/000_Harmony` copy.

### Changed
- **A lock is now per *vessel*, not per part.** One craft has one lock and one PIN however
  many command parts it carries: setting, locking or unlocking from any of them applies to
  all. Previously a three-pod station had three independent locks and three codes, which is
  both tedious and weaker than it looks — the craft is only as locked as whichever pod the
  owner forgot about. The control-lock key is keyed on the vessel, so the parts collapse to a
  single entry in the lock stack.

  Docking merges two locks, cautiously: **locked if either half was locked**, and the PIN is
  taken from the half that *was* locked. Docking cannot be used to launder a locked craft
  into an unlocked one. Undocking leaves both halves locked with the same code, because the
  code lives on every command part.
- **Crewed command pods are now covered too**, and the opt-in `CrewedPods.cfg.txt` is gone.
  0.1.0 restricted the patch to `minimumCrew = 0` on the reasoning that a locked crewed pod
  is a capsule whose own crew cannot fly it. That reasoning was sound and the scope was still
  wrong: a craft whose probe core is locked but whose command pod is not is simply not a
  locked craft. Excluding parts is now a two-line patch, documented in `AegisLock.cfg` and
  the README.

### Fixed
- **The part menu stopped updating when a vessel-wide change was made.** Locking a craft left
  the readout on `Aegis: No PIN set` and the button on *Engage Aegis Lock*, while the lock
  itself worked correctly — the other part-menu buttons vanished exactly as they should.

  `ApplySync` refreshed the menu only when the values it was handed differed from what the
  module already held. That looks like an obvious optimisation and is a bug: the module that
  *initiates* a change writes its own fields first and then calls `SyncVessel`, so by the time
  the sync arrives its values match and "nothing changed" is true — for the one part whose
  menu the player is actually looking at. It now always refreshes. Caught from a screenshot
  where the lock plainly worked and the labels plainly disagreed.
- **`ControlLock.ReleaseAll` now says *why* it released.** It logged only
  `Released N Aegis control lock(s)`, which reads like somebody entered a correct PIN — and
  was read exactly that way in a summary of a test session where the player could not unlock
  anything at all, because the button was hidden. The two lines in that log were the scene
  changing, not an unlock.

  This is the mod's own logging rule broken in its first week: *a line that cannot
  distinguish the outcomes it reports is worse than no line.* Every release path now names
  its cause ("the scene is changing to SPACECENTER", "the command part holding it was
  destroyed or unloaded", "no loaded, active, locked vessel still claims it"), and the
  release line explicitly says it is not an unlock.
- **The boarding suppression could adopt a corrupted baseline.** If `CanBoard` was already
  `false` when the mod first wanted it off, that `false` was captured as "the player's
  setting" and boarding would never be re-enabled. This had already happened in testing — the
  giveaway was the log line `restoring to False afterwards`.

  Now the switch is **only ever flipped true → false**. If boarding is already off, the mod
  says so once and leaves it alone forever. Borrowed directly from KSPRedeem's `EvaBlocker`,
  which had the same guard for the same reason.

  > If you tested an earlier build, your save may still have boarding disabled. This build
  > will not turn it back on — by design, since it cannot tell your setting from its own
  > mistake. Re-enable it in the save's difficulty settings.

### Multiplayer
- **Checked against Luna Multiplayer's source, and one mechanism was rejected because of
  it.** EVA prevention stays on the per-attempt `GameEvents.onAttemptEva` veto and
  deliberately does **not** move to KSPRedeem's `GameParameters.Flight.CanEVA` approach:
  LMP's `VesselLockSystem` sets `CanEVA = false` while spectating and unconditionally `true`
  when spectating ends, so two mods would be fighting over one global with last-writer-wins.
  Aegis would be silently overridden, and would break LMP's spectate mode in return.

  `CanBoard` survives the same test — LMP never writes it, and `GameParameters` are
  per-client, so suppressing it cannot stop another player boarding their own unlocked craft.
- **A locked craft could never be unlocked.** Engaging the lock hid the button that
  disengages it, along with *Control From Here*, *Toggle Torque*, *KerbNet Access* and every
  other part-menu button on the craft — leaving no in-game route back in at all. Found on
  the first test flight.

  The cause is not the one 0.1.0's notes claimed. `ALL_SHIP_CONTROLS` contains
  **`ACTIONS_SHIP` (`0x800000`)**, and `UIPartActionWindow.CanActivateEvent` hides every
  part-menu button while that is locked *unless the button sets `guiActiveUncommand`*. The
  tweakables bits 0.1.0 carefully removed were never the gate; removing them changed nothing
  about the symptom, which is why reasoning about the mask a second time would have failed
  too. Settled by disassembling `Assembly-CSharp` with Mono.Cecil and reading the method.

  Both Aegis events now set `guiActiveUncommand = true`. That also let the mask go **back**
  to the full `ALL_SHIP_CONTROLS`, which is a stronger lock than 0.1.0 had: a locked craft
  now cannot decouple, deploy or activate anything from a part menu either, while the two
  Aegis buttons stay reachable.
- **`ModuleAegisLock` now verifies that escape hatch at load and refuses to lock without
  it.** The invariant is the one whose failure cannot be undone in game, and a config patch
  from another mod can clear the flag without either mod knowing. A lock nobody can open is
  worse than no lock.
- **Static `GameEvents` handlers threw at startup.** `CrewRestrictions` subscribed its own
  static methods, and `EventData.Add` NREs inside KSP's `EvtDelegate` constructor for a
  static target. The handlers are now instance methods on `AegisAddon` that forward to it.
  Symptom worth recognising: a `NullReferenceException` whose stack top is
  `EventData'3+EvtDelegate..ctor`.

### Added
- **Crew cannot EVA from a locked craft.** Vetoed per attempt through
  `GameEvents.onAttemptEva` + `FlightEVA.overrideEVA`, which is the game's own cancellation
  path — so the refusal unwinds through KSP's code rather than ours. (Replacing
  `FlightEVA.Spawn` instead throws inside KSP's EVA setup and permanently breaks the crew
  portrait; that route was deliberately not taken.)
- **Nobody can board while a locked craft is loaded.** KSP has no per-attempt boarding hook
  — `KerbalEVA.BoardPart` reads the game-wide `GameParameters.Flight.CanBoard` and nothing
  else — so that switch is suppressed while a locked craft is in the scene and restored
  when it is not.

  Two honest limitations: it is **coarse** (an unrelated craft parked next to a locked one
  also cannot be boarded), and it is **game-wide state that gets saved**. The player's own
  setting is captured before suppression and put back afterwards, and it is restored around
  every save so the suppressed value never reaches the `.sfs` — a hard kill writes no save
  at all, so the on-disk value survives a crash intact.

## [0.1.0] - 2026-08-16

First working version.

### Added
- **`ModuleAegisLock`** — a PIN lock on a command part. Set a code, engage the lock, and
  the craft will not pitch, roll, yaw, throttle, stage, use SAS or RCS, steer its wheels or
  fire an action group until the code is entered back in.
- **A numeric keypad**, built from stock `DialogGUI*` elements inside a `PopupDialog`, so it
  matches the game's own dialogs and needs no asset bundle. OK stays disabled until the
  entry is the full length, so a short code cannot be submitted at all.
- **Keypad audio** from stock clips — a tick per press, a latch on a granted code, a flat
  refusal tone on a wrong one. Nothing is bundled; the clips are pulled from `GameDatabase`.
- **A ModuleManager patch** attaching the module to every part with a `ModuleCommand` whose
  `minimumCrew` is 0 — 14 stock probe cores, plus every modded one that looks like one.
- **An opt-in patch for crewed pods**, shipped as `CrewedPods.cfg.txt` and inert until
  renamed to `.cfg`.
- **A version beacon.** The patch runs at `:FOR[IkosAegis]`, which declares `IkosAegis` as a
  valid `:NEEDS` token, and writes an `IKOSAEGIS_VERSION` node other mods can read.
- **A wrong-code penalty.** Three failures disable the keypad for 30 s, doubling per further
  failure to a 5-minute cap, on real seconds so it cannot be warped through. Configurable
  per part; `lockoutAfter = 0` turns it off.
- **Action-group binding for engaging** the lock. Disengaging is deliberately not bindable —
  it needs a PIN typed by a human.

### Notes on the two things the original concept got wrong

Both are recorded here rather than silently fixed, because both read as correct.

- **`ControlTypes.ALL_SHIP_CONTROLS` would have bricked every locked probe.** The mask is
  `0x0C47FFFFFFFE32BF` and contains both tweakables bits — `TWEAKABLES_ANYCONTROL`
  (`0x1000`) and `TWEAKABLES_FULLONLY` (`0x0800000000000000`) — which gate the Part Action
  Window. That is where the unlock button lives, so locking with the full mask removes the
  only way back in, permanently, with no in-game recovery. IkosAegis locks with
  `ALL_SHIP_CONTROLS` minus those two bits.
- **`KSPAudioSound.PlaySound` does not exist** in KSP 1.12. Feedback goes through an
  `AudioSource` fed from `GameDatabase` instead.

### Design decisions worth knowing about

- **The parts do not take their own control locks.** `InputLockManager` is a single global
  stack, and a leaked lock leaves the player unable to fly anything until they restart. The
  modules own a boolean; one addon reconciles the whole lock set every frame. Every ending
  that never reaches `OnDestroy` — an explosion, an unload, a revert — is covered by the
  module simply no longer being in the list.
- **`:FOR[IkosAegis]`, not `:FINAL`.** `:FINAL` is the last pass ModuleManager runs, so
  nothing downstream can adjust it. `:FOR` leaves `:AFTER[IkosAegis]` available to anyone
  who wants the last word.
- **A part with no PIN refuses to lock**, and a part loaded as locked-with-no-PIN unlocks
  itself with a warning. Both states could otherwise never be opened.
- **PINs are compared as strings, never parsed to `int`.** `007` and `7` are different
  codes; an integer comparison makes them the same one and silently opens the lock.
