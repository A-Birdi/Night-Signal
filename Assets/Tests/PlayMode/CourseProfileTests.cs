using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Art;
using NightSignal.Track;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Addendum 03 §5.2 / T01: a MEASURED 3D profile of every course as generated — the centreline the race, AI and progress
    /// use, and the physical road collider under it (raycast every 5 m) — against the authored route targets. Driven 3D
    /// length, start/end/min/max height, accumulated ascent/descent, grade and banking ranges and a 25 m trace go to
    /// Evidence/courses/profile/. Full-size courses must have an intentional non-flat surface (height range or real
    /// banking); the road collider must follow the centreline.
    /// </summary>
    public sealed class CourseProfileTests
    {
        static IEnumerable<string> Courses()
        {
            var ids = new List<string>();
            foreach (string dir in Directory.GetDirectories("Assets/Content/Courses"))
            {
                string id = Path.GetFileName(dir);
                if (File.Exists($"{dir}/{id}.unity")) ids.Add(id);
            }
            ids.Sort(System.StringComparer.Ordinal);
            return ids;
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator Course_HasAMeasuredElevatedProfile([ValueSource(nameof(Courses))] string course)
        {
#if UNITY_EDITOR
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                $"Assets/Content/Courses/{course}/{course}.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return load;
#else
            Assert.Ignore("Editor-only scene loading");
            yield break;
#endif
            yield return null;
            Physics.SyncTransforms();
            CourseRuntime runtime = CourseRuntime.Active;
            Assert.That(runtime, Is.Not.Null, "course generated");
            TrackData t = runtime.Track;
            RouteDefinition route = runtime.Route;
            TrackSample[] s = t.Samples;

            var p = new ProfileEvidence
            {
                course = course, closedLoop = t.ClosedLoop, laps = t.Laps, revision = route.Revision, sourceHash = runtime.SourceHash,
                sampleCount = s.Length, lengthMetres = t.LengthMetres, unityVersion = Application.unityVersion,
                note = "Measured from the generated centreline (TrackData) and the road collider; authored values are targets, not measurements.",
            };
            float len3d = 0f, len2d = 0f, ascent = 0f, descent = 0f;
            float minY = float.MaxValue, maxY = float.MinValue, minGrade = 0f, maxGrade = 0f, minBank = 0f, maxBank = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                float y = s[i].Position.y;
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
                minBank = Mathf.Min(minBank, s[i].BankDeg);
                maxBank = Mathf.Max(maxBank, s[i].BankDeg);
                if (i == 0) continue;
                Vector3 d = s[i].Position - s[i - 1].Position;
                len3d += d.magnitude;
                len2d += new Vector2(d.x, d.z).magnitude;
                if (d.y > 0) ascent += d.y; else descent -= d.y;
            }
            // Grade over 20 m windows (rise over horizontal run), so single-sample noise does not read as a wall.
            for (int i = 20; i < s.Length; i += 5)
            {
                Vector3 d = s[i].Position - s[i - 20].Position;
                float run = new Vector2(d.x, d.z).magnitude;
                if (run < 1f) continue;
                float grade = d.y / run * 100f;
                minGrade = Mathf.Min(minGrade, grade);
                maxGrade = Mathf.Max(maxGrade, grade);
            }
            p.length3d = len3d;
            p.lengthPlan = len2d;
            p.startHeight = t.SampleAt(t.StartMetres).Position.y;
            p.endHeight = t.ClosedLoop ? p.startHeight : s[s.Length - 1].Position.y;
            p.minHeight = minY;
            p.maxHeight = maxY;
            p.ascent = ascent;
            p.descent = descent;
            p.minGradePercent = minGrade;
            p.maxGradePercent = maxGrade;
            p.minBankDeg = minBank;
            p.maxBankDeg = maxBank;

            List<float> authored = route.ControlPoints.Select(c => c.P[1]).ToList();
            p.authoredMinHeight = authored.Min();
            p.authoredMaxHeight = authored.Max();
            p.authoredAscent = authored.Zip(authored.Skip(1), (a, b) => Mathf.Max(0f, b - a)).Sum();
            p.authoredDescent = authored.Zip(authored.Skip(1), (a, b) => Mathf.Max(0f, a - b)).Sum();
            p.authoredMaxBankDeg = route.ControlPoints.Max(c => Mathf.Abs(c.Bank));

            // Physical agreement: the drivable collider lies on the centreline (cast from just above, so an overpass above
            // the road is not mistaken for it).
            int probes = 0, hits = 0;
            float worst = 0f;
            for (float dist = 0f; dist <= t.LengthMetres; dist += 5f)
            {
                TrackSample a = t.SampleAt(dist);
                probes++;
                Vector3 from = a.Position + a.Up * 1.5f;
                if (Physics.Raycast(from, -a.Up, out RaycastHit hit, 3.5f, GameLayers.DrivableMask, QueryTriggerInteraction.Ignore))
                {
                    float gap = Mathf.Abs(Vector3.Dot(hit.point - a.Position, a.Up));
                    worst = Mathf.Max(worst, gap);
                    if (gap <= 0.25f) hits++;
                    else if (p.surfaceMismatches.Count < 10) p.surfaceMismatches.Add($"{dist:F0} m: surface {gap:F2} m from the centreline");
                }
                else if (p.surfaceMismatches.Count < 10) p.surfaceMismatches.Add($"{dist:F0} m: no drivable collider under the centreline");
            }
            p.surfaceProbes = probes;
            p.surfaceOnCentreline = hits;
            p.worstSurfaceGapMetres = worst;
            for (float dist = 0f; dist <= t.LengthMetres; dist += 25f)
            {
                TrackSample a = t.SampleAt(dist);
                TrackSample b = t.SampleAt(Mathf.Min(t.LengthMetres, dist + 20f));
                float run = new Vector2(b.Position.x - a.Position.x, b.Position.z - a.Position.z).magnitude;
                p.trace.Add(new TracePoint { d = dist, y = a.Position.y, gradePercent = run > 1f ? (b.Position.y - a.Position.y) / run * 100f : 0f, bankDeg = a.BankDeg });
            }

            Directory.CreateDirectory("Evidence/courses/profile");
            File.WriteAllText($"Evidence/courses/profile/{course}.json", JsonUtility.ToJson(p, true));
            Debug.Log($"[NightSignal.Profile] {course}: 3D {len3d:F0} m (plan {len2d:F0} m), height {minY:F1}..{maxY:F1} (start {p.startHeight:F1}, end {p.endHeight:F1}), " +
                      $"ascent {ascent:F0} m / descent {descent:F0} m, grade {minGrade:F1}..{maxGrade:F1} %, bank {minBank:F1}..{maxBank:F1}°, surface {hits}/{probes} on centreline (worst {worst:F2} m)");

            Assert.That(hits, Is.GreaterThanOrEqualTo(probes * 99 / 100), $"{course}: road collider follows the centreline ({hits}/{probes}): {string.Join("; ", p.surfaceMismatches)}");
            if (course != "T00")
            {
                bool relief = maxY - minY >= 5f;
                bool banking = Mathf.Max(Mathf.Abs(minBank), Mathf.Abs(maxBank)) >= 4f;
                Assert.That(relief || banking, Is.True, $"{course}: intentional non-flat surface (height range {maxY - minY:F1} m, max bank {Mathf.Max(Mathf.Abs(minBank), maxBank):F1}°)");
            }
        }

        [System.Serializable]
        sealed class ProfileEvidence
        {
            public string course, sourceHash, unityVersion, note;
            public bool closedLoop;
            public int laps, revision, sampleCount, surfaceProbes, surfaceOnCentreline;
            public float lengthMetres, length3d, lengthPlan, startHeight, endHeight, minHeight, maxHeight, ascent, descent;
            public float minGradePercent, maxGradePercent, minBankDeg, maxBankDeg, worstSurfaceGapMetres;
            public float authoredMinHeight, authoredMaxHeight, authoredAscent, authoredDescent, authoredMaxBankDeg;
            public List<string> surfaceMismatches = new List<string>();
            public List<TracePoint> trace = new List<TracePoint>();
        }

        [System.Serializable]
        sealed class TracePoint
        {
            public float d, y, gradePercent, bankDeg;
        }
    }
}
