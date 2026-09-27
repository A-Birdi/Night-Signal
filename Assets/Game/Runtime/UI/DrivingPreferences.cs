using System;
using System.IO;
using UnityEngine;

namespace NightSignal.UI
{
    /// <summary>
    /// Camera/motion strengths (Addendum 03 §4): 0 = off, 1 = the "normal intensity" design envelope. Speed lines: 0 off,
    /// 1 subtle, 2 strong.
    /// </summary>
    [Serializable]
    public struct MotionStrengths
    {
        public float DriftFraming, BodyMotion, ImpactShake, Roll, SpeedFov;
        public int SpeedLines;

        /// <summary>Arcade: demonstrates the style without maximal effects (moderate framing, light response, subtle lines).</summary>
        public static MotionStrengths Arcade => new MotionStrengths { DriftFraming = 0.7f, BodyMotion = 0.4f, ImpactShake = 0.4f, Roll = 0.2f, SpeedFov = 0.5f, SpeedLines = 1 };

        /// <summary>Comfort: stable follow and road visibility — no bob, roll, impact shake, speed-FOV or lines.</summary>
        public static MotionStrengths Comfort => new MotionStrengths();

        internal MotionStrengths Clamped() => new MotionStrengths
        {
            DriftFraming = Clamp01(DriftFraming), BodyMotion = Clamp01(BodyMotion), ImpactShake = Clamp01(ImpactShake),
            Roll = Clamp01(Roll), SpeedFov = Clamp01(SpeedFov), SpeedLines = Mathf.Clamp(SpeedLines, 0, 2),
        };

        static float Clamp01(float v) => float.IsNaN(v) ? 0f : Mathf.Clamp01(v);
    }

    /// <summary>
    /// Local presentation preferences (Addendum 03 §1.4, §4, D301–D303, D308): speedometer style and units, driving view,
    /// base field of view, motion preset and custom strengths, and the existing accessibility choices. Stored per device
    /// user in a versioned file; never sent to peers, never part of a build or a hash, never a reward or an assist. A
    /// missing file gets the documented defaults (Instrument Dial, km/h, Chase Close, Arcade); a corrupt one is set aside
    /// and replaced by defaults; unknown values fall back field by field. Custom strengths survive switching presets.
    /// </summary>
    [Serializable]
    public sealed class DrivingPreferences
    {
        public const int CurrentSchema = 1;
        public static readonly string[] Styles = { "dial", "strip" };
        public static readonly string[] Views = { "chase-close", "chase-far", "hood", "bumper", "cockpit" };
        public static readonly string[] Presets = { "arcade", "comfort", "custom" };
        public const float MinFov = 50f, MaxFov = 80f, DefaultFov = 58f;

        public int Schema = CurrentSchema;
        /// <summary>dial | strip</summary>
        public string SpeedStyle = "dial";
        /// <summary>kmh | mph</summary>
        public string Units = "kmh";
        /// <summary>chase-close | chase-far | hood | bumper | cockpit</summary>
        public string View = "chase-close";
        /// <summary>Base VERTICAL field of view in degrees (every camera uses the vertical definition).</summary>
        public float VerticalFov = DefaultFov;
        /// <summary>arcade | comfort | custom</summary>
        public string MotionPreset = "arcade";
        public MotionStrengths Custom = MotionStrengths.Arcade;
        public bool ReducedMotion;
        public bool HighContrast;
        public float TextScale = 1f;
        public float HudScale = 1f;
        /// <summary>Existing motion blur toggle — off by default (no post blur is used).</summary>
        public bool MotionBlur;
        /// <summary>Remapped driving controls (the Input System's binding-override JSON); empty = the documented defaults.</summary>
        public string BindingOverrides = "";

        public SpeedUnit Unit => SpeedDisplay.Parse(Units);
        public bool Dial => SpeedStyle != "strip";

        /// <summary>
        /// The strengths in effect: Reduced Motion always wins (adding these effects never re-enables excluded motion), then
        /// the preset; Custom uses the stored custom values.
        /// </summary>
        public MotionStrengths Effective => ReducedMotion || MotionPreset == "comfort" ? MotionStrengths.Comfort
            : MotionPreset == "custom" ? Custom.Clamped() : MotionStrengths.Arcade;

        static DrivingPreferences current;
        public static string FolderOverride;
        static string Folder => FolderOverride ?? Path.Combine(Application.persistentDataPath, "settings");
        public static string FilePath => Path.Combine(Folder, "driving.json");

        /// <summary>The preferences in use (loaded on first access).</summary>
        public static DrivingPreferences Current => current ?? (current = Load());

        /// <summary>Raised after a saved change: HUD and cameras apply it live (no reload, no input loss).</summary>
        public static event Action<DrivingPreferences> Changed;

        public static DrivingPreferences Load()
        {
            string path = FilePath;
            if (!File.Exists(path)) return new DrivingPreferences();
            try
            {
                var p = JsonUtility.FromJson<DrivingPreferences>(File.ReadAllText(path));
                if (p == null) throw new InvalidDataException("empty preferences");
                return p.Normalized();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NightSignal.Prefs] driving preferences unreadable ({e.GetType().Name}); using defaults and keeping a copy");
                try { File.Copy(path, path + ".bad", true); } catch (Exception) { }
                return new DrivingPreferences();
            }
        }

        /// <summary>Clamps every field to a known value (unknown → default), migrating older schemas forward.</summary>
        public DrivingPreferences Normalized()
        {
            if (Array.IndexOf(Styles, SpeedStyle) < 0) SpeedStyle = "dial";
            if (Units != "kmh" && Units != "mph") Units = "kmh";
            if (Array.IndexOf(Views, View) < 0) View = "chase-close";
            if (Array.IndexOf(Presets, MotionPreset) < 0) MotionPreset = "arcade";
            VerticalFov = float.IsNaN(VerticalFov) ? DefaultFov : Mathf.Clamp(VerticalFov, MinFov, MaxFov);
            TextScale = float.IsNaN(TextScale) ? 1f : Mathf.Clamp(TextScale, 0.8f, 1.6f);
            HudScale = float.IsNaN(HudScale) ? 1f : Mathf.Clamp(HudScale, 0.8f, 1.4f);
            Custom = Custom.Clamped();
            if (BindingOverrides == null) BindingOverrides = "";
            Schema = CurrentSchema;
            return this;
        }

        /// <summary>Saves atomically (write then replace) and notifies listeners. Returns false when the disk refused.</summary>
        public bool Save()
        {
            Normalized();
            current = this;
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(this, true));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NightSignal.Prefs] driving preferences not saved: " + e.Message);
                Changed?.Invoke(this);
                return false;
            }
            Changed?.Invoke(this);
            return true;
        }

        /// <summary>Tests: forget the cached preferences so the next access reloads from <see cref="FilePath"/>.</summary>
        public static void ResetCache() => current = null;
    }
}
