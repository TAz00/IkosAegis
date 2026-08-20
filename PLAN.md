# IkosAegis — Plan

## Where it stands

**The core loop works and has been flown.** Set a PIN, engage, the craft is dead, the keypad
opens, the correct code brings it back. That was confirmed in game on 2026-08-16 after the
`guiActiveUncommand` fix — the log line to look for is `Unlocked … on a correct PIN`.

| | |
|---|---|
| Builds | ✅ clean, 0 warnings |
| Unit tests | ✅ 33 passing (`PinCode`, `LockoutPolicy`) |
| Loads into KSP 1.12.5 | ✅ 0 exceptions |
| ModuleManager patch applies | ✅ 0 patch errors |
| Set PIN → engage → keypad → unlock | ✅ **flown and confirmed** |
| Aegis buttons survive the lock | ✅ **flown and confirmed** |
| EVA refusal | ✅ **flown** — `Refused an EVA from locked vessel 'Test Craft'` |
| Boarding "already off, leave it alone" guard | ✅ **flown** — fired correctly on a save whose `CanBoard` was already false |
| Release lines name their cause | ✅ **flown** |
| Vessel-wide lock (one craft, one PIN) | ✅ **flown** — PIN set on a `landerCabinSmall`, lock engaged and unlocked from a `probeStackSmall`, same vessel key `IkosAegis_578c6bec…` |
| Crewed pods covered | ✅ **flown** — the `landerCabinSmall` above carried a working Aegis module |
| Part-menu labels track state | ✅ **flown** — the `ApplySync` refresh fix confirmed by screenshot |
| Harmony patches install | ✅ **flown** — `Recovery guard installed (Harmony dk.drebsdorf.ikosaegis)` |
| Recovery *refusal* actually firing | ❌ patches load; no refusal has been triggered |
| Launch-site exemption (pad + runway) | ❌ built, not flown |
| Graceful degradation without Harmony | ❌ built, not flown |
| CKAN packaging | ✅ zip builds, layout verified, both metadata guards deliberately triggered |
| Missing-dependency install checker | ❌ built, not flown |
| PIN shareable — a second player uses the code | ❌ built, not flown |
| Recovery refusal (flight **and** tracking station) | ✅ **flown** — refused both, and a wrong PIN was rejected |
| Docking blocked on a locked craft | ❌ built, not flown |

## Not yet verified

