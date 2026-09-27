using System;
using System.Net.Http;
using System.Threading.Tasks;
using NightSignal.Net;
using NightSignal.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>
    /// Authored title screen (Addendum 01 §8.1): Online Login, Offline Play, Settings, Quit — always offered, whatever
    /// the network is doing. The service status line reports only what was actually checked; a reachable service is not
    /// a signed-in session, and an unreachable one never traps the title in a retry loop.
    /// </summary>
    public sealed class MainMenuScreen : UIScreen
    {
        public override string ScreenName => "Title";
        public override string MusicCue => "MUS_TITLE";
        Button online, offline;
        TextMeshProUGUI status;
        bool checking;

        protected override void OnBuild(RectTransform root)
        {
            Image shade = UIFactory.Panel("Shade", root, Vector2.zero, new Vector2(0.42f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.86f));
            Image rule = UIFactory.Panel("Rule", shade.transform, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-4, 0), Vector2.zero, SignalTheme.Signal);
            TextMeshProUGUI title = UIFactory.Label("Title", shade.transform, "NIGHT SIGNAL", SignalTheme.Heading * 2.1f, SignalTheme.Label, TextAlignmentOptions.BottomLeft, true);
            title.rectTransform.anchorMin = new Vector2(0, 0.7f);
            title.rectTransform.anchorMax = new Vector2(1, 0.86f);
            title.rectTransform.offsetMin = new Vector2(72, 0);
            title.characterSpacing = 6f;
            TextMeshProUGUI sub = UIFactory.Label("Subtitle", shade.transform, "MOUNTAIN CIRCUIT", SignalTheme.Subheading, SignalTheme.Signal, TextAlignmentOptions.TopLeft, true);
            sub.rectTransform.anchorMin = new Vector2(0, 0.58f);
            sub.rectTransform.anchorMax = new Vector2(1, 0.655f);
            sub.rectTransform.offsetMin = new Vector2(76, 0);
            sub.characterSpacing = 14f;

            RectTransform col = UIFactory.Column("Actions", shade.transform, new Vector2(0, 0.18f), new Vector2(1, 0.56f), new Vector2(72, 0), Vector2.zero, 16f);
            online = UIFactory.Button("OnlineLogin", col, "Online Login", () => App.Router.Show(App.SignIn), 420, 60);
            offline = UIFactory.Button("OfflinePlay", col, "Offline Play", () => App.Router.Show(App.ProfileSelect), 420, 60);
            UIFactory.Button("Settings", col, "Settings", () => App.Router.Show(App.Settings), 420, 60);
            UIFactory.Button("Quit", col, "Quit", Quit, 420, 60);

            status = UIFactory.Label("ServiceStatus", shade.transform, "", SignalTheme.Small, SignalTheme.LabelDim, TextAlignmentOptions.BottomLeft);
            status.rectTransform.anchorMin = new Vector2(0, 0.04f);
            status.rectTransform.anchorMax = new Vector2(1, 0.14f);
            status.rectTransform.offsetMin = new Vector2(72, 0);
            status.textWrappingMode = TextWrappingModes.Normal;
        }

        public override Selectable DefaultFocus => online;

        public override void OnShow()
        {
            App.Domain = SessionDomain.None;
            App.DisplayName = "";
            _ = CheckService();
        }

        async Task CheckService()
        {
            if (checking) return;
            checking = true;
            string url = NetConfig.FromCommandLine().ControlPlaneUrl;
            status.text = "ONLINE SERVICE  ·  checking...";
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) })
                {
                    HttpResponseMessage r = await http.GetAsync(url.TrimEnd('/') + "/healthz");
                    status.text = r.IsSuccessStatusCode
                        ? "ONLINE SERVICE  ·  reachable (not signed in)\nOFFLINE PLAY  ·  always available"
                        : $"ONLINE SERVICE  ·  responded {(int)r.StatusCode}\nOFFLINE PLAY  ·  always available";
                }
            }
            catch (Exception)
            {
                status.text = "ONLINE SERVICE  ·  unreachable — Online Login will retry when chosen\nOFFLINE PLAY  ·  always available";
            }
            checking = false;
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }

    /// <summary>
    /// Email + password sign-in handed straight to the identity provider (Addendum 01 D08, §9.2). Locally that provider is
    /// the development identity service; the production provider is not configured in this environment, and the screen
    /// says so rather than implying an account system that is not there. The password never leaves this call, is never
    /// logged, and the field is cleared afterwards.
    /// </summary>
    public sealed class SignInScreen : UIScreen
    {
        public override string ScreenName => "Sign In";
        TMP_InputField email, password;
        Button submit;
        TextMeshProUGUI message;
        bool busy;

        protected override void OnBuild(RectTransform root)
        {
            Image panel = UIFactory.Panel("Panel", root, new Vector2(0, 0), new Vector2(0.42f, 1f), Vector2.zero, Vector2.zero, new Color(0.055f, 0.06f, 0.07f, 0.9f));
            RectTransform col = UIFactory.Column("Form", panel.transform, new Vector2(0, 0.1f), new Vector2(1, 0.86f), new Vector2(72, 0), new Vector2(-40, 0), 14f);
            UIFactory.Row("Heading", col, "ONLINE LOGIN", SignalTheme.Heading, SignalTheme.Label, 600, 0, true);
            UIFactory.Row("Provider", col, "Development identity service (local stack). The production account provider is not configured on this machine.", SignalTheme.Small, SignalTheme.Caution, 600, 52);
            email = UIFactory.InputField("Email", col, "Email", false, 254, 560);
            email.contentType = TMP_InputField.ContentType.EmailAddress;
            password = UIFactory.InputField("Password", col, "Password", true, 128, 560);
            submit = UIFactory.Button("SignIn", col, "Sign In", () => _ = Submit(), 560, 60);
            UIFactory.Button("Offline", col, "Offline Play instead", () => App.Router.Show(App.ProfileSelect, false), 560, 52);
            message = UIFactory.Row("Message", col, "", SignalTheme.Small, SignalTheme.LabelDim, 560, 90);
        }

        public override Selectable DefaultFocus => email;

        public override void OnShow() => message.text = "";

        async Task Submit()
        {
            if (busy) return;
            string addr = email.text.Trim();
            string secret = password.text;
            password.text = ""; // never keep it in the UI
            if (addr.Length == 0 || secret.Length == 0)
            {
                message.text = "Enter your email and password.";
                return;
            }
            busy = true;
            submit.interactable = false;
            message.text = "Signing in...";
            try
            {
                OnlineSession session = await OnlineSession.SignIn(NetConfig.FromCommandLine().ControlPlaneUrl, addr, secret);
                App.AttachOnline(session);
                message.text = "";
                App.Router.Show(App.Convoy, false);
            }
            catch (HttpRequestException)
            {
                message.text = "The online service could not be reached. Try again, or choose Offline Play.";
            }
            catch (Exception)
            {
                // Generic wording: never reveal whether the email or the password was wrong.
                message.text = "Sign-in failed. Check your details and try again.";
            }
            finally
            {
                busy = false;
                submit.interactable = true;
            }
        }
    }
}
