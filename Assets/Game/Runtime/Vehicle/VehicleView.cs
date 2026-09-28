using System.Collections.Generic;
using System.Linq;
using NightSignal.Art;
using UnityEngine;

namespace NightSignal.Vehicle
{
    /// <summary>
    /// Visual representation of a simulated car. Rendering interpolates between the previous and current fixed
    /// ticks; wheels steer, spin and follow suspension travel from telemetry; a small body lean is derived from
    /// lateral/longitudinal acceleration. Visual only — it never feeds back into the simulation.
    /// </summary>
    public sealed class VehicleView : MonoBehaviour
    {
        VehicleParams p;
        Transform body;
        readonly Transform[] wheels = new Transform[4];
        readonly Transform[] wheelSpin = new Transform[4];
        readonly float[] spinAngle = new float[4];
        Vector3 leanVelocity;
        Vector2 lean;
        Material paint;
        readonly System.Collections.Generic.List<Object> owned = new System.Collections.Generic.List<Object>(); // per-car materials and meshes
        Light[] headlights;

        public Material Paint => paint;

        /// <summary>The appearance this view was built with.</summary>
        public CarAppearance Appearance { get; private set; }
        /// <summary>The body (leans with load transfer): hood and cockpit viewpoints ride on it.</summary>
        public Transform Body => body;
        /// <summary>Front-wheel steering angle last rendered (radians) — the cockpit wheel turns with it.</summary>
        public float SteerRad { get; private set; }
        public CarBodyDef Def { get; private set; }
        public VehicleParams Params => p;
        /// <summary>The fitted cockpit, once built (local driver only).</summary>
        public CockpitRig Cockpit { get; private set; }
        CarMaterialSet materials;

        /// <summary>Builds this car's fitted cockpit on first use (the local driver's car; opponents never need one).</summary>
        public CockpitRig EnsureCockpit()
        {
            if (Cockpit == null) Cockpit = CockpitBuilder.Build(body, Def, p, materials, owned);
            return Cockpit;
        }

        Mesh closedBody, openBody;
        LODGroup lodGroup;
        float[] lodHeights; // the group's screen-relative transition heights, finest first
        int lodLevel = -1;  // the level forced on the group (−1 until first chosen)
        /// <summary>The car's levels of detail (tests and evidence read the meshes and transition heights).</summary>
        public LODGroup Lods => lodGroup;
        /// <summary>The level this car is drawn at: 0 full, 1 mid, 2 far (−1 before the first frame).</summary>
        public int LodLevel => lodLevel;
        /// <summary>A level held regardless of distance (<see cref="HoldLod"/>); −1 chooses by distance.</summary>
        public int LodHold { get; private set; } = -1;
        /// <summary>The camera whose view chooses every car's level (null: <see cref="Camera.main"/>).</summary>
        public static Camera LodCamera;
        /// <summary>A finer level needs this much more screen height than its transition, so a car at a boundary does not flicker.</summary>
        public const float LodHysteresis = 1.1f;

        /// <summary>Holds a level regardless of distance (inspection sheets and evidence); −1 returns to choosing by distance.</summary>
        public void HoldLod(int level)
        {
            LodHold = level;
            if (level >= 0) ForceLod(level);
            else lodLevel = -1;
        }

        void ForceLod(int level)
        {
            if (lodGroup == null || level == lodLevel) return;
            lodGroup.ForceLOD(level);
            lodLevel = level;
        }

        void LateUpdate()
        {
            if (lodGroup == null || LodHold >= 0) return;
            Camera cam = LodCamera != null && LodCamera.isActiveAndEnabled ? LodCamera : Camera.main;
            UpdateLod(cam);
        }

        /// <summary>
        /// Chooses the level from <paramref name="cam"/> and forces it on the group. The GPU Resident Drawer does not
        /// re-evaluate a moving LODGroup's automatic level — measured in the built player: a car kept the level it had
        /// when the group last changed — so every car's level is chosen here, each frame, by Unity's own screen-height
        /// rule (with <see cref="LodHysteresis"/>), and forced only when it changes. No camera: the full body.
        /// </summary>
        public void UpdateLod(Camera cam)
        {
            if (lodGroup == null) return;
            if (LodHold >= 0) { ForceLod(LodHold); return; }
            ForceLod(cam == null ? 0 : ChooseLod(lodHeights, RelativeHeight(cam), Mathf.Max(lodLevel, 0)));
        }

