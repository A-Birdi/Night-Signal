using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NightSignal.Core.Profiles
{
    /// <summary>Error code for one profile file or a whole load.</summary>
    public enum ProfileError
    {
        None = 0,
        Missing = 1,
        /// <summary>Truncated, checksum mismatch, unreadable JSON, wrong profile, failed validation or no migration path.</summary>
        Corrupt = 2,
        /// <summary>Written by a newer game version; never overwritten or downgraded by this build.</summary>
        NewerSchema = 3,
        IoError = 4,
    }

    public enum ProfileLoadStatus
    {
        Loaded = 0,
        /// <summary>Loaded from an interrupted save, the backup or a recovery copy; <see cref="ProfileLoadResult.Error"/> says why.</summary>
        Recovered = 1,
        Failed = 2,
    }

    public enum ProfileCopyKind { Main = 0, InterruptedSave = 1, Backup = 2, RecoveryCopy = 3 }

    public sealed class ProfileFileProblem
    {
        public string Name = "";
        public ProfileError Error;
        public string Detail = "";

        public override string ToString() => $"{Name}: {Error} ({Detail})";
    }

    public sealed class ProfileLoadResult
    {
        public ProfileLoadStatus Status;
        /// <summary>For Loaded: None. For Recovered: the main file's problem (None for a completed interrupted save). For Failed: why.</summary>
        public ProfileError Error;
        public LocalProfile Profile;
        public ProfileCopyKind Source;
        public string SourceName = "";
        public bool Migrated;
        public int MigratedFromVersion;
        public List<ProfileFileProblem> Problems = new List<ProfileFileProblem>();
        public string Message = "";
        public bool Ok => Status != ProfileLoadStatus.Failed;
    }

    public enum ProfileSaveStatus
    {
        Saved = 0,
        Invalid = 1,
        /// <summary>The copy being saved is older than what is on disk (a stale editor); nothing was written.</summary>
        Conflict = 2,
        /// <summary>A newer game version's save is on disk; this build refuses to overwrite it.</summary>
        NewerSchemaOnDisk = 3,
        IoError = 4,
        AlreadyExists = 5,
        LimitReached = 6,
    }

    public sealed class ProfileSaveResult
    {
        public ProfileSaveStatus Status;
        public long Revision;
        public string Message = "";
        public List<string> Warnings = new List<string>();
        public bool Ok => Status == ProfileSaveStatus.Saved;
    }

    public enum ProfileDeleteStatus { Deleted = 0, ConfirmationRequired = 1, NotFound = 2, IoError = 3 }

    public sealed class ProfileDeleteResult
    {
        public ProfileDeleteStatus Status;
        public int FilesRemoved;
        public string Message = "";
    }

    public sealed class ProfileSummary
    {
        public string ProfileId = "";
        public string DisplayName = "";
        public DateTime UpdatedUtc;
        public long Revision;
        public int RankPoints;
        public string Rank = "";
        public long WalletBalance;
        public ProfileLoadStatus Status;
        public ProfileError Error;
        public string Message = "";
    }

    /// <summary>
    /// Schema migration hook: each registered step upgrades a raw profile document from version N to N + 1. Steps work on
    /// JSON so no data is lost to the typed model; a step must never drop named presets or other player-made content.
    /// </summary>
    public sealed class ProfileMigrations
    {
        readonly Dictionary<int, Func<JObject, JObject>> steps = new Dictionary<int, Func<JObject, JObject>>();

        /// <summary>No steps: version 1 is the first Local profile schema.</summary>
        public static ProfileMigrations Default() => new ProfileMigrations();

        public ProfileMigrations Register(int fromVersion, Func<JObject, JObject> step)
        {
            if (fromVersion < 0 || fromVersion >= LocalProfile.CurrentSchemaVersion)
                throw new ArgumentOutOfRangeException(nameof(fromVersion), "Migrations upgrade older versions to the current one");
            steps[fromVersion] = step ?? throw new ArgumentNullException(nameof(step));
            return this;
        }

        public bool CanMigrate(int fromVersion)
        {
            for (int v = fromVersion; v < LocalProfile.CurrentSchemaVersion; v++)
                if (!steps.ContainsKey(v)) return false;
            return true;
        }

        public JObject Migrate(JObject document, int fromVersion)
        {
            JObject doc = (JObject)document.DeepClone();
            for (int v = fromVersion; v < LocalProfile.CurrentSchemaVersion; v++)
            {
                if (!steps.TryGetValue(v, out Func<JObject, JObject> step))
                    throw new InvalidOperationException($"No migration from schema version {v}");
                doc = step(doc) ?? throw new InvalidOperationException($"Migration from version {v} returned nothing");
                doc["schemaVersion"] = v + 1;
            }
            doc["schema"] = LocalProfile.SchemaId;
            return doc;
        }
    }

    /// <summary>
    /// Local save container: one ASCII header line followed by the UTF-8 JSON payload.
    /// <c>NIGHT-SIGNAL-LOCAL-SAVE 1 sha256=&lt;hex&gt; bytes=&lt;n&gt; schemaVersion=&lt;v&gt; revision=&lt;r&gt; profile=&lt;id&gt;</c>.
    /// The SHA-256 detects truncation and accidental corruption only. It is not a signature and proves nothing about how
    /// the progress was earned (no secret is embedded in the game).
    /// </summary>
    public static class ProfileFileCodec
    {
        public const string Magic = "NIGHT-SIGNAL-LOCAL-SAVE";
        public const int ContainerVersion = 1;
        const int MaxHeaderBytes = 512;

        public sealed class Decoded
        {
            public ProfileError Error;
            public string Detail = "";
            public int SchemaVersion;
            public long Revision;
            public string ProfileId = "";
            public string Sha256 = "";
            public string PayloadJson = "";
            public bool Ok => Error == ProfileError.None;
        }

        public static byte[] Encode(LocalProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            string json = ProfileJson.Serialize(profile);
            return EncodePayload(json, profile.SchemaVersion, profile.Revision, profile.ProfileId);
        }

        public static byte[] EncodePayload(string payloadJson, int schemaVersion, long revision, string profileId)
        {
            byte[] payload = new UTF8Encoding(false).GetBytes(payloadJson);
            string header = string.Format(CultureInfo.InvariantCulture, "{0} {1} sha256={2} bytes={3} schemaVersion={4} revision={5} profile={6}\n",
                Magic, ContainerVersion, Sha256Hex(payload), payload.Length, schemaVersion, revision, profileId);
            byte[] head = Encoding.ASCII.GetBytes(header);
            var all = new byte[head.Length + payload.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(payload, 0, all, head.Length, payload.Length);
            return all;
        }

        public static Decoded Decode(byte[] bytes)
        {
            var d = new Decoded();
            if (bytes == null || bytes.Length == 0) return Fail(d, ProfileError.Corrupt, "empty file");
            int newline = Array.IndexOf(bytes, (byte)'\n', 0, Math.Min(bytes.Length, MaxHeaderBytes));
            if (newline < 0) return Fail(d, ProfileError.Corrupt, "no save header");
            for (int i = 0; i < newline; i++)
                if (bytes[i] < 0x20 || bytes[i] > 0x7E) return Fail(d, ProfileError.Corrupt, "malformed save header");
            string[] parts = Encoding.ASCII.GetString(bytes, 0, newline).Split(' ');
            if (parts.Length < 2 || parts[0] != Magic) return Fail(d, ProfileError.Corrupt, "not a Night Signal local save");
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int container))
                return Fail(d, ProfileError.Corrupt, "malformed container version");
            if (container > ContainerVersion) return Fail(d, ProfileError.NewerSchema, $"save container version {container} is newer than this game");
            if (container != ContainerVersion) return Fail(d, ProfileError.Corrupt, $"unknown save container version {container}");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 2; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) return Fail(d, ProfileError.Corrupt, "malformed save header field");
                fields[parts[i].Substring(0, eq)] = parts[i].Substring(eq + 1);
            }
            if (!fields.TryGetValue("sha256", out string sha) || sha.Length != 64 ||
                !fields.TryGetValue("bytes", out string lengthText) || !int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out int length) ||
                !fields.TryGetValue("schemaVersion", out string versionText) || !int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out int version) ||
                !fields.TryGetValue("revision", out string revisionText) || !long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out long revision) ||
                !fields.TryGetValue("profile", out string profileId))
                return Fail(d, ProfileError.Corrupt, "incomplete save header");
            int payloadLength = bytes.Length - newline - 1;
            if (payloadLength != length) return Fail(d, ProfileError.Corrupt, $"payload is {payloadLength} bytes, header says {length} (truncated or extended)");
            string actual = Sha256Hex(bytes, newline + 1, payloadLength);
            if (!string.Equals(actual, sha, StringComparison.Ordinal)) return Fail(d, ProfileError.Corrupt, "checksum mismatch");
            d.SchemaVersion = version;
            d.Revision = revision;
            d.ProfileId = profileId;
            d.Sha256 = sha;
            if (version > LocalProfile.CurrentSchemaVersion)
                return Fail(d, ProfileError.NewerSchema, $"profile schema version {version} is newer than this game ({LocalProfile.CurrentSchemaVersion})");
            try
            {
                d.PayloadJson = new UTF8Encoding(false, true).GetString(bytes, newline + 1, payloadLength);
            }
            catch (ArgumentException)
            {
                return Fail(d, ProfileError.Corrupt, "payload is not valid UTF-8");
            }
            return d;
        }

        static Decoded Fail(Decoded d, ProfileError error, string detail)
        {
            d.Error = error;
            d.Detail = detail;
            return d;
        }

        public static string Sha256Hex(byte[] data) => Sha256Hex(data, 0, data.Length);

        static string Sha256Hex(byte[] data, int offset, int count)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(data, offset, count);
                var sb = new StringBuilder(64);
                foreach (byte b in digest) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Safe Local profile persistence (Addendum 01 §8.2): atomic saves (write a temp file, flush, verify, then replace the
    /// main file while keeping the previous one as a backup), a content checksum, schema-version checks with a migration
    /// hook and a pre-migration backup, bounded recovery copies of the last <see cref="RecoveryCopiesKept"/> good saves,
    /// clear load/save error codes, and recovery from the newest good copy. A save is never silently discarded: corrupt
    /// main files are quarantined, a newer game's save is never overwritten, and deleting a profile requires an explicit
    /// confirmation flag. Several Local profiles can coexist (up to <see cref="MaxProfiles"/>).
    /// </summary>
    /// <remarks>
    /// Layout per profile, under <c>profiles/&lt;profileId&gt;/</c>: <c>profile.nsave</c> (main), <c>profile.nsave.tmp</c>
    /// (in-flight write), <c>profile.nsave.bak</c> (previous main), <c>recovery/rev-NNNNNNNNNN.nsave</c> (last good saves),
    /// <c>quarantine/…</c> (corrupt files set aside), <c>premigration/…</c> (copies taken before a schema upgrade).
    /// One writer per storage root is assumed (one game process); stale in-memory copies are refused by revision.
    /// </remarks>
    public sealed class ProfileRepository
    {
        public const int RecoveryCopiesKept = 3;
        public const int MaxProfiles = 8;
        public const string ProfilesPrefix = "profiles/";
        const string MainFile = "profile.nsave";

        readonly IProfileStorage storage;
        readonly ProfileMigrations migrations;
        readonly Func<DateTime> utcNow;

        public ProfileRepository(IProfileStorage storage, ProfileMigrations migrations = null, Func<DateTime> utcNow = null)
        {
            this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
            this.migrations = migrations ?? ProfileMigrations.Default();
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public static string Folder(string profileId) => ProfilesPrefix + profileId + "/";
        public static string MainName(string profileId) => Folder(profileId) + MainFile;
        public static string TempName(string profileId) => Folder(profileId) + MainFile + ".tmp";
        public static string BackupName(string profileId) => Folder(profileId) + MainFile + ".bak";
        public static string RecoveryPrefix(string profileId) => Folder(profileId) + "recovery/";
        public static string RecoveryName(string profileId, long revision) =>
            RecoveryPrefix(profileId) + "rev-" + revision.ToString("D10", CultureInfo.InvariantCulture) + ".nsave";
        public static string QuarantinePrefix(string profileId) => Folder(profileId) + "quarantine/";

        // ---------------- load ----------------

        sealed class Candidate
        {
            public string Name;
            public ProfileCopyKind Kind;
            public ProfileError Error;
            public string Detail;
            public LocalProfile Profile;
            public long Revision;
            public bool Migrated;
            public int FromVersion;
            public byte[] Raw;
        }

        public ProfileLoadResult Load(string profileId)
        {
            var result = new ProfileLoadResult();
            if (!LocalProfile.IsValidId(profileId))
            {
                result.Status = ProfileLoadStatus.Failed;
                result.Error = ProfileError.Missing;
                result.Message = "No such local profile.";
                return result;
            }

            Candidate main = Examine(profileId, MainName(profileId), ProfileCopyKind.Main);
            if (main.Error == ProfileError.NewerSchema)
            {
                result.Status = ProfileLoadStatus.Failed;
                result.Error = ProfileError.NewerSchema;
                result.Problems.Add(Problem(main));
                result.Message = "This profile was saved by a newer version of Night Signal. Update the game to open it; it has not been changed.";
                return result;
            }
            if (main.Error != ProfileError.None && main.Error != ProfileError.Missing) result.Problems.Add(Problem(main));
            if (main.Error == ProfileError.Corrupt) Quarantine(profileId, main);

            Candidate temp = Examine(profileId, TempName(profileId), ProfileCopyKind.InterruptedSave);
            if (temp.Error != ProfileError.None && temp.Error != ProfileError.Missing) result.Problems.Add(Problem(temp));

            if (main.Error == ProfileError.None)
            {
                // A complete, checksummed newer temp file is a save that was interrupted after writing but before the
                // replace: finish it by loading it (never silently throw a completed save away).
                if (temp.Error == ProfileError.None && temp.Revision > main.Revision)
                    return Choose(result, temp, ProfileError.None,
                        $"An interrupted save (revision {temp.Revision}) was complete and has been recovered.");
                return Choose(result, main, ProfileError.None, "");
            }

            var candidates = new List<Candidate>();
            if (temp.Error == ProfileError.None) candidates.Add(temp);
            Candidate backup = Examine(profileId, BackupName(profileId), ProfileCopyKind.Backup);
            if (backup.Error == ProfileError.None) candidates.Add(backup);
            else if (backup.Error != ProfileError.Missing) result.Problems.Add(Problem(backup));
            IReadOnlyList<string> copies;
            try
            {
                copies = storage.List(RecoveryPrefix(profileId));
            }
            catch (Exception e) when (IsIo(e))
            {
                copies = Array.Empty<string>();
                result.Problems.Add(new ProfileFileProblem { Name = RecoveryPrefix(profileId), Error = ProfileError.IoError, Detail = e.Message });
            }
            foreach (string name in copies.OrderByDescending(n => n, StringComparer.Ordinal))
            {
                Candidate c = Examine(profileId, name, ProfileCopyKind.RecoveryCopy);
                if (c.Error == ProfileError.None) candidates.Add(c);
                else result.Problems.Add(Problem(c));
            }

            Candidate best = candidates.OrderByDescending(c => c.Revision).ThenBy(c => (int)c.Kind).FirstOrDefault();
            if (best != null)
            {
                string why = main.Error == ProfileError.Missing ? "The main save file was missing" :
                    main.Error == ProfileError.IoError ? "The main save file could not be read" : "The main save file was damaged";
                return Choose(result, best, main.Error,
                    $"{why}; recovered revision {best.Revision} from {Describe(best.Kind)}. The damaged file was kept aside, not deleted.");
            }

            result.Status = ProfileLoadStatus.Failed;
            result.Error = main.Error;
            result.Message = main.Error == ProfileError.Missing
                ? "No such local profile."
                : main.Error == ProfileError.IoError
                    ? "The profile could not be read from disk (" + main.Detail + "). Nothing was changed; try again or check disk permissions."
                    : "The profile is damaged and no good recovery copy exists (" + main.Detail + "). The damaged file was kept aside, not deleted.";
            return result;
        }

        ProfileLoadResult Choose(ProfileLoadResult result, Candidate c, ProfileError mainError, string message)
        {
            result.Status = c.Kind == ProfileCopyKind.Main ? ProfileLoadStatus.Loaded : ProfileLoadStatus.Recovered;
            result.Error = mainError;
            result.Profile = c.Profile;
            result.Source = c.Kind;
            result.SourceName = c.Name;
            result.Migrated = c.Migrated;
            result.MigratedFromVersion = c.FromVersion;
            result.Message = message;
            if (c.Migrated)
            {
                string copy = Folder(c.Profile.ProfileId) + "premigration/v" + c.FromVersion.ToString(CultureInfo.InvariantCulture) + "-rev" +
                              c.Revision.ToString(CultureInfo.InvariantCulture) + ".nsave";
                try
                {
                    if (!storage.Exists(copy)) storage.Write(copy, c.Raw);
                }
                catch (Exception e) when (IsIo(e))
                {
                    result.Problems.Add(new ProfileFileProblem { Name = copy, Error = ProfileError.IoError, Detail = "pre-migration copy not written: " + e.Message });
                }
                result.Message = (result.Message.Length > 0 ? result.Message + " " : "") +
                                 $"Upgraded from profile schema version {c.FromVersion}; the original was kept as a backup.";
            }
            return result;
        }

        Candidate Examine(string profileId, string name, ProfileCopyKind kind)
        {
            var c = new Candidate { Name = name, Kind = kind };
            byte[] bytes;
            try
            {
                bytes = storage.Read(name);
            }
            catch (Exception e) when (IsIo(e))
            {
                c.Error = ProfileError.IoError;
                c.Detail = e.Message;
                return c;
            }
            if (bytes == null)
            {
                c.Error = ProfileError.Missing;
                c.Detail = "not found";
                return c;
            }
            c.Raw = bytes;
            ProfileFileCodec.Decoded d = ProfileFileCodec.Decode(bytes);
            c.Revision = d.Revision;
            if (!d.Ok)
            {
                c.Error = d.Error;
                c.Detail = d.Detail;
                return c;
            }
            if (d.ProfileId != profileId)
            {
                c.Error = ProfileError.Corrupt;
                c.Detail = "file belongs to another profile";
                return c;
            }
            try
            {
                JObject doc = ProfileJson.ParseObject(d.PayloadJson);
                int version = (int?)doc["schemaVersion"] ?? -1;
                if (version != d.SchemaVersion) throw new InvalidDataException("header and payload schema versions differ");
                if (version < LocalProfile.CurrentSchemaVersion)
                {
                    if (!migrations.CanMigrate(version)) throw new InvalidDataException($"no migration from profile schema version {version}");
                    doc = migrations.Migrate(doc, version);
                    c.Migrated = true;
                    c.FromVersion = version;
                }
                else if ((string)doc["schema"] != LocalProfile.SchemaId) throw new InvalidDataException("not a local profile document");
                LocalProfile profile = ProfileJson.ToObject<LocalProfile>(doc);
                if (profile == null) throw new InvalidDataException("empty profile");
                if (profile.Revision != d.Revision) throw new InvalidDataException("header and payload revisions differ");
                if (profile.ProfileId != profileId) throw new InvalidDataException("document belongs to another profile");
                IReadOnlyList<string> errors = profile.Validate();
                if (errors.Count > 0) throw new InvalidDataException("failed validation: " + errors[0]);
                c.Profile = profile;
            }
            catch (Exception e) when (e is JsonException || e is InvalidDataException || e is InvalidOperationException ||
                                      e is ArgumentException || e is FormatException || e is OverflowException || e is InvalidCastException)
            {
                c.Error = ProfileError.Corrupt;
                c.Detail = e.Message;
            }
            return c;
        }

        void Quarantine(string profileId, Candidate c)
        {
            if (c.Raw == null) return;
            string name = QuarantinePrefix(profileId) + "main-" + ProfileFileCodec.Sha256Hex(c.Raw).Substring(0, 16) + ".nsave";
            try
            {
                if (!storage.Exists(name)) storage.Write(name, c.Raw);
            }
            catch (Exception e) when (IsIo(e))
            {
                // Recovery continues; the damaged main file stays in place until the next successful save backs it up.
            }
        }

        static ProfileFileProblem Problem(Candidate c) => new ProfileFileProblem { Name = c.Name, Error = c.Error, Detail = c.Detail ?? "" };

        static string Describe(ProfileCopyKind kind)
        {
            switch (kind)
            {
                case ProfileCopyKind.InterruptedSave: return "an interrupted save";
                case ProfileCopyKind.Backup: return "the backup";
                case ProfileCopyKind.RecoveryCopy: return "a recovery copy";
                default: return "the main file";
            }
        }

        // ---------------- save ----------------

        /// <summary>
        /// Atomically saves <paramref name="profile"/> as a new revision. On success the object's <see cref="LocalProfile.Revision"/>
        /// and <see cref="LocalProfile.UpdatedUtc"/> are updated; on any failure nothing on disk that was previously good is lost
        /// and the object is unchanged.
        /// </summary>
        public ProfileSaveResult Save(LocalProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var result = new ProfileSaveResult();
            IReadOnlyList<string> errors = profile.Validate();
            if (errors.Count > 0)
            {
                result.Status = ProfileSaveStatus.Invalid;
                result.Message = "Not saved: " + errors[0];
                return result;
            }
            string id = profile.ProfileId;

            Candidate main = Examine(id, MainName(id), ProfileCopyKind.Main);
            if (main.Error == ProfileError.NewerSchema)
            {
                result.Status = ProfileSaveStatus.NewerSchemaOnDisk;
                result.Message = "Not saved: a newer version of Night Signal wrote this profile; this version will not overwrite it.";
                return result;
            }
            if (main.Error == ProfileError.IoError)
            {
                result.Status = ProfileSaveStatus.IoError;
                result.Message = "Not saved: the existing profile could not be read (" + main.Detail + ").";
                return result;
            }
            if (main.Error == ProfileError.Corrupt) Quarantine(id, main); // keep the damaged file aside before it is rotated out
            Candidate temp = Examine(id, TempName(id), ProfileCopyKind.InterruptedSave);
            long onDisk = Math.Max(main.Error == ProfileError.None ? main.Revision : 0, temp.Error == ProfileError.None ? temp.Revision : 0);
            if (profile.Revision < onDisk)
            {
                result.Status = ProfileSaveStatus.Conflict;
                result.Message = $"Not saved: this copy (revision {profile.Revision}) is older than the saved profile (revision {onDisk}). Reload it first.";
                return result;
            }

            long revision = Math.Max(profile.Revision, onDisk) + 1;
            DateTime now = utcNow();
            LocalProfile copy = ProfileJson.Clone(profile);
            copy.Revision = revision;
            copy.UpdatedUtc = now;
            byte[] bytes = ProfileFileCodec.Encode(copy);

            try
            {
                storage.Write(TempName(id), bytes);
                byte[] check = storage.Read(TempName(id));
                if (check == null || !ProfileFileCodec.Decode(check).Ok || ProfileFileCodec.Sha256Hex(check) != ProfileFileCodec.Sha256Hex(bytes))
                    throw new IOException("the written save did not read back identically");
                storage.Replace(TempName(id), MainName(id), BackupName(id));
            }
            catch (Exception e) when (IsIo(e))
            {
                result.Status = ProfileSaveStatus.IoError;
                result.Message = "Not saved: " + e.Message + " Your previous save is intact.";
                return result;
            }

            try
            {
                storage.Write(RecoveryName(id, revision), bytes);
                PruneRecovery(id);
            }
            catch (Exception e) when (IsIo(e))
            {
                result.Warnings.Add("Saved, but the recovery copy could not be written: " + e.Message);
            }

            profile.Revision = revision;
            profile.UpdatedUtc = now;
            result.Status = ProfileSaveStatus.Saved;
            result.Revision = revision;
            result.Message = "Saved.";
            return result;
        }

        void PruneRecovery(string profileId)
        {
            List<string> copies = storage.List(RecoveryPrefix(profileId)).OrderByDescending(n => n, StringComparer.Ordinal).ToList();
            foreach (string old in copies.Skip(RecoveryCopiesKept))
                storage.Delete(old);
        }

        // ---------------- multiple profiles ----------------

        /// <summary>Distinct profile ids with any file on disk (including damaged ones, so they are never hidden).</summary>
        public IReadOnlyList<string> ProfileIds()
        {
            return storage.List(ProfilesPrefix)
                .Select(n => n.Substring(ProfilesPrefix.Length))
                .Where(n => n.IndexOf('/') > 0)
                .Select(n => n.Substring(0, n.IndexOf('/')))
                .Where(LocalProfile.IsValidId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        public IReadOnlyList<ProfileSummary> List()
        {
            var list = new List<ProfileSummary>();
            foreach (string id in ProfileIds())
            {
                ProfileLoadResult r = Load(id);
                var s = new ProfileSummary { ProfileId = id, Status = r.Status, Error = r.Error, Message = r.Message };
                if (r.Profile != null)
                {
                    s.DisplayName = r.Profile.DisplayName;
                    s.UpdatedUtc = r.Profile.UpdatedUtc;
                    s.Revision = r.Profile.Revision;
                    s.RankPoints = r.Profile.ComputeRankPoints();
                    s.Rank = r.Profile.ComputeRank().Name;
                    s.WalletBalance = r.Profile.WalletBalance;
                }
                list.Add(s);
            }
            return list.OrderByDescending(s => s.UpdatedUtc).ThenBy(s => s.ProfileId, StringComparer.Ordinal).ToList();
        }

        /// <summary>Saves a brand-new profile (from <see cref="LocalProgression.NewProfile"/>). Refuses an existing id.</summary>
        public ProfileSaveResult Create(LocalProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var result = new ProfileSaveResult();
            try
            {
                IReadOnlyList<string> ids = ProfileIds();
                if (ids.Contains(profile.ProfileId))
                {
                    result.Status = ProfileSaveStatus.AlreadyExists;
                    result.Message = "A local profile with this id already exists.";
                    return result;
                }
                if (ids.Count >= MaxProfiles)
                {
                    result.Status = ProfileSaveStatus.LimitReached;
                    result.Message = $"This PC already has {MaxProfiles} local profiles. Delete one to create another.";
                    return result;
                }
            }
            catch (Exception e) when (IsIo(e))
            {
                result.Status = ProfileSaveStatus.IoError;
                result.Message = "Could not list local profiles: " + e.Message;
                return result;
            }
            return Save(profile);
        }

        /// <summary>Deletes every file of a profile. Requires <paramref name="confirmed"/> = true (an explicit player confirmation).</summary>
        public ProfileDeleteResult Delete(string profileId, bool confirmed)
        {
            var result = new ProfileDeleteResult();
            if (!LocalProfile.IsValidId(profileId))
            {
                result.Status = ProfileDeleteStatus.NotFound;
                result.Message = "No such local profile.";
                return result;
            }
            if (!confirmed)
            {
                result.Status = ProfileDeleteStatus.ConfirmationRequired;
                result.Message = "Deleting a local profile permanently removes its progress on this PC. Confirm to continue.";
                return result;
            }
            try
            {
                IReadOnlyList<string> names = storage.List(Folder(profileId));
                if (names.Count == 0)
                {
                    result.Status = ProfileDeleteStatus.NotFound;
                    result.Message = "No such local profile.";
                    return result;
                }
                // Main file first, so an interrupted delete never leaves a profile that looks healthy but lost its history.
                foreach (string name in names.OrderBy(n => n == MainName(profileId) ? 0 : 1))
                {
                    storage.Delete(name);
                    result.FilesRemoved++;
                }
            }
            catch (Exception e) when (IsIo(e))
            {
                result.Status = ProfileDeleteStatus.IoError;
                result.Message = "The profile could not be fully deleted: " + e.Message;
                return result;
            }
            result.Status = ProfileDeleteStatus.Deleted;
            result.Message = "Local profile deleted.";
            return result;
        }

        static bool IsIo(Exception e) => e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException;
    }
}
