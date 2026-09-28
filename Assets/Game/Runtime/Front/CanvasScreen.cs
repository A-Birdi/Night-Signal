using System.Collections.Generic;
using System.Linq;
using NightSignal.Content;
using NightSignal.Core.Toys;
using NightSignal.Core.Toys.Canvas;
using NightSignal.Toys;
using NightSignal.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Convoy Canvas (Addendum 02 §6): a shared vector sheet — freehand strokes, lines, rectangles, ellipses, lettering
    /// and stamps — with per-person undo/redo, new sheets, and whole-sheet clears only by consent (a checkpoint is kept).
    /// The authority owns the document; this screen rasterizes it (text is always rendered as literal text, never markup)
    /// and sends operations addressed to the sheet AND its epoch. Nothing here is progression.
    /// </summary>
    public sealed class CanvasScreen : UIScreen
    {
        public override string ScreenName => "Convoy Canvas";
        public override string MusicCue => "MUS_GARAGE";

        enum Tool { Pen, Line, Rect, Ellipse, Text, Stamp, Eraser }

        const int TexW = 1024, TexH = 512, Scale = CanvasLimits.SheetWidth / TexW;
        static readonly long[] Palette = { 0xFF1B1B1F, 0xFFD7263D, 0xFFF2A541, 0xFF3EC6D8, 0xFF6CC28A, 0xFFECE6D8, 0xFF7A4FD0, 0xFF2E5DB8 };

        ToyConnection toys;
        CanvasStampLibrary stamps;
        RawImage sheetImage;
        RectTransform textLayer;
        RectTransform screenRoot, padCursorMark;
        // Controller pen (the right stick moves it over the sheet, the right trigger draws; the left stick and d-pad keep
        // navigating the tool column). Whichever device moved last drives the pointer.
        Vector2 padCursor;
        bool padActive, padPlaced, padWasDown;
        /// <summary>True while the controller pen (not the mouse) drives the pointer (tours, HUD hint).</summary>
        public bool ControllerPen => padActive;
        /// <summary>The controller pen's screen position (tours).</summary>
        public Vector2 ControllerPenPosition => padCursor;
        Texture2D tex;
        Color32[] px;
        TextMeshProUGUI info, status;
        Stepper toolStep, widthStep, colourStep, stampStep, sheetStep;
        TMP_InputField textField;
        Tool tool = Tool.Pen;
        long drawnRevision = -1;
        string drawnSheet;
        /// <summary>
        /// One stroke being drawn: its points wait here until the authority names it (online that takes a round trip, and
        /// the next stroke may already have begun), then go out in ≤ 128-point chunks and the stroke is closed.
        /// </summary>
        sealed class StrokeDraft
        {
            public string Id;
            public bool Sent, Named, Failed, EndRequested, Ended;
            public int X, Y, Width;
            public long Color;
            public readonly List<int> Pending = new List<int>();
        }

        StrokeDraft stroke;
        // Core keeps one stroke in progress per person (a begin finalizes the previous one), so a stroke begun while the
        // previous one still waits for its name online is held here and begun right after that one is complete.
        StrokeDraft unnamed;
        readonly Queue<StrokeDraft> heldBegins = new Queue<StrokeDraft>();
        Vector2Int dragStart;
        bool dragging;
        readonly List<TextMeshProUGUI> labels = new List<TextMeshProUGUI>();
        public System.Collections.Generic.IEnumerator<Vector2Int[]> AutoDraw;
        public int MyObjects => Sheet()?.Objects.Count(o => !o.Deleted && o.Author == toys?.Member) ?? 0;
        /// <summary>Points in my strokes as the authority holds them (a dropped chunk shows up here, not in the object count).</summary>
        public int MyStrokePoints => Sheet()?.Objects.Where(o => !o.Deleted && o.Author == toys?.Member && o.Kind == CanvasObjectKind.Stroke).Sum(o => (o.Points?.Length ?? 0) / 2) ?? 0;
        /// <summary>Marks on the shared sheet made by anyone else.</summary>
        public int OthersObjects => Sheet()?.Objects.Count(o => !o.Deleted && o.Author != toys?.Member) ?? 0;

        protected override void OnBuild(RectTransform root)
        {
            Image left = UIFactory.Panel("Tools", root, new Vector2(0, 0.08f), new Vector2(0.24f, 0.97f), new Vector2(24, 0), Vector2.zero, new Color(0.04f, 0.045f, 0.05f, 0.92f));
            RectTransform col = UIFactory.Column("ToolColumn", left.transform, Vector2.zero, Vector2.one, new Vector2(24, 20), new Vector2(-16, -24), 8f);
            UIFactory.Row("Title", col, "CONVOY CANVAS", SignalTheme.Heading, SignalTheme.Label, 400, 0, true);
            UIFactory.Row("Domain", col, "While We Wait · draw together. For fun only — it never pays, ranks or unlocks.", SignalTheme.Small, SignalTheme.Caution, 400, 48);
            toolStep = new Stepper(col, "Tool", 7, i => ((Tool)i).ToString(), 0, 400, 0.24f);
            toolStep.Changed += i => tool = (Tool)i;
            colourStep = new Stepper(col, "Colour", Palette.Length, i => new[] { "Ink", "Signal red", "Amber", "Timing cyan", "Green", "Paper white", "Violet", "Blue" }[i], 1, 400, 0.24f);
            widthStep = new Stepper(col, "Width", 6, i => new[] { 2, 4, 8, 14, 24, 40 }[i] + " px", 2, 400, 0.24f);
            stampStep = new Stepper(col, "Stamp", 1, i => stamps != null && i < stamps.Stamps.Count ? stamps.Stamps[i].Name : "—", 0, 400, 0.24f);
            textField = UIFactory.InputField("CanvasText", col, "Lettering (then click the sheet)", false, CanvasLimits.MaxTextLength, 380, 50);
            UIFactory.Button("Undo", col, "Undo (mine)", () => Send("undo", new JObject()), 380, 44);
            UIFactory.Button("Redo", col, "Redo (mine)", () => Send("redo", new JObject()), 380, 44);
            sheetStep = new Stepper(col, "Sheet", 1, i => Doc() != null && i < Doc().Sheets.Count ? Doc().Sheets[i].Name : "—", 0, 400, 0.24f);
            sheetStep.Changed += _ => drawnRevision = -1;
            UIFactory.Button("NewSheet", col, "New Sheet", () => Send("sheet.new", new JObject { ["name"] = "Sheet " + ((Doc()?.Sheets.Count ?? 0) + 1) }), 380, 44);
            UIFactory.Button("ProposeClear", col, "Propose Clear", () => SendOnSheet("clear.propose", new JObject()), 380, 44);
            UIFactory.Button("Back", col, "Leave the Canvas", () => App.Router.Back(), 380, 48);
            status = UIFactory.Row("Status", col, "", SignalTheme.Small, SignalTheme.Caution, 400, 44);

            // The sheet (2:1), with a literal-text overlay.
            RectTransform area = UIFactory.Rect("SheetArea", root, new Vector2(0.26f, 0.12f), new Vector2(0.99f, 0.9f), Vector2.zero, Vector2.zero);
            sheetImage = new GameObject("Sheet", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            sheetImage.rectTransform.SetParent(area, false);
            UIFactory.Stretch(sheetImage.rectTransform);
            var fit = sheetImage.gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fit.aspectRatio = 2f;
            sheetImage.raycastTarget = true;
            textLayer = UIFactory.Rect("TextLayer", sheetImage.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            info = UIFactory.Label("Info", root, "", SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.Center);
            info.rectTransform.anchorMin = new Vector2(0.26f, 0.03f);
            info.rectTransform.anchorMax = new Vector2(0.99f, 0.1f);
            info.rectTransform.offsetMin = info.rectTransform.offsetMax = Vector2.zero;
            info.richText = true;
            screenRoot = root;
            // The controller pen's crosshair (hidden until a controller moves it).
            padCursorMark = UIFactory.Rect("ControllerPen", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            padCursorMark.sizeDelta = new Vector2(30, 30);
            foreach ((Vector2 min, Vector2 max) in new[] { (new Vector2(-15, -2), new Vector2(15, 2)), (new Vector2(-2, -15), new Vector2(2, 15)) })
            {
                Image bar = UIFactory.Panel("Bar", padCursorMark, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), min, max, SignalTheme.Signal);
                bar.raycastTarget = false;
            }
            padCursorMark.gameObject.SetActive(false);
            tex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            px = new Color32[TexW * TexH];
            sheetImage.texture = tex;
        }

        public override Selectable DefaultFocus => toolStep.Left;

        public override void OnShow()
        {
            ToyContent all = ContentLibrary.Load()?.Toys;
            stamps = all?.Stamps;
            toys = all != null ? ToyConnection.Create(all, App) : null;
            if (toys == null) { status.text = "Open a Local profile (or join a convoy) first."; return; }
            toys.Enter(ToyActivityId.Canvas);
            stampStep.SetCount(stamps.Stamps.Count);
            drawnRevision = -1;
            sheetLabelled = false;
        }

        public override void OnHide()
        {
            if (stroke != null) EndStroke();
            toys?.Leave(ToyActivityId.Canvas);
            toys = null;
        }

        bool sheetLabelled;

        CanvasDocument Doc() => toys?.State<CanvasDocument>(ToyActivityId.Canvas);

        CanvasSheet Sheet()
        {
            CanvasDocument d = Doc();
            if (d == null || d.Sheets.Count == 0) return null;
            if (sheetStep.Count != d.Sheets.Count || !sheetLabelled) { sheetStep.SetCount(d.Sheets.Count); sheetLabelled = true; } // label once the document exists
            return d.Sheets[Mathf.Clamp(sheetStep.Index, 0, d.Sheets.Count - 1)];
        }

        public override void Tick()
        {
            if (toys == null) return;
            toys.Tick();
            CanvasDocument d = Doc();
            CanvasSheet s = Sheet();
            if (d == null || s == null) { info.text = "Opening the canvas…"; return; }
            HandleInput(s);
            if (d.Revision != drawnRevision || s.SheetId != drawnSheet || dragging) Redraw(d, s);
            info.text = $"{s.Name}  ·  {s.Objects.Count(o => !o.Deleted)} marks  ·  {d.Sheets.Count} sheet(s)  ·  " +
                        (padActive ? $"right stick moves the pen, hold RT to use the {tool} tool" : $"click and drag on the sheet with the {tool} tool  ·  controller: right stick + RT");
            if (!string.IsNullOrEmpty(toys.Status)) status.text = toys.Status;
        }

        // ------------------------------------------------------------------ input

        bool SheetPoint(Vector2 screen, out Vector2Int p)
        {
            p = default;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(sheetImage.rectTransform, screen, null, out Vector2 local)) return false;
            Rect r = sheetImage.rectTransform.rect;
            float u = (local.x - r.xMin) / r.width, v = 1f - (local.y - r.yMin) / r.height; // sheet y grows downward
            if (u < 0 || u > 1 || v < 0 || v > 1) return false;
            p = new Vector2Int(Mathf.Clamp((int)(u * CanvasLimits.SheetWidth), 0, CanvasLimits.SheetWidth - 1), Mathf.Clamp((int)(v * CanvasLimits.SheetHeight), 0, CanvasLimits.SheetHeight - 1));
            return true;
        }

        void HandleInput(CanvasSheet s)
        {
            if (AutoDraw != null)
            {
                // Tours draw scripted strokes through the same operations a player produces (automation, labelled).
                if (AutoDraw.MoveNext() && AutoDraw.Current != null)
                {
                    Vector2Int[] scripted = AutoDraw.Current;
                    BeginStroke(scripted[0]);
                    foreach (Vector2Int q in scripted.Skip(1)) stroke.Pending.AddRange(new[] { q.x, q.y });
                    FlushStroke(true);
                    EndStroke();
                }
                return;
            }
            if (!ReadPointer(out Vector2 screen, out bool pressedNow, out bool down, out bool up)) return;
            bool inSheet = SheetPoint(screen, out Vector2Int p);
            if (pressedNow && inSheet && !(EventSystem.current?.currentSelectedGameObject?.GetComponent<TMP_InputField>() != null && tool != Tool.Text))
            {
                dragging = true;
                dragStart = p;
                switch (tool)
                {
                    case Tool.Pen: BeginStroke(p); break;
                    case Tool.Text:
                        if (!string.IsNullOrWhiteSpace(textField.text))
                            SendOnSheet("text.add", new JObject { ["text"] = textField.text.Trim(), ["color"] = Palette[colourStep.Index], ["size"] = 96, ["points"] = new JArray(p.x, p.y) });
                        dragging = false;
                        break;
                    case Tool.Stamp:
                        SendOnSheet("stamp.add", new JObject { ["stamp"] = stamps.Stamps[stampStep.Index].Id, ["color"] = Palette[colourStep.Index], ["size"] = 220, ["points"] = new JArray(p.x, p.y) });
                        dragging = false;
                        break;
                    case Tool.Eraser:
                        CanvasObject hit = s.Objects.LastOrDefault(o => !o.Deleted && o.Author == toys.Member && Near(o, p));
                        if (hit != null) SendOnSheet("object.erase", new JObject { ["object"] = hit.Id });
                        dragging = false;
                        break;
                }
            }
            if (dragging && tool == Tool.Pen && down && inSheet && stroke != null)
            {
                List<int> pending = stroke.Pending;
                int n = pending.Count;
                if (n < 2 || Mathf.Abs(pending[n - 2] - p.x) + Mathf.Abs(pending[n - 1] - p.y) > 10) pending.AddRange(new[] { p.x, p.y });
                if (pending.Count >= 64) FlushStroke(false);
            }
            if (dragging && up)
            {
                dragging = false;
                if (tool == Tool.Pen) { FlushStroke(true); EndStroke(); }
                else if (tool == Tool.Line || tool == Tool.Rect || tool == Tool.Ellipse)
                {
                    Vector2Int end = inSheet ? p : dragStart;
                    SendOnSheet("shape.add", new JObject { ["shape"] = tool.ToString(), ["color"] = Palette[colourStep.Index], ["width"] = Width(), ["points"] = new JArray(dragStart.x, dragStart.y, end.x, end.y) });
                }
            }
        }

        /// <summary>
        /// The pointer this frame from the mouse or the controller pen, whichever moved last: screen position and the
        /// press edge / held / release edge of its button (left mouse button, or the right trigger past half travel).
        /// </summary>
        bool ReadPointer(out Vector2 screen, out bool pressed, out bool down, out bool up)
        {
            Mouse m = Mouse.current;
            Gamepad g = Gamepad.current;
            if (m != null && (m.delta.ReadValue().sqrMagnitude > 1f || m.leftButton.wasPressedThisFrame)) padActive = false;
            Vector2 stick = g != null ? g.rightStick.ReadValue() : Vector2.zero;
            float trigger = g != null ? g.rightTrigger.ReadValue() : 0f;
            if (g != null && (stick.magnitude > 0.2f || trigger > 0.5f)) padActive = true;
            if (padActive && g != null)
            {
                Rect r = ScreenRect(sheetImage.rectTransform);
                if (!padPlaced) { padCursor = r.center; padPlaced = true; }
                float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
                if (stick.magnitude > 0.15f) padCursor += stick * stick.magnitude * 900f * dt * (Screen.height / 1080f);
                padCursor = new Vector2(Mathf.Clamp(padCursor.x, r.xMin, r.xMax), Mathf.Clamp(padCursor.y, r.yMin, r.yMax));
                bool isDown = trigger > 0.5f;
                pressed = isDown && !padWasDown;
                up = !isDown && padWasDown;
                down = isDown;
                padWasDown = isDown;
                screen = padCursor;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(screenRoot, screen, null, out Vector2 local))
                    padCursorMark.anchoredPosition = local;
                padCursorMark.gameObject.SetActive(true);
                return true;
            }
            padCursorMark.gameObject.SetActive(false);
            padWasDown = false;
            if (m == null) { screen = default; pressed = down = up = false; return false; }
            screen = m.position.ReadValue();
            pressed = m.leftButton.wasPressedThisFrame;
            down = m.leftButton.isPressed;
            up = m.leftButton.wasReleasedThisFrame;
            return true;
        }

        static Rect ScreenRect(RectTransform rt)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners); // screen-space overlay: world corners are screen pixels
            return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
        }

        int Width() => new[] { 2, 4, 8, 14, 24, 40 }[widthStep.Index];

        static bool Near(CanvasObject o, Vector2Int p)
        {
            if (o.Points == null) return false;
            for (int i = 0; i + 1 < o.Points.Length; i += 2)
            {
                Vec2 q = CanvasGeometry.Transform(o, o.Points[i], o.Points[i + 1]);
                if (Mathf.Abs((float)q.X - p.x) < 60 && Mathf.Abs((float)q.Y - p.y) < 60) return true;
            }
            return false;
        }

        void BeginStroke(Vector2Int p)
        {
            var d = new StrokeDraft { X = p.x, Y = p.y, Color = Palette[colourStep.Index], Width = Width() };
            stroke = d;
            if (unnamed != null || heldBegins.Count > 0) heldBegins.Enqueue(d);
            else SendBegin(d);
        }

        void SendBegin(StrokeDraft d)
        {
            d.Sent = true;
            unnamed = d;
            SendOnSheet("stroke.begin", new JObject { ["color"] = d.Color, ["width"] = d.Width, ["points"] = new JArray(d.X, d.Y) }, a =>
            {
                d.Named = true;
                d.Id = a.Accepted ? a.Value : null;
                d.Failed = d.Id == null;
                Flush(d, false); // points drawn while the name was on its way
                // Released before the name arrived: send this stroke's points, then close it.
                if (d.EndRequested) Finish(d);
                if (unnamed == d && (d.Ended || d.Failed)) BeginNextHeld();
            });
        }

        /// <summary>The previous stroke is complete on the wire: begin the next held one (its points follow once named).</summary>
        void BeginNextHeld()
        {
            unnamed = null;
            if (heldBegins.Count > 0) SendBegin(heldBegins.Dequeue());
        }

        /// <summary>Appends buffered points (≤ 128 per operation); on release everything left is sent before the end.</summary>
        void FlushStroke(bool all) => Flush(stroke, all);

        void Flush(StrokeDraft d, bool all)
        {
            if (d == null || d.Id == null) return;
            while (d.Pending.Count > 0)
            {
                int take = Mathf.Min(d.Pending.Count, CanvasLimits.MaxPointsPerOp * 2);
                var pts = new JArray(d.Pending.Take(take).Select(v => (object)v).ToArray());
                d.Pending.RemoveRange(0, take);
                SendOnSheet("stroke.append", new JObject { ["object"] = d.Id, ["points"] = pts });
                if (!all) break;
            }
        }

        void EndStroke()
        {
            StrokeDraft d = stroke;
            stroke = null;
            if (d == null) return;
            d.EndRequested = true;
            if (!d.Named) return; // the begin answer finishes it
            Finish(d);
            if (unnamed == d) BeginNextHeld();
        }

        void Finish(StrokeDraft d)
        {
            if (d.Ended) return;
            d.Ended = true;
            if (d.Failed) return; // the authority refused the stroke; its points were never shown as accepted
            Flush(d, true);
            SendOnSheet("stroke.end", new JObject { ["object"] = d.Id });
        }

        void SendOnSheet(string kind, JObject payload, System.Action<ToyAnswer> done = null)
        {
            CanvasSheet s = Sheet();
            if (s == null) return;
            payload["sheet"] = s.SheetId;
            payload["sheetEpoch"] = s.Epoch;
            Send(kind, payload, done);
        }

        void Send(string kind, JObject payload, System.Action<ToyAnswer> done = null)
        {
            toys?.Send(ToyActivityId.Canvas, kind, payload, a =>
            {
                if (!a.Accepted) status.text = a.Reason;
                done?.Invoke(a);
            });
        }

        // ------------------------------------------------------------------ raster

        void Redraw(CanvasDocument d, CanvasSheet s)
        {
            drawnRevision = d.Revision;
            drawnSheet = s.SheetId;
            var paper = new Color32(236, 230, 216, 255);
            for (int i = 0; i < px.Length; i++) px[i] = paper;
            foreach (TextMeshProUGUI l in labels) Object.Destroy(l.gameObject);
            labels.Clear();
            foreach (CanvasObject o in s.Objects)
            {
                if (o.Deleted || o.Points == null || o.Points.Length < 2) continue;
                Color32 c = ToColor(o.Color);
                float w = Mathf.Max(1f, o.Width * (o.Scale / 1000f) / Scale);
                switch (o.Kind)
                {
                    case CanvasObjectKind.Stroke:
                    case CanvasObjectKind.Line:
                        for (int i = 2; i + 1 < o.Points.Length; i += 2) Segment(Pt(o, o.Points[i - 2], o.Points[i - 1]), Pt(o, o.Points[i], o.Points[i + 1]), w, c);
                        if (o.Points.Length == 2) Segment(Pt(o, o.Points[0], o.Points[1]), Pt(o, o.Points[0], o.Points[1]), w, c);
                        break;
                    case CanvasObjectKind.Rect:
                        {
                            int x0 = o.Points[0], y0 = o.Points[1], x1 = o.Points[2], y1 = o.Points[3];
                            Vector2 a = Pt(o, x0, y0), b = Pt(o, x1, y0), cc = Pt(o, x1, y1), e = Pt(o, x0, y1);
                            Segment(a, b, w, c); Segment(b, cc, w, c); Segment(cc, e, w, c); Segment(e, a, w, c);
                            break;
                        }
                    case CanvasObjectKind.Ellipse:
                        {
                            float cx = (o.Points[0] + o.Points[2]) / 2f, cy = (o.Points[1] + o.Points[3]) / 2f;
                            float rx = Mathf.Abs(o.Points[2] - o.Points[0]) / 2f, ry = Mathf.Abs(o.Points[3] - o.Points[1]) / 2f;
                            Vector2 prev = Pt(o, cx + rx, cy);
                            for (int k = 1; k <= 48; k++)
                            {
                                float t = k / 48f * Mathf.PI * 2;
                                Vector2 q = Pt(o, cx + Mathf.Cos(t) * rx, cy + Mathf.Sin(t) * ry);
                                Segment(prev, q, w, c);
                                prev = q;
                            }
                            break;
                        }
                    case CanvasObjectKind.Stamp:
                        {
                            StampDef def = stamps?.Stamps.FirstOrDefault(x => x.Id == o.StampId);
                            if (def == null) break;
                            float half = o.Size / 2f;
                            foreach (double[] path in def.Paths)
                                for (int i = 2; i + 1 < path.Length; i += 2)
                                    Segment(Pt(o, o.Points[0] + path[i - 2] * half, o.Points[1] - path[i - 1] * half),
                                            Pt(o, o.Points[0] + path[i] * half, o.Points[1] - path[i + 1] * half), Mathf.Max(2f, o.Size / 60f), c);
                            break;
                        }
                    case CanvasObjectKind.Text:
                        Label(o, c);
                        break;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false);
        }

        static Color32 ToColor(long argb) => new Color32((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

        /// <summary>Sheet point (after the object's transform) → texture pixels (texture y grows upward).</summary>
        static Vector2 Pt(CanvasObject o, double x, double y)
        {
            Vec2 q = CanvasGeometry.Transform(o, x, y);
            return new Vector2((float)q.X / Scale, TexH - 1 - (float)q.Y / Scale);
        }

        void Segment(Vector2 a, Vector2 b, float width, Color32 c)
        {
            float r = width * 0.5f;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / Mathf.Max(0.5f, r * 0.5f)));
            for (int k = 0; k <= steps; k++) Disc(Vector2.Lerp(a, b, k / (float)steps), r, c);
        }

        void Disc(Vector2 p, float r, Color32 c)
        {
            int x0 = Mathf.FloorToInt(p.x - r - 1), x1 = Mathf.CeilToInt(p.x + r + 1);
            int y0 = Mathf.FloorToInt(p.y - r - 1), y1 = Mathf.CeilToInt(p.y + r + 1);
            for (int y = Mathf.Max(0, y0); y <= Mathf.Min(TexH - 1, y1); y++)
            for (int x = Mathf.Max(0, x0); x <= Mathf.Min(TexW - 1, x1); x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), p);
                float cover = Mathf.Clamp01(r + 0.5f - d);
                if (cover <= 0f) continue;
                int i = y * TexW + x;
                px[i] = Color32.Lerp(px[i], c, cover * c.a / 255f);
            }
        }

        void Label(CanvasObject o, Color32 c)
        {
            Vec2 q = CanvasGeometry.Transform(o, o.Points[0], o.Points[1]);
            TextMeshProUGUI l = UIFactory.Label("Text-" + o.Id, textLayer, o.Text, SignalTheme.Body, c, TextAlignmentOptions.BottomLeft);
            l.richText = false; // lettering is always literal text, never markup
            l.rectTransform.anchorMin = l.rectTransform.anchorMax = new Vector2((float)q.X / CanvasLimits.SheetWidth, 1f - (float)q.Y / CanvasLimits.SheetHeight);
            l.rectTransform.pivot = new Vector2(0, 0);
            l.rectTransform.sizeDelta = new Vector2(1200, 200);
            l.rectTransform.localRotation = Quaternion.Euler(0, 0, -o.Rot / 100f);
            UIFactory.Resize(l, o.Size * (o.Scale / 1000f) * sheetImage.rectTransform.rect.height / CanvasLimits.SheetHeight, exact: true);
            labels.Add(l);
        }
    }
}
