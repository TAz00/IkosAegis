using System;
using System.Collections.Generic;
using IkosAegis.Parts;
using UnityEngine;

namespace IkosAegis.Core
{
    /// <summary>
    /// The mod's single persistent MonoBehaviour, and the sole authority on which control
    /// locks exist.
    ///
    /// <b>Why the parts do not take their own locks.</b> The obvious design - and the one
    /// the original concept uses - has each <see cref="ModuleAegisLock"/> call
    /// <c>SetControlLock</c> when it engages and <c>RemoveControlLock</c> in
    /// <c>OnDestroy</c>. That is correct on the paths someone thought of, and
    /// <c>InputLockManager</c> is global, so the paths nobody thought of are the whole
    /// problem: a part destroyed by an explosion, a vessel unloading as the player switches
    /// away, a revert to launch, a scene change while a keypad is open. Any one of those
    /// leaves a lock behind, and a leaked lock means the player cannot fly *anything* until
    /// they restart the game.
    ///
    /// So the modules own no locks. They own a boolean. Every frame this addon recomputes
    /// which locks *should* exist from the live module list and makes reality match -
    /// acquiring what is missing, releasing what is no longer justified. A module that
    /// vanished by any route stops being in the list, so its lock stops being justified, so
    /// it is released on the next frame without anyone having written code for that
    /// particular ending.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class AegisAddon : MonoBehaviour
    {
        public static AegisAddon Singleton { get; private set; }

        /// <summary>Every live lock module, registered from <c>OnStart</c>.</summary>
        private static readonly List<ModuleAegisLock> Modules = new List<ModuleAegisLock>();

        /// <summary>Reused across frames so the per-frame reconcile allocates nothing.</summary>
        private readonly HashSet<string> _desired = new HashSet<string>();
        private readonly List<string> _stale = new List<string>();

        private bool _standDown;

        public void Awake()
        {
            Singleton = this;
            DontDestroyOnLoad(this);

            // Harmony patches go in Awake, before anything they patch has run.
            //
            // Wrapped because Harmony is the mod's only hard binary dependency and the only
            // thing that needs it is the recovery block. If 0Harmony.dll is missing the type
            // load fails here, is reported once, and every other feature carries on.
            try
            {
                RecoveryGuard.Install();
            }
            catch (Exception ex)
            {
                AegisLog.Exception("Could not install the recovery guard - locked vessels " +
                                   "WILL be recoverable. Is 0Harmony.dll installed?", ex);
            }
        }

        public void Start()
        {
            CompatibilityChecker.ReportAtStartup();
            CompatibilityChecker.ReportPinStorage();
            _standDown = !CompatibilityChecker.IsCompatible;

            // Start, not Awake: PopupDialog needs the UI up, and RecoveryGuard.Install has
            // already run in Awake so its success is the honest answer to "is Harmony here?"
            CompatibilityChecker.WarnAboutMissingDependencies(RecoveryGuard.Installed);

            // Instance methods, subscribed in Start rather than Awake: a static handler NREs
            // inside KSP's EvtDelegate constructor, and at Startup.Instantly the GameEvents
            // statics are not populated during Awake.
            // All four of these must be *instance* methods. A static handler throws an NRE
            // inside KSP's EvtDelegate constructor - which is why the crew restrictions'
            // handlers are forwarded from here rather than subscribed in their own class.
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onAttemptEva.Add(OnAttemptEva);
            GameEvents.onGameStateSave.Add(OnGameStateSave);
            GameEvents.onGameStateSaved.Add(OnGameStateSaved);
            GameEvents.onVesselWasModified.Add(OnVesselWasModified);
        }

        private void OnAttemptEva(ProtoCrewMember crew, Part part, Transform hatch)
        {
            CrewRestrictions.HandleAttemptEva(crew, part, hatch);
        }

        private void OnGameStateSave(ConfigNode node)
        {
            CrewRestrictions.HandleGameStateSave(node);
        }

        private void OnGameStateSaved(Game game)
        {
            CrewRestrictions.HandleGameStateSaved(game);
        }

        public void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onAttemptEva.Remove(OnAttemptEva);
            GameEvents.onGameStateSave.Remove(OnGameStateSave);
            GameEvents.onGameStateSaved.Remove(OnGameStateSaved);
            GameEvents.onVesselWasModified.Remove(OnVesselWasModified);
            CrewRestrictions.Restore();
            ControlLock.ReleaseAll("the addon is shutting down");
            DockingGuard.ReleaseAll("the addon is shutting down");
            ControlNeutraliser.DetachAll("the addon is shutting down");
            Modules.Clear();
            Singleton = null;
        }

