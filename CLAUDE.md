# IkosAegis — CLAUDE.md

A Kerbal Space Program 1.12 mod that puts a PIN keypad on command parts. Set a code, engage
the lock, and the craft refuses to fly until somebody types the code back in.

Built from a concept sketch (`Ikos KSPProbeCoreLock concept.txt` and
`Ikos Sol System ProbeCoreLock GUI Audio.txt`). Build, deploy and test loop cloned from
[KSPRedeem](../KSPRedeem/CLAUDE.md); the ModuleManager work follows
`llm-wiki/wiki/tools/module-manager.md`. The running record of decisions lives at
`llm-wiki/wiki/projects/ikosaegis.md`.

> **Credits and the AI disclosure live in the *Credits* section of
> [README.md](README.md)**, and are repeated in the `ReadMe.txt` that `Package.ps1` ships
> inside `GameData` — a user who installs from CKAN may never see the GitHub page, so the two
> must stay in step.
>
> Idea and concept: [Ice King of Space](https://www.twitch.tv/icekingofspace).
> Developed by: [drebsdorf](https://www.twitch.tv/drebsdorf). AI was used in this project.
>
> Keep the disclosure accurate. If the split of work changes, update the wording rather than
> letting it drift into something flattering; it is only worth having while it is true.

---

## The one thing to know before touching this code

**The lock hides every part-menu button on the craft. The two Aegis buttons survive only
because they set `guiActiveUncommand = true`. Remove that and a locked craft can never be
unlocked again.**

`ALL_SHIP_CONTROLS` contains `ACTIONS_SHIP` (`0x800000`).
`UIPartActionWindow.CanActivateEvent`, for a part on the active vessel in flight, is:

```
if (!guiActive || !active || EventIsDisabledByVariant) return false;
if (ACTIONS_SHIP is locked)          return guiActiveUncommand;
if (!vessel.IsControllable)          return guiActiveUncommand;
if (!requireFullControl)             return true;
if (TWEAKABLES_FULLONLY is unlocked) return true;
return guiActiveUncommand;
```

That is read out of the IL with Mono.Cecil (shipped in KSP's own `Managed` folder) and is
the only documentation the rule has. `ModuleAegisLock.VerifyEscapeHatch` asserts it at load
and refuses to lock the part if the flag is missing — because this is the one failure in
the mod that **cannot be undone in game**. Recovery for an already-locked save is editing
`isLocked = false` in the `.sfs`.

### How this was got wrong the first time, which is the more useful lesson

0.1.0 reasoned that `ALL_SHIP_CONTROLS` must be unusable because it contains the two
*tweakables* bits, and shipped a mask with those removed. That reasoning was confident,
documented at length in three files — and wrong. The tweakables bits were never the gate.
The craft was still unlockable, because the actual gate (`ACTIONS_SHIP`) was untouched.

Two rules come out of it:

- **A `ControlTypes` composite is a set, and its name tells you nothing about its contents**
  — but knowing the contents is still not knowing which bit the *consumer* checks. Getting
  the numbers was necessary and not sufficient.
- **When a guess about closed-source behaviour is wrong, do not guess again.** `Mono.Cecil`
  is sitting in `KSP_x64_Data\Managed`; disassembling the exact method took one command and
  produced a definite answer. Reach for it the *first* time a behaviour matters this much.

  ```powershell
  Add-Type -Path "$mgd\Mono.Cecil.dll"
  $asm=[Mono.Cecil.AssemblyDefinition]::ReadAssembly("$mgd\Assembly-CSharp.dll",(New-Object Mono.Cecil.ReaderParameters))
  # then walk .MainModule.Types -> .Methods -> .Body.Instructions
  ```

---

## Build, deploy, debug

| Task | Command |
|---|---|
| Build only | `.\Build.ps1` (add `-Configuration Release`) |
| Unit tests | `dotnet test .\Tests\IkosAegis.Tests.csproj` — seconds, no game |
| Build → deploy → launch → patch debugger port | `.\Publish.ps1` — the default VS Code build task (Ctrl+Shift+B) |
| Deploy without launching | `.\Publish.ps1 -NoLaunch` |
| Attach debugger | F5 ("Attach to KSP"), after the terminal prints `launch.json updated` |

| Path | Value |
|---|---|
| Dev install | `C:\Projects\KSP\Kerbal Space Program Dev\` (`KSP_x64_dbg.exe`) |
| Deployed DLL | `<dev install>\GameData\IkosAegis\Plugins\IkosAegis.dll` |
| Deployed patches | `<dev install>\GameData\IkosAegis\Patches\` |
| Log | `<dev install>\KSP.log`, every line tagged `[IkosAegis]` |
| MM patch log | `<dev install>\Logs\ModuleManager\MMPatch.log` |

`Publish.ps1` kills any running `ksp_x64_dbg` before copying, because a running KSP holds
the plugin file open. The Unity player-connection port is dynamic (55000–57000) and
`-playerConnectionPort` is ignored by this build, so the script discovers it and rewrites
`.vscode\launch.json` every launch. Never hand-edit that `endPoint`.

### The patches are source, and are mirrored on deploy

Half this mod's behaviour is in `.cfg` files, not the DLL. `Publish.ps1` **deletes and
re-copies** `Patches\` rather than merging, so a patch removed from the repo also leaves the
install. A stale `.cfg` sitting in `GameData` still patches the database and is invisible
from the repo — a genuinely confusing afternoon.

ModuleManager keys its cache on every config file's URL *and* contents, so a deploy has
already invalidated it. There is never a reason to delete `ModuleManager.ConfigCache` by
hand.

### Before you launch or relaunch the game — every time

**1. Read `KSP.log` first. Launching destroys it.** KSP truncates the log on startup, so any
error still unexplained in the current log is gone the moment you launch.

```powershell
Select-String -Path "$dev\KSP.log" -Pattern "\[IkosAegis\]\[(ERROR|WARN)\]|^\[EXC" |
    Select-Object -Last 15
```

`Publish.ps1` copies both `KSP.log` and `MMPatch.log` into `GameData\IkosAegis\Debug\`
before killing the game, so this is a safety net rather than the only copy — but read them
before deploying anyway, because the copy is only useful to someone who looks at it.

**2. Check nothing is already running, and close it.** Check for **both** `ksp_x64_dbg` and
`KSP_x64`; `Publish.ps1` kills only the first.

```powershell
Get-Process | Where-Object { $_.ProcessName -match 'ksp' } | Select-Object Id, ProcessName, StartTime
```

### Verifying a change from the log

`KSP.log` is truncated **in place** on launch, so leftover tail content from the previous
run survives past the new content. Grepping for a success marker will happily match the
*previous* run. Match against something unique to the new run rather than the marker alone.

---

## Architecture

```
Source\
  Logic\    pure, KSP-free, unit-tested: PinCode, LockoutPolicy
  Core\     the addon (the one MonoBehaviour), ControlLock, CrewRestrictions,
            RecoveryGuard (the only Harmony in the mod), ProtoLockState,
            LaunchSiteCheck, sound, logging, compat
  Parts\    ModuleAegisLock — the PartModule
  UI\       KeypadDialog — the PopupDialog keypad
Lib\        0Harmony.dll — build reference only, never shipped
GameData\IkosAegis\Patches\
            AegisLock.cfg          the ModuleManager patch (source, not output)
            (no opt-in patches; excluding parts is documented in AegisLock.cfg)
```

### Rules that are not negotiable

- **Nothing outside `Source\Core\ControlLock.cs` may touch `InputLockManager`.** It is a
  single global stack shared with the game and every other mod. One class holds the list of
  what we have taken, which is what makes release-on-every-path provable rather than hoped
  for.
- **A `PartModule` never takes a lock.** It owns a boolean; `AegisAddon.Reconcile` decides
  what locks exist, every frame, from the live module list. This is the whole safety
  argument: an ending nobody wrote code for (explosion, unload, revert, scene change with a
  keypad open) removes the module from the list, so its lock stops being justified. Adding a
  direct `SetControlLock` call anywhere else silently reintroduces every leak this design
  exists to prevent.
- **`Source\Logic\` must not reference KSP or Unity.** The test project loads those types
  outside the game, where `UnityEngine` does not resolve. A single `using UnityEngine;` in
  that folder breaks the whole suite with a `TypeLoadException` that does not name the cause.
- **`Events["MethodName"]` is a runtime string lookup.** Renaming `ToggleLockEvent` or
  `SetPinEvent` compiles cleanly and silently stops the menu updating. Keep the names.
- **Every `GameEvents` handler must be an instance method**, and in this mod they all live
  on `AegisAddon`. `EventData.Add` throws an NRE inside KSP's own `EvtDelegate` constructor
  when handed a static method — the stack top reads `EventData'3+EvtDelegate..ctor` and says
  nothing about the real cause. `CrewRestrictions` therefore exposes `Handle*` statics that
  the addon's instance handlers forward to; do not subscribe them directly.
- **Two different predicates, and conflating them breaks the crew restrictions.**
  `WantsControlLock` requires the locked vessel to be **active** (a global input lock taken
  for a craft the player is not flying disables the one they are). `IsLockedAndLoaded` only
  requires it to be **loaded** — because EVA and boarding restrictions matter precisely when
  the player has stepped outside and the locked craft is no longer active.
- **Every release of a lock or a global flag must name its cause.** `ControlLock.Release`
  and `ReleaseAll` take a mandatory `reason` string for exactly this reason: the earlier
  `"Released N Aegis control lock(s)"` was read as "the player unlocked something" in a
  session where the player provably could not, because the unlock button was hidden. **A log
  line that cannot distinguish the outcomes it reports is worse than no line.** The signature
  makes forgetting impossible; keep it that way.
- **Harmony lives in `RecoveryGuard.cs` and nowhere else**, and `Install()` is called from a
  try/catch. That containment is the whole reason a missing `0Harmony.dll` costs the recovery
  block rather than the mod. Referencing a Harmony type from any other file breaks that
  guarantee silently — the type load fails wherever it is first touched.
- **Never ask a live `PartModule` whether an *unloaded* vessel is locked.** Modules exist
  only inside physics range, so in the tracking station the answer is "no" for every vessel
  in the save. `AegisAddon.VesselIsLocked` / `PinMatches` fall through to
  `ProtoLockState`, which reads the persisted fields out of `ProtoVessel`. Loaded modules win
  where both exist, because the proto is only rewritten on save.
- **`vessel.Landed` is not "on a launch site".** It is true on any terrain on any body.
  `LaunchSiteCheck.IsOnALaunchSite` is `PRELAUNCH`, **or** landed at one of
  `PSystemSetup.SpaceCenterFacilityLaunchSites` (the stock pad and runway — each carries a
  VAB/SPH `editorFacility`, which is what excludes the VAB, R&D and Mission Control), **or**
  landed at something `PSystemSetup.IsLaunchSite` recognises (Making History and mod sites).
  Widening any of this to `Landed` would hand back the entire recovery feature.
- **`IsLaunchSite` alone is not enough, and that was a real bug.** `PSystemSetup` keeps
  *facilities* and *launch sites* in two different lists —
  `IsFacilityOrLaunchSite` is literally `IsFacility(…) || IsLaunchSite(…)` — and the stock
  KSC pad and runway are in the **facility** list. The first version checked only
  `IsLaunchSite`, so a craft that flew and landed back on the pad was not exempt; only
  `PRELAUNCH` was carrying it.
- **Never hardcode a launch-site name.** `vessel.landedAt` is not consistent: a real save
  holds both `LaunchPad` and `KSC_LaunchPad_Platform` for the same pad — the facility name
  and the PQS collider name. All four name fields on each facility (`name`, `pqsName`,
  `facilityName`, `facilityTransformName`) are compared, and the list the game reports is
  logged once per session so a miss is diagnosable.
- **Ask `InputLockManager`, never our own `HeldKeys`.** The lock stack is global and anyone
  may clear it — Luna Multiplayer does, from
  `DeleteAllTheControlLocksSoTheSpaceCentreBugGoesAway()` and again in its KSC-marker patch.
  `ControlLock.Acquire` must re-read `GetControlLock(key)` every call and re-apply when it is
  missing. Re-adding the `HeldKeys.Contains(key)` early return is the single change most
  likely to silently unlock every craft in a multiplayer session.
- **An input lock does not stop code from flying the craft.** `InputLockManager` blocks player
  *input*; `Vessel.OnFlyByWire` writes the control state directly and runs regardless. LMP
  pushes a remote player's throttle through it, and so does every autopilot mod.
  `ControlNeutraliser` is the answer, and it must stay **last** in the callback list to win —
  hence the re-registration check. A lock without it protects against a keyboard, not against
  software.
- **The PIN grant is checked, never consumed, and a refusal must unwind the host UI.** One
  button press crosses two guarded methods — `BtnOnClick_DeleteSelectedVessel` → confirmation
  dialog → `OnVesselDeleteConfirm` — so a single-use grant is spent by the first and missing
  for the second. That soft-locked the tracking station, because the skipped method is where
  `OnDialogDismiss()` lives. Two rules fall out, and both generalise past this patch:
  *a one-shot token is only safe when exactly one thing consumes it, and a UI flow is not one
  thing*; and *skipping a stock method means skipping its teardown, so a prefix that returns
  false must leave the screen in a state the player can escape.*
- **Guard destruction as well as recovery, and guard them identically.** Blocking recovery
  while leaving Terminate open is worse than blocking neither: it converts "I cannot take this
  craft" into "I will delete it". Every entry point routes through
  `RecoveryGuard.MayProceed(vessel, what)` / `Refuse(vessel, what)`, so all five share one
  decision — the PIN prompt, the 45-second grant, and the launch-site exemption.

  Termination briefly refused outright instead, on the reasoning that there is nothing to
  recover afterwards. Rejected: it made the two doors behave differently for no reason a
  player could see, and locked the *owner* out of deleting their own craft. **Knowing the code
  is the whole test.** A new entry point should call `MayProceed`, never invent its own rule.
- **Never express a transient condition with durable state.** Docking used to be blocked by
  pushing the port FSM into `st_disabled` - the game's own mechanism, and wrong, because
  `ModuleDockingNode.OnSave` writes `state` into the ConfigNode **by hand** (not a
  `[KSPField(isPersistant = true)]`, so a check of its persistent fields says otherwise). The
  port came back disabled after every unload while our in-memory record of having disabled it
  did not, and it stuck forever. Blocking is now a Harmony postfix on
  `ModuleDockingNode.FindNodeApproaches` that returns null - purely runtime, nothing written.
  `DockingGuard` only exists now to repair ports the old build poisoned.
- **`Publish.ps1` refuses to stop a running KSP without `-Force`.** `-NoLaunch` is about
  *starting* the game and was mistaken for "leave my session alone", which closed one.
- **PINs are plain text, and must stay that way.** A machine-bound PIN was tried and removed:
  it hid the code in a synced save and made it **impossible to share**, so a crewmate could
  not be given the code to fly your craft. That is most of what a PIN is for. The `Encryption`
  class and its legacy-blob migration path were deleted before the first public release, since
  no published build ever wrote an encrypted PIN — do not reintroduce either.

  The honest security position, for when this comes up again: *a three-digit code cannot be
  protected at rest by anything that must also verify on another machine.* A salted hash falls
  to a thousand offline guesses. Bringing secrecy back needs a much longer minimum PIN and a
  slow KDF, not machine-bound keys.

  If a stored PIN is ever encoded again, use **base64url, never base64**: `//` is a comment in
  a `ConfigNode` and truncates the line, which plain base64 emits about one value in ten. A
  PIN truncated on save is a craft nobody can unlock.
- **Never log a PIN.** The value can ride along in an exception message just as easily as in a
  deliberate log line.
- **One craft, one lock, one PIN.** `LockKey` is keyed on `vessel.id`, not the part, so
  several command parts on a vessel collapse to one entry in the lock stack. Any state change
  goes through `AegisAddon.SyncVessel`; dock/undock goes through `UnifyVessel`. Writing
  `pinCode` or `isLocked` directly on a module without syncing leaves a craft with two
  different PINs, which is the state the vessel-wide design exists to prevent.
- **A refusal must be visible.** Every path that declines to do something posts a
  `ScreenMessage` saying why. A silently no-op button is indistinguishable from a broken one.

### States that must stay impossible

Each of these ends in a craft that can never be unlocked, so each is repaired loudly in
`ModuleAegisLock.ReconcileStoredState`:

| State | Where it comes from | What happens |
|---|---|---|
| `isLocked = true`, no PIN | hand-edited save, or a patch shipping `isLocked = true` | unlock, warn |
| stored PIN's length ≠ `pinLength` | another mod patched `pinLength` after a PIN was set | clear the PIN, unlock, warn |
| PIN containing non-ASCII digits | hand-edited save (`char.IsDigit` accepts dozens of scripts) | rejected by `PinCode.IsValid`, so it falls into the case above |

The shipped patch therefore sets **no** `pinCode` and **no** `isLocked`. A default code
would be the same code in every save, which is worse than none.

---

## ModuleManager rules this mod is shaped around

Grounded in `llm-wiki/wiki/tools/module-manager.md`, which is built from MM 4.2.3's source.

- **`:FOR[IkosAegis]`, never `:FINAL`.** `:FINAL` is the last pass, so nothing downstream
  can adjust it. `:FOR` leaves `:AFTER[IkosAegis]` available to others *and* declares
  `IkosAegis` as a valid `:NEEDS` token whether or not an assembly by that name is loaded.
- **A patch that matches nothing is completely silent.** Misspell a part name and MM issues
  no warning at all. The check is the applied-patch count in `MMPatch.log`, or the part
  tooltip in game.
- **Any error in any patch anywhere in the install suppresses the MM cache entirely.** An
  install that re-patches on every launch is a signal, not a quirk.
- **Nested `:HAS[]` applies to the matched subnode.** `@MODULE[ModuleCommand]:HAS[#minimumCrew[0]]`
  is what makes "a command module that needs no crew" expressible at all.
- **`~key[value]` is "absent or different", not "equal to something else"** — worth knowing
  when narrowing the patch. `#minimumCrew[0]` and `~minimumCrew[0]` between them cover every
  part; `#minimumCrew[0]` and `#minimumCrew[1]` silently miss anything requiring 2.
- **`!MODULE[ModuleAegisLock]` guards every insert**, so running both patch files at once
  still produces exactly one module per part.

---

## Testing

Two layers, answering different questions.

| | |
|---|---|
| `dotnet test Tests\IkosAegis.Tests.csproj` | Pure logic — PIN validity, comparison, masking, lockout arithmetic. Seconds, no game. |
| `.\Publish.ps1` then read the logs | That the patch matched what it should and the module type resolved. |

### What the logs can prove without playing

This is most of the verification for this mod, and it is worth knowing exactly how far it
reaches:

```powershell
$base = "C:\Projects\KSP\Kerbal Space Program Dev"

# The patch matched the parts it should have, and nothing else
Select-String -Path "$base\Logs\ModuleManager\MMPatch.log" -Pattern 'IkosAegis' 

# The module type resolved on every patched part. One line per part if it did NOT.
Select-String -Path "$base\KSP.log" -Pattern "Cannot find a PartModule of typename 'ModuleAegisLock'"

# The patched database really contains what the patch log claims
(Select-String -Path "$base\GameData\ModuleManager.ConfigCache" -Pattern 'name = ModuleAegisLock' -AllMatches).Count
```

The cache count and the patch-log application count must agree. They are separate
observations of the same claim, which is the only reason checking both is worth anything.

### What the logs cannot prove

**Everything about the lock actually working.** Whether the keypad opens, whether the mask
leaves the part menu reachable, whether the controls are genuinely dead, whether the lock is
released on a revert — none of that appears in a load-time log, and all of it has to be
flown.

The rule from KSPRedeem applies with full force here: **a call returning is not evidence.**
`ControlLock.Acquire` reads the mask back with `GetControlLock` and refuses to record a lock
the game did not actually apply, precisely because `SetControlLock` returns just as happily
when nothing happened. Any new state change should do the same — observe the change, or
refuse.

### The manual sweep, in order

Each of these has a specific failure it exists to catch, and the order matters — 3 before 4,
because a mod that locks and cannot unlock is the one bad outcome that cannot be undone
in game.

0. **VAB** — build the test craft with **a probe core *and* a crewed pod**, and crew it. Most
   of what can go wrong now is vessel-wide state, and a single-command craft cannot see any
   of it.
1. **VAB** — read the menu on both. Expect `Aegis: No PIN set` on each, and *Engage*
   refusing with a message.
2. **VAB** — set a PIN **on one part**. Expect the readout on the *other* part to become
   `Unlocked` too, and both buttons to say *Change Aegis PIN*. This is the vessel-wide sync,
   and it is the largest untested change in the mod.
3. **Launch, engage the lock.** Expect controls dead, expect *Control From Here* / *Toggle
   Torque* / *KerbNet Access* to **disappear**, and expect **Change Aegis PIN and Disengage
   Aegis Lock to remain**. This is the `guiActiveUncommand` check and it is the one that has
   already failed once — it is why steps 3 and 4 come before everything else.
4. **Unlock with the right PIN.** Expect controls back and the hidden buttons to return.
5. **Wrong PIN ×3.** Expect the penalty message, and expect the countdown to *not* shorten
   under time warp.
6. **Crewed craft, lock engaged → try to EVA.** Expect a refusal naming the Kerbal and the
   craft. Then unlock and EVA successfully, to prove the veto is conditional and not just
   "EVA is broken now".
7. **EVA near a locked craft → try to board.** Expect the game's own refusal message.
   Then **quicksave, quit, reload, and check boarding works** with no locked craft loaded —
   that is the check that the `CanBoard` suppression never reached the `.sfs`.
8. **Lock, then revert to launch.** Expect controls free and boarding restored — neither
   may survive the scene change.
9. **Lock, then blow up the core** (right-click → explode, or fly it into the ground).
   Expect controls free. This is the path that has no `OnDestroy` guarantee and is the whole
   reason the reconcile exists.
10. **Lock a probe, switch to another vessel.** Expect the *other* vessel to fly normally.
    A global lock taken for a craft you are not flying disables the one you are.
11. **Recovery, in flight.** Locked craft, landed *away from the KSC* → Recover → expect a
    refusal and the keypad. Correct PIN → expect "press Recover again" → Recover → recovers.
12. **Recovery, tracking station.** Same craft, from the tracking station. This is the path
    that needs `ProtoLockState`, because nothing is loaded — if the block passes silently
    here, that is why.
13. **Terminate, tracking station.** Locked craft away from the KSC → Terminate → expect the
    same keypad, titled *authorise termination*. Wrong PIN refuses; correct PIN grants, and
    Terminate again **must actually delete the vessel and leave the tracking station usable** -
    the soft-lock was a grant consumed by the button and missing at the confirmation, so
    "the dialog closed and I can still leave" is the assertion, not just "it deleted". Then check the grant is shared: refuse a **recovery**, enter
    the PIN, and press **Terminate** — it should be allowed, because ownership was proved once.
14. **Recovery and termination on a launch site.** Expect **no prompt at all**, for *both*
    buttons, in all four cases: a locked rocket in `PRELAUNCH` on the pad, a locked spaceplane
    in `PRELAUNCH` on the runway, and each of them after flying and landing back on its launch
    site. The last two exercise the `SpaceCenterFacilityLaunchSites` name matching;
    `PRELAUNCH` alone would mask a failure there. This is the anti-soft-lock exemption, and a
    regression is how a save gets a permanent obstruction in it.
15. **Delete `GameData/000_Harmony` and launch.** Expect one logged error naming Harmony, and
    every other feature working. Recovery and termination blocking are the only things that
    should be lost.
15. **PIN storage and sharing.** Set a PIN, quicksave, grep the `.sfs` for `pinCode` — expect
    the **plain digits**, not a blob. Then have a second player use that code on their own
    machine: it must work. That is the property the encryption removal exists to restore, so
    it is the one to check.

Symptom to recognise for a leaked lock: **SAS still works and nothing else does.**
`AegisAddon.LogLockStack(...)` dumps the whole stack, ours and everyone else's — the useful
answer is often *"the lock is not yours"* (KSP's own `vessel_noControl_<guid>` looks
identical from the player's side).

---

## Screenshots: let the log take them

```powershell
.\Scripts\Watch-Screenshots.ps1 -ListOnly     # what is missing, and how to trigger each
.\Scripts\Watch-Screenshots.ps1               # start watching, then play
.\Scripts\Watch-Screenshots.ps1 -Recapture recovery-keypad
```

`Scripts\Screenshots.json` maps a log pattern to a PNG. When the marker appears the watcher
waits `delayMs` for the dialog to actually render, grabs the KSP window, and records the id in
`Docs\img\auto\manifest.json` so it is **never taken twice** — leave it running across
deploys. Delete a PNG to re-arm that shot.

Rules when adding one:

- **Verify the marker exists at `Info` or above before writing the pattern.** `AegisLog.Debug`
  never reaches `KSP.log` at the default level, so the pattern can be perfect and never fire.
  Two shots on the current list needed a log line adding first, and the docking one was a real
  observability hole: the *success* path of a protective feature was only visible at `Debug`.
- **Grep for the log call, but beware line-based false negatives** when the message is built
  across several source lines. Test the regex against a reconstructed runtime line.
- **Write the `requires` sentence.** A script cannot fly a craft to a launch site. That text is
  the outstanding-work list a human reads.
- Frames contain live state; note anything sensitive next to the shot.

## Finding KSP API names without source

**Use `Scripts\Find-KspType.ps1`** rather than guessing and waiting for the compiler. It
reflects over all ~92 managed assemblies.

```powershell
.\Scripts\Find-KspType.ps1 -Type '^MultiOptionDialog$' -Member '\.ctor'
.\Scripts\Find-KspType.ps1 -Type '^InputLockManager$' -Member . -Static
.\Scripts\Find-KspType.ps1 -Type ControlTypes -Member .          # enums list their values
```

This was decisive three times while building this mod, and each would otherwise have been a
failed build or, worse, a plausible-looking bug:

- `DialogGUILabel` has **no** `(Func<string>, float)` overload — the concept sketch uses one.
  It is `(Func<string>, float, float)`.
- `MultiOptionDialog` takes a `UnityEngine.Rect`, not the `RectBounds` in the sketch.
- `ControlTypes.ALL_SHIP_CONTROLS` contains the tweakables bits, which is the whole subject
  of the top of this file. The enum's *name* says nothing about its contents; only the
  numbers do.

---

## Versioning

`CHANGELOG.md` tracks releases; 0.1.0 is the first working version. Any user-visible change
goes in the **Unreleased** section as it is made, not reconstructed at release time.

Four places carry the version and **all four must match**: `Properties\AssemblyInfo.cs`,
`GameData\IkosAegis\IkosAegis.version`, the `%aegisVersion` beacon in `AegisLock.cfg`
(as `MAJOR*10000 + MINOR*100 + PATCH`, because `:HAS[]` comparisons are numeric and
understand only `<` and `>`), and the git tag.

> **KSP 1.12.5 is the last version there will ever be.** `KSP_VERSION_MAX` of 1.12.99 is not
> a placeholder to revisit — it is permanently correct. "Wait and see what the next release
> changes" is never the answer to an API question here.

## Releasing on CKAN

**`.\Package.ps1` does the build and the checks.** It refuses to produce a zip unless the
version agrees across `AssemblyInfo.cs`, `IkosAegis.version` and the `%aegisVersion` beacon;
the `.netkan` parses; the CKAN identifier, the `GameData` folder name and the `:FOR[]` token
are the same string; both hard dependencies are declared; and no `.pdb`, `0Harmony.dll` or
`ModuleManager*.dll` has crept into the staging tree. Output is
`dist\IkosAegis-<version>.zip`, laid out as `GameData\IkosAegis\...`.

Both guard paths have been exercised deliberately (beacon mismatch, missing dependency) —
they are not decorative.

Then: tag, GitHub release, **attach the zip as an asset** (the `$kref` reads release assets,
not the source archive). First release only, PR `IkosAegis.netkan` into `KSP-CKAN/NetKAN`;
after that the bot picks up each release on its own.

### The rules behind the metadata

- **One identifier, four places.** CKAN `identifier`, `GameData/<folder>`, ModuleManager
  `:FOR[]`, and the DLL filename — CKAN detects manual installs by everything before the
  first dot of a DLL name under `GameData`. All four are `IkosAegis`; `Package.ps1` enforces
  the first three.
- **`$vref: "#/ckan/ksp-avc"` with no path.** Exactly one `.version` file is installed and
  more than one is an error, so naming a path only creates something that can drift.
  **Do not also write `ksp_version`/`_min`/`_max`** — the `.version` file is the single
  source for the compatibility range. The GitHub `$kref` still supplies the mod `version`
  from the release tag.
- **`depends`, never bundle.** Two copies of one assembly in a single Mono process fight
  over the same patch database, which is exactly what shipping our own `0Harmony.dll` would
  cause. `Harmony2` provides the shared `GameData/000_Harmony` copy — and the build
  references *that same file* out of the dev install, so nothing third-party is committed
  either.
- **Harmony is a hard `depends`, not a `recommends`.** Recovery blocking is a security
  feature, and one that is silently absent is worse than an install that refuses. Missing
  ModuleManager is worse still — the mod loads perfectly and does nothing — which is why
  that one gets a `PopupDialog` from
  `CompatibilityChecker.WarnAboutMissingDependencies` and Harmony gets a log line.
- **`filter` excludes build leftovers, not the zip.** The staging tree stays simple and the
  metadata does the excluding.
- `install` here is byte-for-byte what CKAN would default to on its own; it is written out
  only so it can carry the `filter`.
