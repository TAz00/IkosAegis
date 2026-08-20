using UnityEngine;

namespace IkosAegis.Core
{
    /// <summary>
    /// Keeps crew from walking around an engaged lock.
    ///
    /// A control lock stops a craft flying. It does nothing about a Kerbal stepping outside,
    /// and nothing about a fresh Kerbal climbing aboard - so without this, "locked" means
    /// "locked, unless you get out and walk", which is not a lock.
    ///
    /// The two halves work completely differently because KSP offers completely different
    /// hooks for them, and the difference is worth understanding before changing either.
    /// </summary>
    public static class CrewRestrictions
    {
        // ------------------------------------------------------------------
        // EVA - a per-attempt veto, no global state
        // ------------------------------------------------------------------
        //
        // FlightEVA.spawnEVA does this, in order:
        //
        //     overrideEVA = false;
        //     GameEvents.onAttemptEva.Fire(crew, part, transform);
        //     if (overrideEVA) return null;
        //
        // So the event is a genuine veto, and the flag is the game's own way of expressing
        // "this EVA does not happen". Using it means the refusal unwinds through KSP's own
        // code path rather than ours.
        //
        // This matters more than it looks. The tempting alternative - swapping
        // FlightEVA.Spawn for a delegate returning null - throws an NRE *inside KSP's own EVA
        // setup*, which never unwinds and leaves the crew portrait permanently broken. A
        // refusal the host API cannot express is not a refusal.
        //
        // Because the event carries the part being left, this check is exact: it refuses EVA
        // from a locked craft and says nothing about any other craft in the scene.

        // ------------------------------------------------------------------
        // Boarding - a global flag, suppressed and restored
        // ------------------------------------------------------------------
        //
        // KerbalEVA.BoardPart reads HighLogic.CurrentGame.Parameters.Flight.CanBoard first
        // and posts the game's own refusal message if it is false. There is no
        // onAttemptBoard event and no per-part flag on the boarding path - the whole thing
        // runs inside the EVA Kerbal's FSM - so this global is the only stock gate.
        //
        // Two consequences that are accepted rather than solved:
        //
        //   * It is coarse. While a locked craft is loaded, boarding *anything* in the scene
        //     is refused, including an unrelated unlocked craft parked alongside. Narrowing
        //     it would need a Harmony patch on KerbalEVA, which is a much larger dependency
        //     than this buys.
        //
        //   * It is saved with the game. A suppressed value written to a .sfs and then left
        //     there would disable boarding permanently, in a way the player cannot see and
        //     would never attribute to this mod.
        //
        // The second is handled rather than accepted: the player's value is restored *before*
        // every save is written and re-suppressed after, so the suppressed value never
        // reaches disk. A hard kill writes no save at all, so the on-disk value stays
        // correct through a crash too - which is the case a restore-on-quit handler would
        // miss entirely. Plus the "already off, leave it alone" guard in Suppress(), which is
        // what stops a save that *did* get corrupted from being adopted as the baseline.
        //
        // ------------------------------------------------------------------
        // Why EVA does NOT use the matching CanEVA flag
        // ------------------------------------------------------------------
        //
        // KSPRedeem seals hatches with GameParameters.Flight.CanEVA, which is the better
        // mechanism in a single-player mod: the stock UI understands it, so the portrait's
        // EVA button greys itself out with no half-finished state anywhere.
        //
        // It is not usable here, because **Luna Multiplayer already owns that flag**.
        // LmpClient/Systems/VesselLockSys/VesselLockSystem.cs sets `CanEVA = false` in
        // StartSpectating and - the part that matters - **unconditionally `= true`** in
        // StopSpectating. So under LMP:
        //
        //   * an Aegis EVA block would be silently cleared the moment the player stopped
        //     spectating anything, and
        //   * an Aegis restore would re-enable EVA in the middle of LMP's spectate mode.
        //
        // Two mods writing one global flag with no coordination, where whoever ran last wins.
        // `onAttemptEva` has no such problem: it is per-attempt, it holds no state between
        // attempts, and any number of mods can veto independently.
        //
        // CanBoard is safe by the same test - LMP never touches it, and GameParameters are
        // per-client, so suppressing it cannot stop *another player* boarding *their*
        // unlocked craft. It only over-restricts the local player, in the scene where a
        // locked craft is already present.

        private static bool _suppressingBoard;
        private static bool _playerCanBoard = true;

        /// <summary>True while boarding is being held off on our account.</summary>
        public static bool SuppressingBoard { get { return _suppressingBoard; } }

        // ------------------------------------------------------------------
        // Subscription lives on AegisAddon, not here
        // ------------------------------------------------------------------
        //
        // These are plain static logic methods and nothing subscribes them to GameEvents
        // directly. **A static method cannot be a GameEvents handler** - `EventData.Add`
        // throws a NullReferenceException inside KSP's own `EvtDelegate` constructor, which
        // is a confusing enough stack trace that it is worth stating here rather than
        // rediscovering. This was the first exception the mod ever logged, from exactly this
        // file.
        //
        // So AegisAddon (a MonoBehaviour) owns instance handlers that forward to these.

