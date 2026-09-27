using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Local campaign map (Addendum 01 §4.1): the Shiranami region with every stage as a node on its road network.
    /// Cleared stages, the next stage and locked stages read at a glance; acts not reached yet stay veiled and their
    /// nodes hidden, and a newly opened act is revealed once. Selecting a node slides in the right panel with the stage,
    /// its live opposition, the (provisional) benchmark, personal records, car choice and Race.
    /// </summary>
    public sealed class CampaignMapScreen : UIScreen
    {
        public override string ScreenName => "Campaign";
        public override string MusicCue => "MUS_MENU_B";

        const float PanelWidth = 560f;
        static readonly Dictionary<int, Texture2D> PaintedByAct = new Dictionary<int, Texture2D>();

        CampaignMapData map;
        RectTransform mapArea;
        RawImage mapImage, revealImage;
        RectTransform nodesLayer, panel;
        TextMeshProUGUI title, subtitle, banner;
        Stepper mode;
        Button findNext;
        readonly Dictionary<string, Button> nodes = new Dictionary<string, Button>();
        readonly Dictionary<string, Image> nodeFills = new Dictionary<string, Image>();

        // Panel content
        TextMeshProUGUI pStage, pCourse, pOpposition, pTarget, pStatus, pRecord, pNote;
        Stepper carStepper;
        Button raceButton, closeButton;
        List<LocalCarChoice> carChoices = new List<LocalCarChoice>();

        string selected;
        int frontier = 1;
        int revealedAct = 1;
        float panelT = -1f;
        bool panelOpen;
        Task<Color32[]> painting;
        int paintingAct;
        float revealT = -1f, bannerT = -1f;

        LocalSession Session => LocalSession.Current;
        CampaignMode Mode => mode != null && mode.Index == 1 ? CampaignMode.Hard : CampaignMode.Normal;

        protected override void OnBuild(RectTransform root)
        {
            map = CampaignMapData.Load();
            UIFactory.Panel("Backdrop", root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, new Color(0.035f, 0.04f, 0.05f, 0.97f));

            // Map area (left): the painted region, aspect preserved, nodes on top in map units.
            RectTransform area = UIFactory.Rect("MapArea", root, new Vector2(0, 0), new Vector2(1, 1), new Vector2(24, 24), new Vector2(-24, -120));
            mapArea = area;
            mapImage = new GameObject("Map", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            mapImage.rectTransform.SetParent(area, false);
            UIFactory.Stretch(mapImage.rectTransform);
            mapImage.color = new Color(1, 1, 1, 0);
            var fitter = mapImage.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = map != null ? map.Size[0] / map.Size[1] : 5f / 3f;
            revealImage = new GameObject("Reveal", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            revealImage.rectTransform.SetParent(mapImage.rectTransform, false);
            UIFactory.Stretch(revealImage.rectTransform);
            revealImage.raycastTarget = false;
            revealImage.gameObject.SetActive(false);
            nodesLayer = UIFactory.Rect("Nodes", mapImage.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            // Header (top-left): title, domain line, mode and navigation.
            RectTransform header = UIFactory.Rect("Header", root, new Vector2(0, 1), new Vector2(1, 1), new Vector2(40, -112), new Vector2(-40, -12));
            title = UIFactory.Label("Title", header, "CAMPAIGN", SignalTheme.Heading, SignalTheme.Label, TextAlignmentOptions.TopLeft, true);
            title.rectTransform.anchorMin = new Vector2(0, 0.45f);
            title.rectTransform.anchorMax = new Vector2(0.45f, 1);
            title.rectTransform.offsetMin = title.rectTransform.offsetMax = Vector2.zero;
            subtitle = UIFactory.Label("Subtitle", header, "", SignalTheme.Small, SignalTheme.Label, TextAlignmentOptions.TopLeft);
            subtitle.rectTransform.anchorMin = new Vector2(0, 0);
            subtitle.rectTransform.anchorMax = new Vector2(0.6f, 0.42f);
            subtitle.rectTransform.offsetMin = subtitle.rectTransform.offsetMax = Vector2.zero;
            // Controls, right-aligned on one row: [Mode <  Normal  >] [Find Next Stage] [Back]
            RectTransform controls = UIFactory.Rect("Controls", header, new Vector2(1, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            mode = new Stepper(controls, "Mode", 2, i => i == 0 ? "Normal" : Session?.Profile != null && Session.Profile.Campaign.HardUnlocked ? "Hard" : "Hard (locked)", 0, 470);
            TopLeft((RectTransform)mode.Left.transform.parent, -1000);
            mode.Changed += _ => { RefreshNodes(); if (panelOpen && selected != null) FillPanel(selected); };
            findNext = UIFactory.Button("FindNext", controls, "Find Next Stage", FindNext, 300, 56);
            TopLeft((RectTransform)findNext.transform, -510);
            Button back = UIFactory.Button("Back", controls, "Back", () => App.Router.Show(App.OfflineHub, false), 180, 56);
            TopLeft((RectTransform)back.transform, -180);

            banner = UIFactory.Label("ActBanner", root, "", SignalTheme.Heading, SignalTheme.Label, TextAlignmentOptions.Center, true);
            banner.rectTransform.anchorMin = new Vector2(0.1f, 0.46f);
            banner.rectTransform.anchorMax = new Vector2(0.9f, 0.58f);
            banner.rectTransform.offsetMin = banner.rectTransform.offsetMax = Vector2.zero;
            banner.gameObject.SetActive(false);

            BuildPanel(root);
            if (map != null) BuildNodes();
        }

        static void TopLeft(RectTransform rt, float x)
        {
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, 0);
        }

        // ------------------------------------------------------------------ nodes

        void BuildNodes()
        {
            ContentCatalogue cat = Session?.Catalogue;
            if (map.Tutorial != null) AddNode(map.Tutorial.Id, map.Tutorial.Pos, 16f, "T");
            foreach (CampaignMapData.Node n in map.Nodes)
            {
                StageDef stage = cat?.Stages.FirstOrDefault(s => s.Id == n.Stage);
                bool featured = stage != null && StageBenchmark.IsFeaturedEncounter(stage.Type);
                AddNode(n.Stage, n.Pos, featured ? 26f : 18f, stage != null ? stage.Number.ToString("00") : n.Stage);
            }
        }

        void AddNode(string id, float[] pos, float size, string label)
        {
            RectTransform rt = UIFactory.Rect("Node-" + id, nodesLayer, new Vector2(pos[0] / map.Size[0], pos[1] / map.Size[1]),
                new Vector2(pos[0] / map.Size[0], pos[1] / map.Size[1]), Vector2.zero, Vector2.zero);
            rt.sizeDelta = new Vector2(44, 44); // generous hit target; the diamond inside is smaller
            var hit = rt.gameObject.AddComponent<Image>();
            hit.sprite = UIFactory.White;
            hit.color = new Color(0, 0, 0, 0);
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            Image ring = UIFactory.Panel("Focus", rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, SignalTheme.Label);
            ring.rectTransform.sizeDelta = new Vector2(size + 10, size + 10);
            ring.rectTransform.localRotation = Quaternion.Euler(0, 0, 45);
            ring.gameObject.AddComponent<SelectionIndicator>().Target = button;
            Image fill = UIFactory.Panel("Diamond", rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, SignalTheme.Rule);
            fill.rectTransform.sizeDelta = new Vector2(size, size);
            fill.rectTransform.localRotation = Quaternion.Euler(0, 0, 45);
            TextMeshProUGUI l = UIFactory.Label("Label", rt, label, SignalTheme.Small, SignalTheme.Label, TextAlignmentOptions.MidlineLeft);
            l.rectTransform.anchorMin = l.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            l.rectTransform.sizeDelta = new Vector2(60, 26);
            l.rectTransform.anchoredPosition = new Vector2(size * 0.5f + 40, 0);
            l.fontStyle = FontStyles.Bold;
            button.onClick.AddListener(() => Select(id));
            nodes[id] = button;
            nodeFills[id] = fill;
        }

        public override void OnShow()
        {
            if (Session?.Profile == null)
            {
                App.Router.Show(App.ProfileSelect, false);
                return;
            }
            LocalProfile p = Session.Profile;
            if (!p.Campaign.HardUnlocked && mode.Index == 1) mode.Set(0);
            mode.Set(mode.Index); // refresh the Hard label for this profile
            RefreshNodes();
            ClosePanel(true);
            int lastSeen = PlayerPrefs.GetInt(RevealKey(p), 1);
            if (revealedAct > lastSeen)
            {
                // A new act opened since this profile last looked at the map: reveal it once.
                PlayerPrefs.SetInt(RevealKey(p), revealedAct);
                ShowMap(lastSeen, false);
                BeginPaint(revealedAct);
                bannerT = 0f;
                banner.text = $"ACT {revealedAct} OPEN\n<size=60%>{string.Join("  ·  ", map.Regions.Where(r => r.Act == revealedAct).Select(r => r.Name))}</size>";
            }
            else ShowMap(revealedAct, true);
        }

        static string RevealKey(LocalProfile p) => "ns.campaign.revealedAct." + p.ProfileId;

        void RefreshNodes()
        {
            LocalProfile p = Session.Profile;
            ContentCatalogue cat = Session.Catalogue;
            bool[] cleared = p.Campaign.Flags(Mode);
            frontier = CampaignProgress.Frontier(cleared);
            // The map shows every act up to the Normal frontier's act (Hard never hides what Normal revealed).
            int normalFrontier = CampaignProgress.Frontier(p.Campaign.Flags(CampaignMode.Normal));
            revealedAct = normalFrontier > Limits.CampaignStages ? 4 : cat.Stages.First(s => s.Number == normalFrontier).Act;
            bool modeOpen = LocalProgression.CampaignAccess(p, Mode).ModeAllowed;
            string next = frontier <= Limits.CampaignStages ? CampaignProgress.StageLabel(frontier) : "all stages cleared";
            title.text = Mode == CampaignMode.Hard ? "CAMPAIGN  ·  HARD" : "CAMPAIGN  ·  NORMAL";
            subtitle.text = $"LOCAL / OFFLINE   ·   {p.DisplayName}   ·   {p.WalletBalance:N0} cr   ·   {p.ComputeRank().Name}   ·   next: {(modeOpen ? next : "Hard locked")}";
            subtitle.richText = false;

            foreach (KeyValuePair<string, Button> kv in nodes)
            {
                string id = kv.Key;
                Image fill = nodeFills[id];
                if (id == map.Tutorial?.Id)
                {
                    fill.color = p.Tutorial.Completed ? SignalTheme.Timing : SignalTheme.Caution;
                    continue;
                }
                StageDef stage = cat.Stages.FirstOrDefault(s => s.Id == id);
                if (stage == null) { kv.Value.gameObject.SetActive(false); continue; }
                bool visible = stage.Act <= revealedAct;
                kv.Value.gameObject.SetActive(visible);
                bool isCleared = p.Campaign.IsCleared(Mode, stage.Number);
                bool isNext = modeOpen && stage.Number == frontier;
                fill.color = !modeOpen ? SignalTheme.Rule
                    : isCleared ? SignalTheme.Timing
                    : isNext ? SignalTheme.Signal
                    : new Color(0.3f, 0.31f, 0.34f);
                kv.Value.GetComponentInChildren<TextMeshProUGUI>().color = isNext ? SignalTheme.Label : isCleared ? SignalTheme.Label : SignalTheme.LabelDim;
            }
        }

        void FindNext()
        {
            if (frontier > Limits.CampaignStages) return;
            string id = CampaignProgress.StageLabel(frontier);
            if (nodes.TryGetValue(id, out Button b) && b.gameObject.activeInHierarchy)
            {
                UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(b.gameObject);
                Select(id);
            }
        }

        public override Selectable DefaultFocus
        {
            get
            {
                string id = frontier <= Limits.CampaignStages ? CampaignProgress.StageLabel(frontier) : "S30";
                return nodes.TryGetValue(id, out Button b) && b.gameObject.activeSelf ? b : findNext;
            }
        }

        // ------------------------------------------------------------------ map texture

        void ShowMap(int act, bool paintIfMissing)
        {
            if (PaintedByAct.TryGetValue(act, out Texture2D tex))
            {
                mapImage.texture = tex;
                mapImage.color = Color.white;
            }
            else if (paintIfMissing) BeginPaint(act);
        }

        /// <summary>Paints off the main thread (pure C#), uploads when done; cached per revealed act.</summary>
        void BeginPaint(int act)
        {
            if (PaintedByAct.ContainsKey(act))
            {
                StartReveal(PaintedByAct[act]);
                return;
            }
            if (painting != null && paintingAct == act) return;
            paintingAct = act;
            CampaignMapData m = map;
            painting = Task.Run(() => CampaignMapPainter.PaintPixels(m, act, 1600));
        }

        void StartReveal(Texture2D tex)
        {
            if (mapImage.texture == null || SignalTheme.ReducedMotion)
            {
                mapImage.texture = tex;
                mapImage.color = Color.white;
                return;
            }
            revealImage.texture = tex;
            revealImage.color = new Color(1, 1, 1, 0);
            revealImage.gameObject.SetActive(true);
            revealT = 0f;
        }

        public override void Tick()
        {
            if (painting != null && painting.IsCompleted)
            {
                if (painting.Status == TaskStatus.RanToCompletion)
                {
                    Texture2D tex = CampaignMapPainter.ToTexture(painting.Result, 1600, CampaignMapPainter.HeightFor(map, 1600));
                    PaintedByAct[paintingAct] = tex;
                    StartReveal(tex);
                }
                else Debug.LogWarning("[NightSignal.Campaign] map painting failed: " + painting.Exception?.GetBaseException().Message);
                painting = null;
            }
            float dt = Time.unscaledDeltaTime;
            if (revealT >= 0f)
            {
                revealT += dt / 1.4f;
                revealImage.color = new Color(1, 1, 1, Mathf.SmoothStep(0, 1, revealT));
                if (revealT >= 1f)
                {
                    mapImage.texture = revealImage.texture;
                    mapImage.color = Color.white;
                    revealImage.gameObject.SetActive(false);
                    revealT = -1f;
                }
            }
            if (bannerT >= 0f)
            {
                bannerT += dt;
                banner.gameObject.SetActive(true);
                float a = bannerT < 0.4f ? bannerT / 0.4f : bannerT > 3.2f ? Mathf.Clamp01(1f - (bannerT - 3.2f) / 0.6f) : 1f;
                banner.alpha = a;
                if (bannerT > 3.8f) { bannerT = -1f; banner.gameObject.SetActive(false); }
            }
            // The next stage breathes so it can be found without reading.
            if (frontier <= Limits.CampaignStages && nodeFills.TryGetValue(CampaignProgress.StageLabel(frontier), out Image next) && !SignalTheme.ReducedMotion)
            {
                float s = 1f + 0.12f * Mathf.Sin(Time.unscaledTime * 3.2f);
                next.rectTransform.localScale = new Vector3(s, s, 1);
            }
            AnimatePanel(dt);
        }

        // ------------------------------------------------------------------ right panel

        void BuildPanel(RectTransform root)
        {
            Image bg = UIFactory.Panel("StagePanel", root, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-PanelWidth, 0), Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.96f));
            bg.raycastTarget = true;
            panel = bg.rectTransform;
            // Pivot on the right edge: anchoredPosition.x = 0 is flush with the screen edge, > 0 slides it off.
            panel.pivot = new Vector2(1, 0.5f);
            panel.offsetMin = new Vector2(-PanelWidth, 0);
            panel.offsetMax = new Vector2(0, -124); // below the header row, whose controls stay reachable
            UIFactory.Panel("Edge", panel, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, new Vector2(4, 0), SignalTheme.Signal);
            RectTransform col = UIFactory.Column("Content", panel, new Vector2(0, 0.03f), new Vector2(1, 0.97f), new Vector2(40, 0), new Vector2(-32, 0), 10f);
            pStage = UIFactory.Row("Stage", col, "", SignalTheme.Subheading, SignalTheme.Label, 480, 40, true);
            pCourse = UIFactory.Row("Course", col, "", SignalTheme.Body, SignalTheme.Label, 480, 64);
            pOpposition = UIFactory.Row("Opposition", col, "", SignalTheme.Small, SignalTheme.Label, 480, 120);
            pTarget = UIFactory.Row("Target", col, "", SignalTheme.Small, SignalTheme.LabelDim, 480, 64);
            pStatus = UIFactory.Row("Status", col, "", SignalTheme.Small, SignalTheme.Caution, 480, 52);
            pRecord = UIFactory.Row("Record", col, "", SignalTheme.Small, SignalTheme.Label, 480, 52);
            carStepper = new Stepper(col, "Car", 1, i => carChoices.Count == 0 ? "—" : CarLabel(carChoices[i]), 0, 480, 0.12f);
            raceButton = UIFactory.Button("Race", col, "Race", Race, 460, 60);
            closeButton = UIFactory.Button("Close", col, "Close", () => ClosePanel(false), 460, 48);
            pNote = UIFactory.Row("Note", col, "", SignalTheme.Small, SignalTheme.LabelDim, 480, 90);
            foreach (TextMeshProUGUI t in new[] { pCourse, pOpposition, pRecord }) t.richText = true;
            panel.anchoredPosition = new Vector2(PanelWidth + 40, 0);
            panel.gameObject.SetActive(false);
        }

        string CarLabel(LocalCarChoice c)
        {
            CarDef car = Session.Catalogue.Car(c.ModelId);
            OwnedCar owned = c.Loaner ? null : Session.Profile.FindCar(c.InstanceId);
            return (c.Loaner ? "Loaner  " : "") + $"{car.Name}  PI {(owned != null ? Session.AppliedPi(owned) : car.BasePI)}";
        }

        void Select(string id)
        {
            selected = id;
            FillPanel(id);
            if (!panelOpen)
            {
                panelOpen = true;
                panel.gameObject.SetActive(true);
                panelT = SignalTheme.ReducedMotion ? 1f : 0f;
            }
            EventSystemSelect(raceButton.interactable ? raceButton : closeButton);
        }

        static void EventSystemSelect(Selectable s) => UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(s.gameObject);

        void ClosePanel(bool instant)
        {
            if (!panelOpen && !instant) return;
            panelOpen = false;
            panelT = instant || SignalTheme.ReducedMotion ? -1f : 0f;
            if (panelT < 0f)
            {
                panel.anchoredPosition = new Vector2(PanelWidth + 40, 0);
                panel.gameObject.SetActive(false);
                mapArea.offsetMax = new Vector2(-24f, mapArea.offsetMax.y);
            }
            if (!instant && selected != null && nodes.TryGetValue(selected, out Button b)) EventSystemSelect(b);
        }

        public override bool OnBack()
        {
            if (!panelOpen) return false;
            ClosePanel(false);
            return true;
        }

        /// <summary>220 ms slide from the right with an 8 px overshoot (easeOutBack, c1 = 0.65); closes with a plain ease-in.</summary>
        void AnimatePanel(float dt)
        {
            if (panelT < 0f) return;
            panelT = Mathf.Min(1f, panelT + dt / 0.22f);
            float x;
            if (panelOpen)
            {
                const float c1 = 0.65f, c3 = c1 + 1f;
                float u = panelT - 1f;
                float e = 1f + c3 * u * u * u + c1 * u * u;
                x = (1f - e) * (PanelWidth + 40f);
            }
            else x = panelT * panelT * (PanelWidth + 40f);
            panel.anchoredPosition = new Vector2(x, 0);
            // The map makes room for the panel instead of hiding under it.
            float room = Mathf.Clamp01(1f - x / (PanelWidth + 40f));
            mapArea.offsetMax = new Vector2(-24f - room * PanelWidth, mapArea.offsetMax.y);
            if (panelT >= 1f)
            {
                if (!panelOpen) panel.gameObject.SetActive(false);
                panelT = -1f;
            }
        }

        void FillPanel(string id)
        {
            LocalProfile p = Session.Profile;
            ContentCatalogue cat = Session.Catalogue;
            carChoices.Clear();
            if (id == map.Tutorial?.Id)
            {
                CourseDef t = cat.Course(id);
                pStage.text = "TUTORIAL";
                pCourse.text = $"{Escape(t.Name)}\n<size=80%>{t.Format} · {t.TargetLengthKm:0.0} km</size>";
                pOpposition.text = "Learn the car, the reset and the finish window on the campus roads. No opponents.";
                pTarget.text = p.Tutorial.Completed ? "Completed. Repeats are for practice and do not pay." : "First completion pays 3,000 cr once.";
                pStatus.text = "";
                pRecord.text = "";
                FillCars(0);
                raceButton.interactable = carChoices.Count > 0;
                pNote.text = "";
                return;
            }
            StageDef stage = cat.Stage(id);
            CourseDef course = cat.Course(stage.Course);
            StageSide side = Mode == CampaignMode.Hard ? stage.Hard : stage.Normal;
            string type = stage.Type == "regular" ? "" : "  ·  " + stage.Type.ToUpperInvariant();
            pStage.text = $"{stage.Id}{type}";
            string region = map.Regions.FirstOrDefault(r => r.Id == course.Region)?.Name ?? course.Region;
            pCourse.text = $"{Escape(course.Name)}\n<size=80%>{Escape(region)}  ·  {course.Format}{(course.Laps > 1 ? $" ×{course.Laps}" : "")}  ·  {course.TargetLengthKm:0.0} km  ·  PI cap {stage.MaxPI}</size>";
            var lines = new List<string>();
            for (int i = 0; i < side.Opponents.Count; i++)
            {
                if (!cat.TryRival(side.Opponents[i], out RivalDef r)) continue;
                string car = cat.TryCar(r.PrimaryCar, out CarDef c) ? c.Name : r.PrimaryCar;
                lines.Add(i == 0 ? $"<color=#D7263D>Featured</color>  {Escape(r.Name)} <size=85%>({Escape(r.Crew)}, {Escape(car)})</size>" : $"{Escape(r.Name)} <size=85%>({Escape(car)})</size>");
            }
            pOpposition.text = $"{side.Opponents.Count} live rival{(side.Opponents.Count == 1 ? "" : "s")}, light contact\n" + string.Join("\n", lines.Take(4)) + (lines.Count > 4 ? $"\n+{lines.Count - 4} more" : "");
            StageBenchmark b = StageBenchmarks.Provisional(cat, stage, Mode);
            pTarget.text = b.Kind == BenchmarkKind.FourContracts
                ? $"Pass the four contracts within {ResultsScreen.FormatRaceTime(b.TargetTimeMs * 1000)} (provisional)."
                : $"Target {ResultsScreen.FormatRaceTime(b.TargetTimeMs * 1000)} (provisional benchmark)" + (b.RequiresBeatingFeaturedRival ? " and finish ahead of the featured rival." : ".");
            bool canStart = LocalProgression.CanStartCampaignStage(p, cat, stage.Id, Mode, out string reason);
            bool isCleared = p.Campaign.IsCleared(Mode, stage.Number);
            pStatus.text = isCleared ? "Cleared. Replays pay race money only." : canStart ? "Next stage." : reason;
            pStatus.color = isCleared ? SignalTheme.Timing : canStart ? SignalTheme.Signal : SignalTheme.Caution;
            RecordKey key = RecordKey.ForCampaignStage(ProgressionDomain.Local, stage, course, Mode, MetricKind.ElapsedTime,
                LocalEvents.Ruleset(new Race.RaceEventRules { Contact = ContactPolicy.LightContact, CarCapPi = stage.MaxPI }, ""));
            RecordEntry best = p.Records.Entries.FirstOrDefault(e => e.Key.EventId == key.EventId && e.Key.Difficulty == key.Difficulty && e.Key.Metric == MetricKind.ElapsedTime);
            pRecord.text = best != null ? $"Personal best  <b>{ResultsScreen.FormatRaceTime(best.Value * 1000)}</b>  <size=80%>(Local, unverified)</size>" : "No personal record yet.";
            FillCars(stage.MaxPI);
            raceButton.interactable = canStart && carChoices.Count > 0;
            pNote.text = side.StoryBeat ?? "";
        }

        /// <summary>Owned cars within the cap (best first); a loaner only when no owned car is legal.</summary>
        void FillCars(int capPi)
        {
            LocalProfile p = Session.Profile;
            ContentCatalogue cat = Session.Catalogue;
            // Cap checks use each instance's APPLIED build (an upgraded car may exceed a stage cap its model meets stock).
            foreach (OwnedCar car in p.Cars.OrderByDescending(c => Session.AppliedPi(c)))
                if (capPi <= 0 || Session.AppliedPi(car) <= capPi)
                    carChoices.Add(new LocalCarChoice { ModelId = car.ModelId, InstanceId = car.InstanceId });
            if (carChoices.Count == 0)
            {
                CarDef loaner = cat.Cars.Where(c => capPi <= 0 || c.BasePI <= capPi).OrderByDescending(c => c.Starter).ThenByDescending(c => c.BasePI).FirstOrDefault();
                if (loaner != null) carChoices.Add(new LocalCarChoice { ModelId = loaner.Id });
            }
            carStepper.SetCount(Mathf.Max(1, carChoices.Count));
            carStepper.Set(0);
        }

        void Race()
        {
            if (selected == null || carChoices.Count == 0) return;
            LocalCarChoice car = carChoices[Mathf.Clamp(carStepper.Index, 0, carChoices.Count - 1)];
            LocalEventPlan plan = selected == map.Tutorial?.Id
                ? LocalEvents.Tutorial(Session.Catalogue.Course(selected), car)
                : LocalEvents.Campaign(Session, Session.Catalogue.Stage(selected), Mode, car);
            App.StartLocalEvent(plan, this);
        }

        static string Escape(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");
    }
}
