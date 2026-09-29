using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace NightSignal.Core.Tutorial
{
    /// <summary>How a drive lesson is judged (all distances along the T00 loop, metres).</summary>
    public sealed class LessonCheck
    {
        /// <summary>reach | brake | bend | drift | catch | timed | reset | lap</summary>
        public string Type = "";
        public float StartMetres, EndMetres, AfterMetres;
        public int MinCameraChanges;
        public float ArmSpeedKmh, DropKmh, ExitKmh, SlipDeg, RecoverDeg, Seconds;
        public double DriftRaw;
    }

    public sealed class LessonDemonstration
    {
        /// <summary>The autopilot's drift skill while demonstrating (drift lessons); 0 = grip driving.</summary>
        public float DriftSkill;
    }

    /// <summary>One lesson: a drive on the loop (judged, with a demonstration) or a knowledge card with a check question.</summary>
    public sealed class TutorialLesson
    {
        public string Id = "", Kind = "", Title = "", Goal = "", Help = "";
        public List<string> Keywords = new List<string>();
        public LessonCheck Check;
        public LessonDemonstration Demonstration;
        /// <summary>"instructor": the instructor's demonstration ghost is on the road.</summary>
        public string Ghost;
        public string Question, Explanation;
        public List<string> Choices = new List<string>();
        public int Answer = -1;

        public bool IsDrive => Kind == "drive";

        /// <summary>The searchable help index: title, goal, help and keywords.</summary>
        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            string q = query.Trim();
            bool Has(string s) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            return Has(Title) || Has(Goal) || Has(Help) || Keywords.Any(Has);
        }
    }

    /// <summary>
    /// Tutorial T00 lessons (spec §16: "small interactive segments with visible feedback, a model demonstration, local
    /// retry, and a searchable help index"; never required before a first race), from
    /// authored/tutorial/lessons.json (night-signal/tutorial-lessons@1).
    /// </summary>
    public sealed class TutorialLessons
    {
        public const string SchemaId = "night-signal/tutorial-lessons@1";
        public static readonly string[] CheckTypes = { "reach", "brake", "bend", "drift", "catch", "timed", "reset", "lap" };

        public string Course = "T00";
        public List<TutorialLesson> Lessons = new List<TutorialLesson>();

        sealed class Doc
        {
            public string Schema, Course;
            public List<TutorialLesson> Lessons;
        }

        public static TutorialLessons Parse(string json)
        {
            var doc = JsonConvert.DeserializeObject<Doc>(json);
            if (doc?.Schema != SchemaId) throw new FormatException("tutorial lessons: unknown schema " + doc?.Schema);
            var t = new TutorialLessons { Course = doc.Course ?? "T00", Lessons = doc.Lessons ?? new List<TutorialLesson>() };
            List<string> problems = t.Problems();
            if (problems.Count > 0) throw new FormatException("tutorial lessons: " + string.Join("; ", problems));
            return t;
        }

        public List<string> Problems()
        {
            var p = new List<string>();
            if (Lessons.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != Lessons.Count) p.Add("duplicate lesson id");
            foreach (TutorialLesson l in Lessons)
            {
                if (string.IsNullOrEmpty(l.Id) || string.IsNullOrEmpty(l.Title) || string.IsNullOrEmpty(l.Goal) || string.IsNullOrEmpty(l.Help)) p.Add($"{l.Id}: id, title, goal and help are required");
                if (l.Kind == "drive")
                {
                    if (l.Check == null || Array.IndexOf(CheckTypes, l.Check.Type) < 0) p.Add($"{l.Id}: unknown check");
                    else if ((l.Check.Type == "brake" || l.Check.Type == "bend" || l.Check.Type == "drift" || l.Check.Type == "catch" || l.Check.Type == "timed")
                             && !(l.Check.EndMetres > l.Check.StartMetres)) p.Add($"{l.Id}: the judged stretch must end after it starts");
                }
                else if (l.Kind == "read")
                {
                    if (string.IsNullOrEmpty(l.Question) || l.Choices.Count < 2 || l.Answer < 0 || l.Answer >= l.Choices.Count) p.Add($"{l.Id}: a card needs a question, choices and the answer");
                }
                else p.Add($"{l.Id}: kind must be drive or read");
            }
            return p;
        }

        public TutorialLesson Find(string id) => Lessons.FirstOrDefault(l => l.Id == id);

        /// <summary>The help index: lessons whose title, goal, help or keywords contain the query.</summary>
        public List<TutorialLesson> Search(string query) => Lessons.Where(l => l.Matches(query)).ToList();
    }

    /// <summary>What the car did this tick, as a lesson judges it.</summary>
    public struct LessonTick
    {
        public float Seconds, Metres, SpeedKmh, Brake, SlipDeg;
        public int WallIncidents, Resets, CameraChanges;
        public double DriftRaw;
        public bool Finished, Spun;
    }

    public enum LessonStatus { InProgress = 0, Passed = 1, NotYet = 2 }

    /// <summary>
    /// Judges one drive lesson from ticks. The feedback line is what the lesson overlay shows (visible feedback); the
    /// verdict is final once Passed or NotYet (then the player retries locally). Engine-free.
    /// </summary>
    public sealed class LessonJudge
    {
        readonly LessonCheck c;
        public LessonStatus Status { get; private set; }
        public string Feedback { get; private set; } = "";
        bool armed, inside, slid;
        float entryKmh, minKmh = float.MaxValue, startSeconds = -1f, resetAt = -1f;
        int wallsAtStart = -1, resetsAtStart = -1;
        double driftAtStart = -1;

        public LessonJudge(TutorialLesson lesson)
        {
            c = lesson?.Check ?? throw new ArgumentException("a drive lesson with a check is required");
        }

        public LessonStatus Tick(LessonTick t)
        {
            if (Status != LessonStatus.InProgress) return Status;
            if (wallsAtStart < 0) wallsAtStart = t.WallIncidents; // baselines from the first tick (a lesson may begin inside its stretch)
            if (resetsAtStart < 0) resetsAtStart = t.Resets;
            switch (c.Type)
            {
                case "reach":
                    Feedback = $"{Math.Max(0f, c.EndMetres - t.Metres):F0} m to the board · camera changes {t.CameraChanges}/{c.MinCameraChanges}";
                    if (t.Metres >= c.EndMetres)
                        End(t.CameraChanges >= c.MinCameraChanges, "Reached the board and tried the camera views.", "Reached the board — change the camera view on the way next time.");
                    break;
                case "brake":
                    if (t.Metres < c.StartMetres)
                    {
                        armed |= t.SpeedKmh >= c.ArmSpeedKmh;
                        entryKmh = t.SpeedKmh;
                        wallsAtStart = t.WallIncidents;
                        Feedback = $"Build speed: {t.SpeedKmh:F0} / {c.ArmSpeedKmh:F0} km/h before the zone ({c.StartMetres - t.Metres:F0} m)";
                        break;
                    }
                    if (t.Brake > 0.2f) minKmh = Math.Min(minKmh, t.SpeedKmh);
                    float drop = minKmh == float.MaxValue ? 0f : entryKmh - minKmh;
                    Feedback = $"Entered at {entryKmh:F0} km/h · shed {Math.Max(0f, drop):F0} / {c.DropKmh:F0} km/h on the brake";
                    if (t.WallIncidents > wallsAtStart) End(false, "", "A wall in the braking zone: brake earlier and in a straight line.");
                    else if (!armed && t.Metres >= c.StartMetres) End(false, "", $"Arrive faster: at least {c.ArmSpeedKmh:F0} km/h before the zone.");
                    else if (drop >= c.DropKmh) End(true, $"Shed {drop:F0} km/h from {entryKmh:F0} km/h under braking.", "");
                    else if (t.Metres >= c.EndMetres) End(false, "", $"Only {Math.Max(0f, drop):F0} km/h shed — brake harder, earlier in the zone.");
                    break;
                case "bend":
                    if (t.Metres < c.StartMetres)
                    {
                        wallsAtStart = t.WallIncidents;
                        Feedback = $"{c.StartMetres - t.Metres:F0} m to the Demonstration Bend";
                        break;
                    }
                    Feedback = $"In the bend · {t.SpeedKmh:F0} km/h";
                    if (t.WallIncidents > wallsAtStart) End(false, "", "A wall in the bend: turn in later and slower.");
                    else if (t.Metres >= c.EndMetres)
                        End(t.SpeedKmh >= c.ExitKmh, $"Clean through the bend, out at {t.SpeedKmh:F0} km/h.", $"Clean, but out at {t.SpeedKmh:F0} km/h — get back on the throttle as it opens (≥ {c.ExitKmh:F0}).");
                    break;
                case "drift":
                    if (t.Metres < c.StartMetres) { driftAtStart = t.DriftRaw; Feedback = $"{c.StartMetres - t.Metres:F0} m to the judged zone"; break; }
                    if (driftAtStart < 0) driftAtStart = t.DriftRaw;
                    double gained = t.DriftRaw - driftAtStart;
                    Feedback = $"Drift {gained:F0} / {c.DriftRaw:F0} raw";
                    if (gained >= c.DriftRaw) End(true, $"Scored {gained:F0} raw in the zone.", "");
                    else if (t.Metres >= c.EndMetres + 30f) End(false, "", $"{gained:F0} raw — unsettle the rear earlier and hold the angle with the throttle.");
                    break;
                case "catch":
                    if (t.Metres < c.StartMetres) { wallsAtStart = t.WallIncidents; Feedback = $"{c.StartMetres - t.Metres:F0} m to the slide area"; break; }
                    if (t.Spun || t.WallIncidents > wallsAtStart) { End(false, "", "Spun or touched a wall — less throttle, more opposite lock sooner."); break; }
                    if (t.SlipDeg >= c.SlipDeg) slid = true;
                    Feedback = slid ? $"Sliding {t.SlipDeg:F0}° — catch it (under {c.RecoverDeg:F0}°)" : $"Let it slide: {t.SlipDeg:F0} / {c.SlipDeg:F0}°";
                    if (slid && t.SlipDeg <= c.RecoverDeg) End(true, "Caught the slide and straightened the car.", "");
                    else if (t.Metres >= c.EndMetres) End(false, "", slid ? "Still sideways at the end — unwind the lock sooner." : "No slide yet — use more entry speed or the handbrake.");
                    break;
                case "timed":
                    if (t.Metres < c.StartMetres) { Feedback = $"{c.StartMetres - t.Metres:F0} m to the acceleration board"; break; }
                    if (startSeconds < 0f) startSeconds = t.Seconds;
                    float el = t.Seconds - startSeconds;
                    Feedback = $"Timed straight {el:F1} / {c.Seconds:F0} s";
                    if (t.Metres >= c.EndMetres) End(el <= c.Seconds, $"{el:F1} s over the timed straight.", $"{el:F1} s — carry more speed out of the bend before it.");
                    else if (el > c.Seconds) End(false, "", $"Over {c.Seconds:F0} s — carry more speed onto the straight.");
                    break;
                case "reset":
                    if (t.Resets > resetsAtStart && resetAt < 0f) resetAt = t.Metres;
                    Feedback = resetAt < 0f ? "Hold reset once (R, or Y on a pad)" : $"Reset (+3.0 s) — drive on {Math.Max(0f, resetAt + c.AfterMetres - t.Metres):F0} m";
                    if (resetAt >= 0f && t.Metres >= resetAt + c.AfterMetres) End(true, "Reset, took the +3.0 s penalty and drove on.", "");
                    break;
                case "lap":
                    Feedback = $"Lap {t.Metres:F0} m";
                    if (t.Finished) End(true, "Lap finished — the checkpoint deltas were on the HUD.", "");
                    break;
            }
            return Status;
        }

        void End(bool passed, string ok, string notYet)
        {
            Status = passed ? LessonStatus.Passed : LessonStatus.NotYet;
            Feedback = passed ? ok : notYet;
        }

        /// <summary>A card's answer: right or not, with its explanation.</summary>
        public static bool AnswerCard(TutorialLesson card, int choice, out string feedback)
        {
            bool right = card != null && choice == card.Answer;
            feedback = card == null ? "" : (right ? "Right. " : "Not quite. ") + card.Explanation;
            return right;
        }
    }
}
