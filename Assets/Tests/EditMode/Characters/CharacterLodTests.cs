using System.Collections.Generic;
using System.Linq;
using NightSignal.Characters;
using NightSignal.Editor.ArtTools;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Characters
{
    /// <summary>
    /// Character levels of detail (spec §15 "Use level of detail and bounds/culling"): every rival, and a driver dressed in
    /// the reward wardrobe, builds a mid and a far body that are the same person — same submeshes, same footprint and height,
    /// sound normals, soles on the ground — with far fewer vertices; a rig carries the three under a LODGroup sharing one
    /// skeleton and one set of materials.
    /// </summary>
    public sealed class CharacterLodTests
    {
        static IEnumerable<string> RivalIds() => CharacterSheet.LoadLooks().Select(l => l.Id);

        static CharacterLook Dressed()
        {
            CharacterLook l = PlayerLooks.Copy(PlayerLooks.Presets[3]);
            l.Wardrobe = new List<string>
            {
                "cedar-crew-cap", "tea-hour-gloves", "red-horizon-coat", "mizuhana-trousers", "recovery-boots", "highland-scarf", "route-pin-badge",
                "woven-wrist-cuff", "cedar-lantern-keys", "timing-slip-charm", "workshop-satchel", "road-atlas", "old-frequency-radio",
            };
            return CharacterRig.Dressed(l);
        }

        static void AssertLevels(CharacterLook look, string what)
        {
            CharacterBuilder.Skeleton sk = CharacterBuilder.SkeletonFor(look);
            var meshes = new Mesh[CharacterBuilder.LodCount];
            try
            {
                for (int lod = 0; lod < meshes.Length; lod++) meshes[lod] = CharacterBuilder.Build(look, sk, lod);
                Bounds b0 = meshes[0].bounds;
                for (int lod = 1; lod < meshes.Length; lod++)
                {
                    Mesh m = meshes[lod];
                    string at = $"{what} LOD{lod}";
                    Assert.That(m.subMeshCount, Is.EqualTo(meshes[0].subMeshCount), $"{at}: same submeshes (same materials)");
                    Assert.That(m.vertexCount, Is.LessThanOrEqualTo(meshes[0].vertexCount * (lod == 1 ? 0.65f : 0.4f)), $"{at}: vertices {m.vertexCount} vs {meshes[0].vertexCount}");
                    Vector3[] n = m.normals;
                    for (int i = 0; i < n.Length; i++) Assert.That(Mathf.Abs(n[i].magnitude - 1f), Is.LessThan(0.01f), $"{at}: normal {i}");
                    Assert.That(m.boneWeights.Length, Is.EqualTo(m.vertexCount));
                    Assert.That(m.bindposes.Length, Is.EqualTo((int)Bone.Count));
                    Assert.That(m.bounds.min.y, Is.InRange(-0.005f, 0.012f), $"{at}: soles on the ground");
                    Assert.That(m.bounds.max.y, Is.EqualTo(b0.max.y).Within(b0.size.y * 0.03f), $"{at}: height");
                    Assert.That(m.bounds.size.x, Is.EqualTo(b0.size.x).Within(b0.size.x * 0.08f), $"{at}: width");
                    Assert.That(m.bounds.size.z, Is.EqualTo(b0.size.z).Within(b0.size.z * 0.1f), $"{at}: depth");
                }
            }
            finally
            {
                foreach (Mesh m in meshes) if (m != null) Object.DestroyImmediate(m);
            }
        }

        [Test]
        public void EveryRival_HasMidAndFarBodies([ValueSource(nameof(RivalIds))] string id) =>
            AssertLevels(CharacterSheet.LoadLooks().First(l => l.Id == id), id);

        [Test]
        public void ADressedDriver_HasMidAndFarBodies() => AssertLevels(Dressed(), "fully dressed");

        [Test]
        public void Rig_CarriesThreeLevels_OnOneSkeleton()
        {
            CharacterRig rig = CharacterRig.Create(PlayerLooks.Copy(PlayerLooks.Presets[0]), CharacterMaterialSet.Load());
            CharacterRig preview = CharacterRig.Create(PlayerLooks.Copy(PlayerLooks.Presets[0]), CharacterMaterialSet.Load(), cacheMesh: false);
            try
            {
                LODGroup group = rig.GetComponent<LODGroup>();
                Assert.IsNotNull(group);
                LOD[] levels = group.GetLODs();
                Assert.That(levels.Length, Is.EqualTo(CharacterBuilder.LodCount));
                Assert.That(rig.Lods.Length, Is.EqualTo(CharacterBuilder.LodCount));
                Assert.AreSame(rig.Body, rig.Lods[0]);
                for (int i = 0; i < levels.Length; i++)
                {
                    Assert.That(levels[i].renderers, Is.EqualTo(new Renderer[] { rig.Lods[i] }));
                    Assert.That(rig.Lods[i].bones, Is.EqualTo(rig.Bones), "one skeleton");
                    Assert.That(rig.Lods[i].sharedMaterials, Is.EqualTo(rig.Body.sharedMaterials), "one set of materials");
                    if (i > 0) Assert.That(levels[i].screenRelativeTransitionHeight, Is.LessThan(levels[i - 1].screenRelativeTransitionHeight));
                }
                Assert.That(rig.Lods[2].sharedMesh.vertexCount, Is.LessThan(rig.Lods[0].sharedMesh.vertexCount / 2));
                // A preview (the Player Card turntable) is one full-detail body.
                Assert.IsNull(preview.GetComponent<LODGroup>());
                Assert.That(preview.Lods.Length, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(rig.gameObject);
                Object.DestroyImmediate(preview.gameObject);
            }
        }
    }
}
