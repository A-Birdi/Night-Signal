using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;

namespace NightSignal.Core.Meet
{
    public enum MeetKind { Public = 0, Convoy = 1 }

    public enum MeetMemberState { Arriving = 0, Present = 1, Leaving = 2, Disconnected = 3 }

    public enum MeetJoinStatus { Ok = 0, Rejoined = 1, Full = 2, AlreadyHere = 3, Closed = 4, NotAllowed = 5 }

    public enum MeetMoveStatus { Accepted = 0, Corrected = 1, Ignored = 2 }

    /// <summary>One person at the meet (server state; clients render it).</summary>
    public sealed class MeetMember
    {
        public string AccountId = "", DisplayName = "", CarId = "", Livery = "", ConvoyId = "";
        public int Bay = -1;
        public MeetMemberState State;
        /// <summary>Increments on every fresh join (not on a rejoin within the grace): keys the arrival/departure notices.</summary>
        public long Generation;
        public long JoinedMs, StateSinceMs;
        public float X, Z, Yaw, Speed;
        public long PoseMs;
        public int PoseSeq;
        public Emote Emote;
        public long EmoteStartMs;
        public long LastEmoteMs = long.MinValue / 2;
        public int ChatIndex = -1;
        public long ChatMs = long.MinValue / 2;
        public int Likes;
        /// <summary>What others may inspect: the applied build's legal PI and class and a one-line tune summary.</summary>
        public int Pi;
        public string PiClass = "", Tune = "";
        /// <summary>The driver's look from their Player Card (canonical JSON, Characters.PlayerLooks); "" = the default look.</summary>
        public string Look = "";
        public readonly HashSet<string> LikedBy = new HashSet<string>();

        public bool EmoteActive(long now) => Emote != Emote.None && now - EmoteStartMs < (long)(Emotes.Duration(Emote) * 1000f);
    }

    /// <summary>A keyed room event (arrival, departure, disconnect): shown once per key on every client.</summary>
    public sealed class MeetEvent
    {
        public long Seq;
        public string Key = "";
        public NoticeKind Kind;
        public string AccountId = "", Name = "";
        public long AtMs;
    }

    /// <summary>
    /// One meet instance's authoritative state (spec §12; D02: at most six humans): membership, bays and the final parking
    /// transform (the layout's bay — never a client's choice), arrivals and departures as keyed events, avatar poses
    /// accepted inside the enclosure and a walking speed envelope, emotes as ID + start time with a bounded duration,
    /// quick-chat phrase indices, cosmetic likes, friend bay reservations (30 s) and the shared boombox. Engine-free and
    /// deterministic in the supplied clock: the control plane hosts it; the rules tests drive it directly.
    /// </summary>
    public sealed class MeetRoom
    {
        public const long ArrivalMs = (long)(MeetLayout.ArrivalSeconds * 1000f), ArrivalGraceMs = ArrivalMs + 2500, LeaveFadeMs = 500,
            DisconnectGraceMs = 30_000, EmoteGapMs = 400, ChatGapMs = 1500;
        /// <summary>Jog speed plus margin for timing jitter (m/s); poses further than this imply are corrected.</summary>
        public const float MaxSpeed = 4.6f;
        public const int MaxEvents = 32;

        public string Id { get; }
        public MeetKind Kind { get; }
        public string ConvoyId { get; }
        /// <summary>Changes a client must see: the room's own and the boombox's (its controls change it directly).</summary>
        public long Revision => ownRevision + Boombox.Revision;
        public BoomboxState Boombox { get; }
        public IReadOnlyList<MeetMember> Members => members;
        public IReadOnlyList<MeetEvent> Events => events;
        public IReadOnlyDictionary<string, (int Bay, long UntilMs, string Inviter)> Reservations => reservations;

        readonly List<MeetMember> members = new List<MeetMember>();
        readonly List<MeetEvent> events = new List<MeetEvent>();
        readonly Dictionary<string, (int Bay, long UntilMs, string Inviter)> reservations = new Dictionary<string, (int, long, string)>();
        readonly Dictionary<string, long> generations = new Dictionary<string, long>();
        long eventSeq, ownRevision;

        public MeetRoom(string id, MeetKind kind, string convoyId, Func<string, double> cueSeconds, long nowMs)
        {
            Id = id ?? "";
            Kind = kind;
            ConvoyId = convoyId ?? "";
            Boombox = new BoomboxState(Id, cueSeconds, nowMs);
        }

        public MeetMember Find(string account) => members.FirstOrDefault(m => m.AccountId == account);

        /// <summary>Humans counted against the cap: everyone not already leaving, plus live reservations for people not yet here.</summary>
        public int Headcount(long now) =>
            members.Count(m => m.State != MeetMemberState.Leaving) + reservations.Count(r => r.Value.UntilMs > now && Find(r.Key) == null);

        public bool HasRoomFor(string account, long now)
        {
            MeetMember m = Find(account);
            if (m != null && m.State != MeetMemberState.Leaving) return true; // already has a place (a leaver does not)
            return reservations.TryGetValue(account, out var r) && r.UntilMs > now || Headcount(now) < Limits.MaxMeetHumans;
        }

