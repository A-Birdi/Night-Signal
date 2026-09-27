namespace NightSignal.AudioSynth
{
    /// <summary>
    /// Deterministic xorshift32 generator. A value type so every voice owns its own sequence; the same seed
    /// always yields the same noise, which keeps renders bit-identical between runs.
    /// </summary>
    public struct Rng
    {
        uint state;

        public Rng(uint seed) { state = seed == 0u ? 0x6D2B79F5u : seed; }

        public uint NextUInt()
        {
            uint x = state;
            if (x == 0u) x = 0x6D2B79F5u;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }

        /// <summary>Uniform in [0, 1).</summary>
        public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

        /// <summary>Uniform in [-1, 1).</summary>
        public float NextBipolar() => (NextUInt() >> 8) * (2f / 16777216f) - 1f;

        /// <summary>FNV-1a hash of a string, used to derive stable per-track seeds.</summary>
        public static uint Hash(string s, uint salt = 2166136261u)
        {
            uint h = salt;
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619u;
                }
            }
            return h == 0u ? 1u : h;
        }
    }
}
