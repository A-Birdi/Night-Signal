using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NightSignal.Front;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NightSignal.Meet
{
    /// <summary>One remote pose sample (server time).</summary>
    public struct MeetPoseSample
    {
        public double AtMs;
        public float X, Z, Yaw, Speed;
    }

    /// <summary>
    /// The online meet link over the control channel (docs/NETWORKING.md §3.7): joins a room, mirrors its authoritative state
    /// (<c>meet.state</c>) and buffers other people's poses (<c>meet.poses</c>) for interpolation, sends this player's pose at
    /// 10 Hz and applies the server's corrections, and carries emotes, quick chat, likes, invitations and boombox operations.
    /// Server time is estimated from the pushes so replicated emote start times and arrivals line up.
    /// </summary>
    public sealed class MeetNet : IDisposable
    {
        public readonly OnlineSession Session;
        public readonly string Kind, FriendAccountId, InstanceId;
        public string RoomId { get; private set; }
        public JObject State { get; private set; }
        public string LastError { get; private set; } = "";
        /// <summary>A correction from the server to apply to the local avatar (x, yaw, z), consumed by the session.</summary>
        public Vector3? Correction;
        public readonly Dictionary<string, List<MeetPoseSample>> Poses = new Dictionary<string, List<MeetPoseSample>>();
        public int CorrectionsReceived { get; private set; }
        public int PosesSent { get; private set; }

        double offsetMs = double.NaN;
        int seq;
        float nextPose;
        bool moveInFlight;

        public MeetNet(OnlineSession session, string kind, string friendAccountId = null, string instanceId = null)
        {
            Session = session;
            Kind = string.IsNullOrEmpty(kind) ? "public" : kind;
            FriendAccountId = friendAccountId;
            InstanceId = instanceId;
            Session.Client.MeetState += OnState;
            Session.Client.MeetPoses += OnPoses;
        }

        public string Me => Session.AccountId;
        static double LocalMs => Time.realtimeSinceStartupAsDouble * 1000.0;
        /// <summary>Server clock estimate (ms since the epoch).</summary>
        public double ServerNowMs => double.IsNaN(offsetMs) ? LocalMs : LocalMs + offsetMs;

        void Clock(JToken serverTime)
        {
            if (serverTime == null || serverTime.Type == JTokenType.Null) return;
            double o = (double)(long)serverTime - LocalMs;
            // The largest offset seen carries the least transit delay; let it relax slowly so clock drift is followed.
            offsetMs = double.IsNaN(offsetMs) ? o : Math.Max(o, offsetMs - 0.05);
        }

        public async Task<bool> Join()
        {
            try
            {
                object payload = new { kind = Kind, friendAccountId = FriendAccountId, instanceId = InstanceId };
                JToken r = await Session.Client.Request("meet.join", payload);
                RoomId = (string)r["roomId"];
                State = r["state"] as JObject;
                Clock(State?["serverTimeMs"]);
                return RoomId != null && State != null;
            }
            catch (Exception e)
            {
                LastError = e is Net.ControlError ce ? ce.Message : e.Message;
                return false;
            }
        }

        void OnState(JObject p)
        {
            if (p == null || (string)p["roomId"] != RoomId) return;
            Clock(p["serverTimeMs"]);
            if (State != null && (long?)p["revision"] < (long?)State["revision"] && (long?)p["serverTimeMs"] <= (long?)State["serverTimeMs"]) return;
            State = p;
        }

        void OnPoses(JObject p)
        {
            if (p == null || (string)p["roomId"] != RoomId) return;
            Clock(p["serverTimeMs"]);
            foreach (JToken pose in p["poses"] as JArray ?? new JArray())
            {
                string id = (string)pose["accountId"];
                if (id == null || id == Me) continue;
                if (!Poses.TryGetValue(id, out List<MeetPoseSample> list)) Poses[id] = list = new List<MeetPoseSample>();
                double at = (long)pose["poseMs"];
                if (list.Count > 0 && list[list.Count - 1].AtMs >= at) continue;
                list.Add(new MeetPoseSample { AtMs = at, X = (float)pose["x"], Z = (float)pose["z"], Yaw = (float)pose["yaw"], Speed = (float)pose["speed"] });
                if (list.Count > 40) list.RemoveAt(0);
            }
        }

        /// <summary>Interpolated pose of someone else about 150 ms in the past (bounded extrapolation past the last sample).</summary>
        public bool PoseOf(string account, out MeetPoseSample pose)
        {
            pose = default;
            if (!Poses.TryGetValue(account, out List<MeetPoseSample> list) || list.Count == 0) return false;
            double t = ServerNowMs - 150.0;
            if (t <= list[0].AtMs) { pose = list[0]; return true; }
            for (int i = 1; i < list.Count; i++)
            {
                if (t > list[i].AtMs) continue;
                MeetPoseSample a = list[i - 1], b = list[i];
                float u = (float)((t - a.AtMs) / Math.Max(1.0, b.AtMs - a.AtMs));
                pose = new MeetPoseSample
                {
                    AtMs = t, X = Mathf.Lerp(a.X, b.X, u), Z = Mathf.Lerp(a.Z, b.Z, u), Yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, u), Speed = Mathf.Lerp(a.Speed, b.Speed, u),
                };
                return true;
            }
            pose = list[list.Count - 1];
            // Idle or a gap: hold the last pose, and the legs stop once the data is old.
            if (t - pose.AtMs > 400.0) pose.Speed = 0f;
            return true;
        }

        /// <summary>This player's pose, at most 10 per second; the reply may correct it.</summary>
        public void SendPose(Vector3 position, float yaw, float speed)
        {
            if (RoomId == null || moveInFlight || Time.unscaledTime < nextPose) return;
            nextPose = Time.unscaledTime + 0.1f;
            _ = Move(position, yaw, speed, ++seq);
        }

        async Task Move(Vector3 p, float yaw, float speed, int s)
        {
            moveInFlight = true;
            try
            {
                PosesSent++;
                JToken r = await Session.Client.Request("meet.move", new { x = p.x, z = p.z, yaw, speed, seq = s });
                if ((string)r["status"] == "Corrected" && s == seq)
                {
                    Correction = new Vector3((float)r["x"], (float)r["yaw"], (float)r["z"]);
                    CorrectionsReceived++;
                }
            }
            catch (Exception) { }
            finally { moveInFlight = false; }
        }

        /// <summary>A request whose failure is recorded (LastError) rather than thrown.</summary>
        public async Task<JToken> Ask(string type, object payload = null)
        {
            try
            {
                JToken r = await Session.Client.Request(type, payload);
                LastError = "";
                return r;
            }
            catch (Exception e)
            {
                LastError = e is Net.ControlError ce ? ce.Message : e.Message;
                return null;
            }
        }

        public void Fire(string type, object payload = null) => _ = Ask(type, payload);

        public void Dispose()
        {
            Session.Client.MeetState -= OnState;
            Session.Client.MeetPoses -= OnPoses;
        }
    }
}
