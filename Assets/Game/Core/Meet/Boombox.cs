using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Meet
{
    public enum BoomboxStatus
    {
        Ok = 0,
        /// <summary>Someone else holds the control lease.</summary>
        LeaseHeld = 1,
        /// <summary>Controls need the lease (walk up and open the boombox first).</summary>
        NoLease = 2,
        /// <summary>The requester does not own that cue (only music you have unlocked can be queued).</summary>
        NotOwned = 3,
        /// <summary>The queue already holds six requests.</summary>
        QueueFull = 4,
        /// <summary>A user-triggered change happened less than ten seconds ago.</summary>
        TooSoon = 5,
        OutOfRange = 6,
        UnknownTrack = 7,
        NothingQueued = 8,
    }

    /// <summary>One person's pending request (one per person; a new pick replaces it).</summary>
    public sealed class BoomboxRequest
    {
        public string PlayerId = "";
        public string TrackId = "";
        public long SubmittedMs;
    }

    /// <summary>
    /// The meet's ONE shared playback state, owned by the room server (Addendum 01 §11.4): room, track, queue revision,
    /// playback start timestamp, submitting player and a short control lease. Clients receive this metadata and play the
    /// bundled cue locally, seeking late joiners to <see cref="PositionSeconds"/>. Rules: a 15-second renewable lease; one
    /// queued request per person; at most six requests; at least ten seconds between user-triggered track changes room-wide;
    /// only music the requester owns; leaving (or closing the UI, walking out of range, disconnecting) releases the lease
    /// and removes that person's unplayed requests, while a track already playing may finish; with nothing left, the
    /// default meet cue returns. Engine-free and deterministic in the supplied clock.
    /// </summary>
    public sealed class BoomboxState
    {
        public const long LeaseMs = 15_000, MinChangeIntervalMs = 10_000;
        public const int MaxQueue = 6;
        public const string DefaultCue = "MUS_MEET";
        /// <summary>Cues that are spoilers until reached: lieutenant, penultimate and final themes.</summary>
        public static bool IsProtected(string cueId) =>
            cueId != null && (cueId.StartsWith("MUS_LT_", StringComparison.Ordinal) || cueId == "MUS_PENULTIMATE" || cueId.StartsWith("MUS_FINAL_", StringComparison.Ordinal));

        public string RoomId { get; }
        public string TrackId { get; private set; } = DefaultCue;
        /// <summary>Increments on every change a client must see (track, queue, lease).</summary>
        public int Revision { get; private set; }
        public long StartedMs { get; private set; }
        /// <summary>Who queued the playing track ("" for the default cue).</summary>
        public string SubmittedBy { get; private set; } = "";
        public string LeaseHolder { get; private set; } = "";
        public long LeaseUntilMs { get; private set; }
        public long LastUserChangeMs { get; private set; } = long.MinValue / 2;
        public IReadOnlyList<BoomboxRequest> Queue => queue;

        readonly List<BoomboxRequest> queue = new List<BoomboxRequest>();
        readonly Func<string, double> trackSeconds;

        /// <param name="trackSeconds">Full length of a cue in seconds (≤ 0 for an unknown cue); the default cue loops forever.</param>
        public BoomboxState(string roomId, Func<string, double> trackSeconds, long nowMs)
        {
            RoomId = roomId ?? "";
            this.trackSeconds = trackSeconds ?? (_ => 0);
            StartedMs = nowMs;
        }

        public double PositionSeconds(long nowMs)
        {
            double s = Math.Max(0, (nowMs - StartedMs) / 1000.0);
            if (TrackId == DefaultCue)
            {
                double len = trackSeconds(DefaultCue);
                return len > 0 ? s % len : s;
            }
            return s;
        }

        bool LeaseValid(long now) => LeaseHolder.Length > 0 && now < LeaseUntilMs;

        /// <summary>Takes or renews the control lease (the UI renews it while open and in range).</summary>
        public BoomboxStatus Acquire(string player, long now, bool inRange = true)
        {
            if (!inRange) return BoomboxStatus.OutOfRange;
            if (LeaseValid(now) && LeaseHolder != player) return BoomboxStatus.LeaseHeld;
            bool changed = LeaseHolder != player;
            LeaseHolder = player;
            LeaseUntilMs = now + LeaseMs;
            if (changed) Revision++;
            return BoomboxStatus.Ok;
        }

        /// <summary>Closed UI, out of range or a change of activity: gives the lease up (requests stay queued).</summary>
        public void Release(string player)
        {
            if (LeaseHolder != player) return;
            LeaseHolder = "";
            LeaseUntilMs = 0;
            Revision++;
        }

        /// <summary>Queues (or replaces) this person's one request.</summary>
        public BoomboxStatus Enqueue(string player, string trackId, bool owned, long now)
        {
            if (!LeaseValid(now) || LeaseHolder != player) return BoomboxStatus.NoLease;
            if (string.IsNullOrEmpty(trackId) || trackSeconds(trackId) <= 0) return BoomboxStatus.UnknownTrack;
            if (!owned) return BoomboxStatus.NotOwned;
            BoomboxRequest mine = queue.FirstOrDefault(r => r.PlayerId == player);
            if (mine == null && queue.Count >= MaxQueue) return BoomboxStatus.QueueFull;
            if (mine != null) mine.TrackId = trackId;
            else queue.Add(new BoomboxRequest { PlayerId = player, TrackId = trackId, SubmittedMs = now });
            LeaseUntilMs = now + LeaseMs;
            Revision++;
            // Nothing personal playing (the default bed): start it now if the room-wide interval allows.
            if (SubmittedBy.Length == 0 && now - LastUserChangeMs >= MinChangeIntervalMs) Advance(now, true);
            return BoomboxStatus.Ok;
        }

        /// <summary>Removes this person's pending request.</summary>
        public BoomboxStatus Withdraw(string player, long now)
        {
            int n = queue.RemoveAll(r => r.PlayerId == player);
            if (n == 0) return BoomboxStatus.NothingQueued;
            Revision++;
            return BoomboxStatus.Ok;
        }

        /// <summary>Skips to the next request (a user-triggered change: lease and the ten-second room interval apply).</summary>
        public BoomboxStatus Skip(string player, long now)
        {
            if (!LeaseValid(now) || LeaseHolder != player) return BoomboxStatus.NoLease;
            if (now - LastUserChangeMs < MinChangeIntervalMs) return BoomboxStatus.TooSoon;
            if (queue.Count == 0 && SubmittedBy.Length == 0) return BoomboxStatus.NothingQueued;
            LeaseUntilMs = now + LeaseMs;
            Advance(now, true);
            return BoomboxStatus.Ok;
        }

        /// <summary>Someone left the room: their lease ends and their unplayed requests go; a track already playing finishes.</summary>
        public void PlayerLeft(string player, long now)
        {
            bool changed = queue.RemoveAll(r => r.PlayerId == player) > 0;
            if (LeaseHolder == player)
            {
                LeaseHolder = "";
                LeaseUntilMs = 0;
                changed = true;
            }
            if (changed) Revision++;
        }

        /// <summary>Server tick: expires the lease and moves on when a queued track has played through.</summary>
        public void Tick(long now)
        {
            if (LeaseHolder.Length > 0 && now >= LeaseUntilMs)
            {
                LeaseHolder = "";
                LeaseUntilMs = 0;
                Revision++;
            }
            if (SubmittedBy.Length > 0)
            {
                double len = trackSeconds(TrackId);
                if (len > 0 && (now - StartedMs) / 1000.0 >= len) Advance(now, false);
            }
        }

        void Advance(long now, bool userTriggered)
        {
            if (queue.Count > 0)
            {
                BoomboxRequest next = queue[0];
                queue.RemoveAt(0);
                TrackId = next.TrackId;
                SubmittedBy = next.PlayerId;
            }
            else
            {
                TrackId = DefaultCue;
                SubmittedBy = "";
            }
            StartedMs = now;
            if (userTriggered) LastUserChangeMs = now;
            Revision++;
        }

        /// <summary>
        /// What a listener actually hears: the shared track, unless it is a protected boss cue they have not unlocked and
        /// they keep "Protect unreached boss music" on — then the neutral meet bed, locally. Hearing grants nothing.
        /// </summary>
        public static string AudibleFor(string trackId, Func<string, bool> listenerOwns, bool protectSpoilers) =>
            protectSpoilers && IsProtected(trackId) && !listenerOwns(trackId) ? DefaultCue : trackId;

        /// <summary>The title a listener may see: a protected cue they have not reached shows no rival name.</summary>
        public static string TitleFor(string trackId, string title, Func<string, bool> listenerOwns, bool protectSpoilers) =>
            protectSpoilers && IsProtected(trackId) && !listenerOwns(trackId) ? "Unreached encounter theme" : title;
    }
}