        /// <summary>The car's height on screen as Unity's LOD rule measures it (group size over the view height at its distance, × the LOD bias).</summary>
        public float RelativeHeight(Camera cam)
        {
            Vector3 s = transform.lossyScale;
            float size = lodGroup.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            if (cam.orthographic) return size * 0.5f / cam.orthographicSize * QualitySettings.lodBias;
            float d = Vector3.Distance(cam.transform.position, transform.TransformPoint(lodGroup.localReferencePoint));
            return size * 0.5f / (Mathf.Max(d, 1e-4f) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) * QualitySettings.lodBias;
        }

        /// <summary>
        /// The finest level whose transition height <paramref name="h"/> reaches; a switch to a finer level than
        /// <paramref name="current"/> needs <see cref="LodHysteresis"/> times its height. Never culls: below the last
        /// transition the far body stays (a car that far away is a few pixels and some four thousand triangles).
        /// </summary>
        public static int ChooseLod(float[] heights, float h, int current)
        {
            int plain = heights.Length - 1;
            for (int i = 0; i < heights.Length; i++)
                if (h >= heights[i]) { plain = i; break; }
            if (plain >= current) return plain;
            for (int i = 0; i < current; i++)
                if (h >= heights[i] * LodHysteresis) return i;
            return current;
        }

        /// <summary>
        /// Cockpit view on/off for this car: shows the fitted cabin and swaps to the open-cabin body (no lid over the
        /// interior); off restores the closed body the other views and other players see.
        /// </summary>
        public void SetCockpitMode(bool on)
        {
            MeshFilter mf = body != null ? body.GetComponent<MeshFilter>() : null;
            if (mf == null) return;
            if (closedBody == null) closedBody = mf.sharedMesh;
            if (on)
            {
                EnsureCockpit();
                EnsureOpenBody();
            }
            mf.sharedMesh = on ? openBody : closedBody;
            Cockpit?.SetVisible(on);
        }

        /// <summary>
        /// Builds the fitted cockpit and the open-cabin body ahead of time, hidden, so the first switch to Cockpit view does not
        /// stall a frame mid-race (the detailed body takes some milliseconds to generate). Called when a camera takes this car.
        /// </summary>
        public void PrepareCockpit()
        {
            MeshFilter mf = body != null ? body.GetComponent<MeshFilter>() : null;
            if (mf == null) return;
            if (closedBody == null) closedBody = mf.sharedMesh;
            bool showing = openBody != null && mf.sharedMesh == openBody;
            EnsureCockpit();
            EnsureOpenBody();
            Cockpit?.SetVisible(showing);
        }

        void EnsureOpenBody()
        {
            if (openBody != null) return;
            openBody = CarBodyGenerator.BuildBody(Def, p, Appearance, openCabin: true);
            owned.Add(openBody);
        }

        public static VehicleView Create(string name, VehicleParams p, CarBodyDef body, CarMaterialSet mats, Color paintColor, CarAppearance appearance = null)
        {
            var root = new GameObject(name);
            var view = root.AddComponent<VehicleView>();
            view.Build(p, body, mats, appearance ?? CarAppearance.Stock(paintColor));
            return view;
        }

        Material Instance(Material source, string name, Color color, string finish)
        {
            var m = new Material(source) { name = name };
            m.SetColor("_BaseColor", color);
            if (finish != null)
            {
                (float metallic, float smoothness) = CarAppearance.FinishValues(finish);
                m.SetFloat("_Metallic", metallic);
                m.SetFloat("_Smoothness", smoothness);
            }
            owned.Add(m);
            return m;
        }

