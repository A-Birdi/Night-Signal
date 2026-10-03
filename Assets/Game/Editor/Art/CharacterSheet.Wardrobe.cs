using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NightSignal.Characters;
using NightSignal.Core.Customization;
using UnityEditor;
using UnityEngine;

namespace NightSignal.Editor.ArtTools
{
    public static partial class CharacterSheet
    {
        /// <summary>
        /// The reward wardrobe on a driver (spec: "Clothing/accessories must render on the driver's avatar"): one look per item
        /// over the same starting look — front, back and three-quarter sheets, and a close-up of each item — plus one look
        /// wearing a garment in every place and all eight accessories. Output: Builds/Screenshots/characters/wardrobe-*.png and
        /// wardrobe.txt (item, slot, shapes, the look's submesh triangles in the item's own colours).
        /// </summary>
        [MenuItem("Night Signal/Art/Render Wardrobe Sheets")]
        public static void RenderWardrobeDefault() => RenderWardrobe(Path.GetFullPath(Path.Combine("Builds", "Screenshots", "characters")));

        public static string RenderWardrobe(string dir)
        {
            Directory.CreateDirectory(dir);
            WardrobeCatalogue wardrobe = NightSignal.Content.ContentLibrary.Load().Customization.Wardrobe;
            var looks = new List<CharacterLook>();
            var labels = new List<string>();
            foreach (WardrobeItemDef d in wardrobe.Items)
            {
                CharacterLook l = PlayerLooks.Copy(PlayerLooks.Presets[0]);
                l.Id = d.Id;
                l.Wardrobe = new List<string> { d.Id };
                looks.Add(l);
                labels.Add(d.Slot);
            }
            CharacterLook all = PlayerLooks.Copy(PlayerLooks.Presets[3]);
            all.Id = "everything";
            all.Wardrobe = new List<string>
            {
                "cedar-crew-cap", "tea-hour-gloves", "red-horizon-coat", "mizuhana-trousers", "recovery-boots", "highland-scarf", "route-pin-badge",
                "woven-wrist-cuff", "cedar-lantern-keys", "timing-slip-charm", "workshop-satchel", "road-atlas", "old-frequency-radio",
            };
            looks.Add(all);
            labels.Add("full");

            Render(Path.Combine(dir, "wardrobe-front.png"), looks, View.Front);
            Render(Path.Combine(dir, "wardrobe-back.png"), looks, View.Back);
            Render(Path.Combine(dir, "wardrobe-quarter.png"), looks, View.Quarter);
            RenderCloseUps(Path.Combine(dir, "wardrobe-closeup.png"), looks, labels);

            // What each item adds: triangles in its own two colour slots, and in all, over the bare starting look.
            var sb = new StringBuilder();
            sb.AppendLine("Reward wardrobe: each item worn alone over starting look 1 (CharacterBuilder, rest pose).");
            sb.AppendLine("item                       slot   shapes                                        own-colour tris  added tris  submeshes");
            CharacterLook bare = PlayerLooks.Copy(PlayerLooks.Presets[0]);
            int bareTris = Triangles(bare, out _);
            foreach (CharacterLook l in looks)
            {
                CharacterLook dressed = CharacterRig.Dressed(l);
                int tris = Triangles(dressed, out Mesh m);
                int own = 0;
                for (int i = (int)CharacterBuilder.Slot.Count; i < m.subMeshCount; i++) own += (int)m.GetSubMesh(i).indexCount / 3;
                string shapes = string.Join("+", dressed.Worn.SelectMany(w => w.Shapes));
                if (shapes.Length > 45) shapes = shapes.Substring(0, 42) + "...";
                WardrobeItemDef d = wardrobe.Item(l.Id);
                sb.AppendLine($"{l.Id,-26} {(d?.Slot ?? "all"),-6} {shapes,-45} {own,15}  {tris - bareTris,10}  {m.subMeshCount,9}");
                Object.DestroyImmediate(m);
            }
            File.WriteAllText(Path.Combine(dir, "wardrobe.txt"), sb.ToString());
            Debug.Log($"[NightSignal.Art] wardrobe sheets written to {dir} ({looks.Count} looks)");
            return sb.ToString();
        }

        static int Triangles(CharacterLook look, out Mesh mesh)
        {
            mesh = CharacterBuilder.Build(look, CharacterBuilder.SkeletonFor(look));
            int n = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) n += (int)mesh.GetSubMesh(i).indexCount / 3;
            return n;
        }

        /// <summary>A close-up of each item where it is worn (perspective, from the side it sits on).</summary>
        static void RenderCloseUps(string path, List<CharacterLook> looks, List<string> slots, int columns = 6)
        {
            const int tw = 320, th = 320;
            int rows = (looks.Count + columns - 1) / columns;
            var sheet = new Texture2D(tw * columns, th * rows, TextureFormat.RGB24, false);
            using (var stage = new Stage(tw, th, true, new Color(0.6f, 0.68f, 0.78f)))
            {
                for (int i = 0; i < looks.Count; i++)
                {
                    CharacterRig rig = stage.Spawn(looks[i]);
                    rig.gameObject.AddComponent<CharacterMotion>().Pose(0f);
                    float h = rig.Skeleton.H;
                    // Height fraction, yaw (+ = the driver's right side), distance.
                    float y = 0.6f, yaw = 25f, dist = 1.7f;
                    switch (slots[i])
                    {
                        case "head": y = 0.93f; yaw = 30f; dist = 0.9f; break;
                        case "hands": y = 0.47f; yaw = 70f; dist = 0.8f; break;
                        case "upper": y = 0.62f; yaw = 30f; dist = 2.4f; break;
                        case "full": y = 0.52f; yaw = 30f; dist = 3.4f; break;
                        case "lower": y = 0.28f; yaw = 25f; dist = 1.9f; break;
                        case "feet": y = 0.06f; yaw = 40f; dist = 0.9f; break;
                        case "neck": y = 0.78f; yaw = -40f; dist = 1.3f; break;
                        case "chest": y = 0.76f; yaw = -25f; dist = 0.8f; break;
                        case "wrist": y = 0.5f; yaw = 70f; dist = 0.8f; break;
                        case "hip": y = 0.52f; yaw = 30f; dist = 0.8f; break;
                        case "charm": y = 0.52f; yaw = -30f; dist = 0.8f; break;
                        case "bag": y = 0.53f; yaw = -60f; dist = 1.3f; break;
                        case "carry": y = 0.47f; yaw = -55f; dist = 1.1f; break;
                        case "belt": y = 0.55f; yaw = -75f; dist = 0.9f; break;
                    }
                    var target = new Vector3(0f, h * y, 0f);
                    Vector3 eye = target + Quaternion.Euler(0f, yaw, 0f) * new Vector3(0f, dist * 0.12f, dist);
                    stage.Cam.orthographic = false;
                    stage.Cam.fieldOfView = 34f;
                    stage.Cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
                    Color[] px = stage.Shoot();
                    int cx = i % columns, cy = rows - 1 - i / columns;
                    sheet.SetPixels(cx * tw, cy * th, tw, th, px);
                    Object.DestroyImmediate(rig.gameObject);
                }
            }
            sheet.Apply();
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            Object.DestroyImmediate(sheet);
        }
    }
}
