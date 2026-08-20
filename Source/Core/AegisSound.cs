using System;
using System.Collections.Generic;
using UnityEngine;

namespace IkosAegis.Core
{
    /// <summary>
    /// Keypad feedback: a tick per keypress, a latch on a granted PIN, a flat clack on a
    /// refused one.
    ///
    /// Every clip is a stock KSP sound pulled out of <c>GameDatabase</c> by its
    /// GameData-relative path, so the mod ships no audio of its own and stays a
    /// single-DLL-plus-configs install. The concept this mod came from calls a
    /// <c>KSPAudioSound.PlaySound("UI_Click", ...)</c> helper - there is no such type in
    /// KSP 1.12, which is why this exists instead.
    /// </summary>
    public static class AegisSound
    {
        /// <summary>A digit was pressed.</summary>
        public const string KeyPress = "Squad/Sounds/sound_click_tick";

        /// <summary>Clear, or the PIN buffer being emptied.</summary>
        public const string Clear = "Squad/Sounds/sound_click_flick";

        /// <summary>The PIN was accepted, or the lock was engaged - a mechanical latch.</summary>
        public const string Granted = "Squad/Sounds/sound_click_latch";

        /// <summary>The PIN was refused.</summary>
        public const string Denied = "Squad/Alarms/Sounds/ComputerShort";

        private static AudioSource _source;
        private static readonly Dictionary<string, AudioClip> Cache = new Dictionary<string, AudioClip>();

        public static void Play(string url)
        {
            if (string.IsNullOrEmpty(url)) return;

            try
            {
                AudioClip clip = Load(url);
                if (clip == null) return;
                if (!EnsureSource()) return;

                // UI volume, not master: these are interface noises and should follow the
                // slider the player associates with interface noises.
                _source.volume = GameSettings.UI_VOLUME;
                _source.PlayOneShot(clip);
            }
            catch (Exception ex)
            {
                AegisLog.Exception("Could not play '" + url + "'", ex);
            }
        }

        private static AudioClip Load(string url)
        {
            AudioClip cached;
            if (Cache.TryGetValue(url, out cached)) return cached;

            // GameDatabase is not populated during Awake at Startup.Instantly, so clips are
            // fetched on first use rather than at construction.
            if (GameDatabase.Instance == null) return null;

            if (!GameDatabase.Instance.ExistsAudioClip(url))
            {
                // Not an error: a stripped or modded install may genuinely lack the clip,
                // and a silent keypad is a cosmetic loss, not a broken lock.
                AegisLog.Warn("Sound '" + url + "' not found; that feedback will be silent.");
                Cache[url] = null;      // remember the miss so it is not retried every press
                return null;
            }

            AudioClip clip = GameDatabase.Instance.GetAudioClip(url);
            Cache[url] = clip;
            return clip;
        }

        private static bool EnsureSource()
        {
            if (_source != null) return true;

            GameObject holder = new GameObject("IkosAegisAudio");
            UnityEngine.Object.DontDestroyOnLoad(holder);

            _source = holder.AddComponent<AudioSource>();
            _source.spatialBlend = 0f;    // 2D: heard the same wherever the camera is
            _source.playOnAwake = false;
            _source.loop = false;

            return true;
        }
    }
}
