using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using IkosAegis.UI;
using UnityEngine;

namespace IkosAegis.Core
{
    /// <summary>
    /// Stops a locked vessel being recovered <b>or terminated</b>, and asks for the PIN
    /// instead. Both are treated identically — same prompt, same grant, same launch-site
    /// exemption — because both are ways of taking a craft away from its owner and proving
    /// you know the code is the whole test.
    ///
    /// <b>Why this file needs Harmony when nothing else in the mod does.</b> Recovery has no
    /// veto and no single choke point. Three separate UI paths each do the whole job
    /// themselves, and none of them asks anybody's permission first:
    ///
    /// <list type="bullet">
    /// <item><c>AltimeterSliderButtons.recoverVessel</c> (the flight scene's Recover button)
    /// fires <c>GameEvents.OnVesselRecoveryRequested</c>, whose stock handler saves the game
    /// and loads the Space Centre — the event is a notification, not a request.</item>
    /// <item><c>SpaceTracking.OnRecoverConfirm</c> (the tracking station) fires
    /// <c>onVesselRecovered</c> and saves, inline. It never raises a request event at all.</item>
    /// <item><c>KSCVesselMarkers.RecoverVessel</c> (the KSC scene markers) calls
    /// <c>ShipConstruction.RecoverVesselFromFlight</c> directly.</item>
    /// </list>
    ///
    /// <c>Vessel.IsRecoverable</c> looks like the flag to use and is not: it is a computed
    /// property (<c>LandedOrSplashed &amp;&amp; mainBody.isHomeWorld</c>) with no setter, and
    /// forcing it false would remove the button rather than prompt for a code.
    ///
    /// So: a prefix on each of the three, returning false to skip the original. This reverses
    /// an explicit "no Harmony" decision in PLAN.md, and it is the only place in the mod that
    /// needs it — everything else uses a stock hook.
    ///
    /// <b>The mod must keep working if Harmony is absent.</b> All Harmony types stay inside
    /// this file, and <see cref="Install"/> is called from a try/catch, so a missing
    /// <c>0Harmony.dll</c> costs the recovery block and nothing else.
    /// </summary>
    public static class RecoveryGuard
    {
        private const string HarmonyId = "dk.drebsdorf.ikosaegis";

        /// <summary>
        /// Vessels the player has just proved the PIN for, and how long that lasts.
        ///
        /// The PIN prompt does not recover the craft itself. It authorises the *next*
        /// attempt, and the player presses Recover again — one code path for all three UI
        /// flows, rather than three different re-entry tricks to drive somebody else's
        /// button from inside its own prefix.
        ///
        /// Real seconds, and short: this is permission to do the thing now, not a state the
        /// craft is left in.
        /// </summary>
        private const float AuthorisationSeconds = 45f;

        /// <summary>
        /// The <c>what</c> label for the launch-site clear. A constant because it is compared
        /// in <see cref="PastTense"/>, and a literal typed twice is a bug waiting for a typo.
        /// </summary>
        private const string LaunchSiteClearing = "launch site clearing";

        private static readonly Dictionary<Guid, float> Authorised = new Dictionary<Guid, float>();

        private static bool _installed;

        public static bool Installed { get { return _installed; } }

        public static void Install()
        {
            if (_installed) return;

            Harmony harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(RecoveryGuard).Assembly);
            _installed = true;

            AegisLog.Info("Recovery guard installed (Harmony " + HarmonyId + ").");
            LogPatchedMethods(harmony);
        }

        /// <summary>
        /// Lists what Harmony actually attached to, once, at startup.
        ///
        /// <b>Every guard in this file is invisible when it works and invisible when it never
        /// ran.</b> A prefix that failed to bind throws nothing, warns about nothing, and
        /// leaves a log identical to one where the player simply did not press the button.
        /// That ambiguity cost a whole debugging round: a KSC-scene recovery went through with
        /// no Aegis line anywhere in the log, and "the patch is not attached" and "the craft
        /// was not locked" were indistinguishable from the evidence.
        ///
        /// One line at startup removes the first of those possibilities permanently, which is
        /// worth far more than the line costs.
        /// </summary>
        private static void LogPatchedMethods(Harmony harmony)
        {
            try
            {
                StringBuilder sb = new StringBuilder("Guarded methods: ");
                int found = 0;

                foreach (MethodBase m in harmony.GetPatchedMethods())
                {
                    if (found++ > 0) sb.Append("; ");
                    sb.Append(m.DeclaringType != null ? m.DeclaringType.Name : "?")
                      .Append('.').Append(m.Name);
                }

                if (found == 0)
                {
                    sb.Append("(none - every patch failed to bind, so recovery, termination, " +
                              "launch-site clearing and docking are all unguarded)");
                    AegisLog.Warn(sb.ToString());
                    return;
                }

                AegisLog.Info(sb.ToString());
            }
            catch (Exception ex)
            {
                AegisLog.Exception("Could not list the patched methods (the patches themselves " +
                                   "are unaffected by this)", ex);
            }
        }

