using System;
using System.Reflection;
using IkosAegis.Parts;

namespace IkosAegis.Core
{
    /// <summary>
    /// Makes the lock state travel between players under <b>Luna Multiplayer</b>.
    ///
    /// <b>The bug this exists for.</b> Two players in the same scene: one locks their craft,
    /// the other can still dock to it. The lock was never wrong - it simply never arrived.
    /// LMP syncs a vessel's parts by shipping the savegame's <c>ProtoVessel</c> around, and a
    /// <c>[KSPField(isPersistant = true)]</c> only reaches that snapshot when the game saves.
    /// Between saves, a field changed in memory on one machine does not exist on any other.
    ///
    /// LMP has a mechanism for exactly this, and it needs <b>both halves wiring up</b>.
    ///
    /// <b>Sending.</b> <c>LmpClient.Events.PartModuleEvent</c> exposes a set of public static
    /// <c>EventData</c> objects - <c>onPartModuleBoolFieldChanged</c> and friends. Firing one
    /// hands LMP a (module, field, value) triple, which it validates and puts on the wire.
    /// LMP fires these itself from its own Harmony patches (see its
    /// <c>ModuleEngines_Activate</c>), so this is the sanctioned route and not a back door.
    ///
    /// LMP can also discover fields <i>declaratively</i>, from XML dropped in
    /// <c>GameData/LunaMultiplayer/PartSync/</c>, and that route does not work here. Its
    /// transpiler snapshots the listed fields at the top of a method and compares them at the
    /// bottom, and it only instruments Update-family methods, <c>[KSPAction]</c>s, and
    /// <c>[KSPEvent]</c>s with <c>guiActive</c>. <b>Our unlock happens in the keypad's
    /// callback</b>, long after <c>ToggleLockEvent</c> has returned, so the comparison would
    /// see nothing changed. Locking would sync and unlocking would not - worse than not
    /// syncing at all.
    ///
    /// <b>Receiving.</b> The half that is easy to miss: LMP applies an incoming field change
    /// by writing it into the <c>ProtoPartModuleSnapshot</c>'s <c>moduleValues</c> and
    /// <b>nothing else</b>. It never touches the live <c>PartModule</c>, and nothing inside
    /// LMP subscribes to the <c>...FieldProcessed</c> events it raises afterwards - they exist
    /// purely for mods like this one. So on the receiving client the saved state said "locked"
    /// while <c>ModuleAegisLock.isLocked</c> stayed false, which is exactly the shape of the
    /// reported bug: <see cref="AegisAddon.VesselIsLocked"/> prefers loaded modules and only
    /// falls back to the ProtoVessel when there are none. In the tracking station there are
    /// none, so the lock worked. In a shared flight scene the modules are loaded and answered
    /// "not locked".
    ///
    /// <b>Bound by reflection, on purpose.</b> IkosAegis must build and run with no LMP
    /// present. Nothing here is referenced at compile time except KSP's own
    /// <c>EventData</c>, which lets the handlers stay strongly typed while the only reflection
    /// is reading a static field.
    /// </summary>
    public static class LmpBridge
    {
        private const string EventsTypeName = "LmpClient.Events.PartModuleEvent";
        private const string LockedField = "isLocked";
        private const string PinField = "pinCode";

        private static bool _lookedForLmp;
        private static Type _events;
        private static bool _bound;

        /// <summary>
        /// The object whose <b>instance</b> methods are the subscribers, held in a static so it
        /// outlives the subscription.
        ///
        /// <b>This must not be a static method group, and the reason is not obvious.</b> KSP's
        /// <c>EventData.Add</c> wraps the delegate in an <c>EvtDelegate</c> whose constructor
        /// does, in IL:
        ///
        /// <code>
        /// this.originator     = evt.Target;
        /// this.originatorType = evt.Target.GetType().Name;   // unguarded
        /// </code>
        ///
        /// <c>Delegate.Target</c> is <b>null for a static method</b>, so subscribing a static
        /// handler throws <c>NullReferenceException</c> inside KSP's own constructor, every
        /// time, on every <c>EventData</c> in the game. It is not a race and not LMP's doing -
        /// every LMP subscriber is an instance method, which is why LMP never trips it.
        /// </summary>
        private static readonly Handlers Sink = new Handlers();