The manual sweep in [CLAUDE.md](CLAUDE.md#the-manual-sweep-in-order) is the outstanding work.
The items carrying real risk, in order:

1. **The launch-site exemption, before anything else.** A regression here is the only failure
   in the mod that can put a permanent obstruction in a save. Four cases, and the last two
   are the ones the first implementation got wrong:
   - a locked rocket in `PRELAUNCH` on the pad → Recover, expect no prompt;
   - a locked spaceplane in `PRELAUNCH` on the runway → same;
   - a locked craft that **flew and landed back on the pad** → same;
   - a locked spaceplane that **flew and landed back on the runway** → same.

   The startup line `Launch sites recognised for the recovery exemption — …` names everything
   the game reported, so a miss can be matched against the `landed at …` value in the
   refusal line rather than guessed at.
2. **Recovery from the tracking station.** The path that depends on `ProtoLockState` reading
   an unloaded vessel. If the block silently passes anywhere, it will be here.
3. ~~**A multi-command-part craft.**~~ **Done 2026-08-16.** A craft with a
   `landerCabinSmall` and a `probeStackSmall` shared one vessel key: the PIN was set on the
   pod, the lock engaged and released from the probe core. Crewed-pod coverage and the menu
   refresh fix came with it.
4. **Docking two locked craft with different PINs.** `UnifyVessel` picks a winner; that has
   never run against a real dock event.
5. **Boarding restored after a reload.** Quicksave with a locked craft loaded, quit, reload
   with nothing locked, and confirm boarding works. This is the check that `CanBoard` never
   reached the `.sfs`.
6. **A locked craft destroyed mid-lock**, and **a locked craft left behind while flying
   another vessel**. Both are reconcile paths with no `OnDestroy` guarantee.

## Known limitations, accepted deliberately

- **Boarding suppression is game-wide while a locked craft is loaded.** KSP offers no
  per-attempt boarding hook, so an unrelated craft parked next to a locked one also cannot be
  boarded. Narrowing it needs a Harmony patch on `KerbalEVA.BoardPart`, which is a bigger
  dependency than the precision is worth today.
- **The PIN is readable by anyone you share the save with.** Deliberate, and the price of it
  being shareable at all - see the CHANGELOG entry for why machine-bound encryption was tried
  and removed. A three-digit code cannot be both verifiable on someone else's machine and
  secret at rest.
- **The wrong-code lockout resets on quickload.** Also deliberate — see `ModuleAegisLock`.
- **A craft with no command part cannot be locked**, which means a fuel can on a decoupler is
  never protected. Correct, but worth knowing before relying on the lock for a station.

## Deliberately not built

Recorded so they are decisions rather than omissions:

- **A master/override code.** Tempting as a lockout rescue; it would make every individual
  PIN meaningless. The save file is the rescue.
- ~~**Encrypting the stored PIN.**~~ **Tried, and removed.** Machine-bound encryption (DPAPI,
  with a portable AES fallback) hid the code in a synced multiplayer save and made it
  impossible to *share* — a crewmate could not be given the PIN to fly your craft, because
  their client could not check it. That is most of what a PIN is for.

  The bar for bringing it back: *verifiable on someone else's machine.* That rules out
  machine-bound keys entirely, and a plain hash is no help at three digits — a thousand
  offline guesses. It would need a much longer minimum PIN and a slow KDF, and even then buys
  little against someone who can also just edit `isLocked`.
- ~~**Harmony.**~~ **Reversed.** Recovery blocking needs it: there is no stock veto, no single
  choke point, and `Vessel.IsRecoverable` is a computed property with no setter. Three prefix
  patches in `RecoveryGuard.cs` are the only way to refuse a recovery. Contained so that a
  missing `0Harmony.dll` costs that feature and nothing else, and not redistributed — the
  CKAN metadata depends on `Harmony2`.

  The bar it had to clear, kept here for the next candidate: *the stock API offers no way to
  express the refusal at all.* Boarding precision still does not clear it — `CanBoard` is
  coarse but it works.
- **A mod-wide settings window.** Everything configurable is a `[KSPField]`, so ModuleManager
  already configures it, and per-part is the more useful granularity.
- **Persisting the lockout counter.** It would punish forgetfulness harder than brute force.

## Ideas for later

Roughly in order of value.

- **A locked-craft indicator** somewhere ambient — an icon, or a marker near the navball.
  Right now a locked craft is only distinguishable by trying to fly it, which is a poor way
  to find out, and it is the most common piece of feedback a lock mod gets asked for.
- **Restrict crew transfer.** It is the third route in and out of a locked craft and is
  currently open. `Part.crewTransferAvailable` and `GameEvents.onCrewTransferSelected` look
  like the hooks; `crewTransferAvailable` is read by `CrewTransfer.IsValidPart`, so setting
  it per-part on a locked vessel would be precise in a way the boarding block is not.
- **`guiActiveUnfocused` on the keypad events**, so a Kerbal on EVA can work the pad on a
  probe they have floated over to. Fits the fiction well; note it needs `ACTIONS_EXTERNAL`
  considered, which the lock currently takes.
- **Lock on launch**, as a VAB toggle.
- **Distinguish a locked probe from an unconnected one** in the refusal message. They feel
  identical from the cockpit and one is a mod behaviour the player chose.

## Open questions

- What reads `TWEAKABLES_ANYCONTROL`? It is not `CanActivateEvent`, and the consumer was not
  found in the `UIPartAction*` classes. Until that is known, the effect of the lock on
  tweakable *sliders and toggles* is uncharacterised — observationally they stay usable.
- Should a locked craft still be switchable-to from the tracking station? Currently yes;
  `VESSEL_SWITCHING` is not in `ALL_SHIP_CONTROLS`.
