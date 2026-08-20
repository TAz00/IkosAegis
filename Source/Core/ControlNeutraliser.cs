using System;
using System.Collections.Generic;

namespace IkosAegis.Core
{
    /// <summary>
    /// Zeroes the control state of a locked vessel every physics frame.
    ///
    /// <b>Why an input lock is not enough.</b> <c>InputLockManager</c> blocks the player's
    /// *input*. It does nothing about code that writes to <c>Vessel.ctrlState</c> directly,
    /// and <c>Vessel.OnFlyByWire</c> is exactly that: a callback list KSP invokes while
    /// building the control state each frame, after input has been read. Anything on it wins.
    ///
    /// Luna Multiplayer uses it. <c>VesselFlightStateSystem.LunaOnVesselFlyByWire</c> applies
    /// the controlling player's interpolated <c>mainThrottle</c>, pitch, roll and yaw to
    /// every remote vessel. So a locked craft under LMP had a throttle pushed onto it from
    /// the network by a player who never entered the PIN, and no input lock on any client
    /// could have prevented it - the lock and the write never meet.
    ///
    /// MechJeb, kOS, Atmosphere Autopilot, Trajectories and every autopilot in the ecosystem
    /// use the same callback, so this is not an LMP-specific patch: it closes the general hole
    /// where "locked" meant "locked against a human at this keyboard".
    ///
    /// <b>Registration order is the whole trick.</b> A multicast delegate runs in the order
    /// its members were added, so the neutraliser has to be *last* to win. LMP re-adds its own
    /// handler when it notices it is missing, which can put it after ours - so the position is
    /// re-checked and corrected rather than being set once and trusted.
    /// </summary>
    public static class ControlNeutraliser
    {
        private static readonly Dictionary<Guid, FlightInputCallback> Attached =
            new Dictionary<Guid, FlightInputCallback>();

        private static readonly List<Guid> Scratch = new List<Guid>();

        public static int AttachedCount { get { return Attached.Count; } }

        /// <summary>
        /// Attaches to every loaded locked vessel and detaches from the rest. Called from the
        /// addon's per-frame reconcile, so it inherits the same release-on-every-path
        /// guarantee as the control locks.
        /// </summary>
        public static void Reconcile()
        {
            if (!HighLogic.LoadedSceneIsFlight)
            {
                DetachAll("the current scene is not flight");
                return;
            }

            List<Vessel> vessels = FlightGlobals.VesselsLoaded;
            if (vessels == null) return;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null) continue;

                if (AegisAddon.VesselIsLocked(v)) Attach(v);
                else if (Attached.ContainsKey(v.id)) Detach(v, "its vessel is no longer locked");
            }

            PruneMissing(vessels);
        }

        private static void Attach(Vessel v)
        {
            FlightInputCallback existing;
            if (Attached.TryGetValue(v.id, out existing))
            {
                EnsureLast(v, existing);
                return;
            }

            FlightInputCallback callback = st => Neutralise(st);
            v.OnFlyByWire += callback;
            Attached[v.id] = callback;

            AegisLog.Info("Neutralising flight controls on locked vessel '" + v.vesselName +
                          "' - an input lock alone would not stop a control state written by " +
                          "another mod or synced from another player.");
        }

        /// <summary>
        /// Moves our callback to the end of the invocation list if something was added after
        /// it. Cheap: an array walk, and it only re-registers when the order is actually wrong.
        /// </summary>
        private static void EnsureLast(Vessel v, FlightInputCallback ours)
        {
            if (v.OnFlyByWire == null)
            {
                // Everything was cleared. Re-add rather than assume we are still there.
                v.OnFlyByWire += ours;
                return;
            }

            Delegate[] list = v.OnFlyByWire.GetInvocationList();
            if (list.Length > 0 && ReferenceEquals(list[list.Length - 1], ours)) return;

            bool present = false;
            for (int i = 0; i < list.Length; i++)
            {
                if (ReferenceEquals(list[i], ours)) { present = true; break; }
            }

            if (present) v.OnFlyByWire -= ours;
            v.OnFlyByWire += ours;

            AegisLog.Debug("Moved the control neutraliser to the end of OnFlyByWire on '" +
                           v.vesselName + "' (something registered after it).");
        }

        private static void Detach(Vessel v, string reason)
        {
            FlightInputCallback callback;
            if (!Attached.TryGetValue(v.id, out callback)) return;

            Attached.Remove(v.id);
            if (v != null) v.OnFlyByWire -= callback;

            AegisLog.Info("Stopped neutralising controls on '" +
                          (v != null ? v.vesselName : v.id.ToString()) + "' - " + reason + ".");
        }

        /// <summary>
        /// Drops entries for vessels that are no longer loaded. Their <c>OnFlyByWire</c> went
        /// with the vessel, so there is nothing to unhook - but the dictionary would otherwise
        /// grow for the whole session.
        /// </summary>
        private static void PruneMissing(List<Vessel> loaded)
        {
            if (Attached.Count == 0) return;

            Scratch.Clear();
            foreach (KeyValuePair<Guid, FlightInputCallback> entry in Attached)
            {
                bool stillLoaded = false;
                for (int i = 0; i < loaded.Count; i++)
                {
                    if (loaded[i] != null && loaded[i].id == entry.Key) { stillLoaded = true; break; }
                }
                if (!stillLoaded) Scratch.Add(entry.Key);
            }

            for (int i = 0; i < Scratch.Count; i++) Attached.Remove(Scratch[i]);
            Scratch.Clear();
        }

        public static void DetachAll(string reason)
        {
            if (Attached.Count == 0) return;

            foreach (KeyValuePair<Guid, FlightInputCallback> entry in Attached)
            {
                Vessel v = FlightGlobals.FindVessel(entry.Key);
                if (v != null) v.OnFlyByWire -= entry.Value;
            }

            AegisLog.Info("Stopped neutralising controls on " + Attached.Count + " vessel(s) - " + reason + ".");
            Attached.Clear();
        }

        /// <summary>
        /// The actual refusal. <c>Neutralize()</c> zeroes the stick axes; the throttle is
        /// cleared explicitly because it is the one the reported bug arrived through, and
        /// being explicit about it means a future change to what `Neutralize` covers cannot
        /// quietly reopen the hole.
        /// </summary>
        private static void Neutralise(FlightCtrlState st)
        {
            if (st == null) return;

            st.Neutralize();
            st.mainThrottle = 0f;
            st.killRot = false;
        }
    }
}
