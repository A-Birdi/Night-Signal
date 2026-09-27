using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// While We Wait selector (Addendum 02 §1): the five diversions. Online they are the convoy's shared toys and never
    /// change anyone's readiness; offline they are this profile's own table. None of them is progression.
    /// </summary>
    public sealed class WhileWeWaitScreen : UIScreen
    {
        public override string ScreenName => "While We Wait";
        public override string MusicCue => "MUS_GARAGE";
        TextMeshProUGUI domain;
        Button first;

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0, 0), new Vector2(0.46f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.92f));
            RectTransform col = UIFactory.Column("Column", panel.transform, new Vector2(0, 0.05f), new Vector2(1, 0.92f), new Vector2(64, 0), new Vector2(-32, 0), 12f);
            UIFactory.Row("Heading", col, "WHILE WE WAIT", SignalTheme.Heading, SignalTheme.Label, 700, 0, true);
            domain = UIFactory.Row("Domain", col, "", SignalTheme.Small, SignalTheme.Caution, 700, 52);
            first = Entry(col, "PocketCircuit", "Pocket Circuit", "Six-lane tabletop slot cars: analog throttle, harmless de-slots, clean-lap collection.", () => App.Router.Show(App.PocketCircuit));
            Entry(col, "Greenlight", "Greenlight", "Reaction station: Lights Out, Shift Window, Hold the Mark.", () => App.Router.Show(App.Greenlight));
            Entry(col, "CapClash", "Cap Clash", "Flick bottle caps across a toolbox tabletop; one shot each, bank shots, a shared card.", () => App.Router.Show(App.CapClash));
            Entry(col, "PitCrew", "Pit-Crew Project", "Build a miniature together, one task each; finished models go on the shelf.", () => App.Router.Show(App.PitCrew));
            Entry(col, "Canvas", "Convoy Canvas", "Draw, stamp and letter a shared sheet or a car hood.", null);
            UIFactory.Button("Back", col, "Back", () => App.Router.Back(), 620, 52);
        }

        Button Entry(Transform col, string name, string title, string text, System.Action open)
        {
            Button b = UIFactory.Button("Toy-" + name, col, open != null ? title : title + "  (presentation not built yet)", open, 680, 56);
            b.interactable = open != null;
            UIFactory.Row(name + "Note", col, text, SignalTheme.Small, SignalTheme.LabelDim, 700, 26);
            return b;
        }

        public override Selectable DefaultFocus => first;

        public override void OnShow()
        {
            bool online = App.Domain == SessionDomain.Online && OnlineSession.Current?.InConvoy == true;
            domain.text = online
                ? "The convoy's shared toys. Playing never changes your Mode or Event Ready; a starting race pauses them and keeps everyone's progress."
                : "Local toys on this PC. Toy results are for fun: they never pay, rank or unlock anything.";
        }
    }
}
