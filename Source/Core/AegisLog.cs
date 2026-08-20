using System;

namespace IkosAegis.Core
{
    /// <summary>Severity for <see cref="AegisLog"/>. Error always reaches KSP.log.</summary>
    public enum LogLevel
    {
        Error = 0,
        Warn = 1,
        Info = 2,
        Debug = 3
    }

    /// <summary>
    /// Tagged logging into KSP.log. Every line is prefixed with [IkosAegis] so it can be
    /// grepped out of a log shared with the game and every other mod.
    ///
    /// No queue and no thread marshalling, unlike KSPRedeem's equivalent: this mod has no
    /// background threads at all, so everything here already runs on the main thread. If
    /// that ever stops being true, this is the first thing that has to change.
    ///
    /// The rule the messages follow: **say what happened, not what was attempted**, and
    /// label a value with when it was sampled. A line that cannot distinguish the outcomes
    /// it is reporting is worse than no line.
    /// </summary>
    public static class AegisLog
    {
        public const string Tag = "[IkosAegis]";

        public static LogLevel Level = LogLevel.Info;

        public static void Error(string message) { Write(LogLevel.Error, message); }
        public static void Warn(string message) { Write(LogLevel.Warn, message); }
        public static void Info(string message) { Write(LogLevel.Info, message); }
        public static void Debug(string message) { Write(LogLevel.Debug, message); }

        public static void Exception(string context, Exception ex)
        {
            if (ex == null)
            {
                Error(context);
                return;
            }

            Error(context + " -> " + ex.GetType().Name + ": " + ex.Message);
            if (ex.InnerException != null)
            {
                Error("  inner -> " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
            }
            // At Error, not Debug. This was gated behind Debug and a real
            // NullReferenceException reached a bug report as the single line "Object reference
            // not set to an instance of an object" - true, useless, and it took a disassembly
            // to find out which reference. An exception we bothered to report is worth the
            // three lines that say where it happened; nothing here is on a hot path.
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                Error("  at " + ex.StackTrace);
            }
        }

        private static void Write(LogLevel level, string message)
        {
            if (level > Level) return;

            string line = Tag + "[" + level.ToString().ToUpperInvariant() + "] " + message;

            switch (level)
            {
                case LogLevel.Error:
                    UnityEngine.Debug.LogError(line);
                    break;
                case LogLevel.Warn:
                    UnityEngine.Debug.LogWarning(line);
                    break;
                default:
                    UnityEngine.Debug.Log(line);
                    break;
            }
        }
    }
}