        void Build(VehicleParams parameters, CarBodyDef def, CarMaterialSet mats, CarAppearance a)
        {
            p = parameters;
            Appearance = a;
            Def = def;
            materials = mats;
            paint = Instance(mats.Paint, $"{def.Id}_Paint", a.Primary, a.Finish);
            CarMaterials cm = mats.ForPaint(paint);
            cm.Paint2 = Instance(mats.Paint, $"{def.Id}_Paint2", a.Secondary, a.Finish);
            cm.Accent = Instance(mats.Paint, $"{def.Id}_Accent", a.Accent, "satin");
            cm.Rim = Instance(mats.Rim, $"{def.Id}_Rim", a.RimColor, a.RimFinish);
            if (a.GlassTransmission < 0.89f) cm.Glass = Instance(mats.Glass, $"{def.Id}_Glass", CarAppearance.GlassTint(a.GlassTransmission, mats.Glass.GetColor("_BaseColor")), null);
            if (a.HeadTint != "clear") cm.HeadLamp = Instance(mats.HeadLamp, $"{def.Id}_Head", CarAppearance.LampTint(a.HeadTint, mats.HeadLamp.GetColor("_BaseColor")), null);
            if (a.TailTint != "clear") cm.TailLamp = Instance(mats.TailLamp, $"{def.Id}_Tail", CarAppearance.LampTint(a.TailTint, mats.TailLamp.GetColor("_BaseColor")), null);

            // Model space has the ground at y = 0; the simulation origin is the CG.
            float groundOffset = p.CgHeightM - StaticCompression();
            body = new GameObject("Body").transform;
            body.SetParent(transform, false);
            body.localPosition = new Vector3(0f, -groundOffset, 0f);
            Mesh built = CarBodyGenerator.BuildBody(def, p, a);
            owned.Add(built);
            body.gameObject.AddComponent<MeshFilter>().sharedMesh = built;
            body.gameObject.AddComponent<MeshRenderer>().sharedMaterials = cm.BodyArray;
            if (!string.IsNullOrEmpty(a.PlateText)) Plate(def, a, mats.Trim);
            CarDecals.Build(body, def, p, a.Decals, mats.Paint, owned, a.Primary);

            // Levels of detail (spec §15): the distant bodies share the paint and the body transform (so they lean too).
            // The livery decals stay at mid distance (they sit on the full body's surface; the coarser loft lies inside it on
            // convex panels, so they never sink); the plate shows only up close; the wheels belong to every level.
            var lodBodies = new List<Renderer>();
            for (int lod = 1; lod <= 2; lod++)
            {
                Mesh lodMesh = CarBodyGenerator.BuildBody(def, p, a, false, lod);
                owned.Add(lodMesh);
                var go = new GameObject($"BodyLod{lod}");
                go.transform.SetParent(body, false);
                go.AddComponent<MeshFilter>().sharedMesh = lodMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = cm.BodyArray;
                lodBodies.Add(r);
            }

            Mesh wheelMesh = CarBodyGenerator.BuildWheel(def, a);
            for (int i = 0; i < 4; i++)
            {
                var pivot = new GameObject($"Wheel{i}").transform;
                pivot.SetParent(transform, false);
                var spin = new GameObject("Spin").transform;
                spin.SetParent(pivot, false);
                var mesh = new GameObject("Mesh").transform;
                mesh.SetParent(spin, false);
                // Mesh is authored with its outer face toward +x; mirror left wheels so rims face outward.
                bool left = i % 2 == 0;
                mesh.localScale = new Vector3(left ? -1f : 1f, 1f, 1f);
                mesh.gameObject.AddComponent<MeshFilter>().sharedMesh = wheelMesh;
                mesh.gameObject.AddComponent<MeshRenderer>().sharedMaterials = cm.WheelArray;
                wheels[i] = pivot;
                wheelSpin[i] = spin;
            }

            var near = new List<Renderer>(body.GetComponentsInChildren<Renderer>(true).Where(r => !lodBodies.Contains(r)));
            var wheelRenderers = new List<Renderer>();
            foreach (Transform w in wheels) wheelRenderers.AddRange(w.GetComponentsInChildren<Renderer>(true));
            Transform decals = body.Find("Decals");
            Renderer[] livery = decals != null ? decals.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
            near.AddRange(wheelRenderers);
            // Screen-relative heights (× the quality level's LOD bias, 2 on PC): with the 58° race camera a car switches to
            // the mid body at about 45 m and to the far body at about 135 m. The level is chosen by UpdateLod (see there),
            // which never culls; the last height only matters to renderers outside the game's own choice (editor views).
            lodGroup = gameObject.AddComponent<LODGroup>();
            lodGroup.SetLODs(new[]
            {
                new LOD(0.18f, near.ToArray()),
                new LOD(0.06f, wheelRenderers.Concat(livery).Append(lodBodies[0]).ToArray()),
                new LOD(0.008f, wheelRenderers.Append(lodBodies[1]).ToArray()),
            });
            lodGroup.RecalculateBounds();
            lodHeights = lodGroup.GetLODs().Select(l => l.screenRelativeTransitionHeight).ToArray();

            headlights = new Light[2];
            for (int s = 0; s < 2; s++)
            {
                var lgo = new GameObject(s == 0 ? "HeadlightL" : "HeadlightR");
                lgo.transform.SetParent(transform, false);
                lgo.transform.localPosition = new Vector3((s == 0 ? -1f : 1f) * p.WidthM * 0.32f, def.NoseHeight - 0.1f - groundOffset, p.FrontAxleZ + def.FrontOverhang * 0.9f);
                lgo.transform.localRotation = Quaternion.Euler(4f, 0f, 0f);
                var l = lgo.AddComponent<Light>();
                l.type = LightType.Spot;
                l.range = 70f;
                l.spotAngle = 52f;
                l.innerSpotAngle = 24f;
                l.intensity = 30f;
                l.color = new Color(1f, 0.96f, 0.88f);
                l.shadows = LightShadows.None;
                l.enabled = false;
                headlights[s] = l;
            }
            SetLayerRecursive(transform, GameLayers.Vehicle);
        }

