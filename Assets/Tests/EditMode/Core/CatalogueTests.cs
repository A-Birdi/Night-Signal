using System.Collections.Generic;
using System.IO;
using System.Linq;
using NightSignal.Core.Content;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    public sealed class CatalogueTests
    {
        const string GeneratedDir = "Assets/Content/Data/generated";

        static Dictionary<string, string> LoadDocuments() => AddendumRulesTests.LoadDocuments();

        [Test]
        public void GeneratedCatalogue_LoadsAndPassesAppendixGValidation()
        {
            ContentCatalogue c = ContentCatalogue.Load(LoadDocuments());
            ValidationReport report = CatalogueValidator.Validate(c);
            Assert.That(report.Errors.Select(e => e.ToString()), Is.Empty);
            Assert.That(report.Counts["courses"], Is.EqualTo(29)); // 26 original + FP01–FP03 (Addendum 01 D03)
            Assert.That(report.Counts["regularCourses"], Is.EqualTo(24));
            Assert.That(report.Counts["stages"], Is.EqualTo(30));
            Assert.That(report.Counts["cars"], Is.EqualTo(18));
            Assert.That(report.Counts["rivals"], Is.EqualTo(48));
            Assert.That(report.Counts["challenges"], Is.EqualTo(75));
            Assert.That(report.Counts["rp.total"], Is.EqualTo(15_000));
        }

        [Test]
        public void ContentHash_IsStableAndLineEndingIndependent()
        {
            Dictionary<string, string> docs = LoadDocuments();
            string h1 = ContentCatalogue.Load(docs).ContentHash;
            var crlf = docs.ToDictionary(kv => kv.Key, kv => kv.Value.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            Assert.That(ContentCatalogue.Load(crlf).ContentHash, Is.EqualTo(h1));
            Assert.That(h1, Has.Length.EqualTo(64));
        }

        [Test]
        public void Validator_DetectsASeventhLieutenantStyleError()
        {
            Dictionary<string, string> docs = LoadDocuments();
            docs["rivals.json"] = docs["rivals.json"].Replace("\"role\": \"normal-final\"", "\"role\": \"crew\"");
            ValidationReport report = CatalogueValidator.Validate(ContentCatalogue.Load(docs));
            Assert.That(report.Errors.Any(e => e.Code == "FINAL_NORMAL"), Is.True);
        }

        [Test]
        public void Loader_RejectsDuplicateIds()
        {
            Dictionary<string, string> docs = LoadDocuments();
            docs["cars.json"] = docs["cars.json"].Replace("\"id\": \"V02\"", "\"id\": \"V01\"");
            Assert.Throws<ContentLoadException>(() => ContentCatalogue.Load(docs));
        }
    }
}