        public static void HandleAttemptEva(ProtoCrewMember crew, Part part, Transform hatch)
        {
            if (part == null || part.vessel == null) return;
            if (!AegisAddon.VesselIsLocked(part.vessel)) return;

            FlightEVA eva = FlightEVA.fetch;
            if (eva == null)
            {
                // Cannot veto without it, so say so rather than let the EVA proceed while
                // the log claims the craft is locked.
                AegisLog.Error("An EVA from locked vessel '" + part.vessel.vesselName +
                               "' could not be refused: FlightEVA.fetch was null. The EVA WENT AHEAD.");
                return;
            }

            eva.overrideEVA = true;

            AegisSound.Play(AegisSound.Denied);
            ScreenMessages.PostScreenMessage(
                "[Aegis] " + (crew != null ? crew.name : "That Kerbal") +
                " cannot EVA - " + part.vessel.vesselName + " is locked.",
                4f, ScreenMessageStyle.UPPER_CENTER);

            AegisLog.Info("Refused an EVA from locked vessel '" + part.vessel.vesselName + "'.");
        }

        /// <summary>
        /// Brings the boarding flag in line with whether any locked craft is loaded. Called
        /// from the addon's per-frame reconcile, so it is subject to exactly the same
        /// release-on-every-path guarantee as the control locks.
        /// </summary>
        public static void Reconcile(bool anyLockedVesselLoaded)
        {
            if (anyLockedVesselLoaded)
            {
                Suppress();
            }
            else
            {
                Restore();
            }
        }

        private static GameParameters.FlightParams Flight
        {
            get
            {
                Game game = HighLogic.CurrentGame;
                if (game == null || game.Parameters == null) return null;
                return game.Parameters.Flight;
            }
        }

        private static void Suppress()
        {
            GameParameters.FlightParams flight = Flight;
            if (flight == null) return;

            if (!_suppressingBoard)
            {
                // **Only ever flip true -> false.** If boarding is already off, there is
                // nothing to do and nothing to restore, and turning it back on later would
                // hand the player something they had deliberately switched off.
                //
                // This guard is borrowed from KSPRedeem's EvaBlocker, and it is not a nicety:
                // without it, the very first build of this class captured an already-false
                // value as "the player's setting" and would have kept boarding disabled for
                // good. The log line read "restoring to False afterwards", which is what the
                // bug looks like from outside.
                if (!flight.CanBoard)
                {
                    if (!_alreadyOff)
                    {
                        _alreadyOff = true;
                        AegisLog.Info("A locked craft is loaded, but boarding is already off in " +
                                      "this save's settings - leaving it alone, and it will not be " +
                                      "turned back on by this mod.");
                    }
                    return;
                }

                _alreadyOff = false;
                _playerCanBoard = true;     // it was true; that is the only way we get here
                _suppressingBoard = true;
                AegisLog.Info("Boarding suppressed while a locked craft is loaded.");
            }

            flight.CanBoard = false;
        }

        /// <summary>
        /// True when boarding was already off before we wanted it off, so this mod is not
        /// responsible for it and must not turn it on.
        /// </summary>
        private static bool _alreadyOff;

        /// <summary>
        /// Puts the player's value back. Safe to call when not suppressing.
        /// </summary>
        public static void Restore()
        {
            if (!_suppressingBoard) return;

            GameParameters.FlightParams flight = Flight;
            if (flight != null) flight.CanBoard = _playerCanBoard;

            _suppressingBoard = false;
            _alreadyOff = false;
            AegisLog.Info("Boarding re-enabled - no locked craft is loaded any more.");
        }

        /// <summary>
        /// Fired before the game state is serialised. Puts the player's value back so the
        /// suppressed one is never what gets written.
        /// </summary>
        public static void HandleGameStateSave(ConfigNode node)
        {
            if (!_suppressingBoard) return;

            GameParameters.FlightParams flight = Flight;
            if (flight != null) flight.CanBoard = _playerCanBoard;

            AegisLog.Debug("Restored CanBoard for the duration of a save.");
        }

        /// <summary>
        /// Fired after the save is written. Re-applies the suppression; the reconcile would
        /// do it on the next frame anyway, but leaving a one-frame hole in a restriction is
        /// the kind of thing that is found by accident and never reproduced.
        /// </summary>
        public static void HandleGameStateSaved(Game game)
        {
            if (!_suppressingBoard) return;

            GameParameters.FlightParams flight = Flight;
            if (flight != null) flight.CanBoard = false;

            AegisLog.Debug("Re-suppressed CanBoard after the save.");
        }
    }
}