        HashSet<int> Taken(string except)
        {
            var taken = new HashSet<int>(MeetLayout.AmbienceBays);
            foreach (MeetMember m in members) if (m.AccountId != except && m.Bay >= 0) taken.Add(m.Bay);
            foreach (var r in reservations) if (r.Key != except) taken.Add(r.Value.Bay);
            return taken;
        }

        void Bump() => ownRevision++;

        void Post(NoticeKind kind, string key, MeetMember m, long now)
        {
            if (events.Any(e => e.Key == key)) return;
            events.Add(new MeetEvent { Seq = ++eventSeq, Key = key, Kind = kind, AccountId = m.AccountId, Name = m.DisplayName, AtMs = now });
            if (events.Count > MaxEvents) events.RemoveAt(0);
        }

        /// <summary>
        /// Admits a person with their car. A disconnected member returning within the grace resumes in the same bay with no
        /// new arrival; otherwise the server allocates the bay (their reservation, else one next to a convoy mate, else the
        /// first free) and the arrival drive begins.
        /// </summary>
        public MeetJoinStatus Join(string account, string displayName, string carId, string livery, string convoyId, long now, out MeetMember member)
        {
            member = Find(account);
            if (member != null)
            {
                if (member.State == MeetMemberState.Disconnected)
                {
                    member.State = member.PoseMs > 0 ? MeetMemberState.Present : MeetMemberState.Arriving;
                    member.StateSinceMs = now;
                    Bump();
                    return MeetJoinStatus.Rejoined;
                }
                if (member.State == MeetMemberState.Leaving) members.Remove(member);
                else return MeetJoinStatus.AlreadyHere;
            }
            if (!HasRoomFor(account, now)) { member = null; return MeetJoinStatus.Full; }
            int bay;
            if (reservations.TryGetValue(account, out var res) && res.UntilMs > now) bay = res.Bay;
            else
            {
                int near = -1;
                if (!string.IsNullOrEmpty(convoyId))
                {
                    MeetMember mate = members.FirstOrDefault(m => m.ConvoyId == convoyId && m.Bay >= 0 && m.State != MeetMemberState.Leaving);
                    if (mate != null) near = mate.Bay;
                }
                bay = MeetLayout.AllocateBay(Taken(account), near);
            }
            reservations.Remove(account);
            if (bay < 0) { member = null; return MeetJoinStatus.Full; }
            generations.TryGetValue(account, out long gen);
            generations[account] = ++gen;
            MeetBay b = MeetLayout.Bays[bay];
            MeetPoint door = MeetLayout.DoorPoint(bay);
            member = new MeetMember
            {
                AccountId = account, DisplayName = displayName ?? "", CarId = carId ?? "", Livery = livery ?? "", ConvoyId = convoyId ?? "",
                Bay = bay, State = MeetMemberState.Arriving, Generation = gen, JoinedMs = now, StateSinceMs = now,
                X = door.X, Z = door.Z, Yaw = b.Yaw,
            };
            members.Add(member);
            Bump();
            return MeetJoinStatus.Ok;
        }

        /// <summary>The arrival drive ended (the client says so, or the grace passed): the avatar stands at a validated free point.</summary>
        public bool CompleteArrival(string account, long now)
        {
            MeetMember m = Find(account);
            if (m == null || m.State != MeetMemberState.Arriving) return false;
            var others = members.Where(o => o != m && o.State == MeetMemberState.Present).Select(o => new MeetPoint(o.X, o.Z)).ToList();
            MeetLayout.TryFreeSpot(m.Bay, Occupied(), others, out MeetPoint spot);
            m.X = spot.X;
            m.Z = spot.Z;
            m.Yaw = MeetLayout.Bays[m.Bay].Yaw;
            m.PoseMs = now;
            m.State = MeetMemberState.Present;
            m.StateSinceMs = now;
            Post(NoticeKind.Arrived, $"arrived:{account}:{m.Generation}", m, now);
            Bump();
            return true;
        }

        /// <summary>Bays with a car parked in them (members' cars and the ambience display cars).</summary>
        public HashSet<int> Occupied()
        {
            var set = new HashSet<int>(MeetLayout.AmbienceBays);
            foreach (MeetMember m in members) if (m.Bay >= 0) set.Add(m.Bay);
            return set;
        }

        /// <summary>
        /// A walking pose from the owner. Accepted when newer, inside the walkable area (fixtures and parked cars excluded) and
        /// within the speed envelope from the last accepted pose; otherwise the last good pose stands (the client is corrected).
        /// </summary>
        public MeetMoveStatus Move(string account, float x, float z, float yaw, float speed, int seq, long now)
        {
            MeetMember m = Find(account);
            if (m == null || m.State != MeetMemberState.Present) return MeetMoveStatus.Ignored;
            if (seq <= m.PoseSeq) return MeetMoveStatus.Ignored;
            m.PoseSeq = seq;
            if (float.IsNaN(x) || float.IsNaN(z) || float.IsInfinity(x) || float.IsInfinity(z)) return MeetMoveStatus.Corrected;
            float dt = Math.Min(1.5f, Math.Max(0.05f, (now - m.PoseMs) / 1000f));
            float dx = x - m.X, dz = z - m.Z;
            bool tooFar = Math.Sqrt(dx * dx + dz * dz) > MaxSpeed * dt + 0.6f;
            bool blocked = !MeetLayout.Walkable(x, z, MeetLayout.AvatarRadius * 0.75f, Occupied());
            if (tooFar || blocked) return MeetMoveStatus.Corrected;
            m.X = x;
            m.Z = z;
            m.Yaw = yaw;
            m.Speed = Math.Min(Math.Max(0f, speed), MaxSpeed);
            m.PoseMs = now;
            return MeetMoveStatus.Accepted;
        }

