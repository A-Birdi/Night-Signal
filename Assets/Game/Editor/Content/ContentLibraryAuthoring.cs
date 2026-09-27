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
            if (create) AssetDatabase.CreateAsset(lib, LibraryPath);
            EditorUtility.SetDirty(lib);

            CarMaterialAuthoring.Ensure();
            AssetDatabase.SaveAssets();
            Debug.Log($"[NightSignal.Content] Library: {lib.Documents.Length} documents, catalogue hash {lib.Catalogue.ContentHash.Substring(0, 12)}");
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
