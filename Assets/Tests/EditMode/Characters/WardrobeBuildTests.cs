using System.Collections.Generic;
using System.Linq;
using NightSignal.Characters;
using NightSignal.Content;
using NightSignal.Core.Customization;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Characters
{
    /// <summary>
    /// The reward wardrobe on the driver's body (spec: "Clothing/accessories must render on the driver's avatar"): the builder
    /// constructs every shape the catalogue names; every item, worn alone, builds a sound body within the meet's per-character
    /// budget with its own two colours as their own materials; a driver wearing a garment in every place and all eight
    /// accessories still builds soundly; the rig and its materials agree.
    /// </summary>
    public sealed class WardrobeBuildTests
    {
        static WardrobeCatalogue Wardrobe => ContentLibrary.Load().Customization.Wardrobe;

        static IEnumerable<string> ItemIds() => ContentLibrary.Load().Customization.Wardrobe.Items.Select(i => i.Id);

        static CharacterLook Wearing(int preset, params string[] items)
        {
            CharacterLook l = PlayerLooks.Copy(PlayerLooks.Presets[preset]);
            l.Wardrobe = items.ToList();
            return l;
        }

        static readonly string[] Everything =
        {
            "cedar-crew-cap", "tea-hour-gloves", "red-horizon-coat", "mizuhana-trousers", "recovery-boots", "highland-scarf", "route-pin-badge",
            "woven-wrist-cuff", "cedar-lantern-keys", "timing-slip-charm", "workshop-satchel", "road-atlas", "old-frequency-radio",
        };

        static void AssertSound(Mesh m, string what, int maxVertices)
        {
            Vector3[] n = m.normals;
            for (int i = 0; i < n.Length; i++)
                Assert.That(Mathf.Abs(n[i].magnitude - 1f), Is.LessThan(0.01f), $"{what}: normal {i} = {n[i]}");
            Assert.That(m.bounds.min.y, Is.InRange(-0.005f, 0.012f), $"{what}: soles on the ground");
            Assert.That(m.bounds.size.x, Is.LessThan(0.9f), $"{what}: width");
            Assert.That(m.vertexCount, Is.LessThanOrEqualTo(maxVertices), $"{what}: vertex budget");
            Assert.That(m.boneWeights.Length, Is.EqualTo(m.vertexCount));
        }

        [Test]
        public void EveryShape_IsConstructedByTheBuilder()
        {
            var built = new HashSet<string>();
            foreach (WardrobeItemDef d in Wardrobe.Items)
            {
                CharacterLook dressed = CharacterRig.Dressed(Wearing(0, d.Id));
                foreach (string shape in d.Shapes)
                {
                    // The same dressed look with only this one shape, against it with none: the shape adds geometry.
                    CharacterLook bare = PlayerLooks.Copy(dressed);
                    bare.Worn = new List<WornPiece>();
                    CharacterLook one = PlayerLooks.Copy(dressed);
                    one.Worn = new List<WornPiece> { new WornPiece { Item = d.Id, Shapes = new List<string> { shape }, ColourA = d.Gear[0], ColourB = d.Gear[1] } };
                    Mesh m0 = CharacterBuilder.Build(bare, CharacterBuilder.SkeletonFor(bare));
                    Mesh m1 = CharacterBuilder.Build(one, CharacterBuilder.SkeletonFor(one));
                    try
                    {
                        Assert.That(m1.vertexCount, Is.GreaterThan(m0.vertexCount), $"{d.Id}: shape {shape} builds nothing");
                        built.Add(shape);
                    }
                    finally
                    {
                        Object.DestroyImmediate(m0);
                        Object.DestroyImmediate(m1);
                    }
                }
            }
            Assert.That(built, Is.EquivalentTo(WardrobeCatalogue.Shapes));
        }

        [Test]
        public void EveryItem_WornAlone_BuildsSoundly_InItsOwnColours([ValueSource(nameof(ItemIds))] string id)
        {
            CharacterLook look = CharacterRig.Dressed(Wearing(0, id));
            Assert.That(look.Worn.Count, Is.EqualTo(1));
            Mesh m = CharacterBuilder.Build(look, CharacterBuilder.SkeletonFor(look));
            try
            {
                AssertSound(m, id, 4500);
                Assert.That(m.subMeshCount, Is.EqualTo((int)CharacterBuilder.Slot.Count + 2));
                int own = (int)(m.GetSubMesh(CharacterBuilder.GearSlot(0, false)).indexCount + m.GetSubMesh(CharacterBuilder.GearSlot(0, true)).indexCount);
                Assert.That(own, Is.GreaterThan(0), $"{id}: nothing drawn in its own colours");
                CharacterMaterialSet mats = CharacterMaterialSet.Load();
                Material[] mm = mats.For(look);
                Assert.That(mm.Length, Is.EqualTo(m.subMeshCount));
                WardrobeItemDef d = Wardrobe.Item(id);
                Assert.That("#" + ColorUtility.ToHtmlStringRGB(mm[CharacterBuilder.GearSlot(0, false)].GetColor("_BaseColor")), Is.EqualTo(d.Gear[0]));
                Assert.That("#" + ColorUtility.ToHtmlStringRGB(mm[CharacterBuilder.GearSlot(0, true)].GetColor("_BaseColor")), Is.EqualTo(d.Gear[1]));
            }
            finally
            {
                Object.DestroyImmediate(m);
            }
        }

        [Test]
        public void FullyDressed_BuildsSoundly_AndTheRigWearsIt()
        {
            CharacterLook look = Wearing(3, Everything);
            Assert.That(Wardrobe.Problems(look, _ => true), Is.Empty);
            CharacterRig rig = CharacterRig.Create(look, CharacterMaterialSet.Load(), cacheMesh: false);
            try
            {
                Assert.That(rig.Look.Worn.Count, Is.EqualTo(Everything.Length), "the rig built the dressed look");
                Mesh m = rig.Body.sharedMesh;
                AssertSound(m, "fully dressed", 6000);
                Assert.That(m.subMeshCount, Is.EqualTo((int)CharacterBuilder.Slot.Count + 2 * Everything.Length));
                Assert.That(rig.Body.sharedMaterials.Length, Is.EqualTo(m.subMeshCount));
                Assert.That(rig.Body.sharedMaterials.All(x => x != null), Is.True);
                // The look the player saved is unchanged; a rival (no wardrobe) builds exactly as before.
                Assert.That(look.Worn, Is.Empty);
                CharacterLook rival = PlayerLooks.Copy(PlayerLooks.Presets[1]);
                Assert.That(CharacterRig.Dressed(rival), Is.SameAs(rival));
            }
            finally
            {
                Object.DestroyImmediate(rig.gameObject);
            }
        }
    }
}
