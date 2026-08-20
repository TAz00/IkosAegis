using IkosAegis.Logic;
using NUnit.Framework;

namespace IkosAegis.Tests
{
    [TestFixture]
    public class LockoutPolicyTests
    {
        private const int Threshold = 3;
        private const double Penalty = 30.0;
        private const double Now = 1000.0;

        [Test]
        public void No_penalty_below_the_threshold()
        {
            // "Returns now" is the encoding of "no lockout" - IsLockedOut(now, now) is false.
            Assert.AreEqual(Now, LockoutPolicy.NextLockoutUntil(1, Threshold, Penalty, Now));
            Assert.AreEqual(Now, LockoutPolicy.NextLockoutUntil(2, Threshold, Penalty, Now));
            Assert.IsFalse(LockoutPolicy.IsLockedOut(
                LockoutPolicy.NextLockoutUntil(2, Threshold, Penalty, Now), Now));
        }

        [Test]
        public void The_threshold_attempt_costs_the_base_penalty()
        {
            Assert.AreEqual(Now + 30.0, LockoutPolicy.NextLockoutUntil(3, Threshold, Penalty, Now));
        }

        [Test]
        public void Each_further_failure_doubles_the_penalty()
        {
            Assert.AreEqual(Now + 60.0, LockoutPolicy.NextLockoutUntil(4, Threshold, Penalty, Now));
            Assert.AreEqual(Now + 120.0, LockoutPolicy.NextLockoutUntil(5, Threshold, Penalty, Now));
            Assert.AreEqual(Now + 240.0, LockoutPolicy.NextLockoutUntil(6, Threshold, Penalty, Now));
        }

        [Test]
        public void The_penalty_is_capped()
        {
            // 7 failures would be 480s unclamped; 50 would overflow into absurdity.
            Assert.AreEqual(Now + LockoutPolicy.MaxPenaltySeconds,
                LockoutPolicy.NextLockoutUntil(7, Threshold, Penalty, Now));
            Assert.AreEqual(Now + LockoutPolicy.MaxPenaltySeconds,
                LockoutPolicy.NextLockoutUntil(50, Threshold, Penalty, Now));
            Assert.AreEqual(Now + LockoutPolicy.MaxPenaltySeconds,
                LockoutPolicy.NextLockoutUntil(int.MaxValue, Threshold, Penalty, Now));
        }

        [Test]
        public void A_threshold_of_zero_disables_the_lockout()
        {
            // The config field documents 0 as "off"; this is the assertion that it is.
            Assert.AreEqual(Now, LockoutPolicy.NextLockoutUntil(99, 0, Penalty, Now));
            Assert.IsFalse(LockoutPolicy.IsLockedOut(
                LockoutPolicy.NextLockoutUntil(99, 0, Penalty, Now), Now));
        }

        [Test]
        public void A_zero_or_negative_penalty_disables_the_lockout()
        {
            Assert.AreEqual(Now, LockoutPolicy.NextLockoutUntil(99, Threshold, 0.0, Now));
            Assert.AreEqual(Now, LockoutPolicy.NextLockoutUntil(99, Threshold, -10.0, Now));
        }

        [Test]
        public void IsLockedOut_is_true_only_before_the_deadline()
        {
            double until = Now + 30.0;

            Assert.IsTrue(LockoutPolicy.IsLockedOut(until, Now));
            Assert.IsTrue(LockoutPolicy.IsLockedOut(until, Now + 29.9));
            Assert.IsFalse(LockoutPolicy.IsLockedOut(until, until));
            Assert.IsFalse(LockoutPolicy.IsLockedOut(until, Now + 31.0));
        }

        [Test]
        public void SecondsRemaining_rounds_up_so_it_never_reads_zero_while_still_refusing()
        {
            // A keypad that says "0s remaining" and then refuses is the log-lying failure in
            // miniature: the number cannot distinguish the two outcomes it is reporting.
            double until = Now + 30.0;

            Assert.AreEqual(30, LockoutPolicy.SecondsRemaining(until, Now));
            Assert.AreEqual(1, LockoutPolicy.SecondsRemaining(until, Now + 29.99));
            Assert.AreEqual(29, LockoutPolicy.SecondsRemaining(until, Now + 1.0));
        }

        [Test]
        public void SecondsRemaining_is_zero_once_expired()
        {
            double until = Now + 30.0;

            Assert.AreEqual(0, LockoutPolicy.SecondsRemaining(until, until));
            Assert.AreEqual(0, LockoutPolicy.SecondsRemaining(until, Now + 100.0));
        }
    }
}
