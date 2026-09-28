using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Core.Rules;
using Newtonsoft.Json.Linq;

namespace NightSignal.Core.Story
{
    /// <summary>One spoken or narrated line: a rival id (Rxx), "radio", "timing-crew" or "narration".</summary>
    public sealed class StoryLine
    {
        public string Speaker = "";
        public string Line = "";
    }

    /// <summary>One campaign side (Normal or Hard) of a stage's authored story.</summary>
    public sealed class StageStorySide
    {
        public string Title = "";
        public List<StoryLine> Intro = new List<StoryLine>();
        public List<StoryLine> Win = new List<StoryLine>();
        public List<StoryLine> Loss = new List<StoryLine>();
        public List<StoryLine> ClearedButLost = new List<StoryLine>();
        /// <summary>The race-diary entry that keeps the full story after a shortened rematch intro.</summary>
        public string Diary = "";
    }

    public sealed class StageStory
    {
        public string Id = "";
        public StageStorySide Normal = new StageStorySide(), Hard = new StageStorySide();
        public StageStorySide Side(CampaignMode mode) => mode == CampaignMode.Hard ? Hard : Normal;
    }

    public sealed class ActStory
    {
        public int Act;
        public string Title = "", Summary = "";
    }

    /// <summary>A crew introduction for the race diary, readable once its stage has been cleared on Normal.</summary>
    public sealed class CrewIntroduction
    {
        public string Crew = "", Title = "", UnlockAfterStage = "", Text = "";
    }

    /// <summary>A radio or timing-slip record collected through Normal progression (Appendix E CH74).</summary>
    public sealed class StoryRecord
    {
        public string Id = "", Title = "", AwardedAfterStage = "", Kind = "", Text = "";
    }

    /// <summary>One scene of an ending: where it happens, and its lines.</summary>
    public sealed class StoryScene
    {
        public string Setting = "";
        public List<StoryLine> Lines = new List<StoryLine>();
    }

    /// <summary>How a stage ended, read from the convoy's side (stages.story.json conventions).</summary>
    public enum StoryOutcome
    {
        /// <summary>The stage clear earned and the featured rival beaten on track.</summary>
        Win = 0,
        /// <summary>The featured rival won and no stage clear.</summary>
        Loss = 1,
        /// <summary>The featured rival won the race but the convoy still earned the clear (spec §5.2).</summary>
        ClearedButLost = 2,
        /// <summary>The rival beaten on track but the benchmark missed: no per-stage line; the rival's generic "loss" line.</summary>
        BeatRivalMissedBenchmark = 3,
    }

    /// <summary>A race-diary entry the player can read.</summary>
    public sealed class DiaryEntry
    {
        /// <summary>stage | crew | record</summary>
        public string Kind = "";
        public string Id = "", Title = "", Text = "";
        public CampaignMode Mode;
    }

    /// <summary>
    /// The authored campaign story (spec §5.3): stage intros and reactions (stages.story.json), crew introductions
    /// (crews.diary.json), radio/timing-slip records (radio-records.json) and the rivals' generic lines (rivals.story.json).
    /// Presentation text only — never part of the race content hash. Engine-free, so the rules for a shortened rematch
    /// intro, the reaction for an outcome and what the race diary holds are testable outside Unity.
    /// </summary>
    public sealed class StoryText
    {
        /// <summary>A rematch intro keeps the opening line and the closing line (the lesson); the diary keeps the rest.</summary>
        public const int RematchLines = 2;
        /// <summary>Scene pacing (spec §5.3: a 10–18 s introductory scene): read at about this many characters a second.</summary>
        public const float CharactersPerSecond = 22f, SceneMinSeconds = 10f, SceneMaxSeconds = 18f, LineMinSeconds = 1.8f;

        /// <summary>
        /// How long each line of a scene stays up by itself: the scene's length at a comfortable reading pace, held to the
        /// 10–18 s target (a short rematch scene has no floor), shared between the lines by their length, at least 1.8 s each.
        /// Next and Skip are always there for a faster reader.
        /// </summary>
        public static float[] Holds(IList<StoryLine> lines, bool rematch)
        {
            if (lines == null || lines.Count == 0) return new float[0];
            int chars = lines.Sum(l => Math.Max(1, (l.Line ?? "").Length));
            float scene = Math.Min(SceneMaxSeconds, Math.Max(rematch ? 0f : SceneMinSeconds, chars / CharactersPerSecond));
            return lines.Select(l => Math.Max(LineMinSeconds, scene * Math.Max(1, (l.Line ?? "").Length) / chars)).ToArray();
        }

