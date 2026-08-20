using System;
using System.Collections.Generic;
using System.Text;

namespace IkosAegis.Core
{
    /// <summary>
    /// Answers one question: <b>is this vessel parked somewhere it launched from — a pad or a
    /// runway?</b>
    ///
    /// It exists because that is the exemption from the recovery block, and the exemption is
    /// what stops the mod being able to soft-lock a save. A craft locked in orbit with a
    /// forgotten PIN is a story; a craft locked on the pad that can be neither flown nor
    /// recovered nor removed is a save with a permanent obstruction in it.
    ///
    /// <b><c>vessel.Landed</c> is not the test.</b> It is true on any terrain anywhere in the
    /// system — a locked lander sitting on the Mun is "landed", and exempting it would give
    /// away the whole feature.
    ///
    /// Three things are checked instead, any of which is sufficient.
    /// </summary>
    public static class LaunchSiteCheck
    {
        private static bool _dumped;

        public static bool IsOnALaunchSite(Vessel v)
        {
            if (v == null) return false;

            // 1. PRELAUNCH is unambiguous: sitting on a pad or runway, never yet flown.
            //    Covers the common case for both rockets and spaceplanes.
            if (v.situation == Vessel.Situations.PRELAUNCH) return true;

            // Anything not on the ground is not parked anywhere, whatever else is true of it.
            if (!v.Landed) return false;

            string where = v.landedAt;
            if (string.IsNullOrEmpty(where)) return false;

            PSystemSetup setup = PSystemSetup.Instance;
            if (setup == null) return false;

            DumpKnownSitesOnce(setup);

            // 2. A Space Centre facility you can launch from. That list is exactly the pad and
            //    the runway - each entry carries an `editorFacility` of VAB or SPH, which is
            //    what makes it the launch sites and not the VAB, R&D or Astronaut Complex.
            //    Landing on the lawn outside Mission Control is still not an exemption.
            //
            //    Four name fields are compared because `landedAt` does not consistently hold
            //    any one of them. Real values seen in a save: "LaunchPad" *and*
            //    "KSC_LaunchPad_Platform" - the facility name and the PQS collider name, for
            //    the same physical pad. Guessing which one applies to the runway would be
            //    exactly the kind of assumption that has already cost this project a bug, so
            //    all of them are checked and none of them is hardcoded.
            List<PSystemSetup.SpaceCenterFacility> facilities = setup.SpaceCenterFacilityLaunchSites;
            if (facilities != null)
            {
                for (int i = 0; i < facilities.Count; i++)
                {
                    PSystemSetup.SpaceCenterFacility f = facilities[i];
                    if (f == null) continue;

                    if (Same(where, f.name) ||
                        Same(where, f.pqsName) ||
                        Same(where, f.facilityName) ||
                        Same(where, f.facilityTransformName))
                    {
                        return true;
                    }
                }
            }

            // 3. A registered launch site proper - Making History's Woomerang and Dessert, and
            //    anything a mod adds. Note this list does NOT contain the stock KSC pad and
            //    runway, which is why step 2 exists: PSystemSetup keeps facilities and launch
            //    sites in separate lists, and IsFacilityOrLaunchSite is literally
            //    `IsFacility(...) || IsLaunchSite(...)`.
            string resolved;
            return setup.IsLaunchSite(where, out resolved);
        }

        /// <summary>
        /// Logs the launch sites the game actually knows about, once per session.
        ///
        /// The failure mode this guards against is silent: if a name never matches, recovery
        /// is refused on a pad and the only symptom is a player being asked for a PIN they
        /// did not expect. One line in the log turns "why was I prompted?" into an answerable
        /// question, next to the `landed at ...` value that <see cref="Describe"/> already
        /// reports on every refusal.
        /// </summary>
        private static void DumpKnownSitesOnce(PSystemSetup setup)
        {
            if (_dumped) return;
            _dumped = true;

            try
            {
                StringBuilder sb = new StringBuilder("Launch sites recognised for the recovery exemption -- facilities: ");

                List<PSystemSetup.SpaceCenterFacility> facilities = setup.SpaceCenterFacilityLaunchSites;
                if (facilities != null)
                {
                    for (int i = 0; i < facilities.Count; i++)
                    {
                        PSystemSetup.SpaceCenterFacility f = facilities[i];
                        if (f == null) continue;
                        if (i > 0) sb.Append("; ");
                        sb.Append(f.name).Append(" [pqs=").Append(f.pqsName)
                          .Append(", facility=").Append(f.facilityName)
                          .Append(", editor=").Append(f.editorFacility).Append("]");
                    }
                }

                List<LaunchSite> sites = setup.LaunchSites;
                sb.Append(" -- launch sites: ");
                if (sites == null || sites.Count == 0)
                {
                    sb.Append("(none)");
                }
                else
                {
                    for (int i = 0; i < sites.Count; i++)
                    {
                        if (i > 0) sb.Append("; ");
                        sb.Append(sites[i].name);
                    }
                }

                AegisLog.Info(sb.ToString());
            }
            catch (Exception ex)
            {
                AegisLog.Exception("Could not enumerate launch sites (the exemption still works)", ex);
            }
        }

        private static bool Same(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A short phrase for messages and logs, never null.</summary>
        public static string Describe(Vessel v)
        {
            if (v == null) return "no vessel";
            if (v.situation == Vessel.Situations.PRELAUNCH) return "pre-launch on a pad or runway";
            if (!v.Landed) return v.situation.ToString().ToLowerInvariant();
            return string.IsNullOrEmpty(v.landedAt)
                ? "landed"
                : "landed at " + v.landedAt;
        }
    }
}