        public void OnApplicationQuit()
        {
            CrewRestrictions.Restore();
            ControlLock.ReleaseAll("the addon is shutting down");
            DockingGuard.ReleaseAll("the addon is shutting down");
            ControlNeutraliser.DetachAll("the addon is shutting down");
        }

        /// <summary>
        /// Drops every lock before the scene changes.
        ///
        /// The per-frame reconcile would get there anyway once the modules unregister, but
        /// not before the new scene has had a chance to read the lock stack - and a lock held
        /// across the boundary into the editor or the tracking station belongs to nothing at
        /// all.
        /// </summary>
        private void OnSceneLoadRequested(GameScenes scene)
        {
            ControlLock.ReleaseAll("the scene is changing to " + scene);
            DockingGuard.ReleaseAll("the scene is changing");
            ControlNeutraliser.DetachAll("the scene is changing");
            CrewRestrictions.Restore();

            // A recovery grant is permission to press a button now, not a state. Carrying one
            // across a scene change would let a player authorise in flight and spend it in the
            // tracking station much later.
            RecoveryGuard.ClearAuthorisations();
        }

        /// <summary>
        /// Pushes one PIN and one lock state to every Aegis module on <paramref name="v"/>.
        ///
        /// One craft, one lock, one PIN. Called whenever any module changes state, so a
        /// three-pod station behaves as a single locked object rather than three.
        /// </summary>
        public static void SyncVessel(Vessel v, string pin, bool locked)
        {
            if (v == null) return;

            int touched = 0;
            for (int i = 0; i < Modules.Count; i++)
            {
                ModuleAegisLock module = Modules[i];
                if (module == null || module.vessel != v) continue;

                module.ApplySync(pin, locked);

                // Tell the other players, if there are any. This is the single choke point
                // for an Aegis state change, which is exactly why the announcement lives here
                // rather than in each of the half-dozen call sites that reach it.
                LmpBridge.Announce(module);

                touched++;
            }

            if (touched > 1)
            {
                AegisLog.Debug("Synced " + touched + " Aegis modules on '" + v.vesselName +
                               "' to locked=" + locked + ".");
            }
        }

        /// <summary>
        /// Settles disagreement between modules on one vessel, then syncs them.
        ///
        /// Two craft that dock become one vessel, and they may arrive with different PINs and
        /// different lock states. The rule is deliberately the cautious one:
        ///
        /// <list type="bullet">
        /// <item>the vessel is <b>locked if any part was locked</b> — docking must not be a
        /// way to launder a locked craft into an unlocked one;</item>
        /// <item>the PIN is taken from a <b>locked</b> part if there is one, so the code that
        /// opens the merged craft is the code of the thing that was actually locked.</item>
        /// </list>
        ///
        /// Consequence worth knowing: docking a locked craft to an unlocked one locks the
        /// whole stack, and the unlocked half's PIN is discarded. That is documented rather
        /// than clever, and it is the safe direction to be wrong in.
        /// </summary>
        public static void UnifyVessel(Vessel v)
        {
            if (v == null) return;

            bool anyLocked = false;
            string lockedPin = null;
            string anyPin = null;
            int count = 0;

            for (int i = 0; i < Modules.Count; i++)
            {
                ModuleAegisLock module = Modules[i];
                if (module == null || module.vessel != v) continue;

                count++;
                if (!string.IsNullOrEmpty(module.pinCode))
                {
                    if (anyPin == null) anyPin = module.pinCode;
                    if (module.isLocked && lockedPin == null) lockedPin = module.pinCode;
                }
                if (module.isLocked) anyLocked = true;
            }

            if (count < 2) return;      // nothing to reconcile on a single-command craft

            string pin = lockedPin ?? anyPin ?? string.Empty;

            // A locked vessel with no usable PIN could never be opened; refuse to create that
            // state even here.
            if (anyLocked && string.IsNullOrEmpty(pin))
            {
                AegisLog.Warn("'" + v.vesselName + "' merged into a locked state with no PIN on any " +
                              "command part. Unlocking it - nothing could have opened it.");
                anyLocked = false;
            }

            SyncVessel(v, pin, anyLocked);
        }

        private void OnVesselWasModified(Vessel v)
        {
            // Fires on dock, undock, decouple and part loss. An undocked half keeps its own
            // copy of the PIN because the state is persisted per part, which is the behaviour
            // wanted: splitting a locked station leaves both halves locked with the same code.
            UnifyVessel(v);
        }