        /// <summary>
        /// How many binding attempts may fail before giving up, and why it is not one.
        ///
        /// The first version set <c>_events = null</c> on the first exception, which turned a
        /// single failure into a permanently dead bridge for the rest of the session - and the
        /// failure it hit was thrown on *every* attempt, so the retry budget would not have
        /// saved it. It is here for the opposite case: a genuine transient during load should
        /// not cost the feature for the session. Bounded so a permanent fault cannot fill the
        /// log with one exception per frame.
        /// </summary>
        private const int MaxBindAttempts = 5;

        private static int _failedBindAttempts;

        private static EventData<PartModule, string, bool> _outBool;
        private static EventData<PartModule, string, string> _outString;

        /// <summary>
        /// Guards against a change bouncing back out again. A value written by
        /// <see cref="OnRemoteBool"/> is already the network's, and re-announcing it would put
        /// two clients in a loop trading the same value forever.
        /// </summary>
        private static bool _applyingRemote;

        /// <summary>True once the outgoing and incoming halves are both live.</summary>
        public static bool Connected { get { return _bound; } }

        /// <summary>
        /// Binds if it can, cheaply enough to call every frame.
        ///
        /// Retried rather than done once at startup because LMP creates these event objects in
        /// its own <c>MainSystem.Awake</c>, and two <c>[KSPAddon]</c>s have no ordering
        /// guarantee between them. Binding once, early, would work or not depending on load
        /// order - which is the kind of intermittent that costs a week.
        /// </summary>
        public static bool EnsureBound()
        {
            if (_bound) return true;

            if (!_lookedForLmp)
            {
                _lookedForLmp = true;
                _events = FindEventsType();

                if (_events == null)
                {
                    AegisLog.Info("Luna Multiplayer is not installed, so Aegis state is not " +
                                  "broadcast to other players. Single-player behaviour is unaffected.");
                    return false;
                }
            }

            // LMP present but not awake yet - its event objects are still null. Try again next
            // frame; that costs four static field reads and nothing else.
            if (_events == null) return false;

            try
            {
                EventData<PartModule, string, bool> outBool =
                    Read<EventData<PartModule, string, bool>>("onPartModuleBoolFieldChanged");
                EventData<PartModule, string, string> outString =
                    Read<EventData<PartModule, string, string>>("onPartModuleStringFieldChanged");
                EventData<ProtoPartModuleSnapshot, string, bool> inBool =
                    Read<EventData<ProtoPartModuleSnapshot, string, bool>>("onPartModuleBoolFieldProcessed");
                EventData<ProtoPartModuleSnapshot, string, string> inString =
                    Read<EventData<ProtoPartModuleSnapshot, string, string>>("onPartModuleStringFieldProcessed");

                if (outBool == null || outString == null || inBool == null || inString == null) return false;

                _outBool = outBool;
                _outString = outString;

                inBool.Add(Sink.OnRemoteBool);
                inString.Add(Sink.OnRemoteString);

                _bound = true;
                AegisLog.Info("Luna Multiplayer bridge connected: Aegis lock state and PIN are now " +
                              "sent to other players the moment they change, and applied to the live " +
                              "module on arrival (LMP itself only writes the savegame snapshot).");
                return true;
            }
            catch (Exception ex)
            {
                _failedBindAttempts++;

                bool givingUp = _failedBindAttempts >= MaxBindAttempts;
                if (givingUp) _events = null;

                AegisLog.Exception("Could not connect the Luna Multiplayer bridge (attempt " +
                                   _failedBindAttempts + " of " + MaxBindAttempts + ")" +
                                   (givingUp
                                       ? ". Giving up for this session: Aegis still works locally, but a " +
                                         "lock will not reach other players until the game saves"
                                       : ". Will retry"), ex);
                return false;
            }
        }

