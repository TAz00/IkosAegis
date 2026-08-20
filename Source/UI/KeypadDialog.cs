using System;
using System.Collections.Generic;
using IkosAegis.Core;
using IkosAegis.Logic;
using UnityEngine;

namespace IkosAegis.UI
{
    /// <summary>
    /// The numeric keypad, built from stock <c>DialogGUI*</c> elements inside a
    /// <c>PopupDialog</c>.
    ///
    /// Stock uGUI rather than IMGUI or an asset bundle, for three reasons: it matches the
    /// game's own dialogs for free by passing <c>HighLogic.UISkin</c>, it needs no prefabs
    /// and no Unity project, and - the one that matters here - <c>PopupDialog</c> manages
    /// its own input handling, so a modal that appears while the vessel is under an Aegis
    /// control lock cannot leak a second lock of its own.
    ///
    /// Layout:
    /// <code>
    ///   +---------------------------+
    ///   |      Enter Lock PIN       |
    ///   |         * * _             |
    ///   +---------------------------+
    ///   |  [ 1 ]   [ 2 ]   [ 3 ]    |
    ///   |  [ 4 ]   [ 5 ]   [ 6 ]    |
    ///   |  [ 7 ]   [ 8 ]   [ 9 ]    |
    ///   |  [CLR]   [ 0 ]   [ OK]    |
    ///   +---------------------------+
    ///   |         [Cancel]          |
    ///   +---------------------------+
    /// </code>
    /// </summary>
    public static class KeypadDialog
    {
        /// <summary>Called with the digits the player entered. Never called with null.</summary>
        public delegate void OnSubmitPin(string pin);

        private const float ButtonSize = 52f;
        private const float ButtonHeight = 40f;
        private const float RowSpacing = 4f;
        private const float DialogWidth = 230f;
        private const float DialogHeight = 300f;

        /// <summary>
        /// Only one keypad at a time.
        ///
        /// Without this, right-clicking two probe cores gives two stacked modals over the
        /// same craft, and dismissing the top one leaves the other holding a stale reference
        /// to a part that may since have been destroyed.
        /// </summary>
        private static PopupDialog _open;

        public static bool IsOpen { get { return _open != null; } }

        /// <summary>Closes any open keypad. Safe to call when none is open.</summary>
        public static void DismissOpen()
        {
            if (_open == null) return;

            PopupDialog dialog = _open;
            _open = null;

            try
            {
                dialog.Dismiss();
            }
            catch (Exception ex)
            {
                AegisLog.Exception("Could not dismiss the keypad", ex);
            }
        }

        /// <summary>
        /// Spawns the keypad.
        /// </summary>
        /// <param name="title">Dialog title - says which operation the PIN is for.</param>
        /// <param name="pinLength">How many digits the display expects and OK requires.</param>
        /// <param name="onSubmit">
        /// Invoked when OK is pressed with a full-length entry. Not invoked on Cancel, and
        /// not invoked on a short entry - the OK button refuses those itself, so the caller
        /// never has to re-check the length.
        /// </param>
        public static void Show(string title, int pinLength, OnSubmitPin onSubmit)
        {
            DismissOpen();

            int length = PinCode.ClampLength(pinLength);

            // Captured by every lambda below. A local rather than a field so two sequential
            // keypads cannot see each other's digits.
            string entered = string.Empty;

            // The label re-evaluates its Func every frame, so the mask updates as digits are
            // pressed with no explicit refresh call. Note the (Func<string>, float, float)
            // overload - DialogGUILabel has no (Func<string>, float) form, which is the one
            // the concept sketch reaches for.
            DialogGUILabel display = new DialogGUILabel(
                () => PinCode.Mask(entered, length),
                DialogWidth - 40f,
                26f);

            Action<string> addDigit = digit =>
            {
                if (entered.Length >= length)
                {
                    // Full. Say nothing and play nothing - a keypad that clicks at a press
                    // it ignored is telling the player it accepted a digit it did not.
                    return;
                }

                entered += digit;
                AegisSound.Play(AegisSound.KeyPress);
            };

            Callback clear = () =>
            {
                entered = string.Empty;
                AegisSound.Play(AegisSound.Clear);
            };

            Callback submit = () =>
            {
                string value = entered;
                entered = string.Empty;

                // Dismissal is handled by this button's dismissOnSelect flag; clear the
                // handle so DismissOpen does not later poke a dialog KSP has already torn
                // down.
                _open = null;

                if (onSubmit != null) onSubmit(value);
            };

            Callback cancel = () =>
            {
                entered = string.Empty;
                _open = null;
            };

            List<DialogGUIBase> layout = new List<DialogGUIBase>
            {
                new DialogGUISpace(6f),
                new DialogGUIHorizontalLayout(TextAnchor.MiddleCenter, new DialogGUIBase[] { display }),
                new DialogGUISpace(8f),

                Row(Digit("1", addDigit), Digit("2", addDigit), Digit("3", addDigit)),
                Row(Digit("4", addDigit), Digit("5", addDigit), Digit("6", addDigit)),
                Row(Digit("7", addDigit), Digit("8", addDigit), Digit("9", addDigit)),
                Row(
                    new DialogGUIButton("CLR", clear, ButtonSize, ButtonHeight, false, new DialogGUIBase[0]),
                    Digit("0", addDigit),
                    // OK is enabled only on a full-length entry, so a short PIN cannot be
                    // submitted at all. The alternative - accept it and report an error - is
                    // the same refusal delivered later and less clearly.
                    new DialogGUIButton(
                        () => "OK",
                        submit,
                        () => entered.Length == length,
                        ButtonSize, ButtonHeight, true,
                        new DialogGUIBase[0])
                ),

                new DialogGUISpace(8f),
                new DialogGUIButton("Cancel", cancel, DialogWidth - 40f, 28f, true, new DialogGUIBase[0])
            };

            try
            {
                _open = PopupDialog.SpawnPopupDialog(
                    new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f),
                    new MultiOptionDialog(
                        "IkosAegisKeypad",
                        string.Empty,
                        title,
                        HighLogic.UISkin,
                        // x/y are normalized anchors (0-1); width/height are pixels.
                        new Rect(0.5f, 0.5f, DialogWidth, DialogHeight),
                        layout.ToArray()),
                    false,                  // persistAcrossScenes: a PIN prompt must not
                                            // outlive the flight it belongs to
                    HighLogic.UISkin);
            }
            catch (Exception ex)
            {
                _open = null;
                AegisLog.Exception("Could not open the keypad", ex);
                ScreenMessages.PostScreenMessage(
                    "[Aegis] The keypad failed to open - see KSP.log.",
                    5f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        private static DialogGUIButton Digit(string digit, Action<string> addDigit)
        {
            return new DialogGUIButton(
                digit,
                () => addDigit(digit),
                ButtonSize, ButtonHeight,
                false,                      // dismissOnSelect: a digit must not close the pad
                new DialogGUIBase[0]);
        }

        private static DialogGUIHorizontalLayout Row(params DialogGUIBase[] buttons)
        {
            return new DialogGUIHorizontalLayout(
                true, false,
                RowSpacing,
                new RectOffset(0, 0, 0, 0),
                TextAnchor.MiddleCenter,
                buttons);
        }
    }
}
