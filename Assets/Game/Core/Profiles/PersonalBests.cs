using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;

namespace NightSignal.Core.Profiles
{
    /// <summary>Per-key attempt bookkeeping so the UI can tell "not attempted" from "did not finish" from "no result".</summary>
    public sealed class RecordAttempt
    {
        public RecordKey Key { get; set; }
        public int Attempts { get; set; }
        public int Finishes { get; set; }
        public int ValidResults { get; set; }
        public RunOutcome LastOutcome { get; set; }
        public DateTime LastAttemptUtc { get; set; }
    }

    /// <summary>Display state, distinct from any value (Addendum 01 §4.3).</summary>
    public enum RecordDisplayState
    {
        /// <summary>A rankable best exists (show the value).</summary>
        HasValue = 0,
        /// <summary>Attempted and finished, but no eligible value for this metric.</summary>
        NoResult = 1,
        NotAttempted = 2,
        /// <summary>Attempted, never finished.</summary>
        DidNotFinish = 3,
        /// <summary>Only an Online result awaiting server settlement.</summary>
        PendingVerification = 4,
        /// <summary>Only results recorded under other rules (archived or incompatible versions).</summary>
        LegacyIncompatible = 5,
    }

    public enum RecordUpdateOutcome
    {
        NewPersonalBest = 0,
        /// <summary>Not an overall PB, but the best for this car model (same-car filter).</summary>
        NewCarBest = 1,
        /// <summary>Equals the personal best at the stored precision; the earlier record is kept.</summary>
        Tie = 2,
        NotImproved = 3,
        Rejected = 4,
    }

    public sealed class RecordUpdateResult
    {
        public RecordUpdateOutcome Outcome;
        public RecordKey Key;
        public long Value;
        /// <summary>Overall best before this offer (null when there was none).</summary>
        public RecordEntry Previous;
        public bool CarBestImproved;
        public string Reason = "";
        public bool IsNewPersonalBest => Outcome == RecordUpdateOutcome.NewPersonalBest;
    }

    /// <summary>What a record panel shows for one key: state, value text, verification label and any legacy results.</summary>
    public sealed class RecordView
    {
        public RecordKey Key;
        public RecordDisplayState State;
        public RecordEntry Best;
        public RecordEntry Pending;
        public IReadOnlyList<RecordEntry> Legacy = Array.Empty<RecordEntry>();
        public int Attempts;
        /// <summary>Formatted value or the metric's "no value" placeholder (e.g. --:--.---, Score: N/A, Best: — / Target: 5).</summary>
        public string ValueText = "";
        /// <summary>"" when a value exists; otherwise "No result", "Not attempted", "Did not finish", …</summary>
        public string StateText = "";
        public string VerificationText = "";
    }

    /// <summary>
    /// A domain's personal records. Rules: a value improves a best only when strictly better under the metric's direction
    /// (ties keep the earlier record); values are kept per car model so an optional same-car filter works; a legal losing
    /// run can set a PB (placing and stage clears are independent of records); records stay after clearing; entries whose
    /// rules changed are ARCHIVED as legacy, never deleted.
    /// </summary>
    public sealed class PersonalBests
    {
        public ProgressionDomain Domain { get; set; } = ProgressionDomain.Local;
        /// <summary>At most one entry per (key, car model).</summary>
        public List<RecordEntry> Entries { get; set; } = new List<RecordEntry>();
        /// <summary>Legacy entries; only ever appended to.</summary>
        public List<RecordEntry> Archived { get; set; } = new List<RecordEntry>();
        /// <summary>Online client caches only: server results not yet settled (one per key and car model).</summary>
        public List<RecordEntry> Pending { get; set; } = new List<RecordEntry>();
        public List<RecordAttempt> Attempts { get; set; } = new List<RecordAttempt>();

