using System.Collections.Generic;

namespace NightSignal.Race
{
    /// <summary>
    /// A stable insertion sort for the handful of cars a race has (at most twelve), with no allocation: this Mono's
    /// List.Sort wraps the comparer in a new delegate on every call, which the per-frame standings and the car-audio budget
    /// paid every frame (spec §14 "no per-frame allocations in the driving hot path"; V-151).
    /// </summary>
    public static class SmallSort
    {
        public static void Insertion<T>(List<T> items, IComparer<T> comparer)
        {
            for (int i = 1; i < items.Count; i++)
            {
                T x = items[i];
                int j = i - 1;
                while (j >= 0 && comparer.Compare(items[j], x) > 0)
                {
                    items[j + 1] = items[j];
                    j--;
                }
                items[j + 1] = x;
            }
        }
    }
}
