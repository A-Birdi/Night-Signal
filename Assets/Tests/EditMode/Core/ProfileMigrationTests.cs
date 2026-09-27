using System.IO;
using NightSignal.Core.Profiles;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Core
{
    /// <summary>
    /// Addendum 03 I02: Local saves written before Addendum 03 load with this build without losing ownership or progress.
    /// The fixtures are real saves written by the built game's isolated automation tours before the first Addendum 03
    /// change (Test Yard tour 06:59, Appearance tour 08:11 on 2026-09-27): wallet, starter car, frozen builds, applied
    /// livery, music. Loaded through the repository (header, SHA-256, schema), validated, saved again by this build and
    /// reloaded — every owned or earned section must be byte-for-byte the same data.
    /// </summary>
    public sealed class ProfileMigrationTests
    {
        static readonly string[] Kept = { "walletBalance", "walletHistory", "starterCarModelId", "cars", "unassignedParts", "courses", "campaign",
                                          "challenges", "cosmetics", "music", "records", "tutorial" };
        string folder;

        [SetUp]
        public void SetUp() => folder = Path.Combine(Path.GetTempPath(), "ns-migration-" + System.Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        [TestCase("livery-driver-0811.nsave")]
        [TestCase("yard-driver-0659.nsave")]
        public void PreAddendum03Save_Loads_Validates_AndKeepsEverythingItOwns(string fixture)
        {
            byte[] original = File.ReadAllBytes(Path.Combine(Application.dataPath, "Tests", "EditMode", "Fixtures", "PreA03", fixture));
            ProfileFileCodec.Decoded decoded = ProfileFileCodec.Decode(original);
            Assert.That(decoded.Ok, Is.True, "the fixture is an intact save: " + decoded.Detail);

            var storage = new FileSystemProfileStorage(folder);
            Directory.CreateDirectory(Path.Combine(folder, "profiles", decoded.ProfileId));
            File.WriteAllBytes(Path.Combine(folder, "profiles", decoded.ProfileId, "profile.nsave"), original);
            var repo = new ProfileRepository(storage);

            ProfileLoadResult loaded = repo.Load(decoded.ProfileId);
            Assert.That(loaded.Status, Is.EqualTo(ProfileLoadStatus.Loaded), loaded.Message);
            Assert.That(loaded.Migrated, Is.False, "same schema version: nothing to migrate");
            Assert.That(loaded.Profile.Validate(), Is.Empty);
            Assert.That(loaded.Profile.Cars, Is.Not.Empty, "the starter car is still owned");

            ProfileSaveResult saved = repo.Save(loaded.Profile);
            Assert.That(saved.Ok, Is.True, saved.Message);
            ProfileLoadResult again = repo.Load(decoded.ProfileId);
            Assert.That(again.Status, Is.EqualTo(ProfileLoadStatus.Loaded), again.Message);

            JObject before = JObject.Parse(decoded.PayloadJson);
            JObject after = JObject.Parse(ProfileFileCodec.Decode(ProfileFileCodec.Encode(again.Profile)).PayloadJson);
            foreach (string section in Kept)
                Assert.That(JToken.DeepEquals(before[section], after[section]), Is.True, $"'{section}' unchanged after a load and save by this build");
        }
    }
}
