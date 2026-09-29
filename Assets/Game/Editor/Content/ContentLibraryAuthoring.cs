using System.Collections.Generic;
using System.IO;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Editor.Art;
using UnityEditor;
using UnityEngine;

namespace NightSignal.Editor.ContentTools
{
    /// <summary>Keeps Assets/Content/Resources/ContentLibrary.asset referencing every content document.</summary>
    public static class ContentLibraryAuthoring
    {
        const string ResourcesFolder = "Assets/Content/Resources";
        const string LibraryPath = ResourcesFolder + "/ContentLibrary.asset";

        [MenuItem("Night Signal/Content/Refresh Content Library")]
        public static ContentLibrary Refresh()
        {
            Directory.CreateDirectory(ResourcesFolder);
            var lib = AssetDatabase.LoadAssetAtPath<ContentLibrary>(LibraryPath);
            bool create = lib == null;
            if (create) lib = ScriptableObject.CreateInstance<ContentLibrary>();
            var docs = new List<TextAsset>();
            foreach (string f in ContentCatalogue.RequiredFiles)
                docs.Add(Load($"Assets/Content/Data/generated/{f}"));
            foreach (string f in ContentCatalogue.AuthoredFiles)
            {
                string path = $"Assets/Content/Data/authored/{f}";
                if (File.Exists(path)) docs.Add(Load(path));
            }
            lib.Documents = docs.ToArray();
            lib.CarBodies = Load("Assets/Content/Data/authored/cars.body.json");
            // While We Wait toy content (Addendum 02): a separate, non-progression document set.
            var toys = new List<TextAsset>();
            foreach (string f in Core.Toys.ToyContent.Files) toys.Add(Load($"Assets/Content/Data/authored/toys/{f}"));
            lib.ToyDocuments = toys.ToArray();
            lib.PartsDocument = Load("Assets/Content/Data/authored/parts.json");
            lib.RecipesDocument = Load("Assets/Content/Data/authored/build-recipes.json");
            lib.CustomizationDocument = Load("Assets/Content/Data/authored/customization.json");
            if (File.Exists("Assets/Content/Data/authored/story/rivals.look.json"))
                lib.CharacterLooks = Load("Assets/Content/Data/authored/story/rivals.look.json");
            if (File.Exists("Assets/Content/Data/authored/story/npcs.look.json"))
                lib.NpcLooks = Load("Assets/Content/Data/authored/story/npcs.look.json");
            lib.MeetText = Load("Assets/Content/Data/authored/story/meet.text.json");
            lib.StageStory = Load("Assets/Content/Data/authored/story/stages.story.json");
            lib.CrewDiary = Load("Assets/Content/Data/authored/story/crews.diary.json");
            lib.StoryRecords = Load("Assets/Content/Data/authored/story/radio-records.json");
            lib.RivalStory = Load("Assets/Content/Data/authored/story/rivals.story.json");
            lib.Endings = Load("Assets/Content/Data/authored/story/endings.json");
            lib.TutorialLessonsDocument = Load("Assets/Content/Data/authored/tutorial/lessons.json");
            if (create) AssetDatabase.CreateAsset(lib, LibraryPath);
            EditorUtility.SetDirty(lib);

            CarMaterialAuthoring.Ensure();
            CharacterMaterialAuthoring.Ensure();
            EnsureMusicLibrary();
            AssetDatabase.SaveAssets();
            Debug.Log($"[NightSignal.Content] Library: {lib.Documents.Length} documents, catalogue hash {lib.Catalogue.ContentHash.Substring(0, 12)}");
            return lib;
        }

        /// <summary>Resources/MusicLibrary.asset: every score document and the instrument library, for the runtime music player.</summary>
        public static GameAudio.MusicLibrary EnsureMusicLibrary()
        {
            const string path = ResourcesFolder + "/MusicLibrary.asset";
            var lib = AssetDatabase.LoadAssetAtPath<GameAudio.MusicLibrary>(path);
            bool create = lib == null;
            if (create) lib = ScriptableObject.CreateInstance<GameAudio.MusicLibrary>();
            lib.Instruments = Load("Assets/Content/Audio/Scores/instruments.json");
            var cues = new List<TextAsset>();
            foreach (string f in Directory.GetFiles("Assets/Content/Audio/Scores", "mus_*.json"))
                cues.Add(Load(f.Replace(Path.DirectorySeparatorChar, '/')));
            cues.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            lib.Cues = cues.ToArray();
            if (create) AssetDatabase.CreateAsset(lib, path);
            EditorUtility.SetDirty(lib);
            return lib;
        }

        static TextAsset Load(string path)
        {
            var t = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (t == null) throw new FileNotFoundException("Content document missing", path);
            return t;
        }
    }
}
