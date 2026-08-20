using System.Collections.Generic;

namespace IkosAegis.Core
{
    /// <summary>
    /// The only place in this mod that touches <c>InputLockManager</c>.
    ///
    /// <c>InputLockManager</c> is a single global stack shared with the game and every other
    /// mod, and a leaked lock leaves the player unable to fly until they restart. Funnelling
    /// every set and remove through one class means there is exactly one list of what this
    /// mod is currently holding, which is what makes
    /// <see cref="AegisAddon"/>'s reconcile able to guarantee release on every path -
    /// including the ones nobody wrote code for, like a part being destroyed mid-flight.
    /// </summary>
    public static class ControlLock
    {
        /// <summary>
        /// What "locked" means.
        ///
        /// The full <c>ALL_SHIP_CONTROLS</c> mask (<c>0x0C47FFFFFFFE32BF</c>), which is the
        /// maximum lockdown available: pitch, roll, yaw, throttle, staging, SAS, RCS, wheel
        /// steering and throttle, every action group, and - because
        /// <c>ACTIONS_SHIP</c> (<c>0x800000</c>) is inside it - **every part's right-click
        /// buttons across the whole craft**. A locked craft cannot decouple, deploy a solar
        /// panel or fire an engine from the part menu either.
        ///
        /// <b>That last property is exactly what nearly made this mod unusable</b>, and the
        /// reason it is safe now is documented on <c>ModuleAegisLock</c>'s events rather than
        /// here: <c>UIPartActionWindow.CanActivateEvent</c> hides every part-menu button
        /// while <c>ACTIONS_SHIP</c> is locked *unless* the button sets
        /// <c>guiActiveUncommand = true</c>. Our two buttons set it; nothing else on the
        /// craft does. So the unlock keypad stays reachable and everything else does not.
        ///
        /// **If you ever widen or narrow this mask, re-read that method first.** The IL is
        /// the only documentation of it, and the failure it produces - a locked craft with no
        /// way back in - is unrecoverable in game.
        ///
        /// Time warp is *not* in <c>ALL_SHIP_CONTROLS</c> (bit <c>0x800</c> is clear), so it
        /// stays available - deliberately left that way, since a locked craft the player
        /// cannot warp past would be a worse experience than a locked one they can.
        /// </summary>
        public const ControlTypes LockedControls = ControlTypes.ALL_SHIP_CONTROLS;

        private static readonly HashSet<string> HeldKeys = new HashSet<string>();

        /// <summary>Lock keys this mod currently believes it holds.</summary>
        public static IEnumerable<string> Held { get { return HeldKeys; } }

        public static int HeldCount { get { return HeldKeys.Count; } }

        public static bool IsHeld(string key)
        {
            return key != null && HeldKeys.Contains(key);
        }

