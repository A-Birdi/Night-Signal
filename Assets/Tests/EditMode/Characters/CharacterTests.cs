using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NightSignal.Characters;
using NightSignal.Content;
using NightSignal.Core.Meet;
using NightSignal.Editor.ArtTools;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Characters
{
    /// <summary>
    /// The rival characters: every one of the 48 sheets has an authored look in the builder's vocabulary; each builds a
    /// modelled, skinned body within budget, standing on the ground at its authored height with sound normals; walking,
    /// jogging and every emote keep the feet grounded and the pose finite; and no two rivals are confusable by silhouette
    /// and colour layout.
    /// </summary>
    public sealed class CharacterTests
    {
        static List<CharacterLook> Looks() => CharacterSheet.LoadLooks();

        static IEnumerable<string> RivalIds()
        {
            var doc = JObject.Parse(File.ReadAllText("Assets/Content/Data/generated/rivals.json"));
            foreach (JToken r in doc["rivals"]) yield return (string)r["id"];
        }

        static CharacterLook Look(string id) => Looks().Find(l => l.Id == id);

        [Test]
        public void EveryRival_HasAnAuthoredLook_InTheVocabulary()
        {
            var ids = new List<string>(RivalIds());
            List<CharacterLook> looks = Looks();
            Assert.That(ids.Count, Is.EqualTo(48));
            Assert.That(looks.ConvertAll(l => l.Id), Is.EquivalentTo(ids), "one look per rival sheet");
            var problems = new List<string>();
            foreach (CharacterLook l in looks) problems.AddRange(CharacterVocabulary.Check(l));
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
            Assert.That(ContentLibrary.Load().Look("R01"), Is.Not.Null, "the content library carries the looks into builds");
        }

        [Test]
        public void Body_IsModelledSkinnedAndWithinBudget([ValueSource(nameof(RivalIds))] string id)
        {
            CharacterLook look = Look(id);
            CharacterBuilder.Skeleton sk = CharacterBuilder.SkeletonFor(look);
            Mesh m = CharacterBuilder.Build(look, sk);
            try
            {
                Assert.That(m.subMeshCount, Is.EqualTo((int)CharacterBuilder.Slot.Count));
                foreach (CharacterBuilder.Slot s in new[] { CharacterBuilder.Slot.Skin, CharacterBuilder.Slot.Dark, CharacterBuilder.Slot.Light })
                    Assert.That(m.GetSubMesh((int)s).indexCount, Is.GreaterThan(0), $"{id}: {s} (face and eyes) drawn");
                Assert.That(m.vertexCount, Is.InRange(1500, 4500), $"{id}: vertex budget (12 avatars + NPCs at the meet)");
                BoneWeight[] w = m.boneWeights;
                Assert.That(w.Length, Is.EqualTo(m.vertexCount));
                Assert.That(m.bindposes.Length, Is.EqualTo((int)Bone.Count));
                foreach (BoneWeight b in w) Assert.That(b.boneIndex0, Is.InRange(0, (int)Bone.Count - 1));
                // Normals: finite and unit length everywhere (faces wound both ways over shared vertices cancel to NaN,
                // which bloom turns into white discs at night — the car art pass defect).
                Vector3[] n = m.normals;
                for (int i = 0; i < n.Length; i++)
                    Assert.That(Mathf.Abs(n[i].magnitude - 1f), Is.LessThan(0.01f), $"{id}: normal {i} = {n[i]}");
                // Standing on the ground at the authored height (hair and hats may add a little).
                Bounds bb = m.bounds;
                Assert.That(bb.min.y, Is.InRange(-0.005f, 0.012f), $"{id}: soles on the ground");
                Assert.That(bb.max.y, Is.InRange(look.Height * 0.97f, look.Height * 1.08f), $"{id}: height {bb.max.y:F3} vs {look.Height}");
                Assert.That(bb.size.x, Is.LessThan(0.9f), $"{id}: width");
            }
            finally
            {
                Object.DestroyImmediate(m);
            }
        }

        [Test]
        public void Motion_KeepsFeetGroundedAndPosesFinite([ValueSource(nameof(RivalIds))] string id)
        {
            CharacterRig rig = CharacterRig.Create(Look(id));
            var motion = rig.gameObject.AddComponent<CharacterMotion>();
            var baked = new Mesh();
            try
            {
                motion.Pose(0f);
                float headRest = rig[Bone.Head].position.y;
                float ankleRest = rig.Skeleton[Bone.FootL].y;
                void Grounded(string what)
                {
                    float low = Mathf.Min(rig[Bone.FootL].position.y, rig[Bone.FootR].position.y);
                    Assert.That(low, Is.EqualTo(ankleRest).Within(0.025f), $"{id} {what}: a foot on the ground");
                    rig.Body.BakeMesh(baked);
                    foreach (Vector3 v in baked.vertices)
                        Assert.That(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z), Is.False, $"{id} {what}: finite skin");
                }
                foreach (float speed in new[] { CharacterMotion.WalkSpeed, CharacterMotion.JogSpeed })
                {
                    motion.Speed = speed;
                    for (int i = 0; i < 90; i++)
                    {
                        motion.Pose(1f / 60f);
                        if (i % 15 == 0) Grounded($"at {speed} m/s");
                    }
                }
                motion.Speed = 0f;
                for (int i = 0; i < 60; i++) motion.Pose(1f / 60f);
                foreach (Emote e in Emotes.Wheel)
                {
                    motion.Play(e, Emotes.Duration(e) * 0.5f);
                    motion.Pose(0.0001f);
                    Assert.That(motion.EmoteActive, Is.True, $"{id}: {e} playing mid-way");
                    if (e != Emote.Cheer) Grounded(e.ToString()); // the cheer hops
                    if (e == Emote.Admire)
                        Assert.That(rig[Bone.Head].position.y, Is.LessThan(headRest - 0.3f), $"{id}: the admiration crouch goes down");
                    if (e == Emote.Cheer || e == Emote.Stretch)
                        Assert.That(Mathf.Min(rig[Bone.HandL].position.y, rig[Bone.HandR].position.y), Is.GreaterThan(headRest), $"{id}: {e} hands above the head");
                    if (e == Emote.Wave)
                        Assert.That(rig[Bone.HandR].position.y, Is.GreaterThan(headRest), $"{id}: wave hand raised");
                    motion.Pose(Emotes.Duration(e));
                    Assert.That(motion.EmoteActive, Is.False, $"{id}: {e} ends after its bounded duration");
                }
            }
            finally
            {
                Object.DestroyImmediate(baked);
                Object.DestroyImmediate(rig.gameObject);
            }
        }

        [Test]
        public void Emotes_AreTwelveBoundedAndDistinct()
        {
            Assert.That(Emotes.Wheel.Length, Is.EqualTo(Emotes.Count));
            Assert.That(new HashSet<Emote>(Emotes.Wheel).Count, Is.EqualTo(12));
            foreach (Emote e in Emotes.Wheel) Assert.That(Emotes.Duration(e), Is.InRange(1f, 4f), e.ToString());
            Assert.That(Emotes.TryParse("wave", out Emote w) && w == Emote.Wave);
            Assert.That(Emotes.TryParse("none", out _), Is.False);
        }

        [Test]
        public void Rivals_AreNotConfusableBySilhouetteAndColour()
        {
            List<CharacterLook> looks = Looks();
            CharacterSheet.Distinctness d = CharacterSheet.Measure(looks);
            TestContext.WriteLine(d.Report(looks));
            Assert.That(d.Confusable, Is.Empty, string.Join("\n", d.Confusable));
            // And not all the same body: heights span at least 35 cm, silhouettes are not near-identical on average.
            float lo = float.MaxValue, hi = float.MinValue, sum = 0f;
            int pairs = 0;
            for (int i = 0; i < looks.Count; i++)
            {
                lo = Mathf.Min(lo, looks[i].Height);
                hi = Mathf.Max(hi, looks[i].Height);
                for (int j = i + 1; j < looks.Count; j++) { sum += d.IoU[i, j]; pairs++; }
            }
            Assert.That(hi - lo, Is.GreaterThanOrEqualTo(0.35f));
            Assert.That(sum / pairs, Is.LessThan(0.8f), "mean pairwise silhouette IoU");
        }
    }
}
