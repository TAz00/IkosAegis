using System;

namespace IkosAegis.Logic
{
    /// <summary>
    /// How the keypad responds to repeated wrong PINs.
    ///
    /// A three-digit PIN is a thousand combinations, and a keypad with no penalty is four
    /// clicks per guess. This does not make the lock secure - nothing here could, since the
    /// PIN sits in the save file in plain text - it makes brute-forcing it *boring*, which
    /// is the whole of what a gameplay lock needs.
    ///
    /// Pure arithmetic over a caller-supplied clock so it can be tested without the game.
    /// The caller passes real elapsed seconds rather than universal time on purpose: UT
    /// jumps forward under time warp, and a penalty that can be skipped by pressing '.'
    /// is not a penalty.
    /// </summary>
    public static class LockoutPolicy
    {
        /// <summary>Never charge more than this, however many attempts have been made.</summary>
        public const double MaxPenaltySeconds = 300.0;

        /// <summary>
        /// The moment the keypad becomes usable again after a failure, or
        /// <paramref name="now"/> when this failure does not warrant one.
        ///
        /// <paramref name="failedAttempts"/> is the running count *including* the failure
        /// being processed. Below <paramref name="threshold"/> nothing happens; at and above
        /// it, the penalty doubles per extra failure - 30s, 60s, 120s - so an honest player
        /// who fat-fingers their own PIN twice pays nothing and a brute-force attempt
        /// becomes untenable within a handful of guesses.
        /// </summary>
        public static double NextLockoutUntil(int failedAttempts, int threshold, double penaltySeconds, double now)
        {
            if (threshold <= 0) return now;                 // lockout disabled
            if (penaltySeconds <= 0.0) return now;          // lockout disabled
            if (failedAttempts < threshold) return now;

            int doublings = failedAttempts - threshold;
            if (doublings > 16) doublings = 16;             // 2^16 * 30s already clamps below

            double penalty = penaltySeconds * Math.Pow(2.0, doublings);
            if (penalty > MaxPenaltySeconds) penalty = MaxPenaltySeconds;

            return now + penalty;
        }

        /// <summary>True while <paramref name="now"/> is still before <paramref name="until"/>.</summary>
        public static bool IsLockedOut(double until, double now)
        {
            return now < until;
        }

        /// <summary>
        /// Whole seconds left on the penalty, rounded up so the message never says "0s
        /// remaining" on a keypad that is still refusing. Zero once the penalty has expired.
        /// </summary>
        public static int SecondsRemaining(double until, double now)
        {
            double remaining = until - now;
            if (remaining <= 0.0) return 0;
            return (int)Math.Ceiling(remaining);
        }
    }
}
