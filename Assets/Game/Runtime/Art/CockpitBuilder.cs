using System.Collections.Generic;
using NightSignal.Vehicle;
using UnityEngine;

namespace NightSignal.Art
{
    /// <summary>
    /// The genuine fitted cockpit for the local driver's car (Addendum 03 §2, D304): built from the same loft as the body so
    /// it sits inside THIS chassis — an inward-facing roof liner, A/B pillars, door cards and inner glass (the body is
    /// one-sided, so without these the cabin would be see-through), a dashboard with a hooded instrument binnacle whose
    /// speed, gear and rev bar read the car's live telemetry, a centre console, the passenger seat and a three-spoke
    /// steering wheel that turns with the steering. Driver side as authored per model. Built only for the local car's
    /// cockpit view (opponents never carry an interior).
    /// </summary>
    public sealed class CockpitRig
    {
        public Transform Root;
        public Transform Wheel;
        public TMPro.TextMeshPro Speed, Gear, Unit;
        public Transform RpmBar;
        public CarBodyGenerator.CabinFrame Frame;
        int lastSpeed = int.MinValue, lastGear = int.MinValue;
        string lastUnit;

        /// <summary>Wheel turn per road-wheel steering angle (a typical 13:1 rack), capped at 1.5 turns.</summary>
        public const float SteeringRatio = 13f;

