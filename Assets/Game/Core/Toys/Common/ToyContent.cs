using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NightSignal.Core.Toys.Canvas;
using NightSignal.Core.Toys.CapClash;
using NightSignal.Core.Toys.PitCrew;
using NightSignal.Core.Toys.PocketCircuit;

namespace NightSignal.Core.Toys
{
    /// <summary>
    /// Authored toy content from <c>Assets/Content/Data/authored/toys/</c> (Addendum 02 D210 minimums): Cap Clash
    /// arrangements, Pit-Crew blueprints, Pocket Circuit layouts and the Canvas stamp library. Miniature content never
    /// counts toward the full-size course/car/rival/challenge minimums and is not part of the race ContentCatalogue hash.
    /// </summary>
    public sealed class ToyContent
    {
        public const string CapClashFile = "capclash.arrangements.json";
        public const string PitCrewFile = "pitcrew.blueprints.json";
        public const string PocketCircuitFile = "pocketcircuit.layouts.json";
        public const string StampsFile = "canvas.stamps.json";
        public static readonly string[] Files = { CapClashFile, PitCrewFile, PocketCircuitFile, StampsFile };

        public CapClashContent CapClash { get; private set; }
        public PitCrewContent PitCrew { get; private set; }
        public PocketCircuitContent PocketCircuit { get; private set; }
        public CanvasStampLibrary Stamps { get; private set; }
        /// <summary>SHA-256 over the four documents (name + LF-normalized text, ordinal order).</summary>
        public string ContentHash { get; private set; }

        /// <summary>Loads and validates every toy document; throws <see cref="FormatException"/> listing what is wrong.</summary>
        public static ToyContent Load(IReadOnlyDictionary<string, string> documents)
        {
            foreach (string f in Files)
                if (!documents.ContainsKey(f)) throw new FormatException("missing toy content document " + f);
            var c = new ToyContent
            {
                CapClash = CapClashContent.Parse(documents[CapClashFile]),
                PitCrew = PitCrewContent.Parse(documents[PitCrewFile]),
                PocketCircuit = PocketCircuitContent.Parse(documents[PocketCircuitFile]),
                Stamps = CanvasStampLibrary.Parse(documents[StampsFile]),
            };
            using (SHA256 sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (string f in Files.OrderBy(x => x, StringComparer.Ordinal))
                    sb.Append(f).Append('\n').Append(documents[f].Replace("\r\n", "\n")).Append('\n');
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                c.ContentHash = string.Concat(h.Select(b => b.ToString("x2")));
            }
            return c;
        }
    }
}
