using System;
using NightSignal.Core.Toys;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NightSignal.Toys
{
    /// <summary>
    /// Local (offline) host for the While We Wait diversions: the SAME authoritative Core <see cref="DowntimeSession"/>
    /// the control plane runs, owned by this process for one Local profile (Addendum 02 §1: solo play works offline).
    /// Commands go through the full Core envelope (sequence, request id, epoch, rate limit). Nothing here is progression:
    /// the snapshot is stored in the profile's non-progression toy workspace.
    /// </summary>
    public sealed class LocalToyHost
    {
        public const string DocumentKey = "downtime-session";
        public const string DocumentSchema = "night-signal/downtime-session@1";

        public DowntimeSession Session { get; }
        public string Member { get; }
        long sequence;
        int requests;

        public LocalToyHost(ToyContent content, string member, string snapshotJson = null)
        {
            Member = member;
            long now = NowMs();
            if (!string.IsNullOrEmpty(snapshotJson))
            {
                try
                {
                    Session = DowntimeSession.Restore(DowntimeCodec.Deserialize(snapshotJson), content, now);
                }
                catch (Exception e)
                {
                    // A damaged or incompatible toy save never blocks play: start a fresh table and say so in the log.
                    Debug.LogWarning("[NightSignal.Toys] Local toy save could not be restored, starting fresh: " + e.Message);
                }
            }
            if (Session == null) Session = new DowntimeSession("local-" + member, content, now, (ulong)StableSeed(member));
            Session.Join(member, 1, now);
            sequence = Session.Seat(member)?.LastSequence ?? 0;
        }

        /// <summary>Wall-clock milliseconds: toy timers survive the app closing (a restore re-bases frozen tables).</summary>
        public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public ToyResult Do(ToyActivityId activity, string kind, JObject payload = null)
        {
            ToyCommand cmd = Session.Command(Member, activity, kind, payload ?? new JObject(), ++sequence, "local-" + (++requests));
            return Session.Submit(cmd, NowMs());
        }

        public void Advance() => Session.Advance(NowMs());

        /// <summary>Closing the view parks this member's car/controls; other state is kept exactly.</summary>
        public void Close(ToyActivityId activity) => Session.Close(Member, activity, NowMs());

        public string SnapshotJson() => Session.SnapshotJson();

        static int StableSeed(string s)
        {
            unchecked
            {
                int h = 17;
                foreach (char c in s ?? "") h = h * 31 + c;
                return h & 0x7fffffff;
            }
        }
    }
}