        public void Update(float steerRad, float roadSpeedMps, UI.SpeedUnit unit, float rpm, float redline, int gear)
        {
            if (Root == null) return;
            float wheelDeg = Mathf.Clamp(steerRad * Mathf.Rad2Deg * SteeringRatio, -540f, 540f);
            Wheel.localRotation = Quaternion.Euler(0f, 0f, -wheelDeg);
            int shown = Mathf.RoundToInt(UI.SpeedDisplay.Convert(roadSpeedMps, unit));
            if (shown != lastSpeed) { lastSpeed = shown; Speed.text = shown.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            string label = UI.SpeedDisplay.Label(unit);
            if (label != lastUnit) { lastUnit = label; Unit.text = label; }
            if (gear != lastGear) { lastGear = gear; Gear.text = gear < 0 ? "R" : gear == 0 ? "N" : gear.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            float rev = redline > 0f ? Mathf.Clamp01(rpm / redline) : 0f;
            RpmBar.localScale = new Vector3(Mathf.Max(0.001f, rev), 1f, 1f);
        }

        public void SetVisible(bool on)
        {
            if (Root != null) Root.gameObject.SetActive(on);
        }
    }

    public static class CockpitBuilder
    {
        const int Shell = 0, Glass = 1, Dash = 2, Detail = 3;

        public static CockpitRig Build(Transform body, CarBodyDef def, VehicleParams p, CarMaterialSet mats, List<Object> owned)
        {
            CarBodyGenerator.CabinFrame f = CarBodyGenerator.Cabin(def, p);
            var root = new GameObject("Cockpit").transform;
            root.SetParent(body, false);

            Material trim = Owned(new Material(mats.Trim) { name = "CabinTrim" }, owned);
            trim.SetColor("_BaseColor", new Color(0.11f, 0.115f, 0.125f));
            trim.SetFloat("_Smoothness", 0.28f);
            Material dash = Owned(new Material(mats.Trim) { name = "CabinDash" }, owned);
            dash.SetColor("_BaseColor", new Color(0.07f, 0.075f, 0.085f));
            dash.SetFloat("_Smoothness", 0.35f);
            Material detail = Owned(new Material(mats.Chrome) { name = "CabinDetail" }, owned);
            detail.SetColor("_BaseColor", new Color(0.32f, 0.33f, 0.35f));

            var mb = new MeshBuilder(4);
            float zFront = f.WindshieldBaseZ, zRear = f.CabinRearZ;
            const int n = 14;
            float floor = f.SillY + 0.04f;
            // Door cards and inner side glass, both sides, facing into the cabin.
            for (int i = 0; i < n; i++)
            {
                float z0 = Mathf.Lerp(zRear, zFront - 0.02f, i / (float)n), z1 = Mathf.Lerp(zRear, zFront - 0.02f, (i + 1) / (float)n);
                float w0 = f.InteriorHalfWidth(z0), w1 = f.InteriorHalfWidth(z1);
                float belt0 = f.BeltY(z0) - 0.02f, belt1 = f.BeltY(z1) - 0.02f;
                float low = floor;
                foreach (int s in new[] { -1, 1 })
                {
                    // Door card: from the floor line up to the belt.
                    Quad(mb, Shell, new Vector3(s * w0, low, z0), new Vector3(s * w1, low, z1), new Vector3(s * w1, belt1, z1), new Vector3(s * w0, belt0, z0), new Vector3(-s, 0f, 0f));
                    if (f.OpenTop) continue;
                    float top0 = f.RoofUndersideY(z0), top1 = f.RoofUndersideY(z1);
                    if (top0 - belt0 < 0.05f || top1 - belt1 < 0.05f) continue;
                    Quad(mb, Glass, new Vector3(s * (w0 + 0.03f), belt0, z0), new Vector3(s * (w1 + 0.03f), belt1, z1),
                        new Vector3(s * (w1 + 0.01f), top1, z1), new Vector3(s * (w0 + 0.01f), top0, z0), new Vector3(-s, 0f, 0f));
                }
            }
            if (!f.OpenTop)
            {
                // Roof liner (facing down) and inner windscreen (facing back into the cabin), framed by A-pillars.
                for (int i = 0; i < n; i++)
                {
                    float z0 = Mathf.Lerp(f.RoofRearZ - 0.2f, f.RoofFrontZ, i / (float)n), z1 = Mathf.Lerp(f.RoofRearZ - 0.2f, f.RoofFrontZ, (i + 1) / (float)n);
                    float w0 = f.InteriorHalfWidth(z0) * 0.98f, w1 = f.InteriorHalfWidth(z1) * 0.98f;
                    float y0 = f.RoofUndersideY(z0), y1 = f.RoofUndersideY(z1);
                    Quad(mb, Shell, new Vector3(-w0, y0, z0), new Vector3(-w1, y1, z1), new Vector3(w1, y1, z1), new Vector3(w0, y0, z0), Vector3.down);
                }
                float cowl = f.CowlY, header = f.RoofUndersideY(f.RoofFrontZ);
                float wb = f.InteriorHalfWidth(zFront) * 0.97f, wt = f.InteriorHalfWidth(f.RoofFrontZ) * 0.95f;
                Vector3 bl = new Vector3(-wb, cowl, zFront - 0.02f), br = new Vector3(wb, cowl, zFront - 0.02f);
                Vector3 tl = new Vector3(-wt, header, f.RoofFrontZ), tr = new Vector3(wt, header, f.RoofFrontZ);
                Quad(mb, Glass, bl, br, tr, tl, Vector3.back);
                foreach (int s in new[] { -1, 1 })
                {
                    Vector3 b0 = s < 0 ? bl : br, t0 = s < 0 ? tl : tr;
                    Vector3 inward = new Vector3(-s * 0.075f, 0f, 0f);
                    Quad(mb, Shell, b0, b0 + inward, t0 + inward * 0.8f, t0, Vector3.back);
                }
                // Windscreen header strip (a slim trim edge, not a visor) and B-pillars.
                Quad(mb, Shell, tl + Vector3.down * 0.025f, tr + Vector3.down * 0.025f, tr, tl, Vector3.back);
                float zb = Mathf.Lerp(f.RoofRearZ, f.RoofFrontZ, def.Style == "wagon" ? 0.62f : 0.45f);
                foreach (int s in new[] { -1, 1 })
                {
                    float w = f.InteriorHalfWidth(zb) + 0.005f;
                    Quad(mb, Shell, new Vector3(s * w, f.BeltY(zb), zb - 0.06f), new Vector3(s * w, f.BeltY(zb), zb + 0.06f),
                        new Vector3(s * w, f.RoofUndersideY(zb), zb + 0.06f), new Vector3(s * w, f.RoofUndersideY(zb), zb - 0.06f), new Vector3(-s, 0f, 0f));
                }
            }

            // The tub the open-cabin body leaves exposed: floor, firewall under the dash and a bulkhead behind the seats, plus
            // door-top capping from the door cards out to the body side (all trim, facing into the cabin).
            {
                float wr = f.InteriorHalfWidth(zRear), wf = f.InteriorHalfWidth(zFront - 0.02f);
                Quad(mb, Shell, new Vector3(-wr, floor, zRear), new Vector3(wr, floor, zRear), new Vector3(wf, floor, zFront - 0.02f), new Vector3(-wf, floor, zFront - 0.02f), Vector3.up);
                float cowlIn = f.CowlY - 0.02f;
                Quad(mb, Shell, new Vector3(-wf, floor, zFront - 0.02f), new Vector3(wf, floor, zFront - 0.02f), new Vector3(wf, cowlIn, zFront - 0.02f), new Vector3(-wf, cowlIn, zFront - 0.02f), Vector3.back);
                float beltR = f.BeltY(zRear);
                Quad(mb, Shell, new Vector3(-wr, floor, zRear), new Vector3(wr, floor, zRear), new Vector3(wr, beltR, zRear), new Vector3(-wr, beltR, zRear), Vector3.forward);
                for (int i = 0; i < n; i++)
                {
                    float z0 = Mathf.Lerp(zRear, zFront - 0.02f, i / (float)n), z1 = Mathf.Lerp(zRear, zFront - 0.02f, (i + 1) / (float)n);
                    foreach (int s in new[] { -1, 1 })
                    {
                        float w0 = f.InteriorHalfWidth(z0), w1 = f.InteriorHalfWidth(z1);
                        float o = f.HalfWidth * 0.99f;
                        float b0 = f.BeltY(z0) - 0.02f, b1 = f.BeltY(z1) - 0.02f;
                        Quad(mb, Shell, new Vector3(s * w0, b0, z0), new Vector3(s * w1, b1, z1), new Vector3(s * o, b1 + 0.01f, z1), new Vector3(s * o, b0 + 0.01f, z0), Vector3.up);
                    }
                }
            }

            // Dashboard across the cabin, binnacle ahead of the driver, centre console, passenger seat.
            // Placed from the driver's eye, not from the windscreen, so long cabins (wagons, roadsters with a long scuttle) get
            // the same seating geometry: the wheel ahead of the eye, the dash reaching back to its column, and the binnacle on
            // the sightline through the wheel's open upper half.
            Vector3 eye = f.Eye;
            Vector3 wheelAt = eye + new Vector3(0f, -0.3f, 0.46f);
            float dashRear = Mathf.Min(zFront - 0.46f, wheelAt.z + 0.16f);
            float half = f.InteriorHalfWidth(zFront - 0.3f);
            float dashTop = f.CowlY - 0.04f;
            float dashMid = (dashRear + zFront - 0.02f) * 0.5f, dashHalfDepth = (zFront - 0.02f - dashRear) * 0.5f;
            mb.AddBox(Dash, new Vector3(0f, dashTop - 0.16f, dashMid), new Vector3(half, 0.16f, dashHalfDepth), Quaternion.identity);
            mb.AddBox(Dash, new Vector3(0f, dashTop + 0.005f, zFront - 0.12f), new Vector3(half * 0.98f, 0.012f, 0.12f), Quaternion.Euler(-12f, 0f, 0f));
            Vector3 upperOpening = wheelAt + Quaternion.Euler(24f, 0f, 0f) * new Vector3(0f, 0.09f, 0f);
            float binZ = Mathf.Min(eye.z + 0.78f, zFront - 0.3f);
            float binY = eye.y + (upperOpening.y - eye.y) * (binZ - eye.z) / Mathf.Max(0.1f, upperOpening.z - eye.z);
            Vector3 binnacle = new Vector3(eye.x, Mathf.Max(binY, dashTop + 0.05f), binZ); // never sunk into the dash top
            // Riser from the dash top up to a binnacle that stands above it.
            if (binnacle.y - 0.07f > dashTop) mb.AddBox(Dash, new Vector3(eye.x, (dashTop + binnacle.y - 0.07f) * 0.5f, binZ + 0.02f), new Vector3(0.17f, (binnacle.y - 0.07f - dashTop) * 0.5f + 0.01f, 0.08f), Quaternion.identity);
            mb.AddBox(Dash, binnacle, new Vector3(0.19f, 0.07f, 0.09f), Quaternion.Euler(-8f, 0f, 0f));
            mb.AddBox(Dash, binnacle + new Vector3(0f, 0.075f, 0.02f), new Vector3(0.2f, 0.012f, 0.1f), Quaternion.Euler(-14f, 0f, 0f)); // hood over the dials
            mb.AddBox(Dash, new Vector3(0f, f.SillY + 0.2f, eye.z + 0.05f), new Vector3(0.1f, 0.12f, 0.45f), Quaternion.identity);
            float px = -Mathf.Sign(eye.x == 0f ? 1f : eye.x) * Mathf.Abs(eye.x);
            mb.AddBox(Shell, new Vector3(px, f.SillY + 0.28f, eye.z + 0.02f), new Vector3(0.24f, 0.07f, 0.26f), Quaternion.identity);
            mb.AddBox(Shell, new Vector3(px, f.SillY + 0.62f, eye.z - 0.24f), new Vector3(0.23f, 0.3f, 0.07f), Quaternion.Euler(-12f, 0f, 0f));

            Mesh shell = Owned(mb.Build(def.Id + "_cabin"), owned);
            shell.RecalculateNormals();
            var go = new GameObject("CabinShell");
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = shell;
            // Inner glass: the same tint as the body glass but matte and without specular highlights or reflections, so no
            // glint sits in the middle of the driver's view.
            Material innerGlass = Owned(new Material(mats.Glass) { name = "CabinGlass" }, owned);
            innerGlass.SetFloat("_Smoothness", 0.05f);
            if (innerGlass.HasProperty("_SpecularHighlights")) innerGlass.SetFloat("_SpecularHighlights", 0f);
            innerGlass.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            if (innerGlass.HasProperty("_EnvironmentReflections")) innerGlass.SetFloat("_EnvironmentReflections", 0f);
            innerGlass.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            go.AddComponent<MeshRenderer>().sharedMaterials = new[] { trim, innerGlass, dash, detail };

            var rig = new CockpitRig { Root = root, Frame = f };

            // Steering wheel: rim, three spokes and a hub, in front of the eye and raked toward the driver.
            var wheelPivot = new GameObject("SteeringWheel").transform;
            wheelPivot.SetParent(root, false);
            wheelPivot.localPosition = wheelAt;
            wheelPivot.localRotation = Quaternion.Euler(24f, 0f, 0f);
            var turning = new GameObject("Turning").transform;
            turning.SetParent(wheelPivot, false);
            var wb2 = new MeshBuilder(2);
            const int seg = 28;
            const float radius = 0.18f;
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                Vector3 c = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * radius;
                wb2.AddBox(0, c, new Vector3(0.018f, radius * Mathf.PI / seg + 0.004f, 0.018f), Quaternion.Euler(0f, 0f, a * Mathf.Rad2Deg));
            }
            foreach (float a in new[] { 0f, 180f, 270f }) // left, right and lower spokes: the upper half stays open for the dials
            {
                float r = a * Mathf.Deg2Rad;
                wb2.AddBox(0, new Vector3(Mathf.Cos(r), Mathf.Sin(r), 0f) * radius * 0.5f, new Vector3(radius * 0.5f, 0.016f, 0.01f), Quaternion.Euler(0f, 0f, a));
            }
            wb2.AddBox(1, Vector3.zero, new Vector3(0.05f, 0.05f, 0.03f), Quaternion.identity);
            Mesh wheelMesh = Owned(wb2.Build(def.Id + "_steering"), owned);
            wheelMesh.RecalculateNormals();
            var wgo = new GameObject("Rim");
            wgo.transform.SetParent(turning, false);
            wgo.AddComponent<MeshFilter>().sharedMesh = wheelMesh;
            wgo.AddComponent<MeshRenderer>().sharedMaterials = new[] { dash, detail };
            var column = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Discard(column.GetComponent<Collider>());
            column.name = "Column";
            column.transform.SetParent(wheelPivot, false);
            column.transform.localPosition = new Vector3(0f, 0f, 0.14f);
            column.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            column.transform.localScale = new Vector3(0.05f, 0.14f, 0.05f);
            column.GetComponent<MeshRenderer>().sharedMaterial = dash;
            rig.Wheel = turning;

            // Live instruments on the binnacle face (driver-facing): speed, unit, gear and a rev bar.
            var face = new GameObject("Instruments").transform;
            face.SetParent(root, false);
            face.localPosition = binnacle + new Vector3(0f, 0.005f, -0.092f);
            face.localRotation = Quaternion.Euler(-8f, 0f, 0f);
            rig.Speed = Text(face, "Speed", new Vector3(-0.05f, 0.012f, 0f), 0.34f, new Color(0.93f, 0.91f, 0.86f));
            rig.Unit = Text(face, "Unit", new Vector3(-0.05f, -0.03f, 0f), 0.14f, new Color(0.62f, 0.6f, 0.56f));
            rig.Gear = Text(face, "Gear", new Vector3(0.11f, 0.005f, 0f), 0.36f, new Color(0.95f, 0.65f, 0.25f));
            var barBg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Discard(barBg.GetComponent<Collider>());
            barBg.name = "RevTrack";
            barBg.transform.SetParent(face, false);
            barBg.transform.localPosition = new Vector3(0f, -0.052f, 0.001f);
            barBg.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            barBg.transform.localScale = new Vector3(0.3f, 0.012f, 1f);
            barBg.GetComponent<MeshRenderer>().sharedMaterial = dash;
            var barPivot = new GameObject("RevBar").transform;
            barPivot.SetParent(face, false);
            barPivot.localPosition = new Vector3(0.15f, -0.052f, 0f);
            var bar = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Discard(bar.GetComponent<Collider>());
            bar.transform.SetParent(barPivot, false);
            bar.transform.localPosition = new Vector3(-0.15f, 0f, 0f);
            bar.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            bar.transform.localScale = new Vector3(0.3f, 0.01f, 1f);
            Material revMat = Owned(new Material(mats.Trim) { name = "RevLight" }, owned);
            revMat.SetColor("_BaseColor", new Color(0.24f, 0.78f, 0.85f));
            if (revMat.HasProperty("_EmissionColor"))
            {
                revMat.EnableKeyword("_EMISSION");
                revMat.SetColor("_EmissionColor", new Color(0.12f, 0.4f, 0.45f));
            }
            bar.GetComponent<MeshRenderer>().sharedMaterial = revMat;
            rig.RpmBar = barPivot;
            // No cabin light: the instrument figures are self-lit text and the rev bar is emissive, so they read at night and
            // in tunnels while the dash stays dark, as a real one does (a point light here made a hot spot on the binnacle).
            return rig;
        }

        static TMPro.TextMeshPro Text(Transform parent, string name, Vector3 pos, float size, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var t = go.AddComponent<TMPro.TextMeshPro>();
            t.richText = false;
            t.fontSize = size;
            t.color = color;
            t.alignment = TMPro.TextAlignmentOptions.Center;
            t.rectTransform.sizeDelta = new Vector2(0.14f, 0.06f);
            t.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            t.text = "";
            return t;
        }

        /// <summary>A flat quad whose visible side faces <paramref name="facing"/> (the winding is chosen from its normal).</summary>
        static void Quad(MeshBuilder mb, int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 facing)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(n, facing) >= 0f) mb.AddFlatQuad(sub, a, b, c, d, Vector2.one);
            else mb.AddFlatQuad(sub, a, d, c, b, Vector2.one);
        }

        /// <summary>Destroy that also works while rendering in the editor (contact sheets).</summary>
        internal static void Discard(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        static T Owned<T>(T o, List<Object> owned) where T : Object
        {
            owned?.Add(o);
            return o;
        }
    }
}
