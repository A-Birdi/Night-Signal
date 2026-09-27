using System.Threading;

namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Lock-free single-producer/single-consumer ring for commands from the game (main) thread to the audio
    /// thread. Neither side allocates or blocks. Exactly one thread may enqueue and exactly one may dequeue.
    /// </summary>
    public sealed class SpscRing<T> where T : struct
    {
        readonly T[] items;
        readonly int mask;
        int head; // next slot to read (consumer-owned)
        int tail; // next slot to write (producer-owned)

        public SpscRing(int capacityPowerOfTwo)
        {
            int cap = 2;
            while (cap < capacityPowerOfTwo) cap <<= 1;
            items = new T[cap];
            mask = cap - 1;
        }

        public bool TryEnqueue(in T item)
        {
            int t = tail;
            int h = Volatile.Read(ref head);
            if (unchecked(t - h) >= items.Length) return false;
            items[t & mask] = item;
            Volatile.Write(ref tail, unchecked(t + 1));
            return true;
        }

        public bool TryDequeue(out T item)
        {
            int h = head;
            int t = Volatile.Read(ref tail);
            if (h == t)
            {
                item = default;
                return false;
            }
            item = items[h & mask];
            items[h & mask] = default; // release references held by the slot
            Volatile.Write(ref head, unchecked(h + 1));
            return true;
        }
    }
}
