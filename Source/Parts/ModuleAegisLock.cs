using IkosAegis.Core;
using IkosAegis.Logic;
using IkosAegis.UI;
using UnityEngine;

namespace IkosAegis.Parts
{
    /// <summary>
    /// A PIN lock on a command part. Attached by ModuleManager patch rather than by a part
    /// config of our own, so it lands on stock probe cores and on every modded one that
    /// looks like a probe core, without either mod knowing about the other.
    ///
    /// The module holds state and draws part-menu buttons. It does <b>not</b> touch
    /// <c>InputLockManager</c> - see <see cref="AegisAddon"/> for why the lock itself is
    /// reconciled centrally.
    ///
    /// <b>The PIN is not a secret.</b> It is stored in plain text in the craft file and the
    /// savegame, and anyone with a text editor can read it. That is deliberate: this is a
    /// gameplay device, and a player who has locked themselves out should be able to rescue
    /// their own save without reinstalling anything.
    /// </summary>
    public class ModuleAegisLock : PartModule
    {
        // --- Persistent state (craft file and savegame) ---

        /// <summary>
        /// The code. Empty means "never configured", which is a distinct state from any
        /// particular PIN: a part with no PIN cannot be locked at all, because a lock whose
        /// code is the empty string opens by pressing OK.
        /// </summary>
        [KSPField(isPersistant = true)]
        public string pinCode = "";

        [KSPField(isPersistant = true)]
        public bool isLocked = false;

        // --- Configuration, from the ModuleManager patch ---

        [KSPField(isPersistant = false)]
        public int pinLength = PinCode.DefaultLength;

        /// <summary>Wrong entries before the keypad starts refusing. 0 disables the penalty.</summary>
        [KSPField(isPersistant = false)]
        public int lockoutAfter = 3;

        /// <summary>Base penalty in real seconds; doubles per failure past the threshold.</summary>
        [KSPField(isPersistant = false)]
        public float lockoutSeconds = 30f;

