using System;
using System.Reflection;
using UnityEngine;

namespace IkosAegis.Core
{
    /// <summary>
    /// KSP enforces no version constraint on a mod, so a plugin built against 1.12 will
    /// happily load into a version whose API has moved and then throw on every frame.
    ///
    /// Everything here is advisory: it never prevents loading, it just reports the verdict
    /// once and lets <see cref="AegisAddon"/> stand down rather than take global input locks
    /// on a version it was not built for.
    ///
    /// KSP 1.12.5 is the last version there will ever be, so the upper bound is not a
    /// placeholder to revisit - it is permanently correct.
    /// </summary>
    public static class CompatibilityChecker
    {
        public static readonly Version MinVersion = new Version(1, 12, 0);
        public static readonly Version MaxVersion = new Version(1, 12, 99);

        private static bool _checked;
        private static bool _compatible = true;

        public static Version KspVersion
        {
            get { return new Version(Versioning.version_major, Versioning.version_minor, Versioning.Revision); }
        }

        /// <summary>True when the running KSP is inside the tested range.</summary>
        public static bool IsCompatible
        {
            get
            {
                if (_checked) return _compatible;

                _checked = true;
                try
                {
                    Version current = KspVersion;
                    _compatible = current >= MinVersion && current <= MaxVersion;
                }
                catch (Exception ex)
                {
                    // If the version cannot even be read, assume compatible rather than
                    // disabling a mod the player installed deliberately.
                    AegisLog.Exception("Could not read the KSP version; assuming compatible", ex);
                    _compatible = true;
                }

                return _compatible;
            }
        }

        /// <summary>
        /// States plainly that PINs are not secret, once per session.
        ///
        /// Worth a line at Info rather than leaving it to the README: a player sharing a save
        /// or running a multiplayer server should know that the code is readable, and the log
        /// is the one place that is true of *this* install rather than of the documentation.
        /// </summary>
        public static void ReportPinStorage()
        {
            AegisLog.Info("PINs are stored in PLAIN TEXT in the craft file and savegame, so a code " +
                          "can be shared with another player and used on any machine - and can also " +
                          "be read by anyone you share the save with. Machine-bound encryption was " +
                          "removed deliberately: it made a PIN impossible to share, which is most " +
                          "of what a PIN is for.");
        }

        public static void ReportAtStartup()
        {
            if (IsCompatible)
            {
                AegisLog.Info("KSP " + KspVersion + " is within the supported range (" +
                              MinVersion + " - " + MaxVersion + ").");
            }
            else
            {
                AegisLog.Warn("KSP " + KspVersion + " is outside the supported range (" +
                              MinVersion + " - " + MaxVersion + "). IkosAegis is standing down: " +
                              "no part will lock, and no control lock will be taken.");
            }
        }

        /// <summary>
        /// Warns when ModuleManager is missing.
        ///
        /// Without it, no part ever receives <c>ModuleAegisLock</c>, so the mod loads
        /// cleanly, logs nothing unusual, and does absolutely nothing - the least
        /// diagnosable failure mode this mod has. Detected the way MM's own
        /// <c>ModListGenerator</c> builds its mod list: by loaded assembly *name*, not file
        /// name, so a renamed <c>ModuleManager.4.2.3.dll</c> is still found.
        /// </summary>
        public static bool ModuleManagerPresent()
        {
            try
            {
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < loaded.Length; i++)
                {
                    if (string.Equals(loaded[i].GetName().Name, "ModuleManager", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AegisLog.Exception("Could not enumerate loaded assemblies to look for ModuleManager", ex);
                return true;    // do not cry wolf on a check that itself failed
            }

            return false;
        }

        /// <summary>
        /// Tells the player, on screen, when a dependency is missing.
        ///
        /// CKAN installs both dependencies for you; a hand-installed copy has nothing
        /// enforcing them. The failure modes are very different and so are the messages:
        ///
        /// <list type="bullet">
        /// <item><b>No ModuleManager</b> — no part ever receives the module, so the mod loads
        /// perfectly and does nothing at all. This is the least diagnosable state the mod
        /// has, and the only one worth a dialog: nothing on screen would ever differ from a
        /// working install except the absence of a menu entry nobody knows to look for.</item>
        /// <item><b>No Harmony</b> — everything works except recovery blocking. A log line
        /// is proportionate; a modal on every launch would not be.</item>
        /// </list>
        ///
        /// Modelled on HarmonyKSP's install checker, minus the force-quit: nothing here can
        /// corrupt a save, so refusing to run would be a bigger imposition than the fault.
        /// </summary>
        public static void WarnAboutMissingDependencies(bool harmonyInstalled)
        {
            if (!harmonyInstalled)
            {
                AegisLog.Error("Harmony (GameData/000_Harmony/0Harmony.dll) is not installed. " +
                               "Everything except recovery blocking will work - locked vessels " +
                               "WILL still be recoverable. Install the Harmony2 package.");
            }

            if (ModuleManagerPresent()) return;

            AegisLog.Error("ModuleManager is not installed. No part will receive ModuleAegisLock, " +
                           "so IkosAegis will do nothing at all. Install ModuleManager into GameData.");

            try
            {
                PopupDialog.SpawnPopupDialog(
                    new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f),
                    new MultiOptionDialog(
                        "IkosAegisMissingMM",
                        "IkosAegis needs ModuleManager, and it is not installed.\n\n" +
                        "Without it, no part receives the Aegis lock - the mod loads without " +
                        "errors and then does nothing at all.\n\n" +
                        "Install ModuleManager into GameData and restart.",
                        "IkosAegis - missing dependency",
                        HighLogic.UISkin,
                        new Rect(0.5f, 0.5f, 380f, 160f),
                        new DialogGUIVerticalLayout(
                            new DialogGUIFlexibleSpace(),
                            new DialogGUIHorizontalLayout(
                                new DialogGUIFlexibleSpace(),
                                new DialogGUIButton("OK", null, 120f, 30f, true, new DialogGUIBase[0]),
                                new DialogGUIFlexibleSpace()))),
                    persistAcrossScenes: false,
                    skin: HighLogic.UISkin);
            }
            catch (Exception ex)
            {
                // The log line above is the real notification; the dialog is a courtesy.
                AegisLog.Exception("Could not show the missing-ModuleManager dialog", ex);
            }
        }
    }
}