        /// <summary>
        /// The whole decision, in one place so every patch agrees.
        /// Returns true when the operation may go ahead.
        ///
        /// <b>Recovery and termination are treated identically</b>, deliberately. An earlier
        /// version refused termination outright on the reasoning that there is nothing to
        /// recover afterwards — which made the two doors out of a locked craft behave
        /// differently for no reason the player could see, and left the owner unable to
        /// delete their own vessel. Proving you know the code is the whole test; what you then
        /// do with your own craft is your business.
        /// </summary>
        /// <param name="what">"recovery" or "termination", for the message and the log.</param>
        public static bool MayProceed(Vessel v, string what)
        {
            if (v == null)
            {
                AegisLog.Info("Allowing " + what + " - the guard was handed no vessel, so there " +
                              "is nothing for it to judge.");
                return true;
            }

            if (!AegisAddon.VesselIsLocked(v))
            {
                // Logged even though it is the boring answer. Silence here is what made the
                // KSC-scene report unfalsifiable: "not locked" and "the prefix never ran" both
                // produced an empty log. These calls only happen on a button press, so the
                // volume is a handful of lines per session.
                AegisLog.Info("Allowing " + what + " of '" + v.vesselName + "' - it is not " +
                              "Aegis-locked (" + AegisAddon.DescribeLockSource(v) + ").");
                return true;
            }

            // The soft-lock exemption, and the only case that is not guarded at all. A craft
            // parked where it launched from - a pad or a runway - can always be recovered or
            // terminated, locked or not, so the mod can never leave a save with an immovable
            // obstruction sitting on a launch site.
            if (LaunchSiteCheck.IsOnALaunchSite(v))
            {
                AegisLog.Info("Allowing " + what + " of locked vessel '" + v.vesselName +
                              "' because it is parked on a launch site (" +
                              LaunchSiteCheck.Describe(v) + ").");
                return true;
            }

            if (HasAuthorisation(v))
            {
                AegisLog.Info("Allowing " + what + " of locked vessel '" + v.vesselName +
                              "' - the PIN was entered for it.");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Refuses, and offers the keypad. Always returns false so a prefix can
        /// <c>return RecoveryGuard.Refuse(v, "recovery");</c> and read correctly.
        ///
        /// A granted PIN authorises the **next attempt on this vessel**, whichever of the two
        /// it is. Proving ownership once should not have to be done twice because the player
        /// changed their mind between recovering and scrapping.
        /// </summary>
        public static bool Refuse(Vessel v, string what)
        {
            AegisSound.Play(AegisSound.Denied);
            AegisLog.Info("Refused " + what + " of locked vessel '" + v.vesselName + "' (" +
                          LaunchSiteCheck.Describe(v) + "); asking for the PIN.");

            int length = AegisAddon.PinLengthFor(v);

            KeypadDialog.Show("Aegis PIN - authorise " + what, length, entered =>
            {
                if (v == null) return;

                if (!AegisAddon.PinMatches(v, entered))
                {
                    AegisSound.Play(AegisSound.Denied);
                    ScreenMessages.PostScreenMessage(
                        "[Aegis] Access denied: incorrect PIN. " + v.vesselName +
                        " was not " + PastTense(what) + ".",
                        5f, ScreenMessageStyle.UPPER_CENTER);
                    AegisLog.Info(char.ToUpperInvariant(what[0]) + what.Substring(1) +
                                  " PIN rejected for '" + v.vesselName + "'.");
                    return;
                }

                Authorised[v.id] = Time.realtimeSinceStartup + AuthorisationSeconds;

                AegisSound.Play(AegisSound.Granted);
                ScreenMessages.PostScreenMessage(
                    "[Aegis] " + v.vesselName + " unlocked for " + what +
                    ". Press the button again within " + (int)AuthorisationSeconds + "s.",
                    6f, ScreenMessageStyle.UPPER_CENTER);
                AegisLog.Info(char.ToUpperInvariant(what[0]) + what.Substring(1) +
                              " authorised for '" + v.vesselName + "' on a correct PIN.");
            });

            return false;
        }

        private static string PastTense(string what)
        {
            if (what == "termination") return "terminated";
            if (what == LaunchSiteClearing) return "cleared";
            return "recovered";
        }

        /// <summary>
        /// Is this vessel authorised right now? <b>Checks without consuming.</b>
        ///
        /// It used to consume — one grant, one use — which read as a sensible safety property
        /// and produced a soft-lock. **One button press passes through more than one guarded
        /// method.** Terminating goes `BtnOnClick_DeleteSelectedVessel` → KSP's confirmation
        /// dialog → `OnVesselDeleteConfirm`, and both are patched: the button spent the grant,
        /// the confirm step then found none, refused, and skipped the original — which is
        /// where `SpaceTracking.OnDialogDismiss()` lives. The dialog was never dismissed, so
        /// the tracking station stayed modally blocked and the player could not leave it.
        ///
        /// The window is already bounded by <see cref="AuthorisationSeconds"/> and cleared on
        /// every scene change, so single-use bought nothing that the clock did not already
        /// provide, and cost the ability to complete a two-step interaction.
        ///
        /// The general shape, worth remembering: **a one-shot token is only safe when exactly
        /// one thing consumes it, and a UI flow is not one thing.**
        /// </summary>
        private static bool HasAuthorisation(Vessel v)
        {
            float until;
            if (!Authorised.TryGetValue(v.id, out until)) return false;

            if (Time.realtimeSinceStartup < until) return true;

            // Expired: drop it so the table cannot grow, and so a stale entry can never be
            // mistaken for a fresh one.
            Authorised.Remove(v.id);
            return false;
        }

        /// <summary>Drops all outstanding grants. Called when leaving flight-side scenes.</summary>
        public static void ClearAuthorisations()
        {
            if (Authorised.Count == 0) return;
            AegisLog.Debug("Cleared " + Authorised.Count + " outstanding recovery authorisation(s).");
            Authorised.Clear();
        }

        // ------------------------------------------------------------------
        // The patches - five entry points, one decision
        // ------------------------------------------------------------------

        /// <summary>Flight scene — the Recover button on the altimeter.</summary>
        [HarmonyPatch(typeof(KSP.UI.Screens.AltimeterSliderButtons), "recoverVessel")]
        internal static class Patch_AltimeterRecover
        {
            private static bool Prefix()
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (MayProceed(v, "recovery")) return true;
                return Refuse(v, "recovery");
            }
        }

        /// <summary>
        /// Tracking station — patched at the *button click*, before KSP's own confirmation
        /// dialog. Refusing at <c>OnRecoverConfirm</c> instead would make the player confirm
        /// a recovery that was never going to happen.
        /// </summary>
        [HarmonyPatch(typeof(KSP.UI.Screens.SpaceTracking), "BtnOnclick_RecoverSelectedVessel")]
        internal static class Patch_TrackingStationRecover
        {
            // ___selectedVessel: Harmony's private-field injection, by name.
            private static bool Prefix(Vessel ___selectedVessel)
            {
                if (MayProceed(___selectedVessel, "recovery")) return true;
                return Refuse(___selectedVessel, "recovery");
            }
        }

        /// <summary>Space Centre scene — the vessel markers dotted around the KSC.</summary>
        [HarmonyPatch(typeof(KSP.UI.Screens.KSCVesselMarkers), "RecoverVessel")]
        internal static class Patch_KscMarkerRecover
        {
            private static bool Prefix(Vessel v)
            {
                if (MayProceed(v, "recovery")) return true;
                return Refuse(v, "recovery");
            }
        }

        /// <summary>
        /// Tracking station — <b>Terminate</b>. Guarded exactly like recovery: the PIN prompt,
        /// the same 45-second grant, and the same launch-site exemption.
        ///
        /// Leaving this unguarded was a hole big enough to drive a craft through — a player
        /// who cannot recover a locked vessel can simply delete it, which is worse. Found
        /// under Luna Multiplayer, where another player did exactly that, though nothing about
        /// it is multiplayer-specific.
        ///
        /// Note the capitalisation: <c>BtnOnClick_DeleteSelectedVessel</c> with a capital C,
        /// where the recovery button is <c>BtnOnclick_RecoverSelectedVessel</c> with a small
        /// one. Stock KSP is inconsistent here and Harmony matches by exact name, so a patch
        /// written from the pattern of its neighbour silently never applies.
        ///
        /// Patched at the button click, before KSP's confirmation dialog, so the player is not
        /// asked to confirm something that was never going to happen.
        /// </summary>
        [HarmonyPatch(typeof(KSP.UI.Screens.SpaceTracking), "BtnOnClick_DeleteSelectedVessel")]
        internal static class Patch_TrackingStationTerminate
        {
            private static bool Prefix(Vessel ___selectedVessel)
            {
                if (MayProceed(___selectedVessel, "termination")) return true;
                return Refuse(___selectedVessel, "termination");
            }
        }

        /// <summary>
        /// The same guard one step later, for anything that reaches the confirmation without
        /// going through the button — another mod, or a future KSP that rewires the dialog.
        ///
        /// **Skipping this method strands the tracking station**, which is how the first
        /// version of this patch soft-locked a save. `OnVesselDeleteConfirm` ends with
        /// `SpaceTracking.OnDialogDismiss()`, so returning false without dismissing leaves the
        /// confirmation modal logically open: the scene stays blocked and the *Leave* button
        /// greys out, with no exception and nothing in the log to explain it.
        ///
        /// So a refusal here dismisses the dialog itself. Same rule as everywhere else in this
        /// mod: **a refusal the host UI cannot express is not a refusal, it is a broken
        /// screen.**
        ///
        /// Deliberately does not offer the keypad — a second prompt stacked on KSP's own
        /// confirmation would be a mess, and the button prefix has already asked.
        /// </summary>
        [HarmonyPatch(typeof(KSP.UI.Screens.SpaceTracking), "OnVesselDeleteConfirm")]
        internal static class Patch_TrackingStationTerminateConfirm
        {
            private static bool Prefix(KSP.UI.Screens.SpaceTracking __instance, Vessel ___selectedVessel)
            {
                if (MayProceed(___selectedVessel, "termination")) return true;

                string name = ___selectedVessel != null ? ___selectedVessel.vesselName : "unknown vessel";
                AegisLog.Warn("Refused termination of locked vessel '" + name +
                              "' at the confirmation step, and dismissed the dialog so the " +
                              "tracking station is not left blocked.");

                // Unwind the UI the skipped method would have unwound.
                try
                {
                    Traverse.Create(__instance).Method("OnDialogDismiss").GetValue();
                }
                catch (Exception ex)
                {
                    AegisLog.Exception("Could not dismiss the terminate confirmation. The tracking " +
                                       "station may need leaving via the Space Centre.", ex);
                }

                return false;
            }
        }
        /// <summary>
        /// <b>Clear launch site</b> — the fourth way a craft leaves the save, and the one that
        /// does not look like recovery at all.
        ///
        /// When something is parked on or near a pad and the player tries to launch there, KSP
        /// offers to clear the site. <c>LaunchSiteFacility.ClearSite</c> then walks
        /// <c>LaunchSiteClear.GetObstructingVessels()</c> and calls
        /// <c>ShipConstruction.RecoverVesselFromFlight</c> on each one directly — no Recover
        /// button, no tracking station, no marker, and no confirmation naming the craft. It
        /// does not even save afterwards, which is how it is distinguishable from a marker
        /// recovery in a log.
        ///
        /// Left unguarded it is the cheapest bypass in the game: park a rover next to somebody
        /// else's locked craft, press launch, and clear them both away.
        ///
        /// The launch-site exemption still applies per vessel, so anything genuinely sitting
        /// <em>on</em> the pad or runway clears without a prompt exactly as before — which is
        /// the case this whole exemption exists for. Only a locked craft parked <em>near</em>
        /// the site, which <c>landedAt</c> does not call a launch site, is guarded.
        ///
        /// Refuses on the first locked obstruction rather than collecting them all: the prompt
        /// can only ask about one PIN at a time, and a grant is per vessel, so a site blocked
        /// by two locked craft takes two rounds. Slightly tedious and completely honest.
        /// </summary>
        [HarmonyPatch(typeof(LaunchSiteFacility), "ClearSite")]
        internal static class Patch_ClearLaunchSite
        {
            private static bool Prefix(PreFlightTests.LaunchSiteClear ___launchSiteClearTest)
            {
                if (___launchSiteClearTest == null) return true;

                List<ProtoVessel> obstructing;
                try
                {
                    obstructing = ___launchSiteClearTest.GetObstructingVessels();
                }
                catch (Exception ex)
                {
                    // Never block a launch because the guard itself failed.
                    AegisLog.Exception("Could not list the vessels obstructing the launch site; " +
                                       "allowing the clear rather than blocking a launch", ex);
                    return true;
                }

                if (obstructing == null) return true;

                for (int i = 0; i < obstructing.Count; i++)
                {
                    ProtoVessel pv = obstructing[i];
                    if (pv == null) continue;

                    // vesselRef is the loaded-or-tracked Vessel behind the proto, and it is what
                    // every other guard in this file works on. When it is null there is no
                    // vessel to judge, prompt about, or key a grant on, so the clear proceeds.
                    Vessel v = pv.vesselRef;
                    if (v == null)
                    {
                        AegisLog.Info("Launch-site obstruction '" + pv.vesselName + "' has no live " +
                                      "vessel behind it, so Aegis has nothing to check it against; " +
                                      "allowing it to be cleared.");
                        continue;
                    }

                    if (MayProceed(v, LaunchSiteClearing)) continue;

                    return Refuse(v, LaunchSiteClearing);
                }

                return true;
            }
        }

        /// <summary>
        /// Docking — the acquisition gate, and the mod's only non-recovery patch.
        ///
        /// <c>FindNodeApproaches</c> returns the port this one is closing on, and the FSM's
        /// acquire transition uses it. Returning null means "nothing to dock with", so nulling
        /// it blocks docking **without writing anything anywhere** — which is the entire point:
        /// the previous implementation used the port's own disabled state and that state is
        /// saved, so a locked craft left ports permanently dead in the save. See
        /// <see cref="DockingGuard"/> for the full account.
        ///
        /// Checked in both directions, because docking is symmetric and refusing only on the
        /// locked side would let the other port initiate.
        /// </summary>
        [HarmonyPatch(typeof(ModuleDockingNode), "FindNodeApproaches")]
        internal static class Patch_DockingApproach
        {
            /// <summary>
            /// When each port last reported a refusal, so the log gets one line per approach
            /// rather than one per frame.
            /// </summary>
            private static readonly Dictionary<uint, float> LastReported = new Dictionary<uint, float>();

            private const float ReportEverySeconds = 15f;

            private static void Postfix(ModuleDockingNode __instance, ref ModuleDockingNode __result)
            {
                if (__result == null) return;
                if (__instance == null) return;

                bool mineLocked = AegisAddon.VesselIsLocked(__instance.vessel);
                bool theirsLocked = AegisAddon.VesselIsLocked(__result.vessel);
                if (!mineLocked && !theirsLocked) return;

                __result = null;
                Report(__instance, mineLocked, theirsLocked);
            }

            /// <summary>
            /// Says a dock was refused, at most once every <see cref="ReportEverySeconds"/> per port.
            ///
            /// <b>This used to log nothing at all</b>, on the reasoning that the method runs every
            /// frame a port is in range and a locked craft parked beside a station would fill the
            /// log. The reasoning was right and the conclusion was wrong: it made a refused dock
            /// and a dock nobody attempted look identical, and a test session that should have
            /// settled whether the block works produced no evidence either way. Throttling gets
            /// the visibility without the spam - the same answer the recovery guards already use,
            /// arrived at three reports later than it should have been.
            /// </summary>
            private static void Report(ModuleDockingNode node, bool mineLocked, bool theirsLocked)
            {
                if (node.part == null) return;

                uint id = node.part.flightID;
                float now = Time.realtimeSinceStartup;

                float last;
                if (LastReported.TryGetValue(id, out last) && now - last < ReportEverySeconds) return;
                LastReported[id] = now;

                string which = mineLocked && theirsLocked ? "both craft are"
                             : mineLocked ? "this craft is"
                             : "the other craft is";

                AegisLog.Info("Refused a docking approach on " +
                              (node.vessel != null ? "'" + node.vessel.vesselName + "'" : "a vessel") +
                              " because " + which + " Aegis-locked. (Repeats are suppressed for " +
                              (int)ReportEverySeconds + "s per port.)");
            }
        }

    }
}
