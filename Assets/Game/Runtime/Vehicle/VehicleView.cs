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
        Light[] headlights;

        public Material Paint => paint;

        public static VehicleView Create(string name, VehicleParams p, CarBodyDef body, CarMaterialSet mats, Color paintColor)
        {
            var root = new GameObject(name);
            var view = root.AddComponent<VehicleView>();
            view.Build(p, body, mats, paintColor);
            return view;
        }

        void Build(VehicleParams parameters, CarBodyDef def, CarMaterialSet mats, Color paintColor)
        {
            p = parameters;
            paint = new Material(mats.Paint) { name = $"{def.Id}_Paint" };
            paint.SetColor("_BaseColor", paintColor);
            CarMaterials cm = mats.ForPaint(paint);

            // Model space has the ground at y = 0; the simulation origin is the CG.
            float groundOffset = p.CgHeightM - StaticCompression();
            body = new GameObject("Body").transform;
            body.SetParent(transform, false);
            body.localPosition = new Vector3(0f, -groundOffset, 0f);
            body.gameObject.AddComponent<MeshFilter>().sharedMesh = CarBodyGenerator.BuildBody(def, p);
            body.gameObject.AddComponent<MeshRenderer>().sharedMaterials = cm.BodyArray;

            Mesh wheelMesh = CarBodyGenerator.BuildWheel(def);
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
                float travel = w.Grounded ? p.RestLengthM - comp : p.RestLengthM;
                wheels[i].localPosition = mount + Vector3.down * travel;
                float steer = i < 2 ? Mathf.Lerp(previous.SteerAngle, current.SteerAngle, alpha) * Mathf.Rad2Deg : 0f;
                wheels[i].localRotation = Quaternion.Euler(0f, steer, 0f);
                spinAngle[i] = Mathf.Repeat(spinAngle[i] + w.AngularSpeed * Mathf.Rad2Deg * dt, 360f);
                wheelSpin[i].localRotation = Quaternion.Euler(spinAngle[i], 0f, 0f);
            }

            // Visual lean: roll with lateral g, pitch with longitudinal g, critically damped.
            Vector2 target = new Vector2(Mathf.Clamp(-telemetry.LateralG * 2.2f, -4f, 4f), Mathf.Clamp(-telemetry.LongitudinalG * 1.6f, -3f, 3f));
            lean.x = Mathf.SmoothDamp(lean.x, target.x, ref leanVelocity.x, 0.12f, Mathf.Infinity, dt);
            lean.y = Mathf.SmoothDamp(lean.y, target.y, ref leanVelocity.y, 0.12f, Mathf.Infinity, dt);
            body.localRotation = Quaternion.Euler(lean.y, 0f, lean.x);
        }

        /// <summary>Static display pose (garage, meet bays, previews): wheels at static ride height, body level.</summary>
        public void ShowParked(Vector3 groundPosition, Quaternion rotation, float steerDeg = 0f)
        {
            float staticComp = StaticCompression();
            transform.SetPositionAndRotation(groundPosition + rotation * Vector3.up * (p.CgHeightM - staticComp), rotation);
            for (int i = 0; i < 4; i++)
            {
                wheels[i].localPosition = p.WheelMount(i) + Vector3.down * (p.RestLengthM - staticComp);
                wheels[i].localRotation = Quaternion.Euler(0f, i < 2 ? steerDeg : 0f, 0f);
            }
            body.localRotation = Quaternion.identity;
        }

        static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform c in t) SetLayerRecursive(c, layer);
        }

        void OnDestroy()
        {
            if (paint != null) Destroy(paint);
        }
    }
}
