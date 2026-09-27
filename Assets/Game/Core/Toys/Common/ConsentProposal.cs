using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NightSignal.Core.Toys
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProposalState { Open = 0, Accepted = 1, Declined = 2, Lapsed = 3, Withdrawn = 4 }

    /// <summary>
    /// A shared-state change that needs the consent of the toy's CURRENT active users (Addendum 02 §2.2 arrangement switch,
    /// §3.2 destructive project reset, §5.3 layout change, §6.2 whole-board clear). The proposer approves implicitly.
    /// Silence is never consent: an open proposal LAPSES at its deadline and nothing is applied. Required voters who stop
    /// being active members (disconnect, leave) are dropped from the requirement because they are no longer current users;
    /// the protected content keeps a recoverable checkpoint instead.
    /// </summary>
    public sealed class ConsentProposal
    {
        public string Id;
        public string Kind;
        public string Argument;
        public string Proposer;
        public long CreatedAtMs;
        public long ExpiresAtMs;
        public ProposalState State;
        public List<string> Required = new List<string>();
        public List<string> Approved = new List<string>();
        public List<string> Declined = new List<string>();

        public static ConsentProposal Open(string id, string kind, string argument, string proposer, IEnumerable<string> activeUsers, long nowMs)
        {
            var p = new ConsentProposal
            {
                Id = id, Kind = kind, Argument = argument, Proposer = proposer, CreatedAtMs = nowMs,
                ExpiresAtMs = nowMs + ToyLimits.ProposalLifetimeMs, State = ProposalState.Open,
            };
            foreach (string m in activeUsers)
                if (m != proposer && !p.Required.Contains(m)) p.Required.Add(m);
            p.Approved.Add(proposer);
            p.Evaluate();
            return p;
        }

        /// <summary>Records a vote from a required voter. Returns false when the voter is not asked or the proposal is closed.</summary>
        public bool Vote(string member, bool accept)
        {
            if (State != ProposalState.Open || !Required.Contains(member)) return false;
            if (accept) { if (!Approved.Contains(member)) Approved.Add(member); }
            else if (!Declined.Contains(member)) Declined.Add(member);
            Evaluate();
            return true;
        }

        /// <summary>Drops voters that are no longer current active users (not the proposer) and re-evaluates.</summary>
        public void Retain(System.Func<string, bool> stillActive)
        {
            if (State != ProposalState.Open) return;
            Required.RemoveAll(m => !stillActive(m));
            Evaluate();
        }

        public void Tick(long nowMs)
        {
            if (State == ProposalState.Open && nowMs >= ExpiresAtMs) State = ProposalState.Lapsed;
        }

        void Evaluate()
        {
            if (State != ProposalState.Open) return;
            if (Declined.Count > 0) { State = ProposalState.Declined; return; }
            foreach (string m in Required)
                if (!Approved.Contains(m)) return;
            State = ProposalState.Accepted;
        }
    }
}
