using System.Collections.Generic;

namespace IkosAegis.Core
{
    /// <summary>
    /// Repairs docking ports that an earlier version of this mod left permanently disabled.
    ///
    /// <b>The blocking itself no longer lives here.</b> It is a Harmony postfix on
    /// <c>ModuleDockingNode.FindNodeApproaches</c>, in <see cref="RecoveryGuard"/> with the
    /// mod's other patches — see there for why.
    ///
    /// <b>What went wrong, because the lesson is bigger than the bug.</b> The first version
    /// blocked docking by pushing the port's <c>KerbalFSM</c> into its <c>st_disabled</c>
    /// state — the game's own mechanism, which looked like exactly the right answer. It is
    /// not, because that state is **saved**:
    ///
    /// <code>
    /// ModuleDockingNode.OnSave:  node.AddValue("state", fsm.currentStateName);
    /// ModuleDockingNode.OnLoad:  state = node.GetValue("state");   // then lateFSMStart
    /// </code>
    ///
    /// Not a <c>[KSPField(isPersistant = true)]</c> — hand-written into the ConfigNode, which
    /// is why a check of the module's persistent fields says it is not persisted. It is.
    ///
    /// So a port disabled while a craft was locked came back disabled after any unload, and
    /// the in-memory record of "we disabled this" did not: it was cleared on every scene
    /// change. After that the port was stuck forever, and re-locking could not fix it because
    /// the disable path skips a port that is already disabled, so it was never re-recorded.
    ///
    /// The rule worth carrying: <b>a transient condition must not be expressed through
    /// durable state.</b> "This craft is locked right now" is runtime; the FSM state outlives
    /// the session, and the bookkeeping that would have paired with it did not.
    /// </summary>
    public static class DockingGuard
    {
        /// <summary>Vessels already swept this session, so the repair runs once per load.</summary>
        private static readonly HashSet<uint> Repaired = new HashSet<uint>();

        /// <summary>
        /// Re-enables ports left stuck by the old FSM-disabling implementation.
        ///
        /// A port is only touched when it is disabled <b>and not shielded</b> — a port inside
        /// a fairing or cargo bay is disabled for a real reason that is none of our business,
        /// and <c>Part.ShieldedFromAirstream</c> is how the game says so.
        ///
        /// Runs once per vessel per load rather than every frame: repeatedly fighting another
        /// mod over a port would be worse than the bug being fixed, and once is enough to
        /// undo a state that only this mod ever wrote.
        /// </summary>
        public static void Reconcile()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;

            List<Vessel> vessels = FlightGlobals.VesselsLoaded;
            if (vessels == null) return;

            for (int v = 0; v < vessels.Count; v++)
            {
                Vessel vessel = vessels[v];
                if (vessel == null || Repaired.Contains(vessel.persistentId)) continue;

                List<ModuleDockingNode> nodes = vessel.FindPartModulesImplementing<ModuleDockingNode>();
                if (nodes == null) continue;

                // Only mark as swept once the ports are actually alive; a vessel caught
                // mid-load would otherwise be recorded as repaired having been skipped.
                bool anyReady = false;
                int repaired = 0;

                for (int n = 0; n < nodes.Count; n++)
                {
                    ModuleDockingNode node = nodes[n];
                    if (node == null || node.fsm == null || !node.fsm.Started) continue;

                    anyReady = true;

                    if (!node.IsDisabled) continue;
                    if (node.part != null && node.part.ShieldedFromAirstream) continue;

                    node.fsm.RunEvent(node.on_enable);

                    // Observe, do not assume: RunEvent is a silent no-op when the current
                    // state has no transition for it.
                    if (node.IsDisabled)
                    {
                        AegisLog.Warn("Could not re-enable docking port '" + PartName(node) + "' on '" +
                                      vessel.vesselName + "' (FSM state '" + node.state + "'). " +
                                      "If this port was disabled by an old IkosAegis build it will " +
                                      "stay that way; edit 'state = Ready' on it in the save to fix.");
                        continue;
                    }

                    repaired++;
                }

                if (!anyReady) continue;
                Repaired.Add(vessel.persistentId);

                if (repaired > 0)
                {
                    AegisLog.Info("Re-enabled " + repaired + " docking port(s) on '" + vessel.vesselName +
                                  "' that an earlier IkosAegis build had left disabled in the save. " +
                                  "Docking no longer touches port state at all.");
                }
            }
        }

        /// <summary>
        /// Forgets which vessels have been swept, so a reload gets a fresh pass. Called on
        /// scene change and shutdown; there is no longer any state to release.
        /// </summary>
        public static void ReleaseAll(string reason)
        {
            Repaired.Clear();
        }

        private static string PartName(ModuleDockingNode node)
        {
            return node != null && node.part != null && node.part.partInfo != null
                ? node.part.partInfo.name
                : "unknown part";
        }
    }
}