        /// <summary>Offers a new value. The caller's entry is copied, never retained.</summary>
        public RecordUpdateResult Offer(RecordEntry candidate)
        {
            var result = new RecordUpdateResult { Outcome = RecordUpdateOutcome.Rejected, Key = candidate?.Key, Value = candidate?.Value ?? 0 };
            string problem = Problem(candidate);
            if (problem != null)
            {
                result.Reason = problem;
                return result;
            }

            RecordEntry overall = Best(candidate.Key);
            result.Previous = overall?.Clone();
            int carIndex = Entries.FindIndex(e => candidate.Key.Equals(e.Key) && e.CarModelId == candidate.CarModelId && e.Verification == candidate.Verification);
            if (carIndex < 0)
            {
                Entries.Add(candidate.Clone());
                result.CarBestImproved = true;
            }
            else if (Metrics.IsBetter(candidate.Key.Metric, candidate.Value, Entries[carIndex].Value))
            {
                Entries[carIndex] = candidate.Clone();
                result.CarBestImproved = true;
            }

            if (overall == null)
            {
                result.Outcome = RecordUpdateOutcome.NewPersonalBest;
                result.Reason = "First recorded result.";
            }
            else
            {
                int c = Metrics.Compare(candidate.Key.Metric, candidate.Value, overall.Value);
                if (c > 0)
                {
                    result.Outcome = RecordUpdateOutcome.NewPersonalBest;
                    result.Reason = "Better than the previous personal best.";
                }
                else if (c == 0)
                {
                    result.Outcome = RecordUpdateOutcome.Tie;
                    result.Reason = "Equals the personal best; ties keep the earlier record.";
                }
                else
                {
                    result.Outcome = result.CarBestImproved ? RecordUpdateOutcome.NewCarBest : RecordUpdateOutcome.NotImproved;
                    result.Reason = result.CarBestImproved ? "Best with this car." : "Not better than the personal best.";
                }
            }
            return result;
        }

        string Problem(RecordEntry c)
        {
            if (c == null) return "No record.";
            if (c.Key == null) return "A record needs a key.";
            IReadOnlyList<string> keyErrors = c.Key.Validate();
            if (keyErrors.Count > 0) return keyErrors[0];
            if (c.Key.Domain != Domain) return RecordCompatibility.Explain(RecordMismatch.Domain);
            if (!RecordComparer.AllowedIn(Domain, c.Verification) || c.Verification == RecordVerification.Legacy ||
                c.Verification == RecordVerification.PendingVerification)
                return $"A {c.Verification} value cannot be stored as a {DomainNotices.Badge(Domain)} personal best.";
            if (!Metrics.Get(c.Key.Metric).IsValidValue(c.Value)) return "Not a valid value for this metric.";
            if (string.IsNullOrEmpty(c.CarModelId)) return "A record needs the car model it was set with.";
            return null;
        }

        /// <summary>The overall best for <paramref name="key"/> (optionally for one car model), or null.</summary>
        public RecordEntry Best(RecordKey key, RecordFilter filter = null)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            RecordEntry best = null;
            foreach (RecordEntry e in Entries)
            {
                if (!key.Equals(e.Key) || !RecordComparer.Rankable(e.Verification, e.Verification)) continue;
                if (filter != null && !filter.Matches(e)) continue;
                // Strictly better replaces; a tie keeps the earlier achievement.
                if (best == null || Metrics.IsBetter(key.Metric, e.Value, best.Value) ||
                    (e.Value == best.Value && e.Provenance != null && best.Provenance != null && e.Provenance.AchievedUtc < best.Provenance.AchievedUtc))
                    best = e;
            }
            return best;
        }