        /// <summary>Starts an emote now (replicated as ID + start; clients animate locally).</summary>
        public bool PlayEmote(string account, Emote e, long now)
        {
            MeetMember m = Find(account);
            if (m == null || m.State != MeetMemberState.Present || e == Emote.None || now - m.LastEmoteMs < EmoteGapMs) return false;
            m.Emote = e;
            m.EmoteStartMs = now;
            m.LastEmoteMs = now;
            Bump();
            return true;
        }

        public bool Chat(string account, int phraseIndex, int phraseCount, long now)
        {
            MeetMember m = Find(account);
            if (m == null || m.State != MeetMemberState.Present || phraseIndex < 0 || phraseIndex >= phraseCount || now - m.ChatMs < ChatGapMs) return false;
            m.ChatIndex = phraseIndex;
            m.ChatMs = now;
            Bump();
            return true;
        }

        /// <summary>A cosmetic like for someone's car (no currency, no RP); one per person, toggled; never your own.</summary>
        public bool ToggleLike(string from, string to)
        {
            MeetMember t = Find(to);
            if (t == null || from == to || Find(from) == null) return false;
            if (!t.LikedBy.Add(from)) t.LikedBy.Remove(from);
            t.Likes = t.LikedBy.Count;
            Bump();
            return true;
        }

        /// <summary>Holds a bay for an invited friend for 30 s (visible to everyone as a reserved bay).</summary>
        public bool Reserve(string inviter, string friend, long now)
        {
            if (Find(inviter) == null || Find(friend) != null) return false;
            if (reservations.TryGetValue(friend, out var r) && r.UntilMs > now) return true;
            if (Headcount(now) >= Limits.MaxMeetHumans) return false;
            MeetMember host = Find(inviter);
            int bay = MeetLayout.AllocateBay(Taken(friend), host.Bay);
            if (bay < 0) return false;
            reservations[friend] = (bay, now + Limits.FriendMeetReservationMs, inviter);
            Bump();
            return true;
        }

        /// <summary>
        /// Leaving: an explicit departure fades out over half a second and then frees the bay ("left"); a lost connection
        /// keeps the avatar and bay for the grace ("disconnected") so a quick return resumes without new notices. A departure
        /// because the convoy's event allocated is worded "left to race" (spec §12), never for a lost connection.
        /// </summary>
        public void Leave(string account, bool disconnected, long now, bool toRace = false)
        {
            MeetMember m = Find(account);
            if (m == null || m.State == MeetMemberState.Leaving) return;
            if (disconnected)
            {
                if (m.State == MeetMemberState.Disconnected) return;
                m.State = MeetMemberState.Disconnected;
                m.StateSinceMs = now;
                Boombox.Release(account);
                Post(NoticeKind.Disconnected, $"disconnected:{account}:{m.Generation}", m, now);
            }
            else
            {
                m.State = MeetMemberState.Leaving;
                m.StateSinceMs = now;
                Boombox.PlayerLeft(account, now);
                Post(toRace ? NoticeKind.LeftToRace : NoticeKind.Departed, $"left:{account}:{m.Generation}", m, now);
            }
            Bump();
        }

        public void Tick(long now)
        {
            bool changed = false;
            foreach (var r in reservations.Where(r => r.Value.UntilMs <= now).Select(r => r.Key).ToList())
            {
                reservations.Remove(r);
                changed = true;
            }
            for (int i = members.Count - 1; i >= 0; i--)
            {
                MeetMember m = members[i];
                if (m.State == MeetMemberState.Arriving && now - m.StateSinceMs >= ArrivalGraceMs) CompleteArrival(m.AccountId, now);
                else if (m.State == MeetMemberState.Leaving && now - m.StateSinceMs >= LeaveFadeMs)
                {
                    members.RemoveAt(i);
                    changed = true;
                }
                else if (m.State == MeetMemberState.Disconnected && now - m.StateSinceMs >= DisconnectGraceMs)
                {
                    // The grace ran out: the bay is released quietly (the disconnect was already announced once).
                    Boombox.PlayerLeft(m.AccountId, now);
                    members.RemoveAt(i);
                    changed = true;
                }
                if (m.Emote != Emote.None && !m.EmoteActive(now)) { m.Emote = Emote.None; changed = true; }
            }
            Boombox.Tick(now);
            if (changed) Bump();
        }

        public bool Empty(long now) => members.Count == 0 && reservations.All(r => r.Value.UntilMs <= now);
    }
}
