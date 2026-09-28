using NightSignal.Core.Meet;
using UnityEngine;

namespace NightSignal.Characters
{
    /// <summary>
    /// Procedural animation for a <see cref="CharacterRig"/>: posture, idle breathing and glances, a walk/jog cycle driven by
    /// ground speed (feet kept on the ground by leg kinematics), and the twelve meet emotes blended in and out. Nothing is
    /// networked bone by bone: remote avatars feed the interpolated speed and the replicated emote ID and start time.
    /// Angles are local Euler degrees in the rest frame (x right, y up, z forward): +x pitches a hanging limb backward and a
    /// spine forward, +z moves a hanging left arm inward and a right arm outward.
    /// </summary>
    [RequireComponent(typeof(CharacterRig))]
    public sealed class CharacterMotion : MonoBehaviour
    {
        public const float WalkSpeed = 1.5f, JogSpeed = 3.8f;

        /// <summary>Ground speed (m/s) and turn rate (deg/s), set each frame by the controller or remote interpolation.</summary>
        public float Speed, TurnRate;
        /// <summary>Drive the pose from LateUpdate (tools may pose explicitly with <see cref="Pose"/> instead).</summary>
        public bool Animate = true;

        public Emote Emote { get; private set; }
        public float EmoteTime { get; private set; }

        CharacterRig rig;
        float phase, clock, speedSmooth;
        readonly Vector3[] basePose = new Vector3[(int)Bone.Count];
        readonly Vector3[] emotePose = new Vector3[(int)Bone.Count];
        readonly Vector3[] pose = new Vector3[(int)Bone.Count];

        void Awake() => rig = GetComponent<CharacterRig>();

        /// <summary>Starts an emote (optionally already <paramref name="startedAgo"/> seconds in, for late joiners).</summary>
        public void Play(Emote e, float startedAgo = 0f)
        {
            Emote = e;
            EmoteTime = Mathf.Max(0f, startedAgo);
        }

        public void Stop() => Emote = Emote.None;

        public bool EmoteActive => Emote != Emote.None && EmoteTime < Emotes.Duration(Emote);

        void LateUpdate()
        {
            if (Animate) Pose(Time.deltaTime);
        }

        /// <summary>Advances time by <paramref name="dt"/> and poses the bones.</summary>
        public void Pose(float dt)
        {
            if (rig == null) rig = GetComponent<CharacterRig>();
            if (rig == null || rig.Bones == null) return;
            clock += dt;
            speedSmooth = Mathf.MoveTowards(speedSmooth, Mathf.Max(0f, Speed), dt * 8f);
            if (Emote != Emote.None)
            {
                EmoteTime += dt;
                if (EmoteTime >= Emotes.Duration(Emote)) Emote = Emote.None;
            }
            for (int i = 0; i < pose.Length; i++) basePose[i] = Vector3.zero;
            float shrug = 0f, lift = 0f;
            Posture(rig.Look != null ? rig.Look.Posture : "neutral", basePose);
            Locomotion(dt, basePose, out Vector3 sway);
            float w = 0f;
            if (Emote != Emote.None)
            {
                float d = Emotes.Duration(Emote);
                w = Hold(EmoteTime, 0f, 0.25f, d - 0.35f, d);
                for (int i = 0; i < pose.Length; i++) emotePose[i] = basePose[i];
                EmotePose(Emote, EmoteTime, d, emotePose, ref shrug, ref lift);
            }
            for (int i = 0; i < pose.Length; i++) pose[i] = w > 0f ? Vector3.Lerp(basePose[i], emotePose[i], w) : basePose[i];
            Apply(sway * (1f - w), shrug * w, lift * w);
        }

        // ------------------------------------------------------------------ layers

        static void Add(Vector3[] p, Bone b, float x, float y, float z) => p[(int)b] += new Vector3(x, y, z);

