using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NightSignal.Characters;
using UnityEditor;
using UnityEngine;

namespace NightSignal.Editor.ArtTools
{
    public static partial class CharacterSheet
    {
        /// <summary>
        /// Character levels of detail side by side: for a spread of rivals and a fully dressed driver, the full, mid and far
        /// bodies in a three-quarter view at a common scale (each level forced, the LODGroup off), with vertex counts.
        /// Output: Builds/Screenshots/characters/character-lods.png and character-lods.txt.
        /// </summary>
        [MenuItem("Night Signal/Art/Render Character LOD Sheet")]
        public static void RenderLodDefault() => RenderLods(Path.GetFullPath(Path.Combine("Builds", "Screenshots", "characters")));

        public static string RenderLods(string dir)
        {
            Directory.CreateDirectory(dir);
            List<CharacterLook> rivals = LoadLooks();
            var looks = new List<CharacterLook>();
            foreach (string id in new[] { "R01", "R11", "R17", "R30", "R48" })
            {
                CharacterLook l = rivals.FirstOrDefault(x => x.Id == id);
                if (l != null) looks.Add(l);
            }
            CharacterLook dressed = PlayerLooks.Copy(PlayerLooks.Presets[3]);
            dressed.Id = "dressed";
            dressed.Wardrobe = new List<string> { "cedar-crew-cap", "tea-hour-gloves", "red-horizon-coat", "mizuhana-trousers", "recovery-boots", "highland-scarf", "workshop-satchel", "road-atlas" };
            looks.Add(dressed);

            const int tw = 200, th = 400;
            int columns = looks.Count * CharacterBuilder.LodCount;
            var sheet = new Texture2D(tw * columns, th, TextureFormat.RGB24, false);
            var sb = new StringBuilder("Character levels of detail (vertices: full / mid / far)\n");
            using (var stage = new Stage(tw, th, true, new Color(0.6f, 0.68f, 0.78f)))
            {
                for (int i = 0; i < looks.Count; i++)
                {
                    var counts = new int[CharacterBuilder.LodCount];
                    for (int lod = 0; lod < CharacterBuilder.LodCount; lod++)
                    {
                        CharacterRig rig = stage.Spawn(looks[i]);
                        rig.gameObject.AddComponent<CharacterMotion>().Pose(0f);
                        LODGroup group = rig.GetComponent<LODGroup>();
                        if (group != null) group.enabled = false;
                        for (int k = 0; k < rig.Lods.Length; k++) rig.Lods[k].enabled = k == lod;
                        counts[lod] = rig.Lods[Mathf.Min(lod, rig.Lods.Length - 1)].sharedMesh.vertexCount;
                        Aim(stage.Cam, View.Quarter, rig.Skeleton.H);
                        Color[] px = stage.Shoot();
                        sheet.SetPixels((i * CharacterBuilder.LodCount + lod) * tw, 0, tw, th, px);
                        Object.DestroyImmediate(rig.gameObject);
                    }
                    sb.AppendLine($"{looks[i].Id,-8} {counts[0],5} / {counts[1],5} / {counts[2],5}  ({100f * counts[1] / counts[0]:0}% / {100f * counts[2] / counts[0]:0}%)");
                }
            }
            sheet.Apply();
            File.WriteAllBytes(Path.Combine(dir, "character-lods.png"), sheet.EncodeToPNG());
            Object.DestroyImmediate(sheet);
            File.WriteAllText(Path.Combine(dir, "character-lods.txt"), sb.ToString());
            return sb.ToString();
        }
    }
}
