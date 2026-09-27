using System;
using System.Collections.Generic;
using System.IO;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Profiles;
using UnityEngine;

namespace NightSignal.Front
{
    /// <summary>
    /// The Local (offline) domain on this PC (Addendum 01 §8): an atomic, file-backed profile repository under the
    /// player's data folder, the active profile, and the catalogue + soundtrack table that progression is judged by.
    /// Nothing here talks to the network, and nothing here is ever shown or uploaded as Online progress.
    /// </summary>
    public sealed class LocalSession
    {
        public ProfileRepository Repository { get; }
        public ContentCatalogue Catalogue { get; }
        public MusicUnlockTable Music { get; }
        public LocalProfile Profile { get; private set; }
        public string StorageFolder { get; }

        LocalSession(string folder, ContentCatalogue catalogue)
        {
            StorageFolder = folder;
            Repository = new ProfileRepository(new FileSystemProfileStorage(folder));
            Catalogue = catalogue;
            Music = MusicUnlockTable.FromCatalogue(catalogue);
        }

        static LocalSession instance;
        static string folderOverride;

        /// <summary>Points the Local session at another folder (tests and automated tours never touch real saves).</summary>
        public static void UseFolder(string folder)
        {
            folderOverride = folder;
            instance = null;
        }

        /// <summary>The PC's Local session (created on first use). Null only if the content library is missing.</summary>
        public static LocalSession Current
        {
            get
            {
                if (instance != null) return instance;
                ContentCatalogue cat = ContentLibrary.Load()?.Catalogue;
                if (cat == null) return null;
                // -nsLocalProfiles <dir> isolates automated runs (UI tours, tests) from a player's real saves.
                string folder = folderOverride ?? Arg("-nsLocalProfiles") ?? Path.Combine(Application.persistentDataPath, "LocalProfiles");
                instance = new LocalSession(Path.GetFullPath(folder), cat);
                return instance;
            }
        }

        static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public IReadOnlyList<ProfileSummary> ListProfiles() => Repository.List();

        public bool Open(string profileId, out string message)
        {
            ProfileLoadResult r = Repository.Load(profileId);
            message = r.Message;
            if (!r.Ok || r.Profile == null) return false;
            Profile = r.Profile;
            // Baseline cues added by a newer build are granted on open (idempotent).
            LocalProgressionResult sync = LocalProgression.SyncBaselineMusic(Profile, Music, DateTime.UtcNow);
            if (sync.Changed) Commit(sync, out _);
            return true;
        }

        public bool Create(string displayName, string starterCarId, out string message)
        {
            LocalProgressionResult r = LocalProgression.NewProfile(Catalogue, Music,
                new NewLocalProfileRequest { DisplayName = displayName, StarterCarModelId = starterCarId, Utc = DateTime.UtcNow });
            if (!r.Changed)
            {
                message = r.Reason;
                return false;
            }
            ProfileSaveResult s = Repository.Create(r.Profile);
            message = s.Message;
            if (!s.Ok) return false;
            Profile = r.Profile;
            return true;
        }

        /// <summary>Saves an applied progression result atomically; on failure the in-memory profile stays as it was.</summary>
        public bool Commit(LocalProgressionResult result, out string message)
        {
            if (result == null || !result.Changed || result.Profile == null)
            {
                message = result?.Reason ?? "Nothing to save.";
                return false;
            }
            ProfileSaveResult s = Repository.Save(result.Profile);
            message = s.Ok ? (s.Warnings.Count > 0 ? s.Warnings[0] : "Saved.") : s.Message;
            if (!s.Ok) return false;
            Profile = result.Profile;
            return true;
        }

        public void Close() => Profile = null;

        /// <summary>
        /// PI estimate of the car instance's APPLIED (race) build — what cap checks and class ceilings use; the model's base PI
        /// when the garage data is unavailable (a stock build estimates to exactly its base PI).
        /// </summary>
        public int AppliedPi(OwnedCar car)
        {
            CarDef model = Catalogue.Car(car.ModelId);
            Core.Builds.PartsCatalogue parts = ContentLibrary.Load()?.Parts;
            if (parts == null || Profile == null) return model.BasePI;
            LocalWorkspaceLoad load = LocalGarage.LoadWorkspace(Profile, Catalogue, parts, car.InstanceId, DateTime.UtcNow);
            if (!load.Ok) return model.BasePI;
            Core.Builds.BuildEvaluation ev = Core.Builds.BuildEvaluator.Evaluate(load.Workspace.Applied.Build, car.InstanceId,
                LocalGarage.Context(Profile, Catalogue, parts, car.InstanceId));
            return ev.Resolved ? ev.Pi.Value : model.BasePI;
        }

        /// <summary>The frozen applied build of an owned car and its resolved physics, or nulls for a loaner/stock fallback.</summary>
        public Core.Builds.ResolvedCarSpec RaceSpec(string instanceId, out Core.Builds.AppliedVehicleBuild frozen, out string problem)
        {
            frozen = null;
            problem = null;
            Core.Builds.PartsCatalogue parts = ContentLibrary.Load()?.Parts;
            if (string.IsNullOrEmpty(instanceId) || parts == null || Profile == null) return null;
            frozen = LocalGarage.FrozenRaceBuild(Profile, Catalogue, parts, instanceId, DateTime.UtcNow);
            OwnedCar car = Profile.FindCar(instanceId);
            if (frozen == null || car == null) { problem = "The car's garage could not be read; racing it stock."; frozen = null; return null; }
            CarDef model = Catalogue.Car(car.ModelId);
            Core.Builds.ResolveResult r = Core.Builds.BuildResolver.Resolve(model, Catalogue.CarTunings[model.Id], parts, frozen.Build);
            if (!r.Ok) { problem = "The applied build could not be resolved; racing it stock."; frozen = null; return null; }
            return r.Spec;
        }

        /// <summary>The saved While We Wait table for this profile (non-progression domain), or null.</summary>
        public string ToySnapshot(string key) => Profile?.Toys.Get(key)?.Data?.ToString(Newtonsoft.Json.Formatting.None);

        /// <summary>
        /// Stores the toy session snapshot in the profile's NON-PROGRESSION toy workspace (Addendum 02 D209) and saves the
        /// profile atomically. Toy state never touches money, rank, records or unlocks.
        /// </summary>
        public bool SaveToys(string key, string schema, string snapshotJson, out string message)
        {
            if (Profile == null) { message = "No Local profile is open."; return false; }
            LocalProfile copy = ProfileJson.Clone(Profile);
            copy.Toys.Put(key, new VersionedDocument
            {
                Schema = schema, SchemaVersion = 1, UpdatedUtc = DateTime.UtcNow, Data = Newtonsoft.Json.Linq.JToken.Parse(snapshotJson),
            });
            ProfileSaveResult s = Repository.Save(copy);
            message = s.Message;
            if (!s.Ok) return false;
            Profile = copy;
            return true;
        }
    }
}
