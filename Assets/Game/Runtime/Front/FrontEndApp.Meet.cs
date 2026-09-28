using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Art;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Meet;
using NightSignal.Core.Profiles;
using NightSignal.Meet;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace NightSignal.Front
{
    public sealed partial class FrontEndApp
    {
        /// <summary>The meet session while one is open (tours drive it).</summary>
        public MeetSession ActiveMeet { get; private set; }

        /// <summary>Opens Cedar Lantern Terrace offline with the chosen car; back to <paramref name="returnTo"/> on leaving.</summary>
        public void StartOfflineMeet(LocalCarChoice chosen, UIScreen returnTo) => StartCoroutine(RunOfflineMeet(chosen, returnTo));

        IEnumerator RunOfflineMeet(LocalCarChoice chosen, UIScreen returnTo)
        {
            LocalSession s = LocalSession.Current;
            ContentLibrary lib = ContentLibrary.Load();
            if (s?.Profile == null || lib == null || chosen == null) yield break;
            LocalProfile profile = s.Profile;
            OwnedCar owned = chosen.Loaner ? null : profile.FindCar(chosen.InstanceId);
            CarDef def = s.Catalogue.Car(chosen.ModelId);
            CarAppearance appearance = AppearanceMapping.ForLivery(lib.Customization, chosen.ModelId, owned != null ? s.RaceLivery(owned.InstanceId) : "");
            Canvas.gameObject.SetActive(false);
            if (backdropCamera != null) backdropCamera.SetActive(false);
            AsyncOperation load = SceneManager.LoadSceneAsync("Meet", LoadSceneMode.Single);
            while (!load.isDone) yield return null;
            yield return null;
            var go = new GameObject("Meet");
            ActiveMeet = go.AddComponent<MeetSession>();
            ActiveMeet.CarId = chosen.ModelId;
            ActiveMeet.CarName = def.Name;
            ActiveMeet.Appearance = appearance;
            ActiveMeet.Paint = appearance != null ? appearance.Primary : new Color(0.8f, 0.12f, 0.12f);
            ActiveMeet.CarPi = owned != null ? s.AppliedPi(owned) : def.BasePI;
            ActiveMeet.TuneSummary = owned != null ? $"Applied Garage build (PI {ActiveMeet.CarPi})" : "Stock factory build";
            ActiveMeet.PlayerName = string.IsNullOrEmpty(profile.DisplayName) ? "You" : profile.DisplayName;
            ActiveMeet.OwnsCue = profile.HasCue;
            ActiveMeet.RecentSlips = Slips(profile);
            while (ActiveMeet != null && !ActiveMeet.ExitRequested) yield return null;
            if (ActiveMeet != null) Destroy(ActiveMeet.gameObject);
            ActiveMeet = null;
            Canvas.gameObject.SetActive(true);
            yield return LoadBackdrop();
            Router.Show(returnTo ?? OfflineHub, false);
        }

        /// <summary>The player's recent result slips for the timing board (their own records only — never anybody's wallet).</summary>
        static List<string> Slips(LocalProfile p)
        {
            var list = new List<string>();
            foreach (RecordAttempt a in p.Records.Attempts.OrderByDescending(x => x.LastAttemptUtc).Take(8))
            {
                RecordEntry best = p.Records.Best(a.Key);
                string value = best == null ? "no valid result yet" : FormatRecord(best.Value, a.Key.Metric);
                list.Add($"{a.Key.EventId} {a.Key.Format} · best {value} · {a.Finishes}/{a.Attempts} finished · {a.LastAttemptUtc:dd MMM}");
            }
            return list;
        }

        static string FormatRecord(long value, MetricKind metric)
        {
            if (metric == MetricKind.ElapsedTime || metric == MetricKind.BestLap)
            {
                TimeSpan t = TimeSpan.FromMilliseconds(value);
                return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds:000}";
            }
            return $"{value:N0} ({metric})";
        }

        /// <summary>
        /// Meet evidence run (<c>-nsMeetTour</c>): a fresh Local profile → Offline hub → the meet. Watches the arrival, walks and
        /// jogs, pushes into the perimeter and the garden, greets the host (who returns the wave and bow), plays all twelve
        /// emotes, reads a placard and the timing board, queues a boombox cue, sits in the car and revs, photo mode, rescue,
        /// then leaves. Screenshots and checks; exits 0 on PASS. Automation.
        /// </summary>
        IEnumerator MeetTour()
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine("Builds", "Screenshots", "meet-tour"));
            System.IO.Directory.CreateDirectory(dir);
            string profiles = System.IO.Path.Combine(dir, "profiles");
            if (System.IO.Directory.Exists(profiles)) System.IO.Directory.Delete(profiles, true);
            LocalSession.UseFolder(profiles);
            var failures = new List<string>();
            void Fail(string f) { failures.Add(f); Debug.Log("[NightSignal.MeetTour] FAIL " + f); }
            void Note(string n) => Debug.Log("[NightSignal.MeetTour] " + n);
            void Shot(string name) => ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
            bool Click(string name)
            {
                Button b = GameObject.Find(name)?.GetComponent<Button>();
                if (b == null || !b.interactable) { Fail("button not available: " + name); return false; }
                b.onClick.Invoke();
                return true;
            }

            yield return new WaitForSeconds(3f);
            Click("OfflinePlay");
            yield return new WaitForSeconds(1.2f);
            Click("NewProfile");
            yield return new WaitForSeconds(1.2f);
            GameObject.Find("ProfileName").GetComponent<TMPro.TMP_InputField>().text = "Meet Walker";
            Click("Create");
            yield return new WaitForSeconds(1.2f);
            Click("Meet");
            float until = Time.realtimeSinceStartup + 40f;
            while (ActiveMeet == null && Time.realtimeSinceStartup < until) yield return null;
            if (ActiveMeet == null) { Fail("the meet did not open"); Finish(); yield break; }
            MeetSession m = ActiveMeet;
            // Arrival: the presented drive takes about 3.5 s, then the avatar gets out beside the car.
            float t0 = Time.realtimeSinceStartup;
            yield return new WaitForSeconds(1.4f);
            Shot("01-arrival");
            while (!m.Ready && Time.realtimeSinceStartup - t0 < 15f) yield return null;
            float arrival = Time.realtimeSinceStartup - t0;
            Note($"arrival {arrival:F2} s, bay {m.PlayerBay + 1}");
            if (!m.Ready) { Fail("arrival never finished"); Finish(); yield break; }
            if (arrival > 5.5f) Fail($"arrival took {arrival:F1} s (target 3–4 s)");
            Vector3 p0 = m.Player.transform.position;
            if (!MeetLayout.Walkable(p0.x, p0.z, MeetLayout.AvatarRadius * 0.9f)) Fail($"avatar spawned at a non-walkable point {p0}");
            if (MeetLayout.Bays[m.PlayerBay].Footprint.Contains(p0.x, p0.z, 0.2f)) Fail("avatar spawned inside its car");
            yield return new WaitForSeconds(0.6f);
            Shot("02-out-of-car");

            // Walk toward the plaza, then jog.
            m.ScriptMove = _ => new Vector2(0f, 1f);
            m.Camera.Yaw = 90f;
            yield return new WaitForSeconds(3f);
            float walked = Vector3.Distance(p0, m.Player.transform.position);
            Note($"walked {walked:F1} m in 3 s at {m.Player.Speed:F2} m/s");
            if (walked < 3f) Fail($"walking moved only {walked:F1} m");
            m.ScriptJog = true;
            yield return new WaitForSeconds(2f);
            Note($"jog speed {m.Player.Speed:F2} m/s");
            if (m.Player.Speed < 3f) Fail($"jog speed {m.Player.Speed:F2} m/s");
            Shot("03-jog");
            m.ScriptJog = false;
            // The perimeter holds: walk west into the hedge line for a while.
            m.Player.Teleport(new Vector3(-62f, 0f, 36f), 270f);
            m.Camera.Yaw = 270f;
            yield return new WaitForSeconds(6f);
            float minX = m.Player.transform.position.x;
            Note($"pushed west: x {minX:F2}");
            if (minX < MeetLayout.WalkMinX - 0.05f) Fail($"walked through the west perimeter to x {minX:F2}");
            Shot("04-west-edge");
            // …and the garden island's edging.
            m.Player.Teleport(new Vector3(0f, 0f, -12f), 0f);
            m.Camera.Yaw = 0f;
            yield return new WaitForSeconds(3f);
            Vector3 g = m.Player.transform.position;
            if (MeetLayout.GardenIsland.Contains(g.x, g.z, -0.2f)) Fail($"walked onto the dry garden at {g}");
            m.ScriptMove = null;

            // The host: talk, read the emote help, wave and bow; he returns both.
            m.Player.Teleport(new Vector3(MeetLayout.HostSpot.X - 1.6f, 0f, MeetLayout.HostSpot.Z - 1.4f), 45f);
            m.Camera.Yaw = 45f;
            m.Camera.Recenter();
            yield return new WaitForSeconds(0.5f);
            m.Open("host");
            yield return new WaitForSeconds(0.8f);
            Shot("05-host-dialogue");
            if (!m.Hud.PanelOpen || !m.Hud.PanelBody.Contains("Meet Walker")) Fail("host greeting did not address the player");
            m.Hud.PanelButtons.FirstOrDefault(b => b.name == "MeetAction0")?.onClick.Invoke();
            yield return new WaitForSeconds(0.5f);
            if (!m.HostHelpRead) Fail("emote help not shown");
            m.ClosePanel();
            m.PlayEmote(Emote.Wave);
            yield return new WaitForSeconds(1.4f);
            Shot("06-wave-returned");
            yield return new WaitForSeconds(1.2f);
            m.PlayEmote(Emote.Bow);
            yield return new WaitForSeconds(2.4f);
            if (m.Log.Count(l => l.StartsWith("host returned")) < 2) Fail("the host did not return both greetings");
            if (!(m.HostWaved && m.HostBowed && m.HostHelpRead)) Fail("host lesson incomplete");

            // All twelve emotes.
            m.Player.Teleport(new Vector3(-4f, 0f, 20f), 180f);
            m.Camera.Yaw = 0f;
            m.Camera.Recenter();
            m.Camera.Yaw = 200f;
            int emoteShots = 0;
            foreach (Emote e in Emotes.Wheel)
            {
                m.PlayEmote(e);
                yield return new WaitForSeconds(Emotes.Duration(e) * 0.45f);
                if (!m.PlayerMotion.EmoteActive || m.PlayerMotion.Emote != e) Fail($"emote {e} not playing");
                if (e == Emote.Wave || e == Emote.Cheer || e == Emote.Admire || e == Emote.CameraPose) Shot($"07-emote-{++emoteShots}-{e}");
                yield return new WaitForSeconds(Emotes.Duration(e) * 0.6f);
            }
            // Quick chat bubble.
            m.Say("Nice car!");
            yield return new WaitForSeconds(0.4f);
            Shot("08-quick-chat");

            // Placard and timing board.
            m.Open("placard", "VIEW-E");
            yield return new WaitForSeconds(0.5f);
            if (!m.Hud.PanelOpen || !m.Hud.PanelTitle.Contains("East")) Fail("east viewpoint placard not shown");
            m.ClosePanel();
            m.Player.Teleport(new Vector3(MeetLayout.TimingBoard.X, 0f, MeetLayout.TimingBoard.Z - 2.2f), 0f);
            m.Camera.Recenter();
            yield return new WaitForSeconds(0.5f);
            m.Open("board");
            yield return new WaitForSeconds(0.5f);
            Shot("09-timing-board");
            if (!m.Hud.PanelBody.Contains("offline")) Fail("timing board missing the convoy line");
            m.ClosePanel();

            // Boombox: queue an owned cue; walking near plays it, the meet bed further out.
            m.Player.Teleport(new Vector3(MeetLayout.Boombox.X - 0.4f, 0f, MeetLayout.Boombox.Z - 1.4f), 0f);
            m.Camera.Recenter();
            yield return new WaitForSeconds(0.4f);
            m.Open("boombox");
            yield return new WaitForSeconds(0.6f);
            Shot("10-boombox");
            string owned = NightSignal.AudioSynth.MusicCueIds.All.FirstOrDefault(id => id != BoomboxState.DefaultCue && m.OwnsCue(id));
            if (owned == null) Fail("new profile owns no baseline cue besides the meet bed");
            else
            {
                m.Queue(owned);
                yield return new WaitForSeconds(0.5f);
                if (m.Boombox.TrackId != owned) Fail($"boombox did not start {owned} (now {m.Boombox.TrackId})");
                m.ClosePanel();
                yield return new WaitForSeconds(3f);
                string playing = GameAudio.MusicPlayer.Instance?.CurrentCue;
                Note($"near the boombox music is {playing}");
                if (GameAudio.MusicPlayer.Instance != null && playing != owned) Fail($"near the boombox the music is {playing}, not {owned}");
                if (m.Boombox.LeaseHolder.Length != 0) Fail("closing the boombox did not release the lease");
            }

            // Sit in the car, rev (rate-limited), get out.
            MeetBay bay = MeetLayout.Bays[m.PlayerBay];
            m.Player.Teleport(new Vector3(bay.X, 0f, bay.Z) + Quaternion.Euler(0f, bay.Yaw, 0f) * new Vector3(-2.3f, 0f, 0f), bay.Yaw);
            yield return new WaitForSeconds(0.3f);
            m.SitIn();
            yield return new WaitForSeconds(1f);
            bool rev1 = m.RevNow(), rev2 = m.RevNow();
            if (!rev1 || rev2) Fail($"rev rate limit wrong (first {rev1}, immediate second {rev2})");
            yield return new WaitForSeconds(0.8f);
            Shot("11-in-car");
            m.GetOutOfCar();
            yield return new WaitForSeconds(0.6f);
            Vector3 outAt = m.Player.transform.position;
            if (!MeetLayout.Walkable(outAt.x, outAt.z, MeetLayout.AvatarRadius * 0.9f)) Fail($"got out at a non-walkable point {outAt}");

            // Photo mode at the marker (the east bays against the mountains).
            m.Open("photo-marker");
            yield return new WaitForSeconds(1f);
            Shot("12-photo-marker");
            m.ExitPhoto();

            // Rescue from the far corner back to the car.
            m.Player.Teleport(new Vector3(40f, 0f, 44f), 0f);
            yield return new WaitForSeconds(0.3f);
            m.Rescue();
            yield return new WaitForSeconds(0.5f);
            Vector3 r = m.Player.transform.position;
            if (Vector3.Distance(r, new Vector3(bay.X, 0f, bay.Z)) > 5f) Fail($"rescue did not return beside the car ({r})");

            // Overview frames from the avatar's camera.
            m.Player.Teleport(new Vector3(-20f, 0f, -20f), 45f);
            m.Camera.Yaw = 45f;
            m.Camera.Distance = 6f;
            yield return new WaitForSeconds(0.8f);
            Shot("13-plaza");
            Note("fps sample: " + (1f / Mathf.Max(1e-4f, Time.smoothDeltaTime)).ToString("F0"));
            foreach (string l in m.Log) Note("log: " + l);
            m.Leave();
            until = Time.realtimeSinceStartup + 30f;
            while ((ActiveMeet != null || Router.Current != OfflineHub) && Time.realtimeSinceStartup < until) yield return null;
            if (Router.Current != OfflineHub) Fail("leaving the meet did not return to the Offline hub");
            yield return new WaitForSeconds(1f);
            Shot("14-back-in-hub");
            Finish();

            void Finish()
            {
                Note(failures.Count == 0 ? "PASS" : "FAILED: " + string.Join("; ", failures));
                Application.Quit(failures.Count == 0 ? 0 : 1);
            }
        }
    }
}
