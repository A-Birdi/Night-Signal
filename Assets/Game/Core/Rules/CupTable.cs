using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Rules
{
    /// <summary>One entrant's line in a Custom Cup table.</summary>
    public sealed class CupEntrant
    {
        public string Id = "", Name = "";
        public bool Human;
        public int Points;
        /// <summary>Per leg: the placing, or null for a leg not finished (DNF, DQ, not started).</summary>
        public readonly List<int?> Places = new List<int?>();
        public int Wins => Places.Count(p => p == 1);
    }

    /// <summary>One entrant's result in one leg.</summary>
    public struct CupLegResult
    {
        public string Id, Name;
        public bool Human;
        /// <summary>The leg placing for a legal finish; null for DNF / DQ.</summary>
        public int? Place;
    }

    /// <summary>
    /// The Custom Cup table (spec §8: "a published three-event schedule … DQs keep their places in the cup table but cannot
    /// regain the missed race. No buy-in or gambling-like currency stakes"): points per leg (10, 8, 6, 5, 4, 3 for places
    /// 1–6; nothing for a DNF or DQ), every entrant keeps its line once it has raced, ties broken by wins, then the best
    /// placings in order, then the latest leg. Presentation of standings only — the legs pay as ordinary races.
    /// </summary>
    public sealed class CupTable
    {
        public const int Legs = 3;
        public static readonly int[] PointsByPlace = { 10, 8, 6, 5, 4, 3 };

        public readonly List<string> Schedule = new List<string>();
        readonly List<CupEntrant> entrants = new List<CupEntrant>();
        public int LegsRaced { get; private set; }
        public bool Complete => LegsRaced >= Schedule.Count && Schedule.Count > 0;

        public CupTable(IEnumerable<string> schedule)
        {
            Schedule.AddRange(schedule ?? Enumerable.Empty<string>());
            if (Schedule.Count != Legs) throw new ArgumentException($"a Custom Cup has {Legs} legs");
        }

        public static int PointsFor(int? place) => place is int p && p >= 1 && p <= PointsByPlace.Length ? PointsByPlace[p - 1] : 0;

        /// <summary>Adds the next leg's results; an entrant missing from a later leg keeps its line with nothing for that leg.</summary>
        public void AddLeg(IEnumerable<CupLegResult> results)
        {
            if (Complete) throw new InvalidOperationException("the cup is complete");
            foreach (CupLegResult r in results ?? Enumerable.Empty<CupLegResult>())
            {
                CupEntrant e = entrants.FirstOrDefault(x => x.Id == r.Id);
                if (e == null)
                {
                    e = new CupEntrant { Id = r.Id ?? "", Name = r.Name ?? r.Id ?? "", Human = r.Human };
                    for (int i = 0; i < LegsRaced; i++) e.Places.Add(null);
                    entrants.Add(e);
                }
                if (e.Places.Count > LegsRaced) continue; // one result per entrant per leg
                e.Places.Add(r.Place);
                e.Points += PointsFor(r.Place);
            }
            LegsRaced++;
            foreach (CupEntrant e in entrants) while (e.Places.Count < LegsRaced) e.Places.Add(null);
        }

        /// <summary>The table, leader first.</summary>
        public List<CupEntrant> Standings() => entrants
            .OrderByDescending(e => e.Points)
            .ThenBy(e => e, Comparer<CupEntrant>.Create(CompareCountback))
            .ThenBy(e => e.Places.LastOrDefault() ?? int.MaxValue)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        /// <summary>Countback: more wins, then more second places, and so on.</summary>
        static int CompareCountback(CupEntrant a, CupEntrant b)
        {
            for (int place = 1; place <= PointsByPlace.Length; place++)
            {
                int pa = a.Places.Count(p => p == place), pb = b.Places.Count(p => p == place);
                if (pa != pb) return pb.CompareTo(pa);
            }
            return 0;
        }

        /// <summary>An entrant's position in the table (1-based), or 0 when absent.</summary>
        public int PositionOf(string id) => Standings().FindIndex(e => e.Id == id) + 1;
    }
}
