using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Meet
{
    public enum NoticeKind { Info = 0, Arrived = 1, Departed = 2, Disconnected = 3 }

    public sealed class Notice
    {
        public NoticeKind Kind;
        /// <summary>Stable event key: the same key twice (reconnect retries, replays) never shows twice.</summary>
        public string Key = "";
        public List<string> Names = new List<string>();
        public string Text = "";

        public string Display()
        {
            if (Kind == NoticeKind.Info) return Text;
            string verb = Kind == NoticeKind.Arrived ? "arrived" : Kind == NoticeKind.Departed ? "left" : "disconnected";
            return Names.Count == 1 ? $"{Names[0]} {verb}" : $"{Names.Count} drivers {verb}";
        }
    }

    /// <summary>
    /// The horizontal "SIGNAL" notification ribbon's queue (spec §12): one current notice and at most three queued; each
    /// enters, holds about 2.5 s and exits; a busy burst of the same kind coalesces ("3 drivers arrived") instead of
    /// growing the queue; notices are keyed so replays and reconnect retries never repeat one. Engine-free; the UI animates
    /// <see cref="Current"/> by <see cref="Phase"/>.
    /// </summary>
    public sealed class SignalRibbonQueue
    {
        public const float EnterSeconds = 0.35f, HoldSeconds = 2.5f, ExitSeconds = 0.35f;
        public const int MaxQueued = 3;

        readonly List<Notice> queued = new List<Notice>();
        readonly HashSet<string> seen = new HashSet<string>();
        float age;

        public Notice Current { get; private set; }
        public IReadOnlyList<Notice> Queued => queued;
        /// <summary>Everything shown so far, oldest first (the accessible event list).</summary>
        public readonly List<string> History = new List<string>();

        /// <summary>0→1 entering, 1 holding, 1→2 exiting (for the slide animation).</summary>
        public float Phase => Current == null ? 0f : age < EnterSeconds ? age / EnterSeconds
            : age < EnterSeconds + HoldSeconds ? 1f : 1f + (age - EnterSeconds - HoldSeconds) / ExitSeconds;

        public bool Post(NoticeKind kind, string key, string nameOrText)
        {
            if (!string.IsNullOrEmpty(key) && !seen.Add(key)) return false;
            // Coalesce into a waiting notice of the same kind (never the one already on screen).
            if (kind != NoticeKind.Info)
            {
                Notice same = queued.LastOrDefault(n => n.Kind == kind);
                if (same != null)
                {
                    same.Names.Add(nameOrText);
                    return true;
                }
            }
            var notice = new Notice { Kind = kind, Key = key ?? "", Text = nameOrText ?? "" };
            if (kind != NoticeKind.Info) notice.Names.Add(nameOrText);
            if (queued.Count >= MaxQueued)
            {
                // Full: fold into the last waiting notice of the same kind, else drop the oldest info notice, else merge.
                Notice info = queued.FirstOrDefault(n => n.Kind == NoticeKind.Info);
                if (info != null) queued.Remove(info);
                else
                {
                    queued[queued.Count - 1].Names.Add(nameOrText);
                    return true;
                }
            }
            queued.Add(notice);
            return true;
        }

        public void Tick(float dt)
        {
            if (Current != null)
            {
                age += dt;
                if (age >= EnterSeconds + HoldSeconds + ExitSeconds) Current = null;
            }
            if (Current == null && queued.Count > 0)
            {
                Current = queued[0];
                queued.RemoveAt(0);
                age = 0f;
                History.Add(Current.Display());
            }
        }
    }
}
