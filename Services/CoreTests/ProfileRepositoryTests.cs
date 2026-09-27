using System.Text;
using NightSignal.Core.Profiles;
using NightSignal.Core.Rules;
using Newtonsoft.Json.Linq;

namespace NightSignal.CoreTests;

/// <summary>Wraps a storage and injects failures: a crash between the temp write and the replace, torn writes, read errors.</summary>
internal sealed class FaultyStorage(IProfileStorage inner) : IProfileStorage
{
    public bool CrashBeforeReplace;
    public bool TearTempWrites;
    public bool FailReads;
    public int Writes;

    public byte[] Read(string name) => FailReads ? throw new IOException("simulated disk read error") : inner.Read(name);

    public void Write(string name, byte[] data)
    {
        Writes++;
        if (TearTempWrites && name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            inner.Write(name, data.Take(data.Length / 2).ToArray()); // power lost mid-write
            throw new IOException("simulated power loss during write");
        }
        inner.Write(name, data);
    }

    public void Replace(string source, string destination, string backup)
    {
        if (CrashBeforeReplace) throw new IOException("simulated crash before replace");
        inner.Replace(source, destination, backup);
    }

    public bool Exists(string name) => inner.Exists(name);
    public IReadOnlyList<string> List(string prefix) => inner.List(prefix);
    public void Delete(string name) => inner.Delete(name);
}

public sealed class ProfileRepositoryTests
{
    static DateTime clock = TestContent.T0;
    static DateTime Now() => clock = clock.AddSeconds(1);

    static LocalProfile NewProfile(string name = "Robin") => LocalProgressionTests.NewProfile(name);

    static LocalProfile Earn(LocalProfile p) => LocalProgressionTests.Apply(p, LocalProgressionTests.FreeplayRun(p, "C01", 1));