        /// <summary>
        /// Announces one module's state to the other players.
        ///
        /// Called per module rather than per vessel because LMP addresses a change by
        /// <c>part.flightID</c> plus module name - the receiving client resolves it to one
        /// <c>ProtoPartModuleSnapshot</c>, so a single message would update one pod on a
        /// multi-pod craft and leave the rest saying something different.
        ///
        /// <c>pinLength</c> is deliberately not sent: it is <c>isPersistant = false</c> and
        /// comes from the part config, so it is already identical on every machine.
        /// </summary>
        public static void Announce(ModuleAegisLock module)
        {
            if (module == null || _applyingRemote) return;
            if (!EnsureBound()) return;

            // LMP drops changes for vessels that are not ours and for unloaded ones. Not our
            // decision to second-guess; just do not hand it what it cannot use.
            if (module.vessel == null || !module.vessel.loaded || module.part == null) return;

            try
            {
                _outBool.Fire(module, LockedField, module.isLocked);
                _outString.Fire(module, PinField, module.pinCode);
            }
            catch (Exception ex)
            {
                AegisLog.Exception("Could not send the Aegis state of a part to other players", ex);
            }
        }

        // ------------------------------------------------------------------
        // Incoming
        // ------------------------------------------------------------------

        /// <summary>
        /// Holds the subscriber methods as <b>instance</b> methods. See <see cref="Sink"/> for
        /// why this class exists rather than two static methods on <c>LmpBridge</c>.
        /// </summary>
        private sealed class Handlers
        {
            public void OnRemoteBool(ProtoPartModuleSnapshot snapshot, string field, bool value)
            {
                LmpBridge.OnRemoteBool(snapshot, field, value);
            }

            public void OnRemoteString(ProtoPartModuleSnapshot snapshot, string field, string value)
            {
                LmpBridge.OnRemoteString(snapshot, field, value);
            }
        }

        private static void OnRemoteBool(ProtoPartModuleSnapshot snapshot, string field, bool value)
        {
            if (field != LockedField) return;

            ModuleAegisLock module = Live(snapshot);
            if (module == null) return;          // unloaded: LMP's snapshot write is enough
            if (module.isLocked == value) return;

            Apply(module, module.pinCode, value);

            AegisLog.Info("Another player " + (value ? "locked" : "unlocked") + " '" +
                          (module.vessel != null ? module.vessel.vesselName : "a vessel") +
                          "'; applied here to the loaded module, which LMP does not do itself.");
        }

        private static void OnRemoteString(ProtoPartModuleSnapshot snapshot, string field, string value)
        {
            if (field != PinField) return;

            ModuleAegisLock module = Live(snapshot);
            if (module == null) return;
            if (module.pinCode == value) return;

            // No log line: it would timestamp the moment a PIN changed on a named craft, and
            // the lock line above already records everything worth recording.
            Apply(module, value ?? "", module.isLocked);
        }

        /// <summary>
        /// Writes remote state into the live module without announcing it straight back.
        ///
        /// Goes through <see cref="ModuleAegisLock.ApplySync"/> rather than assigning the
        /// fields directly, so the part menu refreshes exactly as it does for a local change.
        /// A craft whose menu still offers "Engage Aegis Lock" while it is locked is the same
        /// class of bug this project has already fixed once.
        /// </summary>
        private static void Apply(ModuleAegisLock module, string pin, bool locked)
        {
            _applyingRemote = true;
            try
            {
                module.ApplySync(pin, locked);
            }
            finally
            {
                _applyingRemote = false;
            }

            // No reconcile call: AegisAddon reconciles every frame, so the control lock, the
            // neutraliser and the docking guard all pick this up on the next one.
        }

        /// <summary>The loaded module behind a snapshot, or null when the craft is not loaded.</summary>
        private static ModuleAegisLock Live(ProtoPartModuleSnapshot snapshot)
        {
            if (snapshot == null) return null;
            if (snapshot.moduleName != ProtoLockState.ModuleName) return null;
            return snapshot.moduleRef as ModuleAegisLock;
        }

        // ------------------------------------------------------------------
        // Reflection, kept to the smallest possible surface
        // ------------------------------------------------------------------

        private static Type FindEventsType()
        {
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                try
                {
                    Type t = loaded[i].GetType(EventsTypeName, false);
                    if (t != null) return t;
                }
                catch (Exception)
                {
                    // Some assemblies in a heavily modded KSP throw on reflection. Skip them:
                    // not finding LMP is a supported outcome, not an error.
                }
            }

            return null;
        }

        private static T Read<T>(string fieldName) where T : class
        {
            FieldInfo f = _events.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            return f == null ? null : f.GetValue(null) as T;
        }
    }
}
