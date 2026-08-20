using System;
using System.Text;

namespace IkosAegis.Logic
{
    /// <summary>
    /// Everything the mod knows about what a PIN *is*: how long it may be, what counts as a
    /// valid one, how it is displayed while being typed, and how two of them are compared.
    ///
    /// Deliberately free of any KSP or Unity type so it can be unit-tested without the game.
    /// The rules here are the ones that are easy to get subtly wrong and impossible to see
    /// wrong from inside a running flight - a comparison that ignores leading zeros, a mask
    /// that reveals the length of the stored PIN, an "is it set?" check that accepts the
    /// empty string.
    /// </summary>
    public static class PinCode
    {
        /// <summary>Shortest PIN the keypad will accept. Below this it is not a lock at all.</summary>
        public const int MinLength = 3;

        /// <summary>
        /// Longest PIN. Bounded because the keypad's masked display is drawn at a fixed
        /// width and because the part config drives this value - an unbounded field read
        /// from a .cfg is a way for a typo to produce a dialog nobody can use.
        /// </summary>
        public const int MaxLength = 8;

        /// <summary>What the stock patch asks for, and what the concept specifies.</summary>
        public const int DefaultLength = 3;

        /// <summary>
        /// Forces a configured length into the supported range instead of trusting it.
        /// A <c>[KSPField]</c> comes from a text file that any other mod may have patched.
        /// </summary>
        public static int ClampLength(int length)
        {
            if (length < MinLength) return MinLength;
            if (length > MaxLength) return MaxLength;
            return length;
        }

        /// <summary>
        /// True when <paramref name="pin"/> is exactly <paramref name="length"/> ASCII
        /// digits.
        ///
        /// Uses an explicit '0'..'9' test rather than <c>char.IsDigit</c>, which returns true
        /// for every Unicode decimal digit in existence - Arabic-Indic, Devanagari and
        /// several dozen more. Those cannot be typed on the keypad, so a PIN containing one
        /// could be stored (by a hand-edited save or a patch) and then never entered.
        /// </summary>
        public static bool IsValid(string pin, int length)
        {
            if (pin == null) return false;
            if (pin.Length != ClampLength(length)) return false;

            for (int i = 0; i < pin.Length; i++)
            {
                if (pin[i] < '0' || pin[i] > '9') return false;
            }

            return true;
        }

        /// <summary>
        /// True when a part has a usable PIN on it. The empty string is the "never
        /// configured" state and must not be lockable - a lock whose PIN is "" can be opened
        /// by pressing OK.
        /// </summary>
        public static bool IsSet(string pin, int length)
        {
            return IsValid(pin, length);
        }

        /// <summary>
        /// Keeps only the digits of <paramref name="raw"/> and truncates to
        /// <paramref name="length"/>. Used when reading a PIN out of a config that a human
        /// or another mod's patch may have written.
        /// </summary>
        public static string Normalise(string raw, int length)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            int max = ClampLength(length);
            StringBuilder sb = new StringBuilder(max);

            for (int i = 0; i < raw.Length && sb.Length < max; i++)
            {
                char c = raw[i];
                if (c >= '0' && c <= '9') sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// The keypad's display: one dot per digit entered, one underscore per digit still
        /// expected. <c>Mask("12", 3)</c> is <c>"* * _"</c>.
        ///
        /// The width comes from the *expected* length, never from the stored PIN, so the
        /// display never leaks how long the real code is on a part whose length was patched
        /// to something other than the default.
        /// </summary>
        public static string Mask(string entered, int length)
        {
            int max = ClampLength(length);
            int typed = entered == null ? 0 : entered.Length;
            if (typed > max) typed = max;

            StringBuilder sb = new StringBuilder(max * 2);
            for (int i = 0; i < max; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(i < typed ? '*' : '_');
            }

            return sb.ToString();
        }

        /// <summary>
        /// Compares an entered PIN against the stored one.
        ///
        /// Ordinal, not culture-aware: these are digit strings, and a culture-sensitive
        /// comparison can treat different code points as equal. "007" and "7" are different
        /// PINs, which is why this is a string comparison and not an integer one - parsing
        /// to <c>int</c> would quietly make them the same.
        /// </summary>
        public static bool Matches(string entered, string stored)
        {
            if (entered == null || stored == null) return false;
            if (stored.Length == 0) return false;   // an unset PIN matches nothing
            return string.Equals(entered, stored, StringComparison.Ordinal);
        }
    }
}
