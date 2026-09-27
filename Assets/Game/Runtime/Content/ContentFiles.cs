using System.Collections.Generic;
using System.IO;
using NightSignal.Core.Content;

namespace NightSignal.Content
{
    /// <summary>Reads content documents from the project folder (editor, tests, dedicated server tooling).</summary>
    public static class ContentFiles
    {
        public const string DataRoot = "Assets/Content/Data";

        public static Dictionary<string, string> ReadFromProject(string dataRoot = DataRoot)
        {
            var docs = new Dictionary<string, string>();
            foreach (string f in ContentCatalogue.RequiredFiles)
                docs[f] = File.ReadAllText(Path.Combine(dataRoot, "generated", f));
            foreach (string f in ContentCatalogue.AuthoredFiles)
            {
                string path = Path.Combine(dataRoot, "authored", f);
                if (File.Exists(path)) docs[f] = File.ReadAllText(path);
            }
            return docs;
        }

        public static ContentCatalogue LoadProjectCatalogue(string dataRoot = DataRoot) => ContentCatalogue.Load(ReadFromProject(dataRoot));
    }
}
