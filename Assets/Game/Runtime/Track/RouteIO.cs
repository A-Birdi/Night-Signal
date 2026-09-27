using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace NightSignal.Track
{
    public struct RouteStats
    {
        public float LengthMetres;
        public float NetElevationMetres;
        public float MinRadiusMetres;
        public float MaxGradePercent;
        public float MinWidthMetres;
    }

    public static class RouteIO
    {
        public const string Schema = "night-signal/route@1";

        public static string RoutePath(string courseId) => $"Assets/Content/Courses/{courseId}/route.json";

        public static RouteDefinition Parse(string json)
        {
            RouteDefinition r = JsonConvert.DeserializeObject<RouteDefinition>(json);
            if (r == null || r.Schema != Schema)
                throw new InvalidDataException($"Route document must declare schema {Schema}");
            return r;
        }

        public static RouteDefinition Load(string courseId) => Parse(File.ReadAllText(RoutePath(courseId)));

        public static string SourceHash(string json)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(json.Replace("\r\n", "\n"))).Select(b => b.ToString("x2")));
        }

        public static RouteStats Measure(TrackSample[] s)
        {
            var stats = new RouteStats
            {
                LengthMetres = s[s.Length - 1].Distance,
                NetElevationMetres = s[s.Length - 1].Position.y - s[0].Position.y,
                MinRadiusMetres = float.MaxValue,
                MinWidthMetres = float.MaxValue,
            };
            for (int i = 0; i < s.Length; i++)
            {
                float k = Mathf.Abs(s[i].Curvature);
                if (k > 1e-5f) stats.MinRadiusMetres = Mathf.Min(stats.MinRadiusMetres, 1f / k);
                stats.MinWidthMetres = Mathf.Min(stats.MinWidthMetres, s[i].Width);
                if (i >= 10)
                {
                    float rise = s[i].Position.y - s[i - 10].Position.y;
                    float run = s[i].Distance - s[i - 10].Distance;
                    stats.MaxGradePercent = Mathf.Max(stats.MaxGradePercent, Mathf.Abs(rise / run) * 100f);
                }
            }
            return stats;
        }
    }
}