        /// <summary>The number plate: a backing in the plate style's colour and the lettering (literal text, never markup).</summary>
        void Plate(CarBodyDef def, CarAppearance a, Material trim)
        {
            var go = new GameObject("Plate");
            go.transform.SetParent(body, false);
            go.transform.localPosition = CarBodyGenerator.RearPlateCentre(def, p);
            var backing = GameObject.CreatePrimitive(PrimitiveType.Quad);
            backing.name = "PlateBacking";
            CockpitBuilder.Discard(backing.GetComponent<Collider>());
            backing.transform.SetParent(go.transform, false);
            backing.transform.localPosition = new Vector3(0f, 0f, 0.01f); // between the trim plate face and the lettering, facing −z
            backing.transform.localScale = new Vector3(0.49f, 0.1f, 1f); // inside the 0.52 × 0.12 m trim plate
            backing.GetComponent<MeshRenderer>().sharedMaterial = Instance(trim, $"{def.Id}_Plate", a.PlateBackground, "satin");
            // TextMeshPro faces −z: readable by someone standing behind the car.
            var t = go.AddComponent<TMPro.TextMeshPro>();
            t.richText = false;
            t.text = a.PlateText;
            t.fontSize = 0.9f;
            t.alignment = TMPro.TextAlignmentOptions.Center;
            t.color = a.PlateTextColor;
            t.rectTransform.sizeDelta = new Vector2(0.5f, 0.12f);
            t.enableAutoSizing = true;
            t.fontSizeMin = 0.2f;
            t.fontSizeMax = 1f;
            t.ForceMeshUpdate();
        }

        void OnDestroy()
        {
            foreach (Object o in owned) if (o != null) Destroy(o);
        }

        float StaticCompression()
        {
            float front = p.MassKg * p.FrontWeightFraction * 0.5f * 9.81f / p.SpringFront;
            float rear = p.MassKg * (1f - p.FrontWeightFraction) * 0.5f * 9.81f / p.SpringRear;
            return (front + rear) * 0.5f;
        }

        public void SetHeadlights(bool on)
        {
            foreach (Light l in headlights) l.enabled = on;
        }

        /// <summary>Race sound, when the session attached it (<see cref="GameAudio.CarAudio.Attach"/>); fed from <see cref="Render"/>.</summary>
        public GameAudio.CarAudio Audio;