        /// <summary>
        /// Takes the lock and then <b>checks that it took</b>.
        ///
        /// <c>SetControlLock</c> returns without complaint in states where nothing happens,
        /// and a lock that was never applied looks identical from the call site to one that
        /// was. So the mask is read back through <c>GetControlLock</c> and compared. Returns
        /// false - and does not record the key as held - when the game did not do what was
        /// asked.
        /// </summary>
        public static bool Acquire(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            // **Ask the game, never our own bookkeeping.**
            //
            // This used to start `if (HeldKeys.Contains(key)) return true;`, which is the
            // obvious optimisation and was a security hole. `InputLockManager` is a global
            // stack that anyone may clear, and Luna Multiplayer does exactly that from a
            // method it calls DeleteAllTheControlLocksSoTheSpaceCentreBugGoesAway() - plus a
            // second `ClearControlLocks()` in its KSC-marker patch. Once either fired, our set
            // still said "held", the reconcile short-circuited, and the lock was never
            // re-applied: the craft flew normally while the mod reported it locked.
            //
            // Re-reading costs a dictionary lookup per locked vessel per frame, which is
            // nothing, and it makes the reconcile self-healing against anything that clears
            // the stack rather than trusting that nobody will.
            ControlTypes already = InputLockManager.GetControlLock(key);
            if ((already & LockedControls) == LockedControls)
            {
                HeldKeys.Add(key);      // HashSet: no-op when it is already there
                return true;
            }

            bool weThoughtWeHadIt = HeldKeys.Contains(key);

            InputLockManager.SetControlLock(LockedControls, key);

            ControlTypes applied = InputLockManager.GetControlLock(key);
            if ((applied & LockedControls) != LockedControls)
            {
                AegisLog.Error("Control lock '" + key + "' did not apply: asked for " +
                               Describe(LockedControls) + ", the game reports " + Describe(applied) +
                               ". The craft is NOT locked.");
                InputLockManager.RemoveControlLock(key);
                HeldKeys.Remove(key);
                return false;
            }

            HeldKeys.Add(key);

            if (weThoughtWeHadIt)
            {
                // Somebody wiped the stack under us. Worth an Info line rather than Debug:
                // it means another mod is clearing global locks, which is the difference
                // between "locked" and "believed to be locked".
                AegisLog.Info("Control lock '" + key + "' had been cleared by something else and " +
                              "has been re-applied. (Luna Multiplayer clears the whole lock stack " +
                              "on some scene transitions; this is the mod recovering from that.)");
            }
            else
            {
                AegisLog.Debug("Control lock '" + key + "' applied and verified (" + HeldKeys.Count + " held).");
            }

            return true;
        }

        /// <summary>
        /// Releases the lock. Safe to call for a key that is not held - the underlying
        /// remove is idempotent, and a release that runs twice is much cheaper than one that
        /// runs zero times.
        /// </summary>
        public static void Release(string key, string reason)
        {
            if (string.IsNullOrEmpty(key)) return;

            InputLockManager.RemoveControlLock(key);

            if (HeldKeys.Remove(key))
            {
                AegisLog.Debug("Control lock '" + key + "' released (" + reason + "); " +
                               HeldKeys.Count + " still held.");
            }
        }

        /// <summary>
        /// Drops everything this mod holds. Called on scene change, on quit, and whenever
        /// the reconcile cannot establish that a lock is still justified.
        /// </summary>
        /// <summary>
        /// Drops everything this mod holds, saying why.
        ///
        /// <b>The reason is not decoration.</b> An earlier version logged only
        /// "Released N Aegis control lock(s)", which reads like the player unlocked
        /// something — and it was read that way, in a summary of a test session where the
        /// player could not unlock anything at all because the button was hidden. The line
        /// could not distinguish "a correct PIN was entered" from "the scene changed" from
        /// "the mod stood down", which are the only three things anyone wants to know.
        ///
        /// A line that cannot tell apart the outcomes it reports is worse than no line: it
        /// actively sends the reader the wrong way. Pass a reason, always.
        /// </summary>
        public static void ReleaseAll(string reason)
        {
            if (HeldKeys.Count == 0) return;

            // Copy first: RemoveControlLock does not touch this set, but releasing while
            // enumerating it is the kind of thing that only breaks after someone edits
            // Release() six months from now.
            string[] keys = new string[HeldKeys.Count];
            HeldKeys.CopyTo(keys);

            for (int i = 0; i < keys.Length; i++)
            {
                InputLockManager.RemoveControlLock(keys[i]);
            }

            AegisLog.Info("Released " + keys.Length + " Aegis control lock(s) - " + reason +
                          ". (This is the lock being lifted, not a vessel being unlocked; " +
                          "an unlock logs its own line and leaves isLocked false.)");
            HeldKeys.Clear();
        }

        /// <summary>
        /// A readable rendering of a <c>ControlTypes</c> mask, for log lines that have to
        /// explain why a lock did not do what was expected. The enum's own ToString on a
        /// composite value prints a long and mostly unhelpful list, so the hex goes with it.
        /// </summary>
        public static string Describe(ControlTypes types)
        {
            return "0x" + ((ulong)types).ToString("X16");
        }
    }
}