        /// <summary>Records an attempt for the display states (call once per event per key, before or after offering).</summary>
        public void RegisterAttempt(RecordKey key, RunOutcome outcome, bool validResult, DateTime utc)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            RecordAttempt a = Attempts.FirstOrDefault(x => key.Equals(x.Key));
            if (a == null)
            {
                a = new RecordAttempt { Key = key.Clone() };
                Attempts.Add(a);
            }
            a.Attempts++;
            if (outcome == RunOutcome.Finished) a.Finishes++;
            if (validResult) a.ValidResults++;
            a.LastOutcome = outcome;
            a.LastAttemptUtc = utc;
        }

        /// <summary>Online client caches: shows an unsettled result without ranking it.</summary>
        public void SetPending(RecordEntry pending)
        {
            if (pending?.Key == null) throw new ArgumentNullException(nameof(pending));
            if (Domain != ProgressionDomain.Online || pending.Key.Domain != ProgressionDomain.Online)
                throw new InvalidOperationException("Only Online results can be pending verification; Local results are never verified.");
            RecordEntry copy = pending.Clone();
            copy.Verification = RecordVerification.PendingVerification;
            Pending.RemoveAll(p => copy.Key.Equals(p.Key) && p.CarModelId == copy.CarModelId);
            Pending.Add(copy);
        }

        public void ClearPending(RecordKey key) => Pending.RemoveAll(p => key.Equals(p.Key));

        /// <summary>
        /// Moves matching entries to <see cref="Archived"/> as <see cref="RecordVerification.Legacy"/> (for example after a
        /// physics or contact-rule change). Nothing is deleted. Returns the number archived.
        /// </summary>
        public int Archive(Func<RecordEntry, bool> predicate, string reason, DateTime utc)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            List<RecordEntry> moving = Entries.Where(predicate).ToList();
            foreach (RecordEntry e in moving)
            {
                Entries.Remove(e);
                e.OriginalVerification = e.Verification;
                e.Verification = RecordVerification.Legacy;
                e.ArchivedReason = string.IsNullOrEmpty(reason) ? "Rules changed" : reason;
                e.ArchivedUtc = utc;
                Archived.Add(e);
            }
            return moving.Count;
        }

        /// <summary>
        /// Results for the same event/metric recorded under other rules: archived entries plus current entries whose full
        /// key differs (versions, contact policy, conditions, cap, assists, composition). Shown as legacy/incompatible.
        /// </summary>
        public IReadOnlyList<RecordEntry> LegacyFor(RecordKey key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            string identity = key.Identity;
            return Archived.Where(e => e.Key != null && e.Key.Identity == identity)
                .Concat(Entries.Where(e => e.Key != null && e.Key.Identity == identity && !key.Equals(e.Key)))
                .ToList();
        }

        public RecordView View(RecordKey key, RecordFilter filter = null, long? target = null)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            var view = new RecordView
            {
                Key = key,
                Best = Best(key, filter),
                Pending = Pending.Where(p => key.Equals(p.Key) && (filter == null || filter.Matches(p)))
                    .OrderByDescending(p => p.Provenance?.AchievedUtc ?? DateTime.MinValue).FirstOrDefault(),
                Legacy = LegacyFor(key),
            };
            RecordAttempt attempt = Attempts.FirstOrDefault(a => key.Equals(a.Key));
            view.Attempts = attempt?.Attempts ?? 0;

            if (view.Best != null) view.State = RecordDisplayState.HasValue;
            else if (view.Pending != null) view.State = RecordDisplayState.PendingVerification;
            else if (view.Attempts > 0) view.State = attempt.Finishes > 0 ? RecordDisplayState.NoResult : RecordDisplayState.DidNotFinish;
            else if (view.Legacy.Count > 0) view.State = RecordDisplayState.LegacyIncompatible;
            else view.State = RecordDisplayState.NotAttempted;

            RecordEntry shown = view.Best ?? view.Pending;
            view.ValueText = RecordFormat.Value(key.Metric, shown?.Value, target);
            view.StateText = RecordFormat.State(view.State);
            view.VerificationText = shown != null ? RecordFormat.Verification(shown.Verification) : DomainNotices.Badge(key.Domain);
            return view;
        }
    }
}