        /// <summary>
        /// True when any part on <paramref name="v"/> has its lock engaged.
        ///
        /// Note this asks about the vessel, not about the active vessel: an EVA veto has to
        /// work while the player is controlling the Kerbal rather than the craft, which is
        /// precisely when the craft is no longer active.
        /// </summary>
        public static bool VesselIsLocked(Vessel v)
        {
            if (v == null) return false;

            bool sawLoadedModule = false;

            for (int i = 0; i < Modules.Count; i++)
            {
                ModuleAegisLock module = Modules[i];
                if (module == null || module.vessel != v) continue;

                sawLoadedModule = true;
                if (module.isLocked) return true;
            }

            // Loaded modules are authoritative when they exist: the ProtoVessel is only
            // rewritten on save, so for a craft in flight it can be minutes stale. When none
            // are loaded - every vessel in the tracking station - the savegame's own record
            // is the only source there is.
            if (sawLoadedModule) return false;

            return ProtoLockState.IsLocked(v);
        }

        /// <summary>
        /// Explains, in a few words, <b>where the answer to "is this locked" came from</b> —
        /// live modules or the savegame's ProtoVessel, and how many of each were seen.
        ///
        /// Purely for the log, and it earns its place. "Not locked" has three very different
        /// causes that all read the same: the craft has no Aegis module at all, it has modules
        /// and they are open, or it has neither loaded modules nor a readable ProtoVessel and
        /// the mod is guessing. The third is a bug and the first two are not, and until this
        /// existed the log could not tell them apart.
        /// </summary>
        public static string DescribeLockSource(Vessel v)
        {
            if (v == null) return "no vessel";

            int loaded = 0;
            for (int i = 0; i < Modules.Count; i++)
            {
                ModuleAegisLock module = Modules[i];
                if (module == null || module.vessel != v) continue;
                loaded++;
            }

            if (loaded > 0) return loaded + " loaded Aegis module(s), none locked";

            if (v.protoVessel == null)
            {
                return "no loaded Aegis modules and no ProtoVessel to read - nothing could be checked";
            }

            int proto = 0;
            List<ProtoPartSnapshot> parts = v.protoVessel.protoPartSnapshots;
            if (parts != null)
            {
                for (int p = 0; p < parts.Count; p++)
                {
                    ProtoPartSnapshot part = parts[p];
                    if (part == null || part.modules == null) continue;
                    for (int m = 0; m < part.modules.Count; m++)
                    {
                        ProtoPartModuleSnapshot module = part.modules[m];
                        if (module != null && module.moduleName == ProtoLockState.ModuleName) proto++;
                    }
                }
            }

            return proto == 0
                ? "unloaded, and its ProtoVessel carries no Aegis module at all"
                : "unloaded, read from " + proto + " saved Aegis module(s)";
        }

        /// <summary>
        /// True when <paramref name="entered"/> opens <paramref name="v"/>.
        ///
        /// Asks the vessel rather than a part, because the PIN is vessel-wide. Any locked
        /// module's PIN will do — after <see cref="UnifyVessel"/> they agree, and if a merge
        /// ever left them disagreeing, accepting either is the behaviour that does not strand
        /// the player.
        /// </summary>
        public static bool PinMatches(Vessel v, string entered)
        {
            if (v == null || string.IsNullOrEmpty(entered)) return false;

            bool sawLoadedModule = false;

            for (int i = 0; i < Modules.Count; i++)
            {
                ModuleAegisLock module = Modules[i];
                if (module == null || module.vessel != v) continue;

                sawLoadedModule = true;

                if (Logic.PinCode.Matches(entered, module.pinCode)) return true;
            }

            if (sawLoadedModule) return false;

            return ProtoLockState.PinMatches(v, entered);
        }

        /// <summary>
        /// The PIN length the keypad should present for this vessel. Falls back to the
        /// default when the vessel has no loaded modules — an unloaded craft being recovered
        /// from the tracking station is exactly that case.
        /// </summary>
        public static int PinLengthFor(Vessel v)
        {
            if (v != null)
            {
                for (int i = 0; i < Modules.Count; i++)
                {
                    ModuleAegisLock module = Modules[i];
                    if (module == null || module.vessel != v) continue;
                    return Logic.PinCode.ClampLength(module.pinLength);
                }

                int fromProto = ProtoLockState.PinLength(v);
                if (fromProto > 0) return fromProto;
            }

            return Logic.PinCode.DefaultLength;
        }

