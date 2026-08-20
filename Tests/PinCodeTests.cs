using IkosAegis.Logic;
using NUnit.Framework;

namespace IkosAegis.Tests
{
    [TestFixture]
    public class PinCodeTests
    {
        // --- ClampLength ---

        [TestCase(0, PinCode.MinLength)]
        [TestCase(2, PinCode.MinLength)]
        [TestCase(-5, PinCode.MinLength)]
        [TestCase(3, 3)]
        [TestCase(8, 8)]
        [TestCase(99, PinCode.MaxLength)]
        public void ClampLength_forces_a_configured_value_into_range(int given, int expected)
        {
            Assert.AreEqual(expected, PinCode.ClampLength(given));
        }

        // --- IsValid ---

        [Test]
        public void IsValid_accepts_exactly_the_expected_number_of_digits()
        {
            Assert.IsTrue(PinCode.IsValid("123", 3));
            Assert.IsTrue(PinCode.IsValid("000", 3));
            Assert.IsTrue(PinCode.IsValid("00000000", 8));
        }

        [Test]
        public void IsValid_rejects_the_wrong_length()
        {
            Assert.IsFalse(PinCode.IsValid("12", 3));
            Assert.IsFalse(PinCode.IsValid("1234", 3));
            Assert.IsFalse(PinCode.IsValid("", 3));
        }

        [Test]
        public void IsValid_rejects_null()
        {
            Assert.IsFalse(PinCode.IsValid(null, 3));
        }

        [Test]
        public void IsValid_rejects_non_digits()
        {
            Assert.IsFalse(PinCode.IsValid("12a", 3));
            Assert.IsFalse(PinCode.IsValid("1 2", 3));
            Assert.IsFalse(PinCode.IsValid("-12", 3));
        }

        [Test]
        public void IsValid_rejects_non_ascii_digits_because_the_keypad_cannot_produce_them()
        {
            // char.IsDigit says true for these. A PIN containing one could be stored by a
            // hand-edited save and then never entered, so the check is deliberately ASCII.
            Assert.IsFalse(PinCode.IsValid("١٢٣", 3), "Arabic-Indic digits");
            Assert.IsFalse(PinCode.IsValid("०१२", 3), "Devanagari digits");
        }

        [Test]
        public void IsValid_uses_the_clamped_length_not_the_raw_one()
        {
            // A patch asking for 2 gets 3, so a 3-digit PIN is what validates.
            Assert.IsTrue(PinCode.IsValid("123", 2));
            Assert.IsFalse(PinCode.IsValid("12", 2));
        }

        // --- IsSet ---

        [Test]
        public void IsSet_treats_empty_as_never_configured()
        {
            Assert.IsFalse(PinCode.IsSet("", 3));
            Assert.IsFalse(PinCode.IsSet(null, 3));
            Assert.IsTrue(PinCode.IsSet("123", 3));
        }

        // --- Normalise ---

        [Test]
        public void Normalise_keeps_digits_and_truncates()
        {
            Assert.AreEqual("123", PinCode.Normalise("123", 3));
            Assert.AreEqual("123", PinCode.Normalise("1-2-3", 3));
            Assert.AreEqual("123", PinCode.Normalise("12345", 3));
            Assert.AreEqual("12", PinCode.Normalise("1a2", 3));
        }

        [Test]
        public void Normalise_preserves_leading_zeros()
        {
            Assert.AreEqual("007", PinCode.Normalise("007", 3));
        }

        [Test]
        public void Normalise_handles_empty_and_null()
        {
            Assert.AreEqual("", PinCode.Normalise("", 3));
            Assert.AreEqual("", PinCode.Normalise(null, 3));
            Assert.AreEqual("", PinCode.Normalise("abc", 3));
        }

        // --- Mask ---

        [Test]
        public void Mask_shows_one_slot_per_expected_digit()
        {
            Assert.AreEqual("_ _ _", PinCode.Mask("", 3));
            Assert.AreEqual("* _ _", PinCode.Mask("1", 3));
            Assert.AreEqual("* * _", PinCode.Mask("12", 3));
            Assert.AreEqual("* * *", PinCode.Mask("123", 3));
        }

        [Test]
        public void Mask_width_comes_from_the_expected_length_not_the_entry()
        {
            Assert.AreEqual("* * * * _ _ _ _", PinCode.Mask("1234", 8));
        }

        [Test]
        public void Mask_does_not_overflow_on_an_over_long_entry()
        {
            Assert.AreEqual("* * *", PinCode.Mask("123456", 3));
        }

        [Test]
        public void Mask_handles_null()
        {
            Assert.AreEqual("_ _ _", PinCode.Mask(null, 3));
        }

        // --- Matches ---

        [Test]
        public void Matches_is_exact()
        {
            Assert.IsTrue(PinCode.Matches("123", "123"));
            Assert.IsFalse(PinCode.Matches("124", "123"));
        }

        [Test]
        public void Matches_treats_leading_zeros_as_significant()
        {
            // The whole reason PINs are strings rather than ints. Parsing to a number would
            // make every one of these pass, silently opening locks that should refuse.
            Assert.IsFalse(PinCode.Matches("7", "007"));
            Assert.IsFalse(PinCode.Matches("07", "007"));
            Assert.IsFalse(PinCode.Matches("007", "7"));
            Assert.IsTrue(PinCode.Matches("007", "007"));
        }

        [Test]
        public void Matches_refuses_an_unset_stored_pin()
        {
            // Otherwise a part that was never configured opens by submitting nothing.
            Assert.IsFalse(PinCode.Matches("", ""));
            Assert.IsFalse(PinCode.Matches("123", ""));
        }

        [Test]
        public void Matches_refuses_nulls()
        {
            Assert.IsFalse(PinCode.Matches(null, "123"));
            Assert.IsFalse(PinCode.Matches("123", null));
            Assert.IsFalse(PinCode.Matches(null, null));
        }
    }
}
