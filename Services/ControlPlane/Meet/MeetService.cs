using System.Text.Json;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Control;
using NightSignal.ControlPlane.Convoys;
using NightSignal.ControlPlane.Garage;
using NightSignal.ControlPlane.Persistence;
using NightSignal.ControlPlane.Security;
using NightSignal.ControlPlane.Toys;
using NightSignal.Core.Content;
using NightSignal.Core.Meet;
using NightSignal.Core.Rules;
using NJ = Newtonsoft.Json.Linq;

namespace NightSignal.ControlPlane.Meet;

/// <summary>What the meet rooms need from content: cue lengths/titles for the shared boombox and the quick-chat phrase count.</summary>
public sealed class MeetContent
{
    public MusicCues Cues { get; init; } = new();
    public int QuickChatPhrases { get; init; }

    public static MeetContent FromContentDirectory(IOptions<ContentOptions> options, ILogger<MeetContent> log)
    {
        string authored = Path.Combine(PathResolver.Resolve(AppContext.BaseDirectory, options.Value.Directory), "authored");
        MusicCues cues = new();
        int phrases = 0;
        try
        {
            string cuesFile = Path.Combine(authored, "music.cues.json");
            if (File.Exists(cuesFile)) cues = MusicCues.Parse(File.ReadAllText(cuesFile));
            string textFile = Path.Combine(authored, "story", "meet.text.json");
            if (File.Exists(textFile)) phrases = (NJ.JObject.Parse(File.ReadAllText(textFile))["quickChat"] as NJ.JArray)?.Count ?? 0;
        }
        catch (Exception e) when (e is IOException or Newtonsoft.Json.JsonException)
        {
            log.LogError(e, "Meet content unreadable");
        }
        log.LogInformation("Meet content: {Cues} cues, {Phrases} quick-chat phrases", cues.All.Count(), phrases);
        return new MeetContent { Cues = cues, QuickChatPhrases = phrases };
    }
}