        void Posture(string posture, Vector3[] p)
        {
            float breath = Mathf.Sin(clock * Mathf.PI * 2f / 4.2f);
            Add(p, Bone.Chest, 1.2f * breath, 0f, 0f);
            Add(p, Bone.Neck, -0.8f * breath, 0f, 0f);
            // Arms relaxed a little closer than the A-pose, elbows soft.
            Add(p, Bone.UpperArmL, 2f, 0f, 5f);
            Add(p, Bone.UpperArmR, 2f, 0f, -5f);
            Add(p, Bone.ForearmL, -8f, 0f, 0f);
            Add(p, Bone.ForearmR, -8f, 0f, 0f);
            switch (posture)
            {
                case "forward":
                    Add(p, Bone.Spine, 5f, 0f, 0f); Add(p, Bone.Chest, 3f, 0f, 0f); Add(p, Bone.Neck, -5f, 0f, 0f);
                    break;
                case "upright":
                    Add(p, Bone.Spine, -2f, 0f, 0f); Add(p, Bone.Chest, -3f, 0f, 0f); Add(p, Bone.Neck, 2f, 0f, 0f);
                    break;
                case "slouch":
                    Add(p, Bone.Spine, 6f, 0f, 0f); Add(p, Bone.Chest, 7f, 0f, 0f); Add(p, Bone.Neck, -9f, 0f, 0f);
                    Add(p, Bone.UpperArmL, -4f, 0f, 3f); Add(p, Bone.UpperArmR, -4f, 0f, -3f);
                    break;
                case "relaxed":
                    Add(p, Bone.Hips, 0f, 0f, 3f); Add(p, Bone.Spine, 0f, 0f, -3f); Add(p, Bone.Neck, 0f, 0f, 3f);
                    Add(p, Bone.ThighR, -5f, 0f, 0f); Add(p, Bone.ShinR, 10f, 0f, 0f);
                    break;
                case "proud":
                    Add(p, Bone.Chest, -6f, 0f, 0f); Add(p, Bone.Neck, -4f, 0f, 0f);
                    Add(p, Bone.UpperArmL, 4f, 0f, -4f); Add(p, Bone.UpperArmR, 4f, 0f, 4f);
                    break;
                case "guarded":
                    Add(p, Bone.Chest, 3f, 0f, 0f);
                    Add(p, Bone.UpperArmL, -18f, 0f, 10f); Add(p, Bone.UpperArmR, -18f, 0f, -10f);
                    Add(p, Bone.ForearmL, -55f, 0f, 20f); Add(p, Bone.ForearmR, -55f, 0f, -20f);
                    break;
                case "stooped":
                    Add(p, Bone.Spine, 9f, 0f, 0f); Add(p, Bone.Chest, 10f, 0f, 0f); Add(p, Bone.Neck, -12f, 0f, 0f);
                    break;
            }
            // Idle glance.
            Add(p, Bone.Neck, 0f, 9f * Mathf.Sin(clock * 0.37f) * Mathf.Sin(clock * 0.23f + 1f), 0f);
        }

        void Locomotion(float dt, Vector3[] p, out Vector3 sway)
        {
            float v = speedSmooth, h = rig.Skeleton.H / 1.72f;
            float move = Mathf.Clamp01(v / 0.6f), jog = Mathf.Clamp01((v - WalkSpeed) / (JogSpeed - WalkSpeed));
            float cycle = Mathf.Lerp(1.35f, 2.3f, jog) * h; // metres per stride (two steps)
            phase += dt * v / cycle * Mathf.PI * 2f;
            if (phase > Mathf.PI * 200f) phase -= Mathf.PI * 200f;
            float s = Mathf.Sin(phase), co = Mathf.Cos(phase);
            float a = Mathf.Lerp(22f, 34f, jog) * move;
            Add(p, Bone.ThighL, -a * s, 0f, 0f);
            Add(p, Bone.ThighR, a * s, 0f, 0f);
            float stance = Mathf.Lerp(6f, 18f, jog) * move, swing = Mathf.Lerp(38f, 82f, jog) * move;
            Add(p, Bone.ShinL, stance + swing * Mathf.Pow(Mathf.Max(0f, co), 1.5f), 0f, 0f);
            Add(p, Bone.ShinR, stance + swing * Mathf.Pow(Mathf.Max(0f, -co), 1.5f), 0f, 0f);
            float arm = Mathf.Lerp(18f, 38f, jog) * move;
            Add(p, Bone.UpperArmL, arm * s, 0f, 3f * move);
            Add(p, Bone.UpperArmR, -arm * s, 0f, -3f * move);
            Add(p, Bone.ForearmL, -Mathf.Lerp(8f, 78f, jog) * move - 6f * Mathf.Max(0f, -s) * move, 0f, 0f);
            Add(p, Bone.ForearmR, -Mathf.Lerp(8f, 78f, jog) * move - 6f * Mathf.Max(0f, s) * move, 0f, 0f);
            Add(p, Bone.Hips, 0f, 6f * s * move, 0f);
            Add(p, Bone.Chest, 0f, -10f * s * move, 0f);
            Add(p, Bone.Spine, Mathf.Lerp(2f, 9f, jog) * move, 0f, Mathf.Clamp(-TurnRate * v * 0.006f, -8f, 8f));
            Add(p, Bone.Neck, -Mathf.Lerp(1f, 6f, jog) * move, 0f, 0f);
            // Toe-off: the trailing foot rolls onto its toes.
            Add(p, Bone.FootL, 18f * move * Mathf.Max(0f, s) * Mathf.Max(0f, -co), 0f, 0f);
            Add(p, Bone.FootR, 18f * move * Mathf.Max(0f, -s) * Mathf.Max(0f, co), 0f, 0f);
            sway = new Vector3(0.014f * co * move * (1f - jog) * h, 0f, 0f);
        }