        // --- Part menu readout ---

        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = true, guiName = "Aegis")]
        public string statusText = "Unlocked";

        /// <summary>
        /// Failure bookkeeping, deliberately <b>not</b> persisted.
        ///
        /// A quickload clears it, which is a real hole in the deterrent and an accepted one:
        /// persisting it would mean a player who forgot their own PIN carries a growing
        /// penalty across every reload, and the alternative rescue - editing the save - is
        /// the same rescue they would have used anyway. The lockout exists to make mashing
        /// the pad tedious, not to be airtight.
        ///
        /// Timed on <c>realtimeSinceStartup</c> rather than universal time so the penalty
        /// cannot be skipped by warping through it.
        /// </summary>
        private int _failedAttempts;
        private double _lockedOutUntil;

        /// <summary>
        /// This part's identity in the global lock stack. Namespaced, because the stack is
        /// shared with the game and every other mod.
        /// </summary>
        /// <summary>
        /// This <b>vessel's</b> identity in the global lock stack. Namespaced, because the
        /// stack is shared with the game and every other mod.
        ///
        /// Keyed on the vessel and not the part: a craft with three command pods is one
        /// craft with one lock, so all three modules produce the same key and the reconcile
        /// naturally collapses them into a single entry.
        /// </summary>
        public string LockKey
        {
            get
            {
                if (vessel == null) return "IkosAegis_orphan";
                return "IkosAegis_" + vessel.id.ToString("N");
            }
        }

        /// <summary>
        /// Whether this part currently justifies a control lock. Read by
        /// <see cref="AegisAddon"/> every frame.
        ///
        /// The active-vessel test is the important one: <c>InputLockManager</c> is global, so
        /// a lock taken for a craft the player is not flying would disable the craft they
        /// <i>are</i> flying.
        /// </summary>
        public bool WantsControlLock
        {
            get
            {
                return isLocked
                    && HighLogic.LoadedSceneIsFlight
                    && part != null
                    && vessel != null
                    && vessel.isActiveVessel;
            }
        }

        /// <summary>
        /// Engaged, and its vessel is loaded in the scene — whether or not the player is
        /// flying it.
        ///
        /// This is the predicate the crew restrictions use, and it is deliberately *not*
        /// <see cref="WantsControlLock"/>. The moment a Kerbal steps outside, the locked
        /// craft stops being the active vessel; a boarding restriction keyed on the active
        /// vessel would therefore switch itself off at exactly the moment it is needed.
        /// </summary>
        public bool IsLockedAndLoaded
        {
            get
            {
                return isLocked
                    && HighLogic.LoadedSceneIsFlight
                    && part != null
                    && vessel != null;
            }
        }

        private int EffectivePinLength { get { return PinCode.ClampLength(pinLength); } }

        /// <summary>
        /// True when this part carries a usable PIN.
        ///
        /// <c>pinCode</c> is plain text: a code that only worked on the machine that set it
        /// could not be shared, and sharing it is most of what a PIN is for.
        /// </summary>
        private bool HasPin
        {
            get { return PinCode.IsSet(pinCode, EffectivePinLength); }
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);

            ReconcileStoredState();
            AegisAddon.Register(this);
            RefreshMenu();
            VerifyEscapeHatch();

            // A craft assembled from parts that were saved separately — or docked together —
            // can arrive with disagreeing state. Settle it now rather than letting the player
            // meet two different PINs on one vessel.
            AegisAddon.UnifyVessel(vessel);
        }

        /// <summary>
        /// Checks, at runtime, that the buttons which release the lock will still be visible
        /// once it is engaged.
        ///
        /// This is the one invariant in the mod whose failure cannot be undone in game: a
        /// craft locked with no reachable Disengage button stays locked for the rest of the
        /// save. The attribute that prevents it is easy to drop in an edit and impossible to
        /// notice until someone has locked something, and another mod's config patch can
        /// override it without either mod knowing.
        ///
        /// So it is asserted rather than assumed, and loudly - the failure is worth a line
        /// somebody greps for.
        /// </summary>
        private void VerifyEscapeHatch()
        {
            CheckUncommand("ToggleLockEvent");
            CheckUncommand("SetPinEvent");
        }

        private void CheckUncommand(string eventName)
        {
            BaseEvent e = Events[eventName];

            if (e == null)
            {
                AegisLog.Error("Part menu event '" + eventName + "' was not found. Events[] is a " +
                               "lookup by method name, so this means the method was renamed. " +
                               "The lock will not be operable from the part menu.");
                return;
            }

            if (!e.guiActiveUncommand)
            {
                AegisLog.Error("Part menu event '" + eventName + "' has guiActiveUncommand = false. " +
                               "The Aegis control lock takes ACTIONS_SHIP, which hides every part-menu " +
                               "button that does not set it - so engaging the lock would hide the " +
                               "button that releases it, permanently. Refusing to allow locking on " +
                               "this part.");
                _escapeHatchBroken = true;
            }
        }

        /// <summary>
        /// Set when <see cref="VerifyEscapeHatch"/> finds the unlock button would be hidden.
        /// Locking is refused while it is true — a lock nobody can open is worse than no lock.
        /// </summary>
        private bool _escapeHatchBroken;

        public void OnDestroy()
        {
            AegisAddon.Unregister(this);

            // A keypad outliving the part it belongs to would fire its callback against a
            // destroyed module. Only close one this part could plausibly own - in flight,
            // and only when something is actually open.
            if (KeypadDialog.IsOpen && HighLogic.LoadedSceneIsFlight)
            {
                KeypadDialog.DismissOpen();
            }
        }

        /// <summary>
        /// Repairs states the module cannot have produced but can still be loaded into: a
        /// hand-edited save, a patch that shipped <c>isLocked = true</c> with no code, or a
        /// <c>pinLength</c> another mod changed after a PIN was already set.
        ///
        /// Each of these ends the same way if left alone - a craft that can never be
        /// unlocked, because the code that would open it cannot be typed. Both are therefore
        /// resolved in favour of the player, loudly.
        /// </summary>
        private void ReconcileStoredState()
        {
            int length = EffectivePinLength;

            if (pinLength != length)
            {
                AegisLog.Warn("[" + part.partInfo.name + "] pinLength " + pinLength +
                              " is outside " + PinCode.MinLength + "-" + PinCode.MaxLength +
                              "; using " + length + ".");
            }

            string storedPin = pinCode;

            if (!string.IsNullOrEmpty(storedPin) && !PinCode.IsValid(storedPin, length))
            {
                AegisLog.Warn("[" + part.partInfo.name + "] stored PIN is not " + length +
                              " digits and could never be entered. Clearing it and unlocking " +
                              "the part so it can be reconfigured.");
                pinCode = "";
                isLocked = false;
                return;
            }

            if (isLocked && !HasPin)
            {
                AegisLog.Warn("[" + part.partInfo.name + "] was saved locked with no PIN set. " +
                              "Nothing could ever have opened it; unlocking.");
                isLocked = false;
            }
        }

        // --- Part menu ---
        //
        // Both handlers are looked up by *method name* through Events[...] in RefreshMenu.
        // That is a runtime string lookup, so renaming either method compiles cleanly and
        // silently stops the menu updating. Keep the names.
        //
        // ===================================================================================
        // guiActiveUncommand = true IS WHAT MAKES THIS MOD USABLE. DO NOT REMOVE IT.
        // ===================================================================================
        //
        // The lock takes ControlTypes.ALL_SHIP_CONTROLS, which contains ACTIONS_SHIP
        // (0x800000). UIPartActionWindow.CanActivateEvent, for a part on the active vessel
        // in flight, reduces to:
        //
        //     if (!guiActive || !active || EventIsDisabledByVariant) return false;
        //     if (ACTIONS_SHIP is locked)          return guiActiveUncommand;
        //     if (!vessel.IsControllable)          return guiActiveUncommand;
        //     if (!requireFullControl)             return true;
        //     if (TWEAKABLES_FULLONLY is unlocked) return true;
        //     return guiActiveUncommand;
        //
        // So while the lock is engaged, *every* part-menu button on the craft is hidden
        // except those that set guiActiveUncommand. Without it here, engaging the lock hides
        // the button that disengages it and the craft can never be unlocked again - which is
        // exactly what the first flight test found. Verified by disassembling
        // Assembly-CSharp with Mono.Cecil; the IL is the only documentation of this rule.
        //
        // The same fact is why the mask is left at full ALL_SHIP_CONTROLS: hiding every other
        // part's buttons is a feature (a locked craft cannot decouple or deploy anything
        // either), and these two events are the deliberate exception.

        [KSPEvent(guiActive = true, guiActiveEditor = true, guiActiveUncommand = true,
                  guiName = "Set Aegis PIN", active = true)]
        public void SetPinEvent()
        {
            int length = EffectivePinLength;

            if (HighLogic.LoadedSceneIsEditor)
            {
                // In the VAB the craft is being designed and there is nothing to defend, so
                // setting the code is unconditional. Asking for the old PIN here would only
                // punish someone iterating on a subassembly.
                KeypadDialog.Show("Set Aegis PIN (" + length + " digits)", length, AcceptNewPin);
                return;
            }

            if (!HasPin)
            {
                KeypadDialog.Show("Set Aegis PIN (" + length + " digits)", length, AcceptNewPin);
                return;
            }

            // A PIN already exists in flight, so changing it is an unlock in disguise: if it
            // could be overwritten freely, every lock would open in four keypresses.
            if (RefuseWhileLockedOut()) return;

            KeypadDialog.Show("Current PIN", length, current =>
            {
                if (this == null || part == null) return;

                if (!PinCode.Matches(current, pinCode))
                {
                    RegisterFailure("Access denied: that is not the current PIN.");
                    return;
                }

                _failedAttempts = 0;
                AegisSound.Play(AegisSound.Granted);
                KeypadDialog.Show("New PIN (" + length + " digits)", length, AcceptNewPin);
            });
        }

        [KSPEvent(guiActive = true, guiActiveUncommand = true,
                  guiName = "Engage Aegis Lock", active = true)]
        public void ToggleLockEvent()
        {
            if (!isLocked)
            {
                Engage();
                return;
            }

            if (RefuseWhileLockedOut()) return;

            KeypadDialog.Show("Enter Aegis PIN", EffectivePinLength, entered =>
            {
                if (this == null || part == null) return;

                if (!PinCode.Matches(entered, pinCode))
                {
                    RegisterFailure("Access denied: incorrect PIN.");
                    return;
                }

                _failedAttempts = 0;
                _lockedOutUntil = 0.0;
                isLocked = false;
                AegisAddon.SyncVessel(vessel, pinCode, false);

                AegisSound.Play(AegisSound.Granted);
                Message("Access granted. " + VesselName() + " unlocked.");
                AegisLog.Info("Unlocked " + Describe() + " on a correct PIN " +
                              "(vessel-wide; all command parts on this craft are now unlocked).");
            });
        }

        /// <summary>
        /// Engaging - but never disengaging - is bound to an action group. Unlocking needs a
        /// PIN typed by a human, so there is nothing sensible to put on a key.
        /// </summary>
        [KSPAction("Engage Aegis Lock")]
        public void EngageAction(KSPActionParam param)
        {
            if (isLocked) return;
            Engage();
        }

        private void Engage()
        {
            if (_escapeHatchBroken)
            {
                AegisSound.Play(AegisSound.Denied);
                Message("Aegis is misconfigured on this part and will not lock - see KSP.log.");
                return;
            }

            if (!HasPin)
            {
                AegisSound.Play(AegisSound.Denied);
                Message("Set an Aegis PIN before locking - there would be no way back in.");
                AegisLog.Info("Refused to lock " + Describe() + ": no PIN is set.");
                return;
            }

            isLocked = true;
            AegisAddon.SyncVessel(vessel, pinCode, true);

            AegisSound.Play(AegisSound.Granted);
            Message(VesselName() + " locked. Crew cannot EVA or board while locked.");

            // Says what was requested, not what happened - the lock itself is applied by the
            // addon's reconcile on the next frame, and it is that call which reports whether
            // the game actually took it.
            AegisLog.Info("Lock requested for " + Describe() + "; the reconcile will apply it.");
        }

        private void AcceptNewPin(string entered)
        {
            if (this == null || part == null) return;

            int length = EffectivePinLength;
            if (!PinCode.IsValid(entered, length))
            {
                // The keypad's OK is disabled below full length, so reaching here means
                // something else produced the string.
                AegisSound.Play(AegisSound.Denied);
                Message("PIN must be exactly " + length + " digits.");
                return;
            }

            // **Stored in the clear, deliberately.**
            //
            // PINs used to be encrypted with a key belonging to this machine and user account,
            // which kept them unreadable in a synced multiplayer save - and made them
            // impossible to *share*. A code you cannot give to a crewmate so they can fly your
            // craft is not a PIN; it is a per-machine binding wearing a keypad. That is the
            // pitfall this reverts.
            //
            // Note what was actually given up. A three-digit code cannot be protected at rest
            // by anything that must also verify on somebody else's machine: a salted hash
            // falls to a thousand offline guesses. The privacy was mostly notional at this
            // length, and the shareability is real.
            pinCode = entered;
            _failedAttempts = 0;
            _lockedOutUntil = 0.0;
            AegisAddon.SyncVessel(vessel, pinCode, isLocked);

            AegisSound.Play(AegisSound.Granted);
            Message("Aegis PIN set for " + VesselName() + ".");
            AegisLog.Info("PIN set on " + Describe() + " (vessel-wide).");
        }

        /// <summary>
        /// Records a wrong entry and tells the player. Returns nothing useful on purpose -
        /// every caller's next move is to stop.
        /// </summary>
        private void RegisterFailure(string reason)
        {
            _failedAttempts++;
            _lockedOutUntil = LockoutPolicy.NextLockoutUntil(
                _failedAttempts, lockoutAfter, lockoutSeconds, Time.realtimeSinceStartup);

            AegisSound.Play(AegisSound.Denied);

            int wait = LockoutPolicy.SecondsRemaining(_lockedOutUntil, Time.realtimeSinceStartup);
            Message(wait > 0
                ? reason + " Keypad disabled for " + wait + "s."
                : reason);

            AegisLog.Info("Rejected a PIN on " + Describe() + " (attempt " + _failedAttempts +
                          (wait > 0 ? ", keypad disabled for " + wait + "s" : "") + ").");
        }

        /// <summary>True when the keypad is in its penalty window, having said so.</summary>
        private bool RefuseWhileLockedOut()
        {
            float now = Time.realtimeSinceStartup;
            if (!LockoutPolicy.IsLockedOut(_lockedOutUntil, now)) return false;

            AegisSound.Play(AegisSound.Denied);
            Message("Keypad disabled - " + LockoutPolicy.SecondsRemaining(_lockedOutUntil, now) + "s remaining.");
            return true;
        }

        /// <summary>
        /// Brings the readout and the button label in line with the state.
        ///
        /// Called on every state change rather than on a timer: nothing here changes except
        /// as a direct result of one of the handlers above, so polling would be work done
        /// forever to notice something that already announced itself.
        /// </summary>
        private void RefreshMenu()
        {
            statusText = isLocked
                ? "LOCKED"
                : (HasPin ? "Unlocked" : "No PIN set");

            BaseEvent toggle = Events["ToggleLockEvent"];
            if (toggle != null)
            {
                toggle.guiName = isLocked ? "Disengage Aegis Lock" : "Engage Aegis Lock";
            }

            BaseEvent setPin = Events["SetPinEvent"];
            if (setPin != null)
            {
                setPin.guiName = HasPin ? "Change Aegis PIN" : "Set Aegis PIN";
            }
        }

        /// <summary>
        /// Applies vessel-wide state pushed by <see cref="AegisAddon.SyncVessel"/>.
        ///
        /// One craft, one lock, one PIN — so a change made on any command part is written to
        /// every other one on the same vessel. The alternative (independent PINs per pod) was
        /// rejected: on a three-pod station it means three codes to remember and a lock that
        /// is only as strong as whichever pod the owner forgot to set.
        /// </summary>
        public void ApplySync(string pin, bool locked)
        {
            pinCode = pin;
            isLocked = locked;

            if (!locked)
            {
                // The penalty belongs to the lock, not to the part. Clearing it here means an
                // unlock through one pod does not leave a sibling still counting down.
                _failedAttempts = 0;
                _lockedOutUntil = 0.0;
            }

            // **Always refresh, never conditionally.**
            //
            // This used to skip the refresh when the values were already equal, which looks
            // like an obvious optimisation and was a real bug: the module that *initiates* a
            // change sets its own fields first and then calls SyncVessel, so by the time this
            // runs its values match and "nothing changed" is true - for the one part whose
            // menu the player is looking at. The result was a craft that locked correctly and
            // went on displaying "Aegis: No PIN set" and "Engage Aegis Lock", caught only
            // because a screenshot showed the lock working and the labels disagreeing.
            //
            // Refreshing is two string assignments and two guiName writes, on a state change
            // and never per frame. There was nothing to save.
            RefreshMenu();
        }

        /// <summary>
        /// Says no to an operation on a craft whose PIN was set elsewhere, and says why.
        ///
        /// The message names the cause explicitly because the alternative — "incorrect PIN" —
        /// would send a player hunting for a code that could not have worked whatever they
        /// typed.
        /// </summary>
        private void Message(string text)
        {
            ScreenMessages.PostScreenMessage("[Aegis] " + text, 4f, ScreenMessageStyle.UPPER_CENTER);
        }

        private string VesselName()
        {
            return vessel != null ? vessel.vesselName : "The vessel";
        }

        /// <summary>Part and vessel, for a log line that has to be findable later.</summary>
        private string Describe()
        {
            string partName = part != null && part.partInfo != null ? part.partInfo.name : "unknown part";
            return partName + " (" + LockKey + ") on " + VesselName();
        }
    }
}