    [Fact]
    public void SaveAndLoad_RoundTripsTheWholeProfile()
    {
        var storage = new InMemoryProfileStorage();
        var repo = new ProfileRepository(storage, utcNow: Now);
        LocalProfile p = LocalProgressionTests.Apply(NewProfile(), LocalProgressionTests.StageRun(NewProfile(), "S01"));
        ProfileSaveResult saved = repo.Create(p);
        Assert.Equal(ProfileSaveStatus.Saved, saved.Status);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(1, p.Revision);

        ProfileLoadResult loaded = repo.Load(p.ProfileId);
        Assert.Equal(ProfileLoadStatus.Loaded, loaded.Status);
        Assert.Equal(ProfileError.None, loaded.Error);
        Assert.Equal(ProfileJson.Serialize(p), ProfileJson.Serialize(loaded.Profile));
        Assert.True(storage.Exists(ProfileRepository.RecoveryName(p.ProfileId, 1)));

        byte[] raw = storage.Read(ProfileRepository.MainName(p.ProfileId));
        string header = Encoding.ASCII.GetString(raw, 0, Array.IndexOf(raw, (byte)'\n'));
        Assert.StartsWith("NIGHT-SIGNAL-LOCAL-SAVE 1 sha256=", header);
        Assert.Contains("schemaVersion=1 revision=1 profile=" + p.ProfileId, header);
        string payload = Encoding.UTF8.GetString(raw, header.Length + 1, raw.Length - header.Length - 1);
        Assert.Contains("\"schema\": \"night-signal/local-profile@1\"", payload);
        Assert.Contains("\"domain\": \"local\"", payload);
        Assert.DoesNotContain("password", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrashBetweenTempWriteAndReplace_LosesNothing_AndTheCompleteSaveIsRecovered()
    {
        var disk = new InMemoryProfileStorage();
        var faulty = new FaultyStorage(disk);
        var repo = new ProfileRepository(faulty, utcNow: Now);
        LocalProfile p = NewProfile();
        Assert.True(repo.Create(p).Ok);
        LocalProfile richer = Earn(p);

        faulty.CrashBeforeReplace = true;
        ProfileSaveResult crashed = repo.Save(richer);
        Assert.Equal(ProfileSaveStatus.IoError, crashed.Status);
        Assert.Contains("previous save is intact", crashed.Message);
        Assert.Equal(1, richer.Revision); // unchanged on failure
        Assert.True(disk.Exists(ProfileRepository.TempName(p.ProfileId)));

        // "Restart": a fresh repository over the same disk.
        ProfileLoadResult after = new ProfileRepository(disk).Load(p.ProfileId);
        Assert.Equal(ProfileLoadStatus.Recovered, after.Status);
        Assert.Equal(ProfileCopyKind.InterruptedSave, after.Source);
        Assert.Equal(ProfileError.None, after.Error);
        Assert.Equal(2, after.Profile.Revision);
        Assert.Equal(richer.WalletBalance, after.Profile.WalletBalance);

        // Saving the recovered profile completes normally and supersedes the temp file.
        var healthy = new ProfileRepository(disk, utcNow: Now);
        Assert.Equal(3, healthy.Save(after.Profile).Revision);
        Assert.False(disk.Exists(ProfileRepository.TempName(p.ProfileId)));
        Assert.Equal(ProfileLoadStatus.Loaded, healthy.Load(p.ProfileId).Status);
    }

    [Fact]
    public void TornTempWrite_KeepsThePreviousGoodSave()
    {
        var disk = new InMemoryProfileStorage();
        var faulty = new FaultyStorage(disk);
        var repo = new ProfileRepository(faulty, utcNow: Now);
        LocalProfile p = NewProfile();
        Assert.True(repo.Create(p).Ok);
        faulty.TearTempWrites = true;
        Assert.Equal(ProfileSaveStatus.IoError, repo.Save(Earn(p)).Status);

        ProfileLoadResult r = new ProfileRepository(disk).Load(p.ProfileId);
        Assert.Equal(ProfileLoadStatus.Loaded, r.Status);
        Assert.Equal(1, r.Profile.Revision);
        Assert.Equal(p.WalletBalance, r.Profile.WalletBalance);
        Assert.Contains(r.Problems, x => x.Name.EndsWith(".tmp") && x.Error == ProfileError.Corrupt);
    }

    [Fact]
    public void CorruptMainFile_RecoversNewestGoodCopy_AndKeepsTheDamagedFileAside()
    {
        var disk = new InMemoryProfileStorage();
        var repo = new ProfileRepository(disk, utcNow: Now);
        LocalProfile p = NewProfile();
        repo.Create(p);
        LocalProfile p2 = Earn(p);
        Assert.Equal(2, repo.Save(p2).Revision);

        byte[] damaged = disk.Read(ProfileRepository.MainName(p.ProfileId));
        damaged[damaged.Length - 20] ^= 0x5A; // one flipped byte in the payload
        disk.Write(ProfileRepository.MainName(p.ProfileId), damaged);

        ProfileLoadResult r = repo.Load(p.ProfileId);
        Assert.Equal(ProfileLoadStatus.Recovered, r.Status);
        Assert.Equal(ProfileError.Corrupt, r.Error);
        Assert.Equal(ProfileCopyKind.RecoveryCopy, r.Source);
        Assert.Equal(2, r.Profile.Revision);
        Assert.Equal(p2.WalletBalance, r.Profile.WalletBalance);
        Assert.Contains(r.Problems, x => x.Detail.Contains("checksum"));
        Assert.Single(disk.List(ProfileRepository.QuarantinePrefix(p.ProfileId)));
        Assert.Contains("kept aside", r.Message);

        // Garbage and truncation are detected too.
        disk.Write(ProfileRepository.MainName(p.ProfileId), Encoding.UTF8.GetBytes("{ not a save"));
        Assert.Equal(ProfileLoadStatus.Recovered, repo.Load(p.ProfileId).Status);
        byte[] good = disk.Read(ProfileRepository.RecoveryName(p.ProfileId, 2));
        disk.Write(ProfileRepository.MainName(p.ProfileId), good.Take(good.Length - 1).ToArray());
        ProfileLoadResult truncated = repo.Load(p.ProfileId);
        Assert.Equal(ProfileLoadStatus.Recovered, truncated.Status);
        Assert.Contains(truncated.Problems, x => x.Detail.Contains("truncated"));

        // Saving straight over a damaged main file (no load first) still keeps the damaged bytes aside.
        byte[] junkMain = Encoding.UTF8.GetBytes("damaged before save");
        disk.Write(ProfileRepository.MainName(p.ProfileId), junkMain);
        Assert.True(repo.Save(r.Profile).Ok);
        Assert.Contains(disk.List(ProfileRepository.QuarantinePrefix(p.ProfileId)), n => disk.Read(n).SequenceEqual(junkMain));

        // With every copy destroyed the load fails clearly — it never invents a blank profile.
        foreach (string name in disk.List(ProfileRepository.Folder(p.ProfileId)).Where(n => !n.Contains("quarantine")))
            disk.Write(name, Encoding.UTF8.GetBytes("junk"));
        ProfileLoadResult failed = repo.Load(p.ProfileId);
        Assert.Equal(ProfileLoadStatus.Failed, failed.Status);
        Assert.Equal(ProfileError.Corrupt, failed.Error);
        Assert.Null(failed.Profile);
    }

    [Fact]
    public void NewerSchema_IsNeverLoadedDowngradedOrOverwritten()
    {
        var disk = new InMemoryProfileStorage();
        var repo = new ProfileRepository(disk, utcNow: Now);
        LocalProfile p = NewProfile();
        repo.Create(p);

        JObject future = ProfileJson.FromObject(p);
        future["schemaVersion"] = 2;
        future["schema"] = "night-signal/local-profile@2";
        future["hoverboards"] = new JArray("HB01");
        byte[] newer = ProfileFileCodec.EncodePayload(future.ToString(), 2, 5, p.ProfileId);
        disk.Write(ProfileRepository.MainName(p.ProfileId), newer);

        ProfileLoadResult r = repo.Load(p.ProfileId);
        Assert.Equal(ProfileLoadStatus.Failed, r.Status);
        Assert.Equal(ProfileError.NewerSchema, r.Error);
        Assert.Contains("newer version", r.Message);

        ProfileSaveResult save = repo.Save(p);
        Assert.Equal(ProfileSaveStatus.NewerSchemaOnDisk, save.Status);
        Assert.Equal(newer, disk.Read(ProfileRepository.MainName(p.ProfileId)));
    }

    [Fact]
    public void OlderSchema_IsMigratedThroughTheHook_WithAPreMigrationCopy()
    {
        var disk = new InMemoryProfileStorage();
        LocalProfile p = NewProfile("Kaede");
        // A hypothetical version-0 document with older member names.
        JObject v0 = ProfileJson.FromObject(p);
        v0["schemaVersion"] = 0;
        v0["schema"] = "night-signal/local-profile@0";
        v0["name"] = v0["displayName"];
        v0.Remove("displayName");
        disk.Write(ProfileRepository.MainName(p.ProfileId), ProfileFileCodec.EncodePayload(v0.ToString(), 0, 0, p.ProfileId));

        ProfileLoadResult noPath = new ProfileRepository(disk).Load(p.ProfileId);
        Assert.Equal(ProfileLoadStatus.Failed, noPath.Status);
        Assert.Equal(ProfileError.Corrupt, noPath.Error);
        Assert.Contains(noPath.Problems, x => x.Detail.Contains("no migration"));

        var migrations = new ProfileMigrations().Register(0, doc =>
        {
            doc["displayName"] = doc["name"];
            doc.Remove("name");
            return doc;
        });
        ProfileLoadResult r = new ProfileRepository(disk, migrations).Load(p.ProfileId);
        Assert.Equal(ProfileLoadStatus.Loaded, r.Status);
        Assert.True(r.Migrated);
        Assert.Equal(0, r.MigratedFromVersion);
        Assert.Equal("Kaede", r.Profile.DisplayName);
        Assert.Equal(LocalProfile.CurrentSchemaVersion, r.Profile.SchemaVersion);
        Assert.Single(disk.List(ProfileRepository.Folder(p.ProfileId) + "premigration/"));
    }

    [Fact]
    public void MissingAndUnreadableProfiles_HaveClearErrorCodes()
    {
        var disk = new InMemoryProfileStorage();
        ProfileLoadResult missing = new ProfileRepository(disk).Load("lp_nothere");
        Assert.Equal(ProfileLoadStatus.Failed, missing.Status);
        Assert.Equal(ProfileError.Missing, missing.Error);
        Assert.Equal(ProfileError.Missing, new ProfileRepository(disk).Load("../../etc").Error);

        var repo = new ProfileRepository(disk, utcNow: Now);
        LocalProfile p = NewProfile();
        repo.Create(p);
        var faulty = new FaultyStorage(disk) { FailReads = true };
        ProfileLoadResult io = new ProfileRepository(faulty).Load(p.ProfileId);
        Assert.Equal(ProfileLoadStatus.Failed, io.Status);
        Assert.Equal(ProfileError.IoError, io.Error);
        Assert.Contains("could not be read", io.Message);
        Assert.Equal(ProfileSaveStatus.IoError, new ProfileRepository(faulty).Save(p).Status);
    }

    [Fact]
    public void RecoveryCopies_AreBoundedToTheLastThreeGoodSaves()
    {
        var disk = new InMemoryProfileStorage();
        var repo = new ProfileRepository(disk, utcNow: Now);
        LocalProfile p = NewProfile();
        repo.Create(p);
        for (int i = 0; i < 5; i++)
        {
            p = Earn(p);
            Assert.True(repo.Save(p).Ok);
        }
        Assert.Equal(6, p.Revision);
        Assert.Equal(
            new[] { 4L, 5L, 6L }.Select(r => ProfileRepository.RecoveryName(p.ProfileId, r)),
            disk.List(ProfileRepository.RecoveryPrefix(p.ProfileId)));
        Assert.True(disk.Exists(ProfileRepository.BackupName(p.ProfileId)));
    }

    [Fact]
    public void StaleCopies_CannotOverwriteANewerSave()
    {
        var disk = new InMemoryProfileStorage();
        var repo = new ProfileRepository(disk, utcNow: Now);
        repo.Create(NewProfile());
        string id = repo.ProfileIds().Single();
        LocalProfile a = repo.Load(id).Profile, b = repo.Load(id).Profile;
        Assert.True(repo.Save(Earn(a)).Ok);
        ProfileSaveResult stale = repo.Save(b);
        Assert.Equal(ProfileSaveStatus.Conflict, stale.Status);
        Assert.Equal(2, repo.Load(id).Profile.Revision);
    }

    [Fact]
    public void InvalidProfiles_AreNotWritten()
    {
        var disk = new InMemoryProfileStorage();
        var repo = new ProfileRepository(disk, utcNow: Now);
        LocalProfile p = NewProfile();
        p.WalletBalance = Limits.WalletCap + 1;
        Assert.Equal(ProfileSaveStatus.Invalid, repo.Save(p).Status);
        p.WalletBalance = 0;
        p.Domain = ProgressionDomain.Online;
        Assert.Equal(ProfileSaveStatus.Invalid, repo.Save(p).Status);
        Assert.Empty(disk.List(""));
    }

    [Fact]
    public void MultipleProfiles_ListCreateAndDeleteWithExplicitConfirmation()
    {
        var disk = new InMemoryProfileStorage();
        var repo = new ProfileRepository(disk, utcNow: Now);
        LocalProfile robin = LocalProgression.NewProfile(TestContent.Catalogue, TestContent.Music,
            new NewLocalProfileRequest { DisplayName = "Robin", StarterCarModelId = "V01", Utc = TestContent.T0, ProfileId = "lp_robin" }).Profile;
        LocalProfile kaede = LocalProgression.NewProfile(TestContent.Catalogue, TestContent.Music,
            new NewLocalProfileRequest { DisplayName = "Kaede", StarterCarModelId = "V03", Utc = TestContent.T0, ProfileId = "lp_kaede" }).Profile;
        Assert.True(repo.Create(robin).Ok);
        Assert.True(repo.Create(kaede).Ok);
        Assert.Equal(ProfileSaveStatus.AlreadyExists, repo.Create(robin).Status);

        IReadOnlyList<ProfileSummary> list = repo.List();
        Assert.Equal(new[] { "Kaede", "Robin" }, list.Select(s => s.DisplayName).OrderBy(x => x));
        Assert.All(list, s => Assert.Equal(ProfileLoadStatus.Loaded, s.Status));
        Assert.All(list, s => Assert.Equal("New Signal", s.Rank));

        ProfileDeleteResult unconfirmed = repo.Delete("lp_robin", confirmed: false);
        Assert.Equal(ProfileDeleteStatus.ConfirmationRequired, unconfirmed.Status);
        Assert.Equal(2, repo.List().Count);
        ProfileDeleteResult deleted = repo.Delete("lp_robin", confirmed: true);
        Assert.Equal(ProfileDeleteStatus.Deleted, deleted.Status);
        Assert.True(deleted.FilesRemoved >= 2);
        Assert.Equal(new[] { "lp_kaede" }, repo.ProfileIds());
        Assert.Equal(ProfileDeleteStatus.NotFound, repo.Delete("lp_robin", confirmed: true).Status);

        for (int i = 0; repo.ProfileIds().Count < ProfileRepository.MaxProfiles; i++)
            Assert.True(repo.Create(LocalProgression.NewProfile(TestContent.Catalogue, null,
                new NewLocalProfileRequest { DisplayName = $"P{i}", StarterCarModelId = "V02", Utc = TestContent.T0, ProfileId = $"lp_p{i}" }).Profile).Ok);
        Assert.Equal(ProfileSaveStatus.LimitReached, repo.Create(LocalProgression.NewProfile(TestContent.Catalogue, null,
            new NewLocalProfileRequest { DisplayName = "One too many", StarterCarModelId = "V02", Utc = TestContent.T0, ProfileId = "lp_extra" }).Profile).Status);
    }

    [Fact]
    public void DamagedProfiles_StillAppearInTheList()
    {
        var disk = new InMemoryProfileStorage();
        var repo = new ProfileRepository(disk, utcNow: Now);
        LocalProfile p = NewProfile();
        repo.Create(p);
        foreach (string name in disk.List(ProfileRepository.Folder(p.ProfileId)))
            disk.Write(name, Encoding.UTF8.GetBytes("junk"));
        ProfileSummary s = Assert.Single(repo.List());
        Assert.Equal(ProfileLoadStatus.Failed, s.Status);
        Assert.Equal(ProfileError.Corrupt, s.Error);
    }

    [Fact]
    public void FileSystemStorage_SavesRecoversAndDeletes_InATemporaryDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-coretests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fs = new FileSystemProfileStorage(root);
            var repo = new ProfileRepository(fs, utcNow: Now);
            LocalProfile p = NewProfile();
            Assert.True(repo.Create(p).Ok);
            LocalProfile p2 = Earn(p);
            Assert.True(repo.Save(p2).Ok);
            Assert.True(File.Exists(Path.Combine(root, "profiles", p.ProfileId, "profile.nsave")));
            Assert.True(File.Exists(Path.Combine(root, "profiles", p.ProfileId, "profile.nsave.bak")));

            // Crash after the temp file was fully written but before the replace.
            LocalProfile p3 = Earn(p2);
            p3.Revision = 3;
            fs.Write(ProfileRepository.TempName(p.ProfileId), ProfileFileCodec.Encode(p3));
            ProfileLoadResult r = new ProfileRepository(new FileSystemProfileStorage(root)).Load(p.ProfileId);
            Assert.Equal(ProfileLoadStatus.Recovered, r.Status);
            Assert.Equal(3, r.Profile.Revision);
            Assert.Equal(p3.WalletBalance, r.Profile.WalletBalance);
            Assert.True(repo.Save(r.Profile).Ok);
            Assert.Equal(ProfileLoadStatus.Loaded, repo.Load(p.ProfileId).Status);

            Assert.Equal(ProfileDeleteStatus.Deleted, repo.Delete(p.ProfileId, confirmed: true).Status);
            Assert.False(Directory.Exists(Path.Combine(root, "profiles", p.ProfileId)));
            Assert.Throws<ArgumentException>(() => fs.Read("../outside.txt"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
