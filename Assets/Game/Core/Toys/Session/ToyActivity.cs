using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NightSignal.Core.Toys
{
    /// <summary>Why a member's active control of a toy ended. None of these is a failure or a penalty.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ReleaseCause
    {
        /// <summary>The member closed the toy panel / walked away from the prop.</summary>
        Closed = 0,
        /// <summary>The member started interacting with a different toy (one active control at a time).</summary>
        Switched = 1,
        /// <summary>No real interaction within the control lease.</summary>
        Idle = 2,
        /// <summary>Network loss: the seat is dormant.</summary>
        Disconnected = 3,
        /// <summary>Explicit Leave/Forget, kick, joining another convoy or revocation: the seat is retired.</summary>
        Departed = 4,
        /// <summary>Focus loss / minimised client: held inputs are cancelled.</summary>
        FocusLost = 5,
    }

    /// <summary>Per-toy run state that is part of every toy snapshot.</summary>
    public sealed class ToyRunState
    {
        /// <summary>Activity epoch; commands must carry it. Bumped by every pause and by layout/arrangement/sheet resets.</summary>
        public int Epoch = 1;
        /// <summary>Frozen at a pause boundary; moving toys stay frozen until an actual participant resumes them.</summary>
        public bool Frozen;
        /// <summary>After an explicit resume, no moving/timing input applies before this session time.</summary>
        public long OrientUntilMs;
        /// <summary>Pause generation this toy last froze in (diagnostics; late-command checks use <see cref="Epoch"/>).</summary>
        public int FrozenInPause;
    }

    /// <summary>What a toy needs from the session that hosts it.</summary>
    public interface IToyHost
    {
        string SessionId { get; }
        long NowMs { get; }
        /// <summary>Active members who really interacted with this toy recently (consent voters, "who is playing").</summary>
        List<string> ActiveUsers(ToyActivityId activity);
        bool IsActiveMember(string member);
        /// <summary>Server-owned random stream (persisted with the session).</summary>
        ulong NextSeed();
        /// <summary>Session-unique id with a readable prefix, e.g. "prop-12".</summary>
        string NextId(string prefix);
    }

    /// <summary>Everything a toy sees while applying one validated command.</summary>
    public sealed class ToyCommandContext
    {
        public ToyCommand Command;
        public string Member;
        public long Generation;
        public long NowMs;
        public PayloadReader P;
        public IToyHost Host;
        public string Kind => Command.Kind;
        public string RequestId => Command.RequestId;
    }

    /// <summary>
    /// One diversion hosted by <see cref="DowntimeSession"/>. Implementations are single-threaded: the host serializes all
    /// calls (one lock per convoy session), which is what makes command order authoritative.
    /// </summary>
    public interface IToyActivity
    {
        ToyActivityId Id { get; }
        ToyRunState Run { get; }
        /// <summary>Moving toys (Cap Clash, Pocket Circuit) stay frozen after a pause until a participant resumes them.</summary>
        bool RequiresExplicitResume { get; }
        /// <summary>True while something is moving and the toy must be stepped.</summary>
        bool NeedsSimulation { get; }
        /// <summary>Transient kinds (held throttle, drafts) are not journaled; their effect is captured by snapshots.</summary>
        bool IsTransient(string kind);
        /// <summary>Kinds that count as actively controlling the toy (switching the member's single active control).</summary>
        bool TakesControl(string kind);

        ToyResult Apply(ToyCommandContext ctx);
        void Advance(long nowMs);
        /// <summary>Pause boundary: freeze without waiting for anything (bodies keep position + velocity, strokes finalize ...).</summary>
        void Freeze(long nowMs);
        /// <summary>The main event is over. Non-moving toys accept input again; moving toys wait for <see cref="Resume"/>.</summary>
        void EndPause(long nowMs);
        void Resume(string member, long nowMs);
        void ReleaseControl(string member, ReleaseCause cause, long nowMs);
        /// <summary>Explicit departure: retire the member's moving piece and queued actions; keep incorporated work.</summary>
        void Retire(string member, long nowMs);
        /// <summary>The toy's serializable state (snapshot payload).</summary>
        object StateObject { get; }
    }

    /// <summary>"Who is playing what" for the compact While We Wait selector (Addendum 02 §1.1).</summary>
    public sealed class ToyOverview
    {
        public ToyActivityId Activity;
        public List<string> Players = new List<string>();
        public bool Paused;
        public int Epoch;
        /// <summary>Short neutral status, e.g. "Workbench Bullseye" or "Night-Shift Coupe 12/26".</summary>
        public string Status;
    }
}
