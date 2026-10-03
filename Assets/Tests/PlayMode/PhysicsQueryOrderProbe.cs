using System.Collections;
using System.Collections.Generic;
using System.Text;
using NightSignal.Art;
using NightSignal.Track;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    /// <summary>
    /// Diagnostic for V-152: does the order in which the physics scene returns overlapping colliders depend on what was loaded
    /// before? Samples a car-body box at both road edges every 7 m of C01 (barrier and drivable layers, as
    /// PhysicsVehicleWorld.ResolveBody queries them) after a fresh load, then again after C12 was loaded first, and compares
    /// the order of the colliders returned. Explicit: a measuring tool.
    /// </summary>
    [ResetAutomationStatics]
    public sealed class PhysicsQueryOrderProbe
    {
        static IEnumerator Load(string course)
        {
#if UNITY_EDITOR
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                $"Assets/Content/Courses/{course}/{course}.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return load;
#endif
            yield return null;
            yield return null;
        }

        static string Key(Collider c)
        {
            Vector3 b = c.bounds.center;
            return $"{c.name}@{b.x:F2},{b.y:F2},{b.z:F2}";
        }

        /// <summary>Per sample with two or more overlaps: the colliders in the order returned.</summary>
        static List<string> Sample(out int multi)
        {
            TrackData t = CourseRuntime.Active.Track;
            var buf = new Collider[32];
            var half = new Vector3(0.95f, 0.7f, 2.3f);
            var rows = new List<string>();
            multi = 0;
            for (float d = 0f; d < t.LengthMetres; d += 7f)
            {
                TrackSample s = t.SampleAt(d);
                foreach (float side in new[] { -1f, 1f })
                {
                    Vector3 c = s.Position + s.Right * side * (s.Width * 0.5f + 0.4f) + Vector3.up * 0.6f;
                    int n = Physics.defaultPhysicsScene.OverlapBox(c, half, buf, Quaternion.LookRotation(s.Tangent, Vector3.up),
                        GameLayers.BarrierMask | GameLayers.DrivableMask, QueryTriggerInteraction.Ignore);
                    if (n < 2) continue;
                    multi++;
                    var sb = new StringBuilder($"{d:F0}/{side:+0;-0}:");
                    for (int i = 0; i < n; i++) sb.Append(' ').Append(Key(buf[i]));
                    rows.Add(sb.ToString());
                }
            }
            return rows;
        }

        [UnityTest, Explicit("diagnostic tool (V-152)"), Timeout(300000)]
        public IEnumerator OverlapOrder_AfterAnotherCourse()
        {
            yield return Load("C01");
            List<string> fresh = Sample(out int multiFresh);
            yield return Load("C12");
            yield return Load("C01");
            List<string> after = Sample(out int multiAfter);
            int differ = 0, sameSet = 0;
            string first = null;
            for (int i = 0; i < Mathf.Min(fresh.Count, after.Count); i++)
            {
                if (fresh[i] == after[i]) continue;
                differ++;
                var a = new List<string>(fresh[i].Split(' '));
                var b = new List<string>(after[i].Split(' '));
                a.Sort(System.StringComparer.Ordinal);
                b.Sort(System.StringComparer.Ordinal);
                if (string.Join(" ", a) == string.Join(" ", b)) sameSet++;
                if (first == null) first = fresh[i] + "  |  " + after[i];
            }
            Debug.Log($"[NightSignal.QueryOrderProbe] samples with 2+ overlaps: fresh {multiFresh}, after C12 {multiAfter}; " +
                      $"rows differing {differ} (same colliders, different order: {sameSet}); first: {first ?? "none"}");
        }
    }
}