        /// <summary>Places the car between two ticks and animates wheels/lean for this render frame.</summary>
        public void Render(in VehicleState previous, in VehicleState current, float alpha, in StepTelemetry telemetry, float dt)
        {
            transform.SetPositionAndRotation(Vector3.Lerp(previous.Position, current.Position, alpha),
                Quaternion.Slerp(previous.Rotation, current.Rotation, alpha));

            for (int i = 0; i < 4; i++)
            {
                Vector3 mount = p.WheelMount(i);
                float comp = Mathf.Lerp(previous.GetCompression(i), current.GetCompression(i), alpha);
                WheelTelemetry w = telemetry.Wheel(i);
                // Remote cars have no local telemetry: their replicated compression tells us they are grounded.
                bool grounded = w.Grounded || comp > 0.001f;
                float travel = grounded ? p.RestLengthM - comp : p.RestLengthM;
                wheels[i].localPosition = mount + Vector3.down * travel + Offset(i);
                float steer = i < 2 ? Mathf.Lerp(previous.SteerAngle, current.SteerAngle, alpha) * Mathf.Rad2Deg : 0f;
                if (i == 0) SteerRad = steer * Mathf.Deg2Rad;
                wheels[i].localRotation = Quaternion.Euler(0f, steer, 0f);
                spinAngle[i] = Mathf.Repeat(spinAngle[i] + w.AngularSpeed * Mathf.Rad2Deg * dt, 360f);
                wheelSpin[i].localRotation = Quaternion.Euler(spinAngle[i], 0f, 0f);
            }

            // Visual lean: roll with lateral g, pitch with longitudinal g, critically damped.
            Vector2 target = new Vector2(Mathf.Clamp(-telemetry.LateralG * 2.2f, -4f, 4f), Mathf.Clamp(-telemetry.LongitudinalG * 1.6f, -3f, 3f));
            lean.x = Mathf.SmoothDamp(lean.x, target.x, ref leanVelocity.x, 0.12f, Mathf.Infinity, dt);
            lean.y = Mathf.SmoothDamp(lean.y, target.y, ref leanVelocity.y, 0.12f, Mathf.Infinity, dt);
            body.localRotation = Quaternion.Euler(lean.y, 0f, lean.x);
            Audio?.OnRender(previous, current, telemetry, dt);
        }

        /// <summary>Static display pose (garage, meet bays, previews): wheels at static ride height, body level.</summary>
        public void ShowParked(Vector3 groundPosition, Quaternion rotation, float steerDeg = 0f)
        {
            float staticComp = StaticCompression();
            transform.SetPositionAndRotation(groundPosition + rotation * Vector3.up * (p.CgHeightM - staticComp), rotation);
            for (int i = 0; i < 4; i++)
            {
                wheels[i].localPosition = p.WheelMount(i) + Vector3.down * (p.RestLengthM - staticComp) + Offset(i);
                wheels[i].localRotation = Quaternion.Euler(0f, i < 2 ? steerDeg : 0f, 0f);
            }
            body.localRotation = Quaternion.identity;
        }

        /// <summary>
        /// Presentation driving (the meet's arrival spline): parked ride height, steered front wheels and wheels rolled by
        /// the distance travelled. No physics, no collision.
        /// </summary>
        public void ShowRolling(Vector3 groundPosition, Quaternion rotation, float steerDeg, float rolledMetres)
        {
            ShowParked(groundPosition, rotation, steerDeg);
            float deg = Mathf.Repeat(rolledMetres / Mathf.Max(0.2f, p.WheelRadiusM) * Mathf.Rad2Deg, 360f);
            for (int i = 0; i < 4; i++) wheelSpin[i].localRotation = Quaternion.Euler(deg, 0f, 0f);
        }

        /// <summary>Visual wheel offset (appearance only): left wheels move −x, right +x.</summary>
        Vector3 Offset(int wheel) => Appearance == null || Appearance.WheelOffsetM == 0f ? Vector3.zero
            : new Vector3(wheel % 2 == 0 ? -Appearance.WheelOffsetM : Appearance.WheelOffsetM, 0f, 0f);

        static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform c in t) SetLayerRecursive(c, layer);
        }

    }
}
