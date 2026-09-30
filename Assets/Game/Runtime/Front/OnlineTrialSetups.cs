using System.Collections.Generic;
using System.IO;
using NightSignal.Core.Builds;
using Newtonsoft.Json;
using UnityEngine;

namespace NightSignal.Front
{
    /// <summary>
    /// An online account's own setups of tunable challenge trial loaners (CH56, CH57), kept on this PC beside the Local
    /// profiles (so <c>-nsLocalProfiles</c> isolates automated runs) and sent with Event Ready. Nothing here is trusted: the
    /// control plane resolves every part and setting before readiness is accepted, and the game server again at the finish.
    /// </summary>
    public static class OnlineTrialSetups
    {
        static string FileFor(string account) =>
            Path.Combine(LocalSession.DataFolder, "online-trial-setups", string.Concat((account ?? "").Split(Path.GetInvalidFileNameChars())) + ".json");

        static Dictionary<string, MechanicalSnapshot> Load(string account)
        {
            try
            {
                string f = FileFor(account);
                return File.Exists(f) ? JsonConvert.DeserializeObject<Dictionary<string, MechanicalSnapshot>>(File.ReadAllText(f)) ?? new Dictionary<string, MechanicalSnapshot>()
                    : new Dictionary<string, MechanicalSnapshot>();
            }
            catch (System.Exception e) when (e is IOException || e is JsonException)
            {
                Debug.LogWarning($"[NightSignal.Online] trial setups unreadable: {e.Message}");
                return new Dictionary<string, MechanicalSnapshot>();
            }
        }

        /// <summary>The account's saved setup of a trial's loaner (null: race it as supplied).</summary>
        public static MechanicalSnapshot Get(string account, string trialId) =>
            string.IsNullOrEmpty(account) || string.IsNullOrEmpty(trialId) ? null : Load(account).TryGetValue(trialId, out MechanicalSnapshot s) ? s : null;

        /// <summary>Keeps the account's setup of a trial's loaner; false (with the reason) when it cannot be written.</summary>
        public static bool Save(string account, string trialId, MechanicalSnapshot setup, out string problem)
        {
            problem = null;
            try
            {
                Dictionary<string, MechanicalSnapshot> all = Load(account);
                all[trialId] = setup;
                string f = FileFor(account);
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                File.WriteAllText(f, JsonConvert.SerializeObject(all, Formatting.Indented));
                return true;
            }
            catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException)
            {
                problem = e.Message;
                return false;
            }
        }
    }
}
