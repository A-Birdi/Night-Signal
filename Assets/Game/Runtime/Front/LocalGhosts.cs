using System;
using System.IO;
using NightSignal.Core.Ghosts;
using UnityEngine;

namespace NightSignal.Front
{
    /// <summary>
    /// A Local profile's personal ghosts (spec §8: "personal practice ghosts can exist locally but cannot be uploaded as trusted
    /// records"): the best valid run per course and format, kept beside the profile (ghosts/&lt;profile&gt;/&lt;course&gt;_&lt;format&gt;.json).
    /// A new valid run replaces the stored one when it is faster under the same rules, or when the stored one no longer matches
    /// the rules (it would only be reference-only).
    /// </summary>
    public static class LocalGhosts
    {
        static string Folder(LocalSession s) => Path.Combine(s.StorageFolder, "ghosts", s.Profile.ProfileId);

        static string FileFor(LocalSession s, string course, string format) => Path.Combine(Folder(s), $"{Safe(course)}_{Safe(format)}.json");

        static string Safe(string v) => string.Concat((v ?? "").Split(Path.GetInvalidFileNameChars()));

        /// <summary>The stored personal ghost for a course and format (null when there is none or it cannot be read).</summary>
        public static GhostRecording Best(LocalSession s, string course, string format)
        {
            if (s?.Profile == null) return null;
            string f = FileFor(s, course, format);
            if (!File.Exists(f)) return null;
            GhostRecording g = GhostRecording.Parse(File.ReadAllText(f), out string error);
            if (g == null) Debug.LogWarning($"[NightSignal.Ghost] {Path.GetFileName(f)} unreadable: {error}");
            return g;
        }

        /// <summary>Keeps <paramref name="run"/> as the personal ghost when it is valid and beats the stored one (or replaces an incompatible one).</summary>
        public static bool Offer(LocalSession s, GhostRecording run, out string note)
        {
            note = "";
            if (s?.Profile == null || run == null) { note = "no run"; return false; }
            if (!run.ValidPersonal)
            {
                note = run.Header.ResultMicros <= 0 ? "no finish: not kept as a ghost" : run.Header.Resets > 0 ? "a reset run is never a ghost target" : "not a valid ghost: " + string.Join("; ", run.Problems());
                return false;
            }
            GhostRecording stored = Best(s, run.Header.CourseId, run.Header.Format);
            if (stored != null && stored.ValidPersonal && stored.CompatibleWith(run.Header) && stored.Header.ResultMicros <= run.Header.ResultMicros)
            {
                note = $"your ghost stays the {stored.Header.ResultMicros / 1e6:F3} s run";
                return false;
            }
            string f = FileFor(s, run.Header.CourseId, run.Header.Format), tmp = f + ".tmp";
            try
            {
                Directory.CreateDirectory(Folder(s));
                File.WriteAllText(tmp, run.ToJson());
                if (!File.Exists(f)) File.Move(tmp, f);
                else
                {
                    // Another process (a scanner, an indexer) may briefly hold the stored ghost: Replace refuses then, a
                    // copy over it usually does not. A ghost that cannot be written is reported, never thrown into the
                    // race-finish flow (it once ended a trial's results page before it was shown).
                    try { File.Replace(tmp, f, null); }
                    catch (IOException) { File.Copy(tmp, f, true); File.Delete(tmp); }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                note = "ghost not saved: " + e.Message;
                Debug.LogWarning($"[NightSignal.Ghost] {Path.GetFileName(f)} not saved: {e.GetType().Name}: {e.Message}");
                return false;
            }
            note = stored == null ? "your first ghost here" : stored.CompatibleWith(run.Header) ? $"new ghost, {(stored.Header.ResultMicros - run.Header.ResultMicros) / 1e6:F3} s faster"
                : "new ghost (the old one was recorded under other rules)";
            return true;
        }
    }
}
