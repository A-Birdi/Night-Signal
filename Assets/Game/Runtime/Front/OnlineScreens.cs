using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Online convoy (Addendum 01 §6–7, Addendum 02 §7): find/create/join a convoy, the roster with readiness, the
    /// leader's Intent → everyone's Mode Ready → Enter Mode → event proposal → everyone's Event Ready → Start, and the
    /// post-event Continue / Service Break decision. Every value on screen is the server's convoy snapshot; buttons
    /// only send requests, the server decides.
    /// </summary>
    public sealed class ConvoyScreen : UIScreen
    {
        public override string ScreenName => "Convoy";
        public override string MusicCue => "MUS_MENU_B";

        static readonly (string Label, object Intent, string Kind, string Mode, string Submode)[] Intents =
        {
            ("Campaign · Normal", new { kind = "campaign", mode = "normal" }, "campaign", "normal", null),
            ("Campaign · Hard", new { kind = "campaign", mode = "hard" }, "campaign", "hard", null),
            ("Freeplay · Sprint", new { kind = "freeplay", submode = "sprint" }, "freeplay", null, "sprint"),
            ("Freeplay · Circuit", new { kind = "freeplay", submode = "circuit" }, "freeplay", null, "circuit"),
            ("Freeplay · Time Attack", new { kind = "freeplay", submode = "time-attack" }, "freeplay", null, "time-attack"),
            ("Challenges · Team or Challenge Trial", new { kind = "challenges" }, "challenges", null, null),
            ("Freeplay · Drift Attack", new { kind = "freeplay", submode = "drift-attack" }, "freeplay", null, "drift-attack"),
            ("Freeplay · Custom Cup", new { kind = "freeplay", submode = "cup" }, "freeplay", null, "cup"),
        };

        TextMeshProUGUI heading, status, error, rosterText, lastResult, intentLine, proposalLine, postLine, inviteLine;
        Button create, createPrivate, joinCode, refresh, rejoin, notNow, chooseStarter, routeChart;
        Button proposeIntent, modeReady, enterMode, proposeEvent, eventReady, tuneLoaner, start, cont, serviceBreak, advance, invite, leave, signOut, table, friendsButton, coursesButton, garageButton, spectate, returnToMeet, cardButton, diaryButton,
            meetPublic, meetConvoy;
        Button votingToggle, openVote, castVote, drawVote, cancelVote;
        List<string> ballotIds = new List<string>();
        Stepper ballotCourse;
        TextMeshProUGUI ballotLine;
        float ballotDeadlineAt;
        long ballotSeenRevision = -1;
        TMP_InputField codeField;
        Stepper starter, intent, stage, course, aiCount, trial, difficulty, challengeTrial, leadRival, cupLeg2, cupLeg3;
        /// <summary>The challenge trials the snapshot offers under the Challenges intent (docs/CHALLENGE_TRIALS.md).</summary>
        List<JObject> challengeTrialDefs = new List<JObject>();
        TextMeshProUGUI cupLine;
        readonly List<RivalDef> rivalChoices = new List<RivalDef>();
        TextMeshProUGUI archetypeLine, stageAccessLine;
        /// <summary>The shared frontier (spec §5.1): stages above it are listed but locked for this convoy.</summary>
        int stageMax;
        float nextArchetypeFetch;
        // Time Attack: a chosen convoy member's shared ghost besides your own best (spec §8); each member picks for themself.
        Stepper ghostChoice;
        readonly List<string> ghostIds = new List<string>(), ghostNames = new List<string>();
        /// <summary>The convoy member whose kept ghost this player chases in Time Attack (null = only their own best).</summary>
        public static string GhostMemberId { get; private set; }
        public static string GhostMemberName { get; private set; }
        List<JObject> trialDefs = new List<JObject>();
        readonly List<Button> listButtons = new List<Button>();
        readonly List<string> listIds = new List<string>();
        List<CarDef> starters = new List<CarDef>();
        List<string> courseIds = new List<string>();
        bool dirty = true, busy;
        string shownNotice = "";
        float nextListRefresh;
        // The leader may ask for readiness at most once per 15 s (server rule); the snapshot says how long is left.
        float cooldownUntil;
        long cooldownRevision = -1;
        int shownCooldown = -1;
        ContentCatalogue cat;

        OnlineSession S => OnlineSession.Current;

        protected override void OnBuild(RectTransform root)
        {
            cat = ContentLibrary.Load()?.Catalogue;
            if (cat != null) starters = cat.Cars.Where(c => c.Starter).OrderBy(c => c.BasePI).ToList();
            UIFactory.Panel("Backdrop", root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.035f, 0.04f, 0.05f, 0.9f));

            // Left: the roster (six seats) and the last result.
            Image left = UIFactory.Panel("Roster", root, new Vector2(0, 0), new Vector2(0.38f, 1), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.95f));
            RectTransform lcol = UIFactory.Column("RosterColumn", left.transform, new Vector2(0, 0.04f), new Vector2(1, 0.95f), new Vector2(48, 0), new Vector2(-24, 0), 10f);
            heading = UIFactory.Row("Heading", lcol, "ONLINE", SignalTheme.Heading, SignalTheme.Label, 640, 0, true);
            rosterText = UIFactory.Row("Seats", lcol, "", SignalTheme.Body, SignalTheme.Label, 640, 360);
            rosterText.richText = true;
            lastResult = UIFactory.Row("LastResult", lcol, "", SignalTheme.Small, SignalTheme.Label, 640, 300);
            lastResult.richText = true;
            routeChart = UIFactory.Button("OnlineRouteChart", lcol, "Route Chart — last race", () => App.Router.Show(App.ChartScreen), 520, 48);

            // Right: whatever the convoy needs next.
            RectTransform col = UIFactory.Column("Flow", root, new Vector2(0.4f, 0.03f), new Vector2(0.98f, 0.96f), Vector2.zero, Vector2.zero, 10f);
            status = UIFactory.Row("Status", col, "", SignalTheme.Body, SignalTheme.Label, 1000, 62);
            status.richText = true;
            error = UIFactory.Row("Error", col, "", SignalTheme.Small, SignalTheme.Caution, 1000, 28);

            // Not in a convoy.
            rejoin = UIFactory.Button("Rejoin", col, "Rejoin convoy", () => Send("convoy.rejoin"), 620, 56);
            notNow = UIFactory.Button("NotNow", col, "Not Now", () => Send("rejoin.dismiss", new { forget = false }), 620, 48);
            create = UIFactory.Button("CreateConvoy", col, "Create Convoy (discoverable)", () => Send("convoy.create", new { privacy = "discoverable" }), 620, 56);
            createPrivate = UIFactory.Button("CreatePrivate", col, "Create Private Convoy", () => Send("convoy.create", new { privacy = "invite-only" }), 620, 52);
            codeField = UIFactory.InputField("InviteCode", col, "Invite code", false, 16, 620, 52);
            joinCode = UIFactory.Button("JoinCode", col, "Join with Code", () => Send("convoy.join", new { code = codeField.text.Trim() }), 620, 52);
            refresh = UIFactory.Button("RefreshList", col, "Refresh Convoy List", () => { nextListRefresh = 0; }, 620, 48);
            for (int i = 0; i < 5; i++)
            {
                int index = i;
                Button b = UIFactory.Button("Listed" + i, col, "", () => { if (index < listIds.Count) Send("convoy.join", new { convoyId = listIds[index] }); }, 1000, 52);
                b.GetComponentInChildren<TextMeshProUGUI>().richText = false;
                listButtons.Add(b);
            }

            // First online sign-in: choose the starter car (server-owned).
            starter = new Stepper(col, "Starter", Mathf.Max(1, starters.Count), i => starters.Count == 0 ? "—" : $"{starters[i].Name}  PI {starters[i].BasePI}  {starters[i].Drive}", 0, 1000);
            chooseStarter = UIFactory.Button("ChooseStarter", col, "Choose Starter Car", ChooseStarter, 620, 56);

            // In a convoy.
            intentLine = UIFactory.Row("Intent", col, "", SignalTheme.Body, SignalTheme.Label, 1000, 34);
            intent = new Stepper(col, "Intent", Intents.Length, i => Intents[i].Label, 0, 1000);
            proposeIntent = UIFactory.Button("ProposeIntent", col, "Ask Everyone: Mode Ready?", ProposeIntent, 620, 56);
            modeReady = UIFactory.Button("ModeReady", col, "Mode Ready", ToggleModeReady, 620, 56);
            enterMode = UIFactory.Button("EnterMode", col, "Enter Mode", () => Send("mode.enter", new { modeRevision = (long)S.Convoy["modeRevision"] }), 620, 56);
            stage = new Stepper(col, "Stage", 1, i => Limits.CampaignStages >= i + 1
                ? CampaignProgress.StageLabel(i + 1) + "  " + StageName(i + 1) + (stageMax > 0 && i + 1 > stageMax ? "  · locked for this convoy" : "") : "", 0, 1000);
            stage.Changed += _ => dirty = true;
            // Whose frontier limits the convoy, said neutrally (spec §5.1): "Next shared stage: S08 — two members have not cleared it."
            stageAccessLine = UIFactory.Row("StageAccess", col, "", SignalTheme.Small, SignalTheme.LabelDim, 1000, 28);
            course = new Stepper(col, "Course", 1, i => i < courseIds.Count ? CourseName(courseIds[i]) : "—", 0, 1000);
            // Custom Cup: the Course row is leg 1; legs 2 and 3 from the same offered courses (the schedule is published).
            cupLeg2 = new Stepper(col, "Leg 2", 1, i => i < courseIds.Count ? CourseName(courseIds[i]) : "—", 1, 1000);
            cupLeg3 = new Stepper(col, "Leg 3", 1, i => i < courseIds.Count ? CourseName(courseIds[i]) : "—", 2, 1000);
            aiCount = new Stepper(col, "Opponents", Limits.MaxRaceVehicles, i => i == 0 ? "none" : $"{i} AI", 3, 1000);
            if (cat != null) rivalChoices.AddRange(cat.Rivals.Where(r => FinalRivals.Allowed(r.Id, AiPlacementContext.FreeplayOpponent)).OrderBy(r => r.Id, System.StringComparer.Ordinal));
            leadRival = new Stepper(col, "Lead rival", rivalChoices.Count + 1,
                i => i == 0 || i > rivalChoices.Count ? "random authored rivals" : $"{rivalChoices[i - 1].Name} · {rivalChoices[i - 1].Tendency.Replace('-', ' ')}", 0, 1000);
            archetypeLine = UIFactory.Row("Archetypes", col, "", SignalTheme.Small, SignalTheme.LabelDim, 1000, 28);
            ghostChoice = new Stepper(col, "Chase ghost", 1, i => i == 0 || i > ghostNames.Count ? "your best only" : "your best + " + ghostNames[i - 1] + "'s", 0, 1000);
            ghostChoice.Changed += _ => { PickGhost(); dirty = true; };
            trial = new Stepper(col, "Team Trial", 1, i => i < trialDefs.Count ? TrialLabel(trialDefs[i]) : "—", 0, 1000);
            trial.Changed += _ => dirty = true;
            difficulty = new Stepper(col, "Difficulty", 1, i => DifficultyLabel(i), 0, 1000);
            // A challenge trial instead of a Team Trial: the first choice keeps the Team Trial rows.
            challengeTrial = new Stepper(col, "Challenge Trial", 1, i => i == 0 || i > challengeTrialDefs.Count ? "none: race a Team Trial"
                : ChallengeTrialLabel(challengeTrialDefs[i - 1]), 0, 1000);
            challengeTrial.Changed += _ => dirty = true;
            proposeEvent = UIFactory.Button("ProposeEvent", col, "Propose Event", ProposeEvent, 620, 56);
            // Freeplay vote (Addendum 01 §6): server deadline, one ticket per ballot, a server draw; the leader can still choose.
            votingToggle = UIFactory.Button("VotingToggle", col, "Voting: Off", ToggleVoting, 620, 48);
            openVote = UIFactory.Button("OpenVote", col, "Open a Course Vote", () => Send("ballot.open", new { durationSeconds = (int?)(S.Convoy?["voting"] as JObject)?["durationSeconds"] ?? 30, aiCount = aiCount.Index }), 620, 52);
            ballotCourse = new Stepper(col, "Your vote", 1, i => i < ballotIds.Count ? CourseName(ballotIds[i]) : "—", 0, 1000);
            castVote = UIFactory.Button("CastVote", col, "Cast / Change Vote", CastVote, 620, 52);
            drawVote = UIFactory.Button("DrawVote", col, "Draw the Course", () => Send("ballot.draw", new { ballotRevision = (long?)(S.Convoy?["ballot"] as JObject)?["revision"] ?? 0 }), 620, 52);
            cancelVote = UIFactory.Button("CancelVote", col, "Cancel the Vote", () => Send("ballot.cancel", new { ballotRevision = (long?)(S.Convoy?["ballot"] as JObject)?["revision"] ?? 0 }), 620, 48);
            ballotLine = UIFactory.Row("Ballot", col, "", SignalTheme.Small, SignalTheme.Label, 1000, 110);
            ballotLine.richText = true;
            cupLine = UIFactory.Row("CupTable", col, "", SignalTheme.Small, SignalTheme.Label, 1000, 110);
            cupLine.richText = true;
            proposalLine = UIFactory.Row("Proposal", col, "", SignalTheme.Small, SignalTheme.Label, 1000, 84);
            proposalLine.richText = true;
            // A tunable challenge trial: the player's own setup of the loaner, kept on this PC and sent with Event Ready.
            tuneLoaner = UIFactory.Button("OnlineTuneLoaner", col, "Tune the Loaner", () =>
            {
                if (ProposedTrial() is ChallengeTrialDef td && td.Loaner.IsTunable) { App.TrialTune.OpenOnline(td, S.AccountId); App.Router.Show(App.TrialTune); }
            }, 620, 48);
            eventReady = UIFactory.Button("EventReady", col, "Event Ready", ToggleEventReady, 620, 56);
            start = UIFactory.Button("StartEvent", col, "Start Event", () => Send("event.start", new { proposalRevision = (long)S.Convoy["eventProposal"]["revision"] }), 620, 60);
            spectate = UIFactory.Button("Spectate", col, "Spectate the Race", Spectate, 620, 56);
            postLine = UIFactory.Row("PostEvent", col, "", SignalTheme.Body, SignalTheme.Label, 1000, 64);
            postLine.richText = true;
            cont = UIFactory.Button("Continue", col, "Continue", () => ChoosePost("continue"), 620, 56);
            serviceBreak = UIFactory.Button("ServiceBreak", col, "Service Break", () => ChoosePost("service-break"), 620, 52);
            advance = UIFactory.Button("Advance", col, "Advance", () => Send("postevent.advance", new { destinationRevision = (long)S.Convoy["postEvent"]["destinationRevision"] }), 620, 56);
            // Spec §12: after a race that started at the meet, a clear way back to it (the bay is allocated afresh).
            returnToMeet = UIFactory.Button("ReturnToMeet", col, "Back to the Meet", () => App.ReturnToMeet(this), 620, 52);
            invite = UIFactory.Button("Invite", col, "Create Invite Code", async () =>
            {
                JToken r = await S.Request("convoy.invite.create");
                if (r != null) inviteLine.text = $"Invite code  <b>{(string)r["code"]}</b>  (15 minutes)";
            }, 620, 48);
            inviteLine = UIFactory.Row("InviteLine", col, "", SignalTheme.Small, SignalTheme.Label, 1000, 26);
            inviteLine.richText = true;
            // While We Wait (Addendum 02 §1): the convoy's shared Pocket Circuit table; readiness is kept while playing.
            table = UIFactory.Button("WhileWeWait", col, "While We Wait", () => App.Router.Show(App.WhileWeWait), 620, 52);
            // The meet (spec §12): a public instance, or the convoy's own (members kept together).
            meetPublic = UIFactory.Button("MeetPublic", col, "Car Meet: Join Public Meet", () => App.StartOnlineMeet("public", null, this), 620, 48);
            meetConvoy = UIFactory.Button("MeetConvoy", col, "Car Meet: Convoy Meet", () => App.StartOnlineMeet("convoy", null, this), 620, 48);
            friendsButton = UIFactory.Button("OpenFriends", col, "Friends", () => App.Router.Show(App.Friends), 620, 48);
            cardButton = UIFactory.Button("OpenCard", col, "Player Card", () => App.Router.Show(App.PlayerCard), 620, 48);
            diaryButton = UIFactory.Button("RaceDiary", col, "Race Diary", () => App.Router.Show(App.Diary), 620, 48);
            coursesButton = UIFactory.Button("OpenCourses", col, "Courses", () => App.Router.Show(App.Courses), 620, 48);
            garageButton = UIFactory.Button("OpenGarage", col, "Garage", () => App.Router.Show(App.Garage), 620, 48);
            leave = UIFactory.Button("Leave", col, "Leave Convoy", () => Send("convoy.leave"), 620, 48);
            signOut = UIFactory.Button("SignOut", col, "Sign Out", SignOut, 620, 48);
        }

        public override void OnShow()
        {
            if (S == null)
            {
                App.Router.Show(App.SignIn, false);
                return;
            }
            App.Domain = SessionDomain.Online;
            App.DisplayName = S.DisplayName;
            App.RefreshStrip();
            S.Changed -= MarkDirty;
            S.Changed += MarkDirty;
            _ = S.Request("presence.set", new { presence = "InMenus" }, quiet: true);
            if (S.InConvoy) _ = EnsureLoadout(); // joined from elsewhere (a friend's invitation, a rejoin)
            dirty = true;
            nextListRefresh = 0;
        }

        public override void OnHide()
        {
            if (S != null) S.Changed -= MarkDirty;
        }

        void MarkDirty() => dirty = true;

        /// <summary>True while a request is in flight: a click then is ignored (toggles must not double-send).</summary>
        public bool Busy => busy;

        public override Selectable DefaultFocus => S?.InConvoy == true ? (Selectable)modeReady : create;

        public override void Tick()
        {
            if (S == null) return;
            if (!S.InConvoy && S.StarterCarId != null && Time.unscaledTime >= nextListRefresh)
            {
                nextListRefresh = Time.unscaledTime + 5f;
                _ = RefreshList();
            }
            if (S.LastNotice != shownNotice) dirty = true; // a time-limited notice lapsed
            int left = Mathf.CeilToInt(cooldownUntil - Time.unscaledTime);
            if (left != shownCooldown && (left >= 0 || shownCooldown > 0)) dirty = true; // tick the countdown label
            if (dirty)
            {
                dirty = false;
                Render();
            }
        }

        async System.Threading.Tasks.Task RefreshList()
        {
            JToken r = await S.Request("convoy.list", null, quiet: true);
            listIds.Clear();
            var labels = new List<string>();
            foreach (JToken c in (r?["convoys"] as JArray) ?? new JArray())
            {
                listIds.Add((string)c["convoyId"]);
                string label = (string)c["intent"]?["label"];
                labels.Add($"Join {(string)c["leaderName"]}'s convoy   ·   {(int)c["members"]}/{(int)c["maxMembers"]}" + (label != null ? "   ·   " + label : ""));
            }
            for (int i = 0; i < listButtons.Count; i++)
            {
                listButtons[i].GetComponentInChildren<TextMeshProUGUI>().text = i < labels.Count ? labels[i] : "";
                listButtons[i].gameObject.SetActive(!S.InConvoy && i < labels.Count);
            }
            if (labels.Count == 0 && !S.InConvoy) status.text = "No open convoys right now. Create one, or join with a code.";
        }

        // ------------------------------------------------------------------ rendering

        void Render()
        {
            JObject c = S.Convoy;
            bool inConvoy = S.InConvoy;
            bool leader = S.IsLeader;
            bool needStarter = S.StarterCarId == null;
            error.text = S.LastError;
            heading.text = inConvoy ? "CONVOY" : "ONLINE";

            // Not in a convoy.
            JObject rj = S.Client.RejoinStatus;
            bool offerRejoin = !inConvoy && rj != null && (bool?)rj["canRejoin"] == true && (bool?)rj["prompt"] == true;
            rejoin.gameObject.SetActive(offerRejoin);
            notNow.gameObject.SetActive(offerRejoin);
            if (offerRejoin) rejoin.GetComponentInChildren<TextMeshProUGUI>().text = $"Rejoin {(string)rj["leaderName"]}'s convoy";
            foreach (GameObject g in new[] { create.gameObject, createPrivate.gameObject, codeField.gameObject, joinCode.gameObject, refresh.gameObject })
                g.SetActive(!inConvoy && !needStarter);
            if (inConvoy || needStarter) foreach (Button b in listButtons) b.gameObject.SetActive(false);
            starter.Root.SetActive(needStarter);
            chooseStarter.gameObject.SetActive(needStarter);

            // Roster.
            rosterText.text = inConvoy ? $"<size=75%><color=#9A968D>{Esc((string)c["privacyLabel"])}</color></size>\n" + RosterText(c) :$"Signed in as <b>{Esc(S.DisplayName)}</b>\n{WalletLine()}";
            foreach (GameObject g in new[] { intentLine.gameObject, leave.gameObject, invite.gameObject, inviteLine.gameObject })
                g.SetActive(inConvoy);
            invite.gameObject.SetActive(inConvoy && (leader || (string)c["privacy"] == "discoverable"));
            signOut.gameObject.SetActive(!inConvoy);
            friendsButton.gameObject.SetActive(!needStarter);
            cardButton.gameObject.SetActive(!needStarter && (!inConvoy || (string)c["phase"] != "Allocating" && (string)c["phase"] != "InMatch"));
            diaryButton.gameObject.SetActive(cardButton.gameObject.activeSelf);
            coursesButton.gameObject.SetActive(!needStarter && (!inConvoy || (string)c["phase"] != "Allocating" && (string)c["phase"] != "InMatch"));
            garageButton.gameObject.SetActive(coursesButton.gameObject.activeSelf);
            int pendingSocial = S.Invites.Count;
            friendsButton.GetComponentInChildren<TextMeshProUGUI>().text = pendingSocial > 0 ? $"Friends   ({pendingSocial} invitation{(pendingSocial == 1 ? "" : "s")})" : "Friends";
            string phaseNow = inConvoy ? (string)c["phase"] : "";
            table.gameObject.SetActive(inConvoy && phaseNow != "Allocating" && phaseNow != "InMatch");
            meetPublic.gameObject.SetActive(!needStarter && phaseNow != "Allocating" && phaseNow != "InMatch");
            meetConvoy.gameObject.SetActive(!needStarter && inConvoy && phaseNow != "Allocating" && phaseNow != "InMatch");
            string back = App.ReturnMeetKind;
            returnToMeet.gameObject.SetActive(back != null && !needStarter && !App.InOnlineRace && phaseNow != "Allocating" && phaseNow != "InMatch" && (back != "convoy" || inConvoy));
            if (back != null)
                returnToMeet.GetComponentInChildren<TextMeshProUGUI>().text = back == "convoy" ? "Back to the Meet (your convoy's meet)"
                    : back == "friend" ? "Back to the Meet (your friend's meet)" : "Back to the Meet (a public meet)";

            string phase = inConvoy ? (string)c["phase"] : "";
            JObject intentObj = inConvoy ? c["intent"] as JObject : null;
            bool modeEntered = inConvoy && (bool?)c["modeEntered"] == true;
            JObject proposal = inConvoy ? c["eventProposal"] as JObject : null;
            JObject post = inConvoy ? c["postEvent"] as JObject : null;
            bool matchOn = phase == "Allocating" || phase == "InMatch";
            JToken me = S.MyMember;

            routeChart.gameObject.SetActive(App.HasOnlineChart && !matchOn);

            // Intent and Mode Ready.
            intentLine.text = intentObj != null ? $"Intent: {(string)intentObj["label"]}{(modeEntered ? "  (entered)" : "")}" : "No intent yet.";
            bool canPropose = inConvoy && leader && post == null && !matchOn;
            intent.Root.SetActive(canPropose && !modeEntered);
            proposeIntent.gameObject.SetActive(canPropose && !modeEntered);
            if (inConvoy && S.Client.ConvoyRevision != cooldownRevision)
            {
                cooldownRevision = S.Client.ConvoyRevision;
                cooldownUntil = Time.unscaledTime + ((long?)c["readyRequestCooldownMs"] ?? 0) / 1000f;
            }
            int wait = Mathf.Max(0, Mathf.CeilToInt(cooldownUntil - Time.unscaledTime));
            shownCooldown = wait;
            string suffix = wait > 0 ? $"   (in {wait} s)" : "";
            proposeIntent.interactable = wait == 0;
            proposeIntent.GetComponentInChildren<TextMeshProUGUI>().text = "Ask Everyone: Mode Ready?" + suffix;
            bool modeOpen = inConvoy && intentObj != null && !modeEntered && !matchOn;
            modeReady.gameObject.SetActive(modeOpen);
            bool iAmModeReady = (bool?)me?["modeReady"] == true;
            modeReady.GetComponentInChildren<TextMeshProUGUI>().text = iAmModeReady ? "Mode Ready: YES   (select to unready)" : "Mode Ready";
            bool allModeReady = inConvoy && c["members"].All(m => (bool?)m["modeReady"] == true);
            enterMode.gameObject.SetActive(modeOpen && leader);
            enterMode.interactable = allModeReady;

            // Event selection (leader).
            string kind = (string)intentObj?["kind"];
            // A live course vote replaces direct selection until it is drawn or cancelled (the server refuses proposals meanwhile).
            string ballotState = (string)(c?["ballot"] as JObject)?["state"];
            bool selecting = modeEntered && leader && proposal == null && post == null && !matchOn && ballotState != "open" && ballotState != "frozen";
            stage.Root.SetActive(selecting && kind == "campaign");
            course.Root.SetActive(selecting && kind == "freeplay");
            aiCount.Root.SetActive(selecting && kind == "freeplay" && (string)intentObj?["submode"] != "time-attack");
            bool namedRace = selecting && kind == "freeplay" && (string)intentObj?["submode"] != "time-attack" && aiCount.Index > 0;
            leadRival.Root.SetActive(namedRace);
            // Every member sees their own progress on the rival-archetype challenges while a Freeplay race is being set up.
            bool showArchetypes = inConvoy && modeEntered && !matchOn && post == null && kind == "freeplay" && (string)intentObj?["submode"] != "time-attack";
            archetypeLine.gameObject.SetActive(showArchetypes);
            if (showArchetypes && Time.unscaledTime >= nextArchetypeFetch)
            {
                nextArchetypeFetch = Time.unscaledTime + 20f;
                FetchArchetypes();
            }
            List<JObject> offered = ((inConvoy ? c["challengeTrials"] : null) as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (offered.Count != challengeTrialDefs.Count || offered.Where((d, i) => (string)d["id"] != (string)challengeTrialDefs[i]["id"]).Any())
            {
                challengeTrialDefs = offered;
                challengeTrial.SetCount(offered.Count + 1);
            }
            bool trialPicked = challengeTrial.Index > 0 && challengeTrial.Index <= challengeTrialDefs.Count;
            challengeTrial.Root.SetActive(selecting && kind == "challenges" && challengeTrialDefs.Count > 0);
            trial.Root.SetActive(selecting && kind == "challenges" && !trialPicked);
            difficulty.Root.SetActive(selecting && kind == "challenges" && !trialPicked);
            if (selecting && kind == "challenges")
            {
                List<JObject> defs = ((c["teamTrials"] as JArray) ?? new JArray()).OfType<JObject>().ToList();
                if (defs.Count != trialDefs.Count || defs.Where((d, i) => (string)d["id"] != (string)trialDefs[i]["id"]).Any())
                {
                    trialDefs = defs;
                    trial.SetCount(Mathf.Max(1, defs.Count));
                    trial.Set(0);
                }
                int levels = ((trialDefs.ElementAtOrDefault(trial.Index)?["difficulties"] as JArray) ?? new JArray()).Count;
                if (difficulty.Count != Mathf.Max(1, levels)) difficulty.SetCount(Mathf.Max(1, levels));
            }
            proposeEvent.gameObject.SetActive(selecting);
            proposeEvent.interactable = wait == 0;
            proposeEvent.GetComponentInChildren<TextMeshProUGUI>().text = "Propose Event" + suffix;
            JToken access = inConvoy && kind == "campaign" ? c["campaignAccess"]?[(string)intentObj?["mode"] ?? "normal"] : null;
            if (selecting && kind == "campaign")
            {
                // The stages the most-progressed member could race stay listed, visibly locked; when the shared frontier
                // moves (a member joins or leaves, a stage is cleared) the selection returns to it, a valid node.
                int max = Mathf.Max(1, (int?)access?["maxSelectableStage"] ?? 1);
                int shown = Mathf.Max(max, (int?)access?["highestStage"] ?? max);
                if (max != stageMax || shown != stage.Count)
                {
                    stageMax = max;
                    stage.SetCount(shown);
                    stage.Set(max - 1);
                }
                if (stage.Index + 1 > stageMax) proposeEvent.interactable = false;
            }
            bool showAccess = access != null && modeEntered && !matchOn && post == null;
            stageAccessLine.gameObject.SetActive(showAccess);
            if (showAccess) stageAccessLine.text = Esc((string)access["explanation"] ?? "");
            RenderBallot(c, leader, kind, modeEntered, proposal, post, matchOn);
            bool cupSetup = selecting && kind == "freeplay" && (string)intentObj?["submode"] == "cup";
            cupLeg2.Root.SetActive(cupSetup);
            cupLeg3.Root.SetActive(cupSetup);
            JObject cupState = inConvoy ? c["cup"] as JObject : null;
            cupLine.gameObject.SetActive(cupState != null);
            if (cupState != null) cupLine.text = CupText(cupState);
            if (selecting && kind == "freeplay")
            {
                List<string> ids = ((c["freeplayAccess"] as JObject)?["courses"] as JArray)?.Select(x => (string)x["courseId"]).ToList() ?? new List<string>();
                if (!ids.SequenceEqual(courseIds))
                {
                    courseIds = ids;
                    course.SetCount(Mathf.Max(1, ids.Count));
                    course.Set(0);
                    cupLeg2.SetCount(Mathf.Max(1, ids.Count));
                    cupLeg2.Set(Mathf.Min(1, ids.Count - 1));
                    cupLeg3.SetCount(Mathf.Max(1, ids.Count));
                    cupLeg3.Set(Mathf.Min(2, ids.Count - 1));
                }
                int humans = c["members"].Count();
                aiCount.SetCount(Mathf.Max(1, Limits.MaxRaceVehicles - humans + 1));
            }

            // Time Attack: whose shared ghost to chase as well as your own (the convoy's other members).
            List<JToken> others = inConvoy ? c["members"].Where(m => (string)m["accountId"] != S.AccountId).ToList() : new List<JToken>();
            if (!others.Select(m => (string)m["accountId"]).SequenceEqual(ghostIds))
            {
                string keep = GhostMemberId;
                ghostIds.Clear();
                ghostIds.AddRange(others.Select(m => (string)m["accountId"]));
                ghostNames.Clear();
                ghostNames.AddRange(others.Select(m => (string)m["displayName"]));
                ghostChoice.SetCount(ghostIds.Count + 1);
                ghostChoice.Set(keep != null && ghostIds.Contains(keep) ? ghostIds.IndexOf(keep) + 1 : 0);
                PickGhost();
            }
            ghostChoice.Root.SetActive(inConvoy && modeEntered && !matchOn && post == null && kind == "freeplay"
                                       && (string)intentObj?["submode"] == "time-attack" && ghostIds.Count > 0);

            // Proposal and Event Ready.
            proposalLine.gameObject.SetActive(proposal != null);
            proposalLine.text = proposal != null ? ProposalText(proposal) : "";
            bool readyOpen = proposal != null && !matchOn && post == null;
            eventReady.gameObject.SetActive(readyOpen);
            tuneLoaner.gameObject.SetActive(readyOpen && ProposedTrial() is ChallengeTrialDef tunable && tunable.Loaner.IsTunable && (bool?)S.MyMember?["eventReady"] != true);
            bool iAmEventReady = (bool?)me?["eventReady"] == true;
            eventReady.GetComponentInChildren<TextMeshProUGUI>().text = iAmEventReady ? "Event Ready: YES   (select to unready)" : "Event Ready";
            bool allEventReady = inConvoy && c["members"].Where(m => (bool?)m["spectator"] != true).All(m => (bool?)m["eventReady"] == true);
            start.gameObject.SetActive(readyOpen && leader);
            // Not while a request is still being answered: the screen takes one command at a time, and the Event Ready
            // snapshot can arrive before the ready request's own reply (a start pressed then was silently dropped).
            start.interactable = allEventReady && !busy;
            // Spec §4.4: a member who is not racing this event (disqualified, joined late) may watch it; never drive it.
            spectate.gameObject.SetActive(inConvoy && phase == "InMatch" && (bool?)me?["spectator"] == true && !App.InOnlineRace);

            // Post-event decision.
            postLine.gameObject.SetActive(post != null);
            cont.gameObject.SetActive(post != null);
            serviceBreak.gameObject.SetActive(post != null);
            advance.gameObject.SetActive(post != null && leader);
            if (post != null)
            {
                string mine = (string)post["choices"]?.FirstOrDefault(x => (string)x["accountId"] == S.AccountId)?["choice"] ?? "undecided";
                postLine.text = $"Next: <b>{Esc((string)post["destination"]?["label"])}</b>" +
                                $"\n<size=80%>Continue {(int?)post["continueCount"] ?? 0}  ·  Service Break {(int?)post["serviceBreakCount"] ?? 0}  ·  Undecided {(int?)post["undecidedCount"] ?? 0}   (you: {mine})</size>";
                advance.interactable = (bool?)post["advanceEnabled"] == true && !busy;
            }

            status.text = StatusText(inConvoy, leader, needStarter, phase, intentObj, modeEntered, proposal, post, allModeReady, allEventReady);
            shownNotice = S.LastNotice;
            if (!string.IsNullOrEmpty(shownNotice)) status.text += $"\n<size=80%><color=#F2A541>{Esc(shownNotice)}</color></size>";
            lastResult.text = App.LastOnlineResult ?? "";
        }

        string StatusText(bool inConvoy, bool leader, bool needStarter, string phase, JObject intentObj, bool modeEntered, JObject proposal, JObject post, bool allModeReady, bool allEventReady)
        {
            if (needStarter) return "Choose your starter car. It is yours on this account, online only.";
            if (!inConvoy) return "Create a convoy, or join one. Up to six drivers race together.";
            if (phase == "Allocating") return "Starting the race: reserving a server…";
            if (phase == "InMatch") return "The race is running.";
            if (post != null) return "Race finished. Continue together, or call a Service Break.";
            if (intentObj == null) return leader ? "Choose what the convoy does next, then ask everyone for Mode Ready." : "Waiting for the leader to choose what to do.";
            if (!modeEntered) return allModeReady ? (leader ? "Everyone is Mode Ready: enter the mode." : "Everyone is Mode Ready: waiting for the leader.") : "Waiting for everyone's Mode Ready.";
            if (proposal == null) return leader ? "Pick the event and propose it." : "Waiting for the leader's event.";
            return allEventReady ? (leader ? "Everyone is Event Ready: start the event." : "Everyone is Event Ready: waiting for the leader to start.") : "Waiting for everyone's Event Ready.";
        }

        string RosterText(JObject c)
        {
            var sb = new System.Text.StringBuilder();
            var members = c["members"].ToList();
            for (int slot = 0; slot < Limits.MaxConvoyHumans; slot++)
            {
                JToken m = members.FirstOrDefault(x => (int?)x["slot"] == slot) ?? (slot < members.Count ? members[slot] : null);
                if (m == null)
                {
                    sb.Append($"<color=#5A5C60>{slot + 1}   open seat</color>\n");
                    continue;
                }
                string name = Esc((string)m["displayName"]);
                string lead = (bool?)m["isLeader"] == true ? " <color=#D7263D>LEADER</color>" : "";
                string you = (string)m["accountId"] == S.AccountId ? " <size=80%>(you)</size>" : "";
                string mode = (bool?)m["modeReady"] == true ? "<color=#3EC6D8>MODE READY</color>" : "<color=#5A5C60>mode -</color>";
                string ev = (bool?)m["eventReady"] == true ? "<color=#3EC6D8>EVENT READY</color>" : "<color=#5A5C60>event -</color>";
                string car = m["carId"]?.Type == JTokenType.String && cat != null && cat.TryCar((string)m["carId"], out CarDef def) ? def.Name : "no car";
                string extra = (bool?)m["away"] == true ? " · away" : (string)m["diversion"] != null ? " · " + (string)m["diversion"] : "";
                sb.Append($"{slot + 1}   <b>{name}</b>{lead}{you}\n    <size=80%>{mode}   {ev}   {Esc(car)}{extra}</size>\n");
            }
            return sb.ToString();
        }

        string ProposalText(JObject p)
        {
            JObject s = p["settings"] as JObject;
            JObject r = p["rosterPreview"] as JObject;
            string trialId = (string)s?["challengeTrialId"];
            JObject trialDef = trialId == null ? null : challengeTrialDefs.FirstOrDefault(t => (string)t["id"] == trialId);
            string what = (string)s?["kind"] == "campaign"
                ? $"{(string)s["stageId"]} · {StageName((int?)s["stageNumber"] ?? 0)} · {((string)s["mode"] ?? "normal").ToUpperInvariant()}"
                : trialId != null ? $"Challenge trial {(trialDef != null ? ChallengeTrialLabel(trialDef) : trialId)}"
                : $"{CourseName((string)s?["courseId"])} · {(string)s?["freeplayMode"]}";
            string target = (long?)s?["benchmarkTargetMs"] is long ms && ms > 0
                ? $"   target {ResultsScreen.FormatRaceTime(ms * 1000)}{((bool?)s["benchmarkProvisional"] == true ? " (provisional)" : "")}" : "";
            string grid = r != null ? $"{(int?)r["humans"]} driver{((int?)r["humans"] == 1 ? "" : "s")} + {(int?)r["opposingAi"]} AI · {(string)r["contact"]}" : "";
            return $"<b>{Esc(what)}</b>\n<size=85%>{grid}{target}</size>";
        }

        string StageName(int number)
        {
            StageDef st = cat?.Stages.FirstOrDefault(x => x.Number == number);
            return st != null && cat.TryCourse(st.Course, out CourseDef cd) ? cd.Name : "";
        }

        string CourseName(string id) => id != null && cat != null && cat.TryCourse(id, out CourseDef cd) ? $"{cd.Id}  {cd.Name}" : id ?? "—";

        string WalletLine()
        {
            JObject me = S.Me;
            if (me == null) return "";
            return $"<size=85%>{(long?)me["wallet"]?["balance"]:N0} cr   ·   {(string)me["rank"]?["name"] ?? ""}   ·   ONLINE progress (server-owned)</size>";
        }

        static string Esc(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");

        // ------------------------------------------------------------------ actions

        async void Spectate()
        {
            if (busy) return;
            busy = true;
            dirty = true;
            try
            {
                // A spectator ticket from the control plane (only convoy members of this match get one), then the same join
                // path as a racer: the server gives a spectator the race to watch and nothing to drive.
                if (await S.Request("match.ticket", new { role = "spectator" }) is JObject ticket) App.StartSpectating(ticket);
            }
            finally
            {
                busy = false;
                dirty = true;
            }
        }

        async void Send(string type, object payload = null)
        {
            if (busy) return;
            busy = true;
            dirty = true; // show the command-taking buttons as unavailable until the reply
            try
            {
                JToken r = await S.Request(type, payload);
                if (r != null && (type == "convoy.create" || type == "convoy.join" || type == "convoy.rejoin")) await EnsureLoadout();
                if (type == "convoy.leave" || type == "convoy.create" || type == "convoy.join") inviteLine.text = "";
            }
            finally
            {
                busy = false;
                dirty = true;
            }
        }

        /// <summary>Event Ready needs a loadout: the owned starter, stock performance and default cosmetics until the Garage exists.</summary>
        async System.Threading.Tasks.Task EnsureLoadout()
        {
            if (S.MyMember?["carId"]?.Type == JTokenType.String || S.StarterCarId == null) return;
            await S.Request("loadout.set", new { carId = S.StarterCarId, performanceHash = "stock", cosmeticHash = "default" });
        }

        async void ChooseStarter()
        {
            if (starters.Count == 0) return;
            (int status, JObject body) = await S.Client.Post("/v1/me/starter", new { carId = starters[starter.Index].Id });
            await S.RefreshMe();
            dirty = true;
            if (status >= 300) error.text = (string)body?["message"] ?? (string)body?["error"] ?? $"The starter car could not be chosen ({status}).";
        }

        void ProposeIntent() => Send("intent.set", Intents[intent.Index].Intent);

        /// <summary>Automation hook (UI tours): choose an intent row as a player would with the stepper.</summary>
        public void SelectIntent(int index) => intent.Set(index);

        void PickGhost()
        {
            int i = ghostChoice.Index;
            GhostMemberId = i > 0 && i <= ghostIds.Count ? ghostIds[i - 1] : null;
            GhostMemberName = GhostMemberId != null ? ghostNames[i - 1] : null;
        }

        /// <summary>Automation hook (UI tours): chase this convoy member's shared ghost, as the stepper would.</summary>
        public bool SelectGhostMember(string accountId)
        {
            int i = ghostIds.IndexOf(accountId);
            if (i < 0) return false;
            ghostChoice.Set(i + 1);
            PickGhost();
            return true;
        }

        async void FetchArchetypes()
        {
            try
            {
                JObject r = await S.Client.Get("/v1/me/archetypes");
                var s = new ArchetypeState();
                foreach (JToken t in (r?["raced"] as JArray) ?? new JArray()) s.Raced.Add((string)t);
                foreach (JToken t in (r?["wonSinceQuit"] as JArray) ?? new JArray()) s.WonStreak.Add((string)t);
                if (archetypeLine != null) archetypeLine.text = ArchetypeChallenges.ProgressLine(s);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[NightSignal.Online] archetype progress unavailable: " + e.Message);
            }
        }

        /// <summary>Automation hook (tours): name the lead rival for a Freeplay race, as the stepper would.</summary>
        public bool SelectLeadRival(string rivalId)
        {
            int i = rivalChoices.FindIndex(r => r.Id == rivalId);
            if (i < 0) return false;
            leadRival.Set(i + 1);
            return true;
        }

        /// <summary>The cup table as the server keeps it: the schedule with the legs raced, then the standings.</summary>
        string CupText(JObject cup)
        {
            List<string> schedule = ((cup["schedule"] as JArray) ?? new JArray()).Select(x => (string)x).ToList();
            int raced = (int?)cup["legsRaced"] ?? 0;
            var sb = new System.Text.StringBuilder("<color=#9A968D>CUSTOM CUP</color>  ");
            sb.Append(string.Join("  →  ", schedule.Select((cid, i) => (i < raced ? "done " : i == raced ? "next " : "") + cid))).Append('\n');
            int pos = 0;
            foreach (JToken e in (cup["standings"] as JArray) ?? new JArray())
            {
                pos++;
                string places = string.Join(" ", ((e["places"] as JArray) ?? new JArray()).Select(p => p.Type == JTokenType.Null ? "–" : (string)p));
                string line = $"{pos}. {Esc((string)e["name"])}  {(int?)e["points"] ?? 0} pts  ({places})";
                sb.Append((string)e["id"] == S.AccountId ? $"<color=#D7263D>{line}</color>" : line).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Automation hook (tours): the Custom Cup's legs 2 and 3 by course id (leg 1 is the Course row).</summary>
        public bool SelectCupLegs(string second, string third)
        {
            int a = courseIds.IndexOf(second), b = courseIds.IndexOf(third);
            if (a < 0 || b < 0) return false;
            cupLeg2.Set(a);
            cupLeg3.Set(b);
            dirty = true;
            return true;
        }

        /// <summary>Puts a campaign stage (1-based, locked ones included) on the stage row once it is listed (tours).</summary>
        public bool SelectStage(int number)
        {
            if (!stage.Root.activeInHierarchy || number < 1 || number > stage.Count) return false;
            stage.Set(number - 1);
            dirty = true;
            return true;
        }

        /// <summary>The stage row as shown (tours): selected stage, stages listed, the shared frontier, the row's text.</summary>
        public int SelectedStage => stage.Index + 1;
        public int StagesListed => stage.Count;
        public int SharedFrontier => stageMax;
        public string StageText => stage.Value.text;
        public string StageAccessText => stageAccessLine.gameObject.activeInHierarchy ? stageAccessLine.text : "";

        /// <summary>Selects a freeplay course in the event setup once the snapshot offers it (tours).</summary>
        public bool SelectCourse(string courseId)
        {
            int i = courseIds.IndexOf(courseId);
            if (i < 0) return false;
            course.Set(i);
            dirty = true;
            return true;
        }

        /// <summary>Selects a challenge trial in the event setup once the snapshot offers it (tours).</summary>
        public bool SelectChallengeTrial(string trialId)
        {
            int i = challengeTrialDefs.FindIndex(t => (string)t["id"] == trialId);
            if (i < 0) return false;
            challengeTrial.Set(i + 1);
            dirty = true;
            return true;
        }

        static string ChallengeTrialLabel(JObject t) =>
            $"{(string)t["challenge"]} {(string)t["title"]} · {(string)t["tier"]} · {(string)t["course"]} in a loaned {(string)t["car"]}";

        /// <summary>Selects a Team Trial (and its difficulty) in the event setup once the snapshot lists them (tours).</summary>
        public bool SelectTrial(string trialId, string difficultyId)
        {
            int i = trialDefs.FindIndex(t => (string)t["id"] == trialId);
            if (i < 0) return false;
            trial.Set(i);
            int d = ((trialDefs[i]["difficulties"] as JArray) ?? new JArray()).ToList().FindIndex(x => (string)x["id"] == difficultyId);
            difficulty.SetCount(Mathf.Max(1, ((trialDefs[i]["difficulties"] as JArray) ?? new JArray()).Count));
            difficulty.Set(Mathf.Max(0, d));
            dirty = true;
            return true;
        }

        void ToggleVoting()
        {
            bool on = (bool?)(S.Convoy?["voting"] as JObject)?["enabled"] == true;
            Send("voting.set", new { enabled = !on, durationSeconds = 15 });
        }

        void CastVote()
        {
            JObject b = S.Convoy?["ballot"] as JObject;
            if (b == null || ballotIds.Count == 0) return;
            Send("ballot.vote", new { ballotRevision = (long)b["revision"], courseId = ballotIds[Mathf.Clamp(ballotCourse.Index, 0, ballotIds.Count - 1)] });
        }

        /// <summary>Freeplay ballot panel: counts down to the SERVER deadline, shows tallies and chances, and the draw.</summary>
        void RenderBallot(JObject c, bool leader, string kind, bool modeEntered, JObject proposal, JObject post, bool matchOn)
        {
            bool freeplay = kind == "freeplay" && modeEntered && post == null && !matchOn;
            JObject b = freeplay ? c["ballot"] as JObject : null;
            bool votingOn = (bool?)(c?["voting"] as JObject)?["enabled"] == true;
            string state = (string)b?["state"];
            // A drawn or cancelled vote only matters while its proposal is on the table; after that it is history.
            bool live = state == "open" || state == "frozen";
            if (!live && proposal == null) b = null;
            votingToggle.gameObject.SetActive(freeplay && leader && !live && proposal == null);
            votingToggle.GetComponentInChildren<TextMeshProUGUI>().text = votingOn ? $"Voting: On ({(int?)(c["voting"] as JObject)?["durationSeconds"] ?? 30} s)" : "Voting: Off";
            openVote.gameObject.SetActive(freeplay && leader && votingOn && !live && proposal == null);
            bool open = state == "open";
            ballotCourse.Root.SetActive(open);
            castVote.gameObject.SetActive(open);
            drawVote.gameObject.SetActive(leader && state == "frozen");
            ballotLine.gameObject.SetActive(b != null);
            if (b == null)
            {
                cancelVote.gameObject.SetActive(false);
                return;
            }
            cancelVote.gameObject.SetActive(leader && (open || state == "frozen"));
            List<string> ids = ((c["freeplayAccess"] as JObject)?["courses"] as JArray)?.Select(x => (string)x["courseId"]).ToList() ?? new List<string>();
            string mine = (string)(b["ballots"] as JObject)?[S.AccountId];
            if (!ids.SequenceEqual(ballotIds))
            {
                ballotIds = ids;
                ballotCourse.SetCount(Mathf.Max(1, ids.Count));
                ballotCourse.Set(Mathf.Max(0, ids.IndexOf(mine ?? ids.FirstOrDefault())));
            }
            if ((long?)b["revision"] != ballotSeenRevision)
            {
                ballotSeenRevision = (long?)b["revision"] ?? -1;
                ballotDeadlineAt = Time.unscaledTime + ((long?)b["remainingMs"] ?? 0) / 1000f;
            }
            var sb = new System.Text.StringBuilder();
            float left = Mathf.Max(0f, ballotDeadlineAt - Time.unscaledTime);
            sb.Append(open ? $"<b>Vote open</b> · {left:0} s left (server deadline)" : state == "frozen" ? "<b>Voting closed</b> · ballots frozen — the leader draws" : "<b>Vote result</b>");
            if (mine != null) sb.Append($"   ·   your vote: {Esc(CourseName(mine))}");
            sb.Append("\n");
            foreach (JToken t in (b["tallies"] as JArray) ?? new JArray())
                sb.Append($"{Esc(CourseName((string)t["courseId"]))}   {(int?)t["votes"]} vote(s)   {((double?)t["chance"] ?? 0) * 100:0}%\n");
            JToken r = b["result"];
            if (r != null && r.Type == JTokenType.Object)
                sb.Append($"<color=#3EC6D8>Drawn: {Esc(CourseName((string)r["courseId"]))}</color>  <size=80%>(ballot {(int?)r["ballotIndex"] + 1} of {(int?)r["totalBallots"]}, {(double?)r["chance"] * 100:0}% chance)</size>");
            ballotLine.text = sb.ToString();
            if (open) dirty = true; // keep the countdown ticking
        }

        string TrialLabel(JObject t) =>
            $"{(string)t["name"]}  ·  {CourseName((string)t["course"])}" + ((bool?)t["provisional"] == true ? "  (provisional targets)" : "");

        string DifficultyLabel(int i)
        {
            JToken level = (trialDefs.ElementAtOrDefault(trial?.Index ?? 0)?["difficulties"] as JArray)?.ElementAtOrDefault(i);
            return level != null ? (string)level["label"] : "—";
        }

        void ToggleModeReady()
        {
            bool ready = (bool?)S.MyMember?["modeReady"] == true;
            Send("mode.ready", new { modeRevision = (long)S.Convoy["modeRevision"], ready = !ready });
        }

        void ProposeEvent()
        {
            string kind = (string)S.Convoy["intent"]?["kind"];
            if (kind == "campaign") Send("event.propose", new { stageId = CampaignProgress.StageLabel(stage.Index + 1) });
            else if (kind == "challenges" && challengeTrial.Index > 0 && challengeTrialDefs.ElementAtOrDefault(challengeTrial.Index - 1) is JObject picked)
                Send("event.propose", new { challengeTrialId = (string)picked["id"] });
            else if (kind == "challenges")
            {
                JObject t = trialDefs.ElementAtOrDefault(trial.Index);
                JToken level = (t?["difficulties"] as JArray)?.ElementAtOrDefault(difficulty.Index);
                if (t != null && level != null) Send("event.propose", new { trialId = (string)t["id"], difficulty = (string)level["id"] });
            }
            else if (courseIds.Count > 0)
            {
                string sub = (string)S.Convoy["intent"]?["submode"];
                string[] named = sub != "time-attack" && aiCount.Index > 0 && leadRival.Index > 0 && leadRival.Index <= rivalChoices.Count
                    ? new[] { rivalChoices[leadRival.Index - 1].Id } : null;
                string[] legs = sub == "cup" ? new[] { courseIds[course.Index], courseIds[Mathf.Min(cupLeg2.Index, courseIds.Count - 1)], courseIds[Mathf.Min(cupLeg3.Index, courseIds.Count - 1)] } : null;
                Send("event.propose", new { courseId = courseIds[course.Index], freeplayMode = sub, aiCount = sub == "time-attack" ? 0 : aiCount.Index, aiRivals = named, cupLegs = legs });
            }
        }

        async void ToggleEventReady()
        {
            await EnsureLoadout();
            JToken me = S.MyMember;
            bool ready = (bool?)me?["eventReady"] == true;
            // A tunable challenge trial: the saved setup goes with readiness (none = the loaner as supplied).
            ChallengeTrialDef td = ProposedTrial();
            MechanicalSnapshot mine = !ready && td != null && td.Loaner.IsTunable ? OnlineTrialSetups.Get(S.AccountId, td.Id) : null;
            object trialSetup = mine == null ? null : new { parts = mine.Parts, tuning = mine.Tuning.Values };
            Send("event.ready", new { proposalRevision = (long)S.Convoy["eventProposal"]["revision"], loadoutRevision = (long?)me?["loadoutRevision"] ?? 0, ready = !ready, trialSetup });
        }

        /// <summary>The challenge trial the open proposal races, from this client's catalogue (null otherwise).</summary>
        ChallengeTrialDef ProposedTrial()
        {
            string id = (string)S.Convoy?["eventProposal"]?["settings"]?["challengeTrialId"];
            return string.IsNullOrEmpty(id) ? null : ContentLibrary.Load()?.Catalogue?.ChallengeTrials.Find(id);
        }

        void ChoosePost(string choice) => Send("postevent.choose", new { destinationRevision = (long)S.Convoy["postEvent"]["destinationRevision"], choice });

        void SignOut()
        {
            S.Dispose();
            App.Domain = SessionDomain.None;
            App.DisplayName = "";
            App.Router.Show(App.MainMenu, false);
        }
    }
}