/// <summary>
/// Hosts Cedar Lantern Terrace rooms (spec §12) on the control channel: Join Public Meet (the fullest public room with a
/// free place, else a new one; never with someone either side has blocked), Join Friend's Meet (the room a friend is in:
/// friends only, a reserved bay or a free place), Convoy Meet (one room per convoy session, members together). Each room
/// is a Core <see cref="MeetRoom"/> — membership, the server-chosen bay and parking transform, arrivals/departures as keyed
/// events, pose validation, emote ID/start/duration, quick chat, likes, reservations and the shared boombox — under this
/// service's lock. <see cref="Pump"/> (≈10 Hz) ticks the rooms and pushes <c>meet.state</c> when a room changed and
/// <c>meet.poses</c> on the low-priority lane. Nothing here touches money, RP, unlocks, convoys or readiness.
/// </summary>
public sealed class MeetService(ConvoyDirectory directory, IPlayerStore store, ISocialStore social, GarageService garage, IToyNotifier notifier,
    IConvoyNotifier control, RateLimiter limiter, TimeProvider clock, MeetContent content, Content.MusicUnlockManifest music, Content.ContentService cars,
    ILogger<MeetService> log)
{
    public static readonly (int Limit, TimeSpan Window) MoveFlood = (30, TimeSpan.FromSeconds(1));
    public static readonly (int Limit, TimeSpan Window) ActionFlood = (20, TimeSpan.FromSeconds(10));
    public static readonly (int Limit, TimeSpan Window) JoinFlood = (10, TimeSpan.FromMinutes(1));
    static readonly TimeSpan EmptyRoomLifetime = TimeSpan.FromMinutes(2);

    sealed class Room
    {
        public required MeetRoom Core;
        public long PushedRevision = -1;
        public DateTimeOffset EmptySince = DateTimeOffset.MaxValue;
    }

    sealed class Visitor
    {
        public required string RoomId;
        public HashSet<string> OwnedCues = new(StringComparer.Ordinal);
        /// <summary>Accounts this visitor blocked or was blocked by (social actions and chat are filtered both ways).</summary>
        public HashSet<string> Blocked = new(StringComparer.Ordinal);
    }

    readonly object gate = new();
    readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);
    readonly Dictionary<string, Visitor> visitors = new(StringComparer.Ordinal);
    /// <summary>Touring progress per account while the service runs (the grant itself is stored once, forever).</summary>
    readonly Dictionary<string, MeetTouringProgress> touring = new(StringComparer.Ordinal);
    long roomCounter;

    long NowMs => clock.GetUtcNow().ToUnixTimeMilliseconds();

    sealed record JoinPayload(string? Kind, string? FriendAccountId, string? InstanceId);
    sealed record MovePayload(float X, float Z, float Yaw, float Speed, int Seq);
    sealed record EmotePayload(string? Emote);
    sealed record ChatPayload(int Index);
    sealed record AccountPayload(string? AccountId);
    sealed record TouringPayload(string? Step, string? Id);
    sealed record LeavePayload(string? Reason);
    sealed record BoomboxPayload(string? Op, string? TrackId);

    static T Read<T>(JsonElement payload) where T : class =>
        (payload.ValueKind == JsonValueKind.Object ? payload.Deserialize<T>(ControlConnection.Json) : JsonSerializer.Deserialize<T>("{}", ControlConnection.Json))
        ?? throw new JsonException();

    // ------------------------------------------------------------------ join and leave

    public async Task<ConvoyResult> JoinAsync(string account, JsonElement payload, CancellationToken ct)
    {
        if (!limiter.TryAcquire("meet-join/" + account, JoinFlood, out long retry))
            return ConvoyResult.Fail("rate_limited", "Too many meet joins.", retry);
        JoinPayload p = Read<JoinPayload>(payload);
        string kind = p.Kind ?? "public";
        if (kind is not ("public" or "friend" or "convoy"))
            return ConvoyResult.Fail("invalid_request", "kind must be public, friend or convoy.");

        // Everything asynchronous first (player, car, blocks), then one short decision under the lock.
        PlayerSnapshot me = await store.GetSnapshotAsync(account, ct);
        string name = me.Card?.DisplayName ?? "";
        if (name.Length == 0) return ConvoyResult.Fail("needs_card", "Create your player card before visiting the meet.");
        string? carId = null, livery = null, piClass = "", tune = "Stock";
        int pi = 0;
        if (!string.IsNullOrEmpty(p.InstanceId))
        {
            var car = await garage.MeetAppearanceAsync(account, p.InstanceId!, ct);
            if (car is null) return ConvoyResult.Fail("not_owned", "That car instance is not yours.");
            (carId, livery, pi, piClass, tune) = car.Value;
        }
        else if (me.Cars.Count > 0)
        {
            carId = me.Cars[0].CarId;
            pi = cars.Catalogue.Cars.FirstOrDefault(c => c.Id == carId)?.BasePI ?? 0;
            piClass = PerformanceIndex.ClassOf(pi).ToString();
        }
        if (carId is null) return ConvoyResult.Fail("needs_car", "Choose your starter car before visiting the meet.");
        FriendGraph graph = await social.GetFriendGraphAsync(account, ct);
        var blocked = new HashSet<string>(graph.Blocked.Select(b => b.AccountId), StringComparer.Ordinal);
        var friends = new HashSet<string>(graph.Friends.Select(f => f.AccountId), StringComparer.Ordinal);
        // People who blocked me: checked against everyone in the candidate rooms.
        List<string> present;
        lock (gate) present = visitors.Keys.Where(a => a != account).ToList();
        foreach (string other in present)
        {
            Relationship r = await social.GetRelationshipAsync(account, other, ct);
            if (r.BlockedMe) blocked.Add(other);
        }
        (string? convoySession, _) = directory.MembershipOf(account);

        lock (gate)
        {
            long now = NowMs;
            // Already somewhere: a rejoin (same room) or a move (leave the old room first).
            if (visitors.TryGetValue(account, out Visitor? existing) && rooms.TryGetValue(existing.RoomId, out Room? current))
            {
                MeetMember m = current.Core.Find(account)!;
                if (m is { State: MeetMemberState.Disconnected } || m is { State: MeetMemberState.Arriving or MeetMemberState.Present })
                {
                    bool wanted = kind switch
                    {
                        "convoy" => current.Core.Kind == MeetKind.Convoy && current.Core.ConvoyId == convoySession,
                        "friend" => p.FriendAccountId is { } f && current.Core.Find(f) is not null,
                        _ => current.Core.Kind == MeetKind.Public,
                    };
                    if (wanted)
                    {
                        MeetJoinStatus again = current.Core.Join(account, name, carId, livery ?? "", convoySession ?? "", now, out _);
                        existing.OwnedCues = Owned(me);
                        existing.Blocked = blocked;
                        return ConvoyResult.Success(new { roomId = current.Core.Id, status = again.ToString(), state = State(current, account, now) });
                    }
                }
                current.Core.Leave(account, false, now);
                visitors.Remove(account);
            }

            Room? room = null;
            switch (kind)
            {
                case "convoy":
                {
                    if (convoySession is null) return ConvoyResult.Fail("not_in_convoy", "Join a convoy first.");
                    string id = "convoy-" + convoySession;
                    if (!rooms.TryGetValue(id, out room))
                        rooms[id] = room = new Room { Core = new MeetRoom(id, MeetKind.Convoy, convoySession, content.Cues.Seconds, now) };
                    if (!room.Core.HasRoomFor(account, now))
                        return ConvoyResult.Fail("meet_full", $"Your convoy's meet is full ({Limits.MaxMeetHumans} people). Join a public meet instead, or wait for a place.");
                    break;
                }
                case "friend":
                {
                    string? friend = p.FriendAccountId;
                    if (friend is null || !friends.Contains(friend)) return ConvoyResult.Fail("not_friends", "You can join the meet of a friend only.");
                    if (blocked.Contains(friend)) return ConvoyResult.Fail("blocked", "That meet is not available.");
                    if (!visitors.TryGetValue(friend, out Visitor? fv) || !rooms.TryGetValue(fv.RoomId, out room))
                        return ConvoyResult.Fail("friend_not_at_meet", "Your friend is not at a meet right now.");
                    if (room.Core.Members.Any(m => blocked.Contains(m.AccountId)))
                        return ConvoyResult.Fail("blocked", "That meet is not available.");
                    if (!room.Core.HasRoomFor(account, now))
                        return ConvoyResult.Fail("meet_full", "Your friend's meet is full. Ask them for an invitation (it holds a place for 30 seconds), or try again shortly.");
                    break;
                }
                default:
                {
                    room = rooms.Values
                        .Where(r => r.Core.Kind == MeetKind.Public && r.Core.HasRoomFor(account, now) && !r.Core.Members.Any(m => blocked.Contains(m.AccountId)))
                        .OrderByDescending(r => r.Core.Headcount(now)).ThenBy(r => r.Core.Id, StringComparer.Ordinal).FirstOrDefault();
                    if (room is null)
                    {
                        string id = $"public-{++roomCounter:D4}";
                        rooms[id] = room = new Room { Core = new MeetRoom(id, MeetKind.Public, "", content.Cues.Seconds, now) };
                    }
                    break;
                }
            }
            MeetJoinStatus status = room.Core.Join(account, name, carId, livery ?? "", convoySession ?? "", now, out MeetMember? member);
            if (member is null) return ConvoyResult.Fail("meet_full", "That meet is full.");
            member.Pi = pi;
            member.PiClass = piClass ?? "";
            member.Tune = tune ?? "";
            member.Look = me.Card?.LookJson ?? "";
            visitors[account] = new Visitor { RoomId = room.Core.Id, OwnedCues = Owned(me), Blocked = blocked };
            room.EmptySince = DateTimeOffset.MaxValue;
            log.LogInformation("{Account} joined meet {Room} ({Kind}) in bay {Bay}: {Status}", account, room.Core.Id, kind, member.Bay + 1, status);
            return ConvoyResult.Success(new { roomId = room.Core.Id, status = status.ToString(), state = State(room, account, now) });
        }
    }

    HashSet<string> Owned(PlayerSnapshot me)
    {
        // Baseline cues are in every collection (the manifest's "baseline" source); granted cues come from the store.
        var owned = new HashSet<string>(me.Music.Select(m => m.CueId), StringComparer.Ordinal);
        foreach (string c in music.BaselineCues) owned.Add(c);
        return owned;
    }

    /// <summary>
    /// An explicit departure (the menu, the Garage, a race starting): the avatar and car fade and the bay is released.
    /// <c>reason: "race"</c> (the convoy's event allocated) is announced as "left to race".
    /// </summary>
    public ConvoyResult Leave(string account, JsonElement payload = default)
    {
        bool toRace = Read<LeavePayload>(payload)?.Reason == "race";
        lock (gate)
        {
            if (!visitors.TryGetValue(account, out Visitor? v)) return ConvoyResult.Success(new { left = false });
            if (rooms.TryGetValue(v.RoomId, out Room? room)) room.Core.Leave(account, false, NowMs, toRace);
            visitors.Remove(account);
            return ConvoyResult.Success(new { left = true });
        }
    }

    /// <summary>The control connection dropped: the room shows "disconnected" and holds the bay for the grace.</summary>
    public void Disconnected(string account)
    {
        lock (gate)
            if (visitors.TryGetValue(account, out Visitor? v) && rooms.TryGetValue(v.RoomId, out Room? room))
                room.Core.Leave(account, true, NowMs);
    }

    // ------------------------------------------------------------------ in the room

    (Room? Room, Visitor? Visitor) Where(string account) =>
        visitors.TryGetValue(account, out Visitor? v) && rooms.TryGetValue(v.RoomId, out Room? r) ? (r, v) : (null, null);

    ConvoyResult NotHere() => ConvoyResult.Fail("not_at_meet", "You are not at a meet.");

    public ConvoyResult Arrived(string account)
    {
        lock (gate)
        {
            (Room? room, _) = Where(account);
            if (room is null) return NotHere();
            bool done = room.Core.CompleteArrival(account, NowMs);
            MeetMember m = room.Core.Find(account)!;
            if (done) GrantLater(account, room.Core.Id, TouringLocked(account, m, TouringAct.Arrived, null));
            return ConvoyResult.Success(new { arrived = done, x = m.X, z = m.Z, yaw = m.Yaw });
        }
    }

    public ConvoyResult Move(string account, JsonElement payload)
    {
        if (!limiter.TryAcquire("meet-move/" + account, MoveFlood, out long retry)) return ConvoyResult.Fail("rate_limited", "Too many poses.", retry);
        MovePayload p = Read<MovePayload>(payload);
        lock (gate)
        {
            (Room? room, _) = Where(account);
            if (room is null) return NotHere();
            MeetMoveStatus s = room.Core.Move(account, p.X, p.Z, p.Yaw, p.Speed, p.Seq, NowMs);
            MeetMember m = room.Core.Find(account)!;
            // Corrections carry the authoritative pose; accepted moves only need the status.
            return s == MeetMoveStatus.Corrected
                ? ConvoyResult.Success(new { status = s.ToString(), x = m.X, z = m.Z, yaw = m.Yaw })
                : ConvoyResult.Success(new { status = s.ToString() });
        }
    }

    public ConvoyResult Emote(string account, JsonElement payload)
    {
        if (!limiter.TryAcquire("meet-act/" + account, ActionFlood, out long retry)) return ConvoyResult.Fail("rate_limited", "Slow down a little.", retry);
        EmotePayload p = Read<EmotePayload>(payload);
        if (!Emotes.TryParse(p.Emote, out Emote e)) return ConvoyResult.Fail("invalid_request", "Unknown emote.");
        lock (gate)
        {
            (Room? room, _) = Where(account);
            if (room is null) return NotHere();
            long now = NowMs;
            if (!room.Core.PlayEmote(account, e, now)) return ConvoyResult.Fail("emote_refused", "Not right now.");
            // A wave or a bow at the tutorial host counts toward CH63 (checked against the server-held position).
            if (e is Core.Meet.Emote.Wave or Core.Meet.Emote.Bow)
                GrantLater(account, room.Core.Id, TouringLocked(account, room.Core.Find(account)!, e == Core.Meet.Emote.Wave ? TouringAct.WaveAtHost : TouringAct.BowToHost, null));
            return ConvoyResult.Success(new { emote = e.ToString(), startMs = now, durationMs = (long)(Emotes.Duration(e) * 1000f) });
        }
    }

    public ConvoyResult Chat(string account, JsonElement payload)
    {
        if (!limiter.TryAcquire("meet-act/" + account, ActionFlood, out long retry)) return ConvoyResult.Fail("rate_limited", "Slow down a little.", retry);
        ChatPayload p = Read<ChatPayload>(payload);
        lock (gate)
        {
            (Room? room, _) = Where(account);
            if (room is null) return NotHere();
            return room.Core.Chat(account, p.Index, content.QuickChatPhrases, NowMs)
                ? ConvoyResult.Success(new { index = p.Index })
                : ConvoyResult.Fail("chat_refused", "Quick chat is limited to the predefined phrases, one every couple of seconds.");
        }
    }

    public ConvoyResult Like(string account, JsonElement payload)
    {
        string? to = Read<AccountPayload>(payload).AccountId;
        lock (gate)
        {
            (Room? room, Visitor? v) = Where(account);
            if (room is null || v is null) return NotHere();
            if (to is null || v.Blocked.Contains(to)) return ConvoyResult.Fail("not_available", "Not available.");
            if (visitors.TryGetValue(to, out Visitor? tv) && tv.Blocked.Contains(account)) return ConvoyResult.Fail("not_available", "Not available.");
            return room.Core.ToggleLike(account, to)
                ? ConvoyResult.Success(new { accountId = to, likes = room.Core.Find(to)!.Likes, cosmeticOnly = true })
                : ConvoyResult.Fail("not_available", "Not available.");
        }
    }

    // ------------------------------------------------------------------ touring challenges (CH61–CH65)

    /// <summary>
    /// A touring act the client reports (own car inspected, placard read, emote help read, a composed photo, the result
    /// slip read): accepted only where the server-held position says the visitor is, and the result slip only after a
    /// finished event. Completed challenges are granted (once, ledgered) and pushed as <c>meet.challenge</c>.
    /// </summary>
    public async Task<ConvoyResult> TouringAsync(string account, JsonElement payload, CancellationToken ct)
    {
        if (!limiter.TryAcquire("meet-act/" + account, ActionFlood, out long retry)) return ConvoyResult.Fail("rate_limited", "Slow down a little.", retry);
        TouringPayload p = Read<TouringPayload>(payload);
        if (!MeetTouring.TryParse(p.Step, out TouringAct act)) return ConvoyResult.Fail("invalid_request", "Unknown touring step.");
        bool eligible = act != TouringAct.ReadResultSlip || await store.HasFinishedEventAsync(account, ct);
        List<string> done;
        string roomId;
        lock (gate)
        {
            (Room? room, _) = Where(account);
            if (room is null) return NotHere();
            MeetMember m = room.Core.Find(account)!;
            if (m.State != MeetMemberState.Present) return ConvoyResult.Fail("not_yet", "Finish arriving first.");
            if (!MeetTouring.InPlace(act, p.Id, m.Bay, m.X, m.Z)) return ConvoyResult.Fail("not_here", "That has to happen where it is — walk over first.");
            if (!eligible) return ConvoyResult.Fail("no_event", "Finish an event first, then come back and read your slip.");
            done = TouringLocked(account, m, act, p.Id);
            roomId = room.Core.Id;
        }
        var granted = new List<object>();
        foreach (string id in done)
            if (await GrantAsync(account, roomId, id, ct) is { } g) granted.Add(g);
        return ConvoyResult.Success(new { recorded = act.ToString(), completed = granted });
    }

    List<string> TouringLocked(string account, MeetMember m, TouringAct act, string? id)
    {
        if (!MeetTouring.InPlace(act, id, m.Bay, m.X, m.Z)) return new List<string>();
        if (!touring.TryGetValue(account, out MeetTouringProgress? progress)) touring[account] = progress = new MeetTouringProgress();
        return MeetTouring.Record(progress, act, id);
    }

    void GrantLater(string account, string roomId, List<string> ids)
    {
        if (ids.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (string id in ids) await GrantAsync(account, roomId, id, CancellationToken.None);
        });
    }

    async Task<object?> GrantAsync(string account, string roomId, string challengeId, CancellationToken ct)
    {
        try
        {
            ChallengeDef ch = cars.Catalogue.Challenge(challengeId);
            ChallengeTier tier = Content.ContentService.ParseTier(ch.Tier);
            var grant = new ChallengeGrant(ch.Id, tier, RankPoints.ChallengeCash(tier), RankPoints.ForChallenge(tier), ch.Reward);
            ChallengeGrantResult r = await store.GrantChallengeAsync(account, grant, "meet:" + roomId, ct);
            if (!r.Granted) return null; // completed before: no repeat reward
            var wire = new { challengeId = ch.Id, name = ch.Name, tier = ch.Tier, cash = r.Credited, rankPoints = grant.RankPoints, cosmeticId = ch.Reward, balance = r.Balance };
            control.Send(account, "meet.challenge", 0, wire);
            log.LogInformation("{Account} completed {Challenge} at meet {Room} (+{Cash} cr)", account, ch.Id, roomId, r.Credited);
            return wire;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Touring grant {Challenge} for {Account} failed", challengeId, account);
            return null;
        }
    }

    /// <summary>Holds a bay for 30 s for a friend and tells them (a meet.invited push); the reservation shows on everyone's screen.</summary>
    public async Task<ConvoyResult> InviteAsync(string account, JsonElement payload, CancellationToken ct)
    {
        string? friend = Read<AccountPayload>(payload).AccountId;
        if (friend is null) return ConvoyResult.Fail("invalid_request", "accountId is required.");
        Relationship r = await social.GetRelationshipAsync(account, friend, ct);
        if (r.State != FriendState.Friends || r.BlockedEitherWay) return ConvoyResult.Fail("not_friends", "You can invite friends only.");
        string inviterName;
        lock (gate)
        {
            (Room? room, _) = Where(account);
            if (room is null) return NotHere();
            long now = NowMs;
            if (!room.Core.Reserve(account, friend, now)) return ConvoyResult.Fail("meet_full", "No place to hold for your friend right now.");
            inviterName = room.Core.Find(account)!.DisplayName;
            var res = room.Core.Reservations[friend];
            control.Send(friend, "meet.invited", 0, new { roomId = room.Core.Id, fromAccountId = account, fromName = inviterName, untilMs = res.UntilMs, bay = res.Bay + 1 });
            return ConvoyResult.Success(new { accountId = friend, bay = res.Bay + 1, untilMs = res.UntilMs });
        }
    }

    public ConvoyResult Boombox(string account, JsonElement payload)
    {
        if (!limiter.TryAcquire("meet-act/" + account, ActionFlood, out long retry)) return ConvoyResult.Fail("rate_limited", "Slow down a little.", retry);
        BoomboxPayload p = Read<BoomboxPayload>(payload);
        lock (gate)
        {
            (Room? room, Visitor? v) = Where(account);
            if (room is null || v is null) return NotHere();
            long now = NowMs;
            MeetMember me = room.Core.Find(account)!;
            bool inRange = Math.Sqrt(Math.Pow(me.X - MeetLayout.Boombox.X, 2) + Math.Pow(me.Z - MeetLayout.Boombox.Z, 2)) < 3.2;
            BoomboxState b = room.Core.Boombox;
            BoomboxStatus s = p.Op switch
            {
                "acquire" => b.Acquire(account, now, inRange),
                "release" => Release(b, account),
                "queue" => inRange ? b.Enqueue(account, p.TrackId ?? "", v.OwnedCues.Contains(p.TrackId ?? ""), now) : BoomboxStatus.OutOfRange,
                "withdraw" => b.Withdraw(account, now),
                "skip" => inRange ? b.Skip(account, now) : BoomboxStatus.OutOfRange,
                _ => BoomboxStatus.UnknownTrack,
            };
            return s == BoomboxStatus.Ok
                ? ConvoyResult.Success(new { status = s.ToString(), boombox = BoomboxWire(b) })
                : new ConvoyResult(new ConvoyError("boombox_" + s.ToString().ToLowerInvariant(), s.ToString()), new { status = s.ToString(), boombox = BoomboxWire(b) });
        }
    }

    static BoomboxStatus Release(BoomboxState b, string account)
    {
        b.Release(account);
        return BoomboxStatus.Ok;
    }

    public ConvoyResult Snapshot(string account)
    {
        lock (gate)
        {
            (Room? room, _) = Where(account);
            return room is null ? NotHere() : ConvoyResult.Success(State(room, account, NowMs));
        }
    }

    // ------------------------------------------------------------------ pushes

    /// <summary>One tick: rooms advance, changed rooms push their state, every room pushes poses (coalesced, low priority).</summary>
    public int Pump()
    {
        var outgoing = new List<(string Account, string Key, string Type, object Payload, bool Low)>();
        lock (gate)
        {
            long now = NowMs;
            foreach (Room room in rooms.Values.ToList())
            {
                room.Core.Tick(now);
                // Visitors whose member record is gone (left, or past the disconnect grace) are no longer here.
                foreach (var gone in visitors.Where(v => v.Value.RoomId == room.Core.Id && room.Core.Find(v.Key) is null).Select(v => v.Key).ToList())
                    visitors.Remove(gone);
                List<string> audience = room.Core.Members.Where(m => m.State != MeetMemberState.Disconnected).Select(m => m.AccountId).ToList();
                if (room.Core.Revision != room.PushedRevision)
                {
                    room.PushedRevision = room.Core.Revision;
                    foreach (string a in audience) outgoing.Add((a, "meet.state", "meet.state", State(room, a, now), false));
                }
                if (audience.Count > 1)
                {
                    object poses = new
                    {
                        roomId = room.Core.Id, serverTimeMs = now,
                        poses = room.Core.Members.Where(m => m.State == MeetMemberState.Present)
                            .Select(m => new { accountId = m.AccountId, x = m.X, z = m.Z, yaw = m.Yaw, speed = m.Speed, poseMs = m.PoseMs }).ToList(),
                    };
                    foreach (string a in audience) outgoing.Add((a, "meet.poses", "meet.poses", poses, true));
                }
                if (room.Core.Empty(now))
                {
                    if (room.EmptySince == DateTimeOffset.MaxValue) room.EmptySince = clock.GetUtcNow();
                    else if (clock.GetUtcNow() - room.EmptySince > EmptyRoomLifetime) rooms.Remove(room.Core.Id);
                }
            }
        }
        foreach (var m in outgoing)
        {
            if (m.Low) notifier.SendLowPriority(m.Account, m.Key, m.Type, 0, m.Payload);
            else control.Send(m.Account, m.Type, 0, m.Payload);
        }
        return outgoing.Count;
    }

    object State(Room room, string viewer, long now)
    {
        MeetRoom r = room.Core;
        visitors.TryGetValue(viewer, out Visitor? v);
        bool Hidden(string other) => v is not null && (v.Blocked.Contains(other) || visitors.TryGetValue(other, out Visitor? ov) && ov.Blocked.Contains(viewer));
        return new
        {
            roomId = r.Id, kind = r.Kind == MeetKind.Convoy ? "convoy" : "public", revision = r.Revision, serverTimeMs = now, capacity = Limits.MaxMeetHumans,
            you = viewer,
            members = r.Members.Select(m => new
            {
                accountId = m.AccountId, displayName = m.DisplayName, carId = m.CarId, livery = m.Livery.Length == 0 ? null : m.Livery,
                bay = m.Bay + 1, state = m.State.ToString().ToLowerInvariant(), stateSinceMs = m.StateSinceMs,
                x = m.X, z = m.Z, yaw = m.Yaw, speed = m.Speed, poseMs = m.PoseMs,
                emote = m.EmoteActive(now) ? m.Emote.ToString() : null, emoteStartMs = m.EmoteStartMs,
                chat = m.ChatIndex >= 0 && now - m.ChatMs < 4000 && !Hidden(m.AccountId) ? new { index = m.ChatIndex, atMs = m.ChatMs } : null,
                likes = m.Likes, likedByYou = m.LikedBy.Contains(viewer), blocked = Hidden(m.AccountId),
                pi = m.Pi, piClass = m.PiClass, tune = m.Tune,
                look = LookWire(m.Look),
            }).ToList(),
            reservations = r.Reservations.Where(x => x.Value.UntilMs > now)
                .Select(x => new { bay = x.Value.Bay + 1, untilMs = x.Value.UntilMs, forYou = x.Key == viewer }).ToList(),
            events = r.Events.Select(e => new { seq = e.Seq, key = e.Key, kind = e.Kind.ToString().ToLowerInvariant(), accountId = e.AccountId, name = e.Name, atMs = e.AtMs }).ToList(),
            boombox = BoomboxWire(r.Boombox),
        };
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonElement> looks = new();

    /// <summary>A member's stored look as a JSON value (parsed once per distinct look), or null for the default look.</summary>
    static JsonElement? LookWire(string look)
    {
        if (string.IsNullOrEmpty(look)) return null;
        if (looks.Count > 4096) looks.Clear();
        return looks.GetOrAdd(look, l => JsonDocument.Parse(l).RootElement.Clone());
    }

    static object BoomboxWire(BoomboxState b) => new
    {
        trackId = b.TrackId, startedMs = b.StartedMs, submittedBy = b.SubmittedBy.Length == 0 ? null : b.SubmittedBy, revision = b.Revision,
        leaseHolder = b.LeaseHolder.Length == 0 ? null : b.LeaseHolder, leaseUntilMs = b.LeaseUntilMs,
        queue = b.Queue.Select(q => new { accountId = q.PlayerId, trackId = q.TrackId }).ToList(),
    };
}

/// <summary>Ticks the meet rooms and pushes their state (≈10 Hz).</summary>
internal sealed class MeetPump(MeetService meets, ILogger<MeetPump> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { meets.Pump(); }
            catch (Exception e) { log.LogError(e, "Meet pump failed"); }
        }
    }
}