        readonly Dictionary<string, StageStory> stages = new Dictionary<string, StageStory>(StringComparer.Ordinal);
        readonly Dictionary<string, Dictionary<string, List<string>>> rivalLines = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        public readonly List<ActStory> Acts = new List<ActStory>();
        public readonly List<CrewIntroduction> Crews = new List<CrewIntroduction>();
        public readonly List<StoryRecord> Records = new List<StoryRecord>();
        /// <summary>The endings (endings.json): Normal "The Terrace After Amanagi", Hard "Before the First Train".</summary>
        public string NormalEndingTitle = "", HardEndingTitle = "", PostGameNote = "";
        public readonly List<StoryScene> NormalEnding = new List<StoryScene>(), HardEnding = new List<StoryScene>();

        public IEnumerable<StageStory> Stages => stages.Values;

        public static StoryText Load(string stagesJson, string crewsJson = null, string recordsJson = null, string rivalsJson = null, string endingsJson = null)
        {
            var s = new StoryText();
            JObject st = string.IsNullOrEmpty(stagesJson) ? new JObject() : JObject.Parse(stagesJson);
            foreach (JObject a in (st["acts"] as JArray ?? new JArray()).OfType<JObject>())
                s.Acts.Add(new ActStory { Act = (int?)a["act"] ?? 0, Title = (string)a["title"] ?? "", Summary = (string)a["summary"] ?? "" });
            foreach (JObject x in (st["stages"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var stage = new StageStory { Id = (string)x["id"] ?? "", Normal = Side(x["normal"] as JObject), Hard = Side(x["hard"] as JObject) };
                if (stage.Id.Length > 0) s.stages[stage.Id] = stage;
            }
            if (!string.IsNullOrEmpty(crewsJson))
                foreach (JObject c in (JObject.Parse(crewsJson)["crews"] as JArray ?? new JArray()).OfType<JObject>())
                    s.Crews.Add(new CrewIntroduction
                    {
                        Crew = (string)c["crew"] ?? "", Title = (string)c["title"] ?? "", UnlockAfterStage = (string)c["unlockAfterStage"] ?? "", Text = (string)c["text"] ?? "",
                    });
            if (!string.IsNullOrEmpty(recordsJson))
                foreach (JObject r in (JObject.Parse(recordsJson)["records"] as JArray ?? new JArray()).OfType<JObject>())
                    s.Records.Add(new StoryRecord
                    {
                        Id = (string)r["id"] ?? "", Title = (string)r["title"] ?? "", AwardedAfterStage = (string)r["awardedAfterStage"] ?? "",
                        Kind = (string)r["kind"] ?? "", Text = (string)r["text"] ?? "",
                    });
            if (!string.IsNullOrEmpty(rivalsJson))
                foreach (JObject r in (JObject.Parse(rivalsJson)["rivals"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var lines = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    if (r["lines"] is JObject l)
                        foreach (JProperty p in l.Properties())
                            lines[p.Name] = (p.Value as JArray ?? new JArray()).Select(v => (string)v ?? "").Where(v => v.Length > 0).ToList();
                    s.rivalLines[(string)r["id"] ?? ""] = lines;
                }
            if (!string.IsNullOrEmpty(endingsJson))
            {
                JObject e = JObject.Parse(endingsJson);
                s.NormalEndingTitle = (string)e["normal"]?["title"] ?? "";
                s.HardEndingTitle = (string)e["hard"]?["title"] ?? "";
                s.PostGameNote = (string)e["postGameNote"] ?? "";
                s.NormalEnding.AddRange(Scenes(e["normal"]?["scenes"]));
                s.HardEnding.AddRange(Scenes(e["hard"]?["scenes"]));
            }
            return s;
        }

        static IEnumerable<StoryScene> Scenes(JToken t) =>
            (t as JArray ?? new JArray()).OfType<JObject>().Select(x => new StoryScene { Setting = (string)x["setting"] ?? "", Lines = Lines(x["lines"]) });

        /// <summary>The last campaign stage: its first clear in a mode ends that campaign.</summary>
        public const int FinaleStage = 30;

        /// <summary>
        /// What plays after the first clear of the finale (spec §5.3/§5.4: a real ending): the whole Normal ending — the terrace
        /// reunion the morning after — or the Hard ending's dawn-run scene at the finish; the Hard ending's terrace scenes are
        /// the epilogue at the radio bench (<see cref="Epilogue"/>).
        /// </summary>
        public List<StoryScene> EndingAfterFinale(CampaignMode mode) =>
            mode == CampaignMode.Hard ? HardEnding.Take(1).ToList() : new List<StoryScene>(NormalEnding);

        /// <summary>The Hard epilogue with Shiori at the radio bench (Appendix E CH75): the Hard ending after its finish scene.</summary>
        public List<StoryScene> Epilogue() => HardEnding.Skip(1).ToList();

        static StageStorySide Side(JObject o)
        {
            var side = new StageStorySide();
            if (o == null) return side;
            side.Title = (string)o["title"] ?? "";
            side.Diary = (string)o["diary"] ?? "";
            side.Intro = Lines(o["intro"]);
            side.Win = Lines(o["win"]);
            side.Loss = Lines(o["loss"]);
            side.ClearedButLost = Lines(o["clearedButLost"]);
            return side;
        }

        static List<StoryLine> Lines(JToken t) =>
            (t as JArray ?? new JArray()).OfType<JObject>().Select(l => new StoryLine { Speaker = (string)l["speaker"] ?? "", Line = (string)l["line"] ?? "" })
                .Where(l => l.Line.Length > 0).ToList();

        public bool TryStage(string id, out StageStory stage) => stages.TryGetValue(id ?? "", out stage);

        public ActStory Act(int act) => Acts.FirstOrDefault(a => a.Act == act);

        /// <summary>
        /// The intro for a stage: the whole scene the first time, and on a rematch (spec §5.3: "default to a shorter intro")
        /// only its opening and closing lines — the full story stays in the race diary.
        /// </summary>
        public List<StoryLine> Intro(string stageId, CampaignMode mode, bool rematch)
        {
            if (!TryStage(stageId, out StageStory s)) return new List<StoryLine>();
            List<StoryLine> all = s.Side(mode).Intro;
            if (!rematch || all.Count <= RematchLines) return new List<StoryLine>(all);
            return new List<StoryLine> { all[0], all[all.Count - 1] };
        }

        /// <summary>
        /// The post-race reaction for an outcome (3–6 s of lines). The rival-beaten-but-benchmark-missed case has no stage line:
        /// the featured rival's generic "loss" line (the rival lost the race) is used, and the results screen explains the rest.
        /// </summary>
        public List<StoryLine> Reaction(string stageId, CampaignMode mode, StoryOutcome outcome, string featuredRival)
        {
            if (!TryStage(stageId, out StageStory s)) return new List<StoryLine>();
            StageStorySide side = s.Side(mode);
            switch (outcome)
            {
                case StoryOutcome.Win: return new List<StoryLine>(side.Win);
                case StoryOutcome.Loss: return new List<StoryLine>(side.Loss);
                case StoryOutcome.ClearedButLost: return new List<StoryLine>(side.ClearedButLost);
                default:
                    string line = RivalLine(featuredRival, "loss");
                    return line == null ? new List<StoryLine>() : new List<StoryLine> { new StoryLine { Speaker = featuredRival, Line = line } };
            }
        }

        /// <summary>A rival's first line of a kind (intro, victory, loss, defeatedRematch, introSolo, introConvoy), or null.</summary>
        public string RivalLine(string rival, string kind) =>
            rivalLines.TryGetValue(rival ?? "", out var lines) && lines.TryGetValue(kind, out List<string> l) && l.Count > 0 ? l[0] : null;

        /// <summary>The story outcome from the stage verdict (convoy's side): the clear and the featured rival on track.</summary>
        public static StoryOutcome OutcomeOf(bool earnedClear, bool beatFeaturedRival) =>
            earnedClear ? (beatFeaturedRival ? StoryOutcome.Win : StoryOutcome.ClearedButLost)
                        : (beatFeaturedRival ? StoryOutcome.BeatRivalMissedBenchmark : StoryOutcome.Loss);

        /// <summary>The story's only placeholders: {player} and {convoy}.</summary>
        public static string Fill(string line, string player, string convoy) =>
            (line ?? "").Replace("{player}", string.IsNullOrEmpty(player) ? "you" : player).Replace("{convoy}", string.IsNullOrEmpty(convoy) ? "the convoy" : convoy);

        /// <summary>
        /// The race diary for a player: every cleared stage's entry (Normal, then Hard, in stage order), the crew introductions
        /// whose stage is cleared on Normal, and the radio / timing-slip records awarded through Normal progression.
        /// <paramref name="cleared"/> answers whether a stage is cleared in a mode.
        /// </summary>
        public List<DiaryEntry> Diary(IEnumerable<string> stageOrder, Func<string, CampaignMode, bool> cleared)
        {
            var list = new List<DiaryEntry>();
            List<string> order = (stageOrder ?? stages.Keys).ToList();
            foreach (CampaignMode mode in new[] { CampaignMode.Normal, CampaignMode.Hard })
                foreach (string id in order)
                    if (cleared(id, mode) && TryStage(id, out StageStory s) && s.Side(mode).Diary.Length > 0)
                        list.Add(new DiaryEntry { Kind = "stage", Id = id, Mode = mode, Title = $"{id}{(mode == CampaignMode.Hard ? " Hard" : "")} · {s.Side(mode).Title}", Text = s.Side(mode).Diary });
            foreach (CrewIntroduction c in Crews)
                if (cleared(c.UnlockAfterStage, CampaignMode.Normal))
                    list.Add(new DiaryEntry { Kind = "crew", Id = c.Crew, Title = c.Title, Text = c.Text });
            foreach (StoryRecord r in Records)
                if (cleared(r.AwardedAfterStage, CampaignMode.Normal))
                    list.Add(new DiaryEntry { Kind = "record", Id = r.Id, Title = r.Title, Text = r.Text });
            return list;
        }
    }
}