        static float Ease(float t, float a, float b) => b <= a ? (t >= b ? 1f : 0f) : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - a) / (b - a)));
        static float Hold(float t, float inA, float inB, float outA, float outB) => Ease(t, inA, inB) * (1f - Ease(t, outA, outB));

        /// <summary>The emote's pose at time t (seconds) of duration d, over the base pose already in <paramref name="p"/>.</summary>
        static void EmotePose(Emote e, float t, float d, Vector3[] p, ref float shrug, ref float lift)
        {
            const float Tau = Mathf.PI * 2f;
            void Set(Bone b, float x, float y, float z) => p[(int)b] = new Vector3(x, y, z);
            switch (e)
            {
                case Emote.Wave:
                    Set(Bone.UpperArmR, -12f, 0f, 150f);
                    Set(Bone.ForearmR, -12f, 0f, 14f + 30f * Mathf.Sin(Tau * 2.4f * t));
                    Set(Bone.HandR, 0f, 0f, 8f);
                    p[(int)Bone.Neck] += new Vector3(-2f, 8f, -5f);
                    break;
                case Emote.Bow:
                {
                    float b = Hold(t, 0.15f, 0.65f, 1.2f, 1.75f);
                    Set(Bone.Hips, 10f * b, 0f, 0f);
                    Set(Bone.ThighL, -10f * b, 0f, 0f); Set(Bone.ThighR, -10f * b, 0f, 0f);
                    Set(Bone.Spine, 20f * b, 0f, 0f); Set(Bone.Chest, 12f * b, 0f, 0f); Set(Bone.Neck, 12f * b, 0f, 0f);
                    Set(Bone.UpperArmL, 4f * b, 0f, 9f); Set(Bone.UpperArmR, 4f * b, 0f, -9f);
                    Set(Bone.ForearmL, -4f, 0f, 0f); Set(Bone.ForearmR, -4f, 0f, 0f);
                    break;
                }
                case Emote.ThumbsUp:
                    Set(Bone.UpperArmR, -58f, 0f, 10f);
                    Set(Bone.ForearmR, -72f, 0f, 0f);
                    Set(Bone.HandR, 0f, 0f, 0f);
                    p[(int)Bone.Neck] += new Vector3(-3f + 6f * Mathf.Max(0f, Mathf.Sin(Tau * t / 0.9f)), 0f, -6f);
                    break;
                case Emote.Clap:
                {
                    float apart = (Mathf.Sin(Tau * 3.2f * t) + 1f) * 0.5f;
                    Set(Bone.UpperArmL, -36f, 0f, 46f - 14f * apart);
                    Set(Bone.UpperArmR, -36f, 0f, -46f + 14f * apart);
                    Set(Bone.ForearmL, -52f, 0f, 0f);
                    Set(Bone.ForearmR, -52f, 0f, 0f);
                    break;
                }
                case Emote.Point:
                    Set(Bone.UpperArmR, -84f, 10f, 14f);
                    Set(Bone.ForearmR, -4f, 0f, 0f);
                    Set(Bone.HandR, 0f, 0f, 0f);
                    p[(int)Bone.Chest] += new Vector3(0f, 10f, 0f);
                    p[(int)Bone.Neck] += new Vector3(-2f, 10f, 0f);
                    break;
                case Emote.CameraPose:
                    // Hand on hip, a V-sign by the face, knee popped, head tilted.
                    Set(Bone.UpperArmR, -28f, 0f, 36f);
                    Set(Bone.ForearmR, -128f, 0f, 0f);
                    Set(Bone.UpperArmL, 14f, 0f, -42f);
                    Set(Bone.ForearmL, -24f, 0f, 88f);
                    Set(Bone.Hips, 0f, -8f, -4f);
                    p[(int)Bone.Spine] += new Vector3(0f, 0f, 5f);
                    p[(int)Bone.Neck] += new Vector3(-2f, 6f, 10f);
                    Set(Bone.ThighL, -8f, 0f, -2f); Set(Bone.ShinL, 16f, 0f, 0f);
                    break;
                case Emote.Stretch:
                {
                    float k = Hold(t, 0.1f, 0.8f, d - 0.8f, d);
                    Set(Bone.UpperArmL, -10f * k, 0f, -168f * k);
                    Set(Bone.UpperArmR, -10f * k, 0f, 168f * k);
                    Set(Bone.ForearmL, -14f, 0f, -10f * k); Set(Bone.ForearmR, -14f, 0f, 10f * k);
                    Set(Bone.Spine, -6f * k, 0f, 12f * Mathf.Sin(Tau * (t - 0.8f) / 1.6f) * Ease(t, 0.8f, 1.1f) * (1f - Ease(t, d - 1f, d - 0.7f)));
                    p[(int)Bone.Neck] += new Vector3(-10f * k, 0f, 0f);
                    Set(Bone.FootL, -10f * k, 0f, 0f); Set(Bone.FootR, -10f * k, 0f, 0f);
                    lift = 0.02f * k;
                    break;
                }
                case Emote.Cheer:
                {
                    float q = Mathf.Sin(Tau * 2f * t);
                    float crouch = 22f * Mathf.Max(0f, -q);
                    Set(Bone.UpperArmL, -14f, 0f, -142f); Set(Bone.UpperArmR, -14f, 0f, 142f);
                    Set(Bone.ForearmL, -12f, 0f, 0f); Set(Bone.ForearmR, -12f, 0f, 0f);
                    Set(Bone.Spine, -4f, 0f, 0f);
                    p[(int)Bone.Neck] += new Vector3(-12f, 0f, 0f);
                    Set(Bone.ThighL, -crouch, 0f, 0f); Set(Bone.ThighR, -crouch, 0f, 0f);
                    Set(Bone.ShinL, crouch * 2f, 0f, 0f); Set(Bone.ShinR, crouch * 2f, 0f, 0f);
                    lift = 0.07f * Mathf.Max(0f, q);
                    break;
                }
                case Emote.Shrug:
                {
                    float k = Hold(t, 0.08f, 0.38f, 1.0f, 1.45f);
                    Set(Bone.UpperArmL, -10f * k, 0f, -12f * k); Set(Bone.UpperArmR, -10f * k, 0f, 12f * k);
                    Set(Bone.ForearmL, -72f * k, -32f * k, 0f); Set(Bone.ForearmR, -72f * k, 32f * k, 0f);
                    p[(int)Bone.Neck] += new Vector3(-2f * k, 0f, 12f * k);
                    shrug = k;
                    break;
                }
                case Emote.Nod:
                    p[(int)Bone.Neck] += new Vector3(15f * Mathf.Max(0f, Mathf.Sin(Tau * t / 0.62f)) * (1f - Ease(t, 1.1f, 1.3f)), 0f, 0f);
                    p[(int)Bone.Head] += new Vector3(6f * Mathf.Max(0f, Mathf.Sin(Tau * t / 0.62f)) * (1f - Ease(t, 1.1f, 1.3f)), 0f, 0f);
                    break;
                case Emote.Footwork:
                {
                    float q = Tau * 2.8f * t, s = Mathf.Sin(q);
                    Set(Bone.ThighL, -30f * Mathf.Max(0f, s), 0f, -4f); Set(Bone.ShinL, 8f + 48f * Mathf.Max(0f, s), 0f, 0f);
                    Set(Bone.ThighR, -30f * Mathf.Max(0f, -s), 0f, 4f); Set(Bone.ShinR, 8f + 48f * Mathf.Max(0f, -s), 0f, 0f);
                    Set(Bone.FootL, 16f * Mathf.Max(0f, s), 0f, 0f); Set(Bone.FootR, 16f * Mathf.Max(0f, -s), 0f, 0f);
                    Set(Bone.Hips, 0f, 14f * s, 4f * s);
                    Set(Bone.Spine, 4f, -8f * s, -5f * s);
                    Set(Bone.UpperArmL, 28f * s, 0f, 12f); Set(Bone.UpperArmR, -28f * s, 0f, -12f);
                    Set(Bone.ForearmL, -95f, 0f, 0f); Set(Bone.ForearmR, -95f, 0f, 0f);
                    p[(int)Bone.Neck] += new Vector3(4f * Mathf.Abs(s), 0f, 0f);
                    break;
                }
                case Emote.Admire:
                {
                    // Crouch to look along the car: forearm on a knee, the other hand at the chin, head scanning.
                    float k = Hold(t, 0.1f, 0.8f, d - 0.8f, d);
                    Set(Bone.ThighL, -96f * k, 0f, -10f * k); Set(Bone.ShinL, 128f * k, 0f, 0f);
                    Set(Bone.ThighR, -80f * k, 0f, 12f * k); Set(Bone.ShinR, 132f * k, 0f, 0f);
                    Set(Bone.Spine, 26f * k, 0f, 0f); Set(Bone.Chest, 6f * k, 0f, 0f);
                    Set(Bone.Neck, -28f * k, 16f * Mathf.Sin(t * 1.6f) * k, 0f);
                    Set(Bone.UpperArmR, -48f * k, 0f, 4f); Set(Bone.ForearmR, -36f * k, 0f, 0f);
                    Set(Bone.UpperArmL, -40f * k, 0f, 22f * k); Set(Bone.ForearmL, -128f * k, 0f, 0f);
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ apply

        void Apply(Vector3 sway, float shrug, float lift)
        {
            CharacterBuilder.Skeleton sk = rig.Skeleton;
            // Feet: flatten against the leg's pitch; then set the hips height so the lower ankle is at its rest height
            // (walk bob, crouches and bows fall out of the kinematics) and, when both feet are down, keep them under the body.
            float hipsPitch = pose[(int)Bone.Hips].x;
            Vector3 ankleL = AnkleOffset(Bone.ThighL), ankleR = AnkleOffset(Bone.ThighR);
            Vector3 restL = sk[Bone.FootL] - sk[Bone.Hips], restR = sk[Bone.FootR] - sk[Bone.Hips];
            float dy = -Mathf.Min(ankleL.y - restL.y, ankleR.y - restR.y);
            float dz = 0f;
            float speedK = Mathf.Clamp01(speedSmooth / 0.6f);
            if (speedK < 0.5f) dz = -((ankleL.z - restL.z) + (ankleR.z - restR.z)) * 0.5f * (1f - speedK * 2f);
            for (int i = 0; i < pose.Length; i++)
            {
                Vector3 e = pose[i];
                if (i == (int)Bone.FootL || i == (int)Bone.FootR)
                {
                    Bone th = i == (int)Bone.FootL ? Bone.ThighL : Bone.ThighR, sh = i == (int)Bone.FootL ? Bone.ShinL : Bone.ShinR;
                    e.x += -(hipsPitch + pose[(int)th].x + pose[(int)sh].x);
                }
                rig.Bones[i].localRotation = Quaternion.Euler(e);
            }
            Transform hips = rig.Bones[(int)Bone.Hips];
            hips.localPosition = sk[Bone.Hips] + new Vector3(sway.x, dy + lift, dz);
            Vector3 up = new Vector3(0f, 0.035f * shrug * sk.H / 1.72f, 0f);
            rig.Bones[(int)Bone.UpperArmL].localPosition = sk[Bone.UpperArmL] - sk[Bone.Chest] + up;
            rig.Bones[(int)Bone.UpperArmR].localPosition = sk[Bone.UpperArmR] - sk[Bone.Chest] + up;
        }

        /// <summary>The ankle relative to the hips bone for the leg's current angles (hips rotation included).</summary>
        Vector3 AnkleOffset(Bone thigh)
        {
            CharacterBuilder.Skeleton sk = rig.Skeleton;
            Bone shin = thigh == Bone.ThighL ? Bone.ShinL : Bone.ShinR, foot = thigh == Bone.ThighL ? Bone.FootL : Bone.FootR;
            Vector3 hipJ = sk[thigh] - sk[Bone.Hips], thighVec = sk[shin] - sk[thigh], shinVec = sk[foot] - sk[shin];
            Quaternion rh = Quaternion.Euler(pose[(int)Bone.Hips]);
            Quaternion rt = Quaternion.Euler(pose[(int)thigh]);
            Quaternion rs = Quaternion.Euler(pose[(int)shin]);
            return rh * (hipJ + rt * (thighVec + rs * shinVec));
        }
    }
}
