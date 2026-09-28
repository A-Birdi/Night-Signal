using System;
using System.Collections.Generic;
using System.Linq;
using NightSignal.Characters;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Meet;
using NightSignal.Core.Story;
using UnityEngine;

namespace NightSignal.Meet
{
    public sealed partial class MeetSession
    {
        /// <summary>
        /// The Hard epilogue (spec §5.4: "Hard ends with the dawn-run story resolved and Shiori at the radio bench"; Appendix E
        /// CH75), set by the front end once the player has cleared the Hard finale: Shiori Kuze waits beside the radio bench and
        /// talking to her reads the epilogue a line at a time. Reading it to its end (not closing it early) is the touring act
        /// the room — or offline the Local profile — turns into CH75.
        /// </summary>
        public List<StoryScene> Epilogue;
        /// <summary>The epilogue was read to its end in this visit (tours read it).</summary>
        public bool EpilogueRead { get; private set; }
        public int EpiloguePage { get; private set; } = -1;
        public int EpiloguePages => epilogueLines.Count;

        Npc shiori;
        readonly List<StoryLine> epilogueLines = new List<StoryLine>();

        /// <summary>Shiori at the bench (only when the epilogue is open for this player).</summary>
        void SpawnEpilogue(ContentLibrary lib, ContentCatalogue cat)
        {
            epilogueLines.Clear();
            if (Epilogue == null || Epilogue.Count == 0) return;
            foreach (StoryScene sc in Epilogue)
            {
                epilogueLines.Add(new StoryLine { Speaker = "setting", Line = sc.Setting });
                epilogueLines.AddRange(sc.Lines);
            }
            CharacterLook look = lib.Look("R48");
            RivalDef r = cat.Rivals.FirstOrDefault(x => x.Id == "R48");
            if (look == null || r == null) return;
            shiori = new Npc { Id = r.Id, Name = r.Name, Crew = r.Crew, Intro = r.IntroSample };
            shiori.Rig = CharacterRig.Create(look);
            MeetBox bench = MeetLayout.RadioBench;
            shiori.Rig.transform.SetPositionAndRotation(new Vector3(bench.X + 1.2f, 0f, bench.Z - 1.1f), Quaternion.Euler(0f, 200f, 0f));
            shiori.Motion = shiori.Rig.gameObject.AddComponent<CharacterMotion>();
            shiori.NextEmote = float.MaxValue;
            spawned.Add(shiori.Rig.gameObject);
            spots.Add(new Spot { Kind = "epilogue", Id = r.Id, Label = $"Talk to {r.Name}", Pos = shiori.Rig.transform.position + Vector3.up * 1.2f, Range = 2.6f, Npc = shiori });
            Note("the Hard epilogue is open: Shiori is at the radio bench");
        }

        /// <summary>Shows epilogue line <paramref name="k"/>; past the last one the epilogue has been read.</summary>
        void ShowEpilogue(int k)
        {
            if (k >= epilogueLines.Count)
            {
                ClosePanel();
                EpilogueRead = true;
                Note("epilogue read to its end");
                Touring(TouringAct.Epilogue);
                return;
            }
            EpiloguePage = k;
            StoryLine l = epilogueLines[k];
            ContentCatalogue cat = ContentLibrary.Load()?.Catalogue;
            bool narration = l.Speaker == "setting" || l.Speaker == "narration";
            string text = StoryText.Fill(l.Line, PlayerName, "").Replace("<", "(").Replace(">", ")");
            string body = narration ? $"<i>{text}</i>" : $"<b>{Front.StoryScreen.SpeakerName(l.Speaker, cat)}</b>\n{text}";
            ShowPanel($"Before the First Train  ·  {k + 1} / {epilogueLines.Count}", body, new List<(string, Action)>
            {
                (k + 1 < epilogueLines.Count ? "Next" : "Finish", () => ShowEpilogue(k + 1)),
            });
        }

        /// <summary>Tours: the epilogue's next line (Next / Finish as the panel offers it).</summary>
        public void EpilogueNext()
        {
            if (EpiloguePage >= 0) ShowEpilogue(EpiloguePage + 1);
        }

        /// <summary>Tours: stand the player beside the radio bench (automation — a person walks there).</summary>
        public void StandAtRadioBench()
        {
            Player?.Teleport(new Vector3(MeetLayout.HostSpot.X - 1.5f, 0f, MeetLayout.HostSpot.Z - 1.5f), 330f);
        }
    }
}