        public static void Register(ModuleAegisLock module)
        {
            if (module == null) return;
            if (Modules.Contains(module)) return;
            Modules.Add(module);
        }

        public static void Unregister(ModuleAegisLock module)
        {
            if (module == null) return;

            Modules.Remove(module);

            // Release immediately as well as letting the reconcile notice. OnDestroy runs
            // for the ordinary endings (part destroyed, vessel unloaded) and there is no
            // reason to leave a global lock standing for even one more frame.
            //
            // Safe even on a multi-pod craft: the reconcile re-acquires the key on the next
            // frame if a sibling module on the same vessel still justifies it.
            ControlLock.Release(module.LockKey, "the command part holding it was destroyed or unloaded");
        }

        public void Update()
        {
            if (_standDown)
            {
                if (ControlLock.HeldCount > 0)
                    ControlLock.ReleaseAll("the mod has stood down on an unsupported KSP version");

                DockingGuard.ReleaseAll("the mod has stood down");
                ControlNeutraliser.DetachAll("the mod has stood down");
                CrewRestrictions.Restore();
                return;
            }

            // Bound here rather than below the scene check, and deliberately. Binding is what
            // subscribes to incoming lock changes, and a subscription that only exists in
            // flight would miss anything that arrived while the player was elsewhere.
            LmpBridge.EnsureBound();

            // Locks only mean anything in flight. Anywhere else, hold none.
            if (!HighLogic.LoadedSceneIsFlight)
            {
                if (ControlLock.HeldCount > 0)
                    ControlLock.ReleaseAll("the current scene is not flight");
                DockingGuard.ReleaseAll("the current scene is not flight");
                ControlNeutraliser.DetachAll("the current scene is not flight");
                CrewRestrictions.Restore();
                return;
            }

            Reconcile();
        }

        /// <summary>
        /// Makes the set of held locks equal the set of justified locks. Runs every frame;
        /// the module list is one entry per command part on loaded vessels, so this is a
        /// walk of a handful of items and no allocation.
        /// </summary>
        private void Reconcile()
        {
            _desired.Clear();
            bool anyLockedLoaded = false;

            for (int i = Modules.Count - 1; i >= 0; i--)
            {
                ModuleAegisLock module = Modules[i];

                // Unity's overloaded == is what makes this work: a destroyed component
                // compares equal to null even though the managed reference is still here.
                // This is the safety net for every ending that never reached OnDestroy.
                if (module == null)
                {
                    Modules.RemoveAt(i);
                    continue;
                }

                // Two different questions, and conflating them was the bug the crew
                // restrictions had to fix. A *control* lock is only justified while the
                // locked craft is the one being flown - a global input lock taken for a
                // craft the player is not flying disables the craft they are. Boarding and
                // EVA restrictions are the opposite: they matter precisely when the player
                // has stepped outside and the locked craft is no longer active.
                if (module.WantsControlLock)
                {
                    _desired.Add(module.LockKey);
                }

                if (module.IsLockedAndLoaded)
                {
                    anyLockedLoaded = true;
                }
            }

            CrewRestrictions.Reconcile(anyLockedLoaded);
            DockingGuard.Reconcile();
            ControlNeutraliser.Reconcile();

            _stale.Clear();
            foreach (string key in ControlLock.Held)
            {
                if (!_desired.Contains(key)) _stale.Add(key);
            }

            for (int i = 0; i < _stale.Count; i++)
            {
                // Says what happened, not what it means: the reconcile cannot tell whether
                // the craft was unlocked, destroyed or switched away from. The handler that
                // *does* know logs its own line.
                ControlLock.Release(_stale[i], "no loaded, active, locked vessel still claims it");
            }

            foreach (string key in _desired)
            {
                if (!ControlLock.IsHeld(key)) ControlLock.Acquire(key);
            }
        }

        /// <summary>
        /// Dumps the whole lock stack - ours and everyone else's - into the log.
        ///
        /// "The controls stopped responding" is the symptom of a dozen different causes, and
        /// the useful answer is often *"the lock is not yours"*: KSP's own
        /// <c>vessel_noControl_&lt;guid&gt;</c> looks exactly like a mod bug from the
        /// player's side. Reading the stack turns that into a named holder.
        /// </summary>
        public static void LogLockStack(string context)
        {
            AegisLog.Info(context + " - Aegis holds " + ControlLock.HeldCount + " lock(s). Full stack:");
            AegisLog.Info(InputLockManager.PrintLockStack());
        }
    }
}
