using IkosAegis.Logic;

namespace IkosAegis.Core
{
    /// <summary>
    /// Reads a vessel's Aegis state out of its <c>ProtoVessel</c>, for craft that are not
    /// loaded.
    ///
    /// <b>Why this is necessary at all.</b> Every other part of the mod works from live
    /// <c>ModuleAegisLock</c> instances, which only exist for vessels inside physics range.
    /// The tracking station lists the whole save, almost none of it loaded — so asking the
    /// live modules whether a craft is locked returns "no" for every vessel in the game, and
    /// the recovery block would silently pass everything.
    ///
    /// A <c>ProtoVessel</c> is the savegame's own record of the craft, and persistent
    /// <c>[KSPField]</c>s are right there in each <c>ProtoPartModuleSnapshot</c>'s
    /// <c>moduleValues</c>. Reading them is exact — it is the same data the module would load
    /// from — and needs nothing to be loaded or instantiated.
    ///
    /// Live modules still win where both exist: the proto is only rewritten on save, so for a
    /// craft in flight it can be several minutes stale.
    /// </summary>
    public static class ProtoLockState
    {
        public const string ModuleName = "ModuleAegisLock";

        private const string LockedKey = "isLocked";
        private const string PinKey = "pinCode";
        private const string LengthKey = "pinLength";

        /// <summary>True when the saved state of any command part on this vessel is locked.</summary>
        public static bool IsLocked(Vessel v)
        {
            if (v == null || v.protoVessel == null) return false;

            for (int p = 0; p < v.protoVessel.protoPartSnapshots.Count; p++)
            {
                ProtoPartSnapshot part = v.protoVessel.protoPartSnapshots[p];
                if (part == null || part.modules == null) continue;

                for (int m = 0; m < part.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot module = part.modules[m];
                    if (module == null || module.moduleName != ModuleName) continue;
                    if (module.moduleValues == null) continue;

                    if (ReadBool(module.moduleValues, LockedKey)) return true;
                }
            }

            return false;
        }

        /// <summary>True when <paramref name="entered"/> matches a saved PIN on this vessel.</summary>
        public static bool PinMatches(Vessel v, string entered)
        {
            if (v == null || v.protoVessel == null || string.IsNullOrEmpty(entered)) return false;

            for (int p = 0; p < v.protoVessel.protoPartSnapshots.Count; p++)
            {
                ProtoPartSnapshot part = v.protoVessel.protoPartSnapshots[p];
                if (part == null || part.modules == null) continue;

                for (int m = 0; m < part.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot module = part.modules[m];
                    if (module == null || module.moduleName != ModuleName) continue;
                    if (module.moduleValues == null) continue;

                    if (PinCode.Matches(entered, module.moduleValues.GetValue(PinKey))) return true;
                }
            }

            return false;
        }

        /// <summary>The keypad length to present, or 0 when this vessel has no Aegis module.</summary>
        public static int PinLength(Vessel v)
        {
            if (v == null || v.protoVessel == null) return 0;

            for (int p = 0; p < v.protoVessel.protoPartSnapshots.Count; p++)
            {
                ProtoPartSnapshot part = v.protoVessel.protoPartSnapshots[p];
                if (part == null || part.modules == null) continue;

                for (int m = 0; m < part.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot module = part.modules[m];
                    if (module == null || module.moduleName != ModuleName) continue;
                    if (module.moduleValues == null) continue;

                    int parsed;
                    string raw = module.moduleValues.GetValue(LengthKey);
                    if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out parsed))
                    {
                        return PinCode.ClampLength(parsed);
                    }

                    return PinCode.DefaultLength;
                }
            }

            return 0;
        }

        /// <summary>
        /// Reads a persisted boolean. <c>ConfigNode</c> stores these as the text "True" or
        /// "False", and a missing key means the module never saved one — treated as false,
        /// which for <c>isLocked</c> is the safe direction: an unreadable craft is not
        /// declared locked and made unrecoverable on a guess.
        /// </summary>
        private static bool ReadBool(ConfigNode node, string key)
        {
            string raw = node.GetValue(key);
            if (string.IsNullOrEmpty(raw)) return false;

            bool parsed;
            return bool.TryParse(raw, out parsed) && parsed;
        }
    }
}
