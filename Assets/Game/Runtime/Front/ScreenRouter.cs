using System.Collections.Generic;
using NightSignal.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NightSignal.Front
{
    /// <summary>One full-screen page of the front end. Built once, shown/hidden by the router.</summary>
    public abstract class UIScreen
    {
        public RectTransform Root { get; private set; }
        public CanvasGroup Group { get; private set; }
        public FrontEndApp App { get; internal set; }
        /// <summary>Coarse local-screen name published as presence (never private detail).</summary>
        public virtual string ScreenName => GetType().Name.Replace("Screen", "");
        /// <summary>Music cue for this screen (null keeps whatever is playing — no restart on small submenus).</summary>
        public virtual string MusicCue => null;
        internal GameObject LastFocus;

        internal void Build(RectTransform parent)
        {
            Root = UIFactory.Rect(ScreenName, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Group = Root.gameObject.AddComponent<CanvasGroup>();
            OnBuild(Root);
            Root.gameObject.SetActive(false);
        }

        protected abstract void OnBuild(RectTransform root);
        public virtual void OnShow() { }
        public virtual void OnHide() { }
        /// <summary>Handle Back locally (close a panel, cancel a draft); return false to let the router pop.</summary>
        public virtual bool OnBack() => false;
        public virtual Selectable DefaultFocus => null;
        public virtual void Tick() { }
    }

    /// <summary>
    /// Local navigation (spec §15): a short diagonal sector-strip wipe (~220 ms) tied to navigation, or a brief crossfade
    /// with Reduced Motion. Input goes to exactly one screen; obsolete screens are hidden (not left as invisible blockers);
    /// Back restores the previous screen and its focus. Slow loads show real progress elsewhere, never a stretched wipe.
    /// </summary>
    public sealed class ScreenRouter : MonoBehaviour
    {
        readonly List<UIScreen> stack = new List<UIScreen>();
        readonly List<UIScreen> built = new List<UIScreen>();
        RectTransform screensRoot, wipeRoot;
        readonly List<RectTransform> strips = new List<RectTransform>();
        UIScreen pending;
        bool pendingPush, pendingPop;
        float transitionT = -1f;
        bool swapped;
        InputAction back;
        FrontEndApp app;

        public UIScreen Current => stack.Count > 0 ? stack[stack.Count - 1] : null;
        public bool Transitioning => transitionT >= 0f;

        public void Init(FrontEndApp owner, RectTransform parent)
        {
            app = owner;
            screensRoot = UIFactory.Rect("Screens", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            wipeRoot = UIFactory.Rect("Wipe", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            for (int i = 0; i < 5; i++)
            {
                Image strip = UIFactory.Panel("Strip" + i, wipeRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero,
                    i == 2 ? SignalTheme.Signal : SignalTheme.Ink);
                strip.rectTransform.sizeDelta = new Vector2(560f, 2600f);
                strip.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -18f);
                strips.Add(strip.rectTransform);
            }
            wipeRoot.gameObject.SetActive(false);
            back = new InputAction("Back", InputActionType.Button);
            back.AddBinding("<Keyboard>/escape");
            back.AddBinding("<Gamepad>/buttonEast");
            back.Enable();
        }

        void OnDestroy() => back?.Dispose();

        T Ensure<T>(T screen) where T : UIScreen
        {
            if (!built.Contains(screen))
            {
                screen.App = app;
                screen.Build(screensRoot);
                built.Add(screen);
            }
            return screen;
        }

        /// <summary>Navigate to a screen (push keeps the current one for Back; otherwise it replaces the whole stack).</summary>
        public void Show(UIScreen screen, bool push = true)
        {
            Ensure(screen);
            Begin(screen, push, false);
        }

        public void Back()
        {
            if (Current != null && Current.OnBack()) return;
            if (stack.Count < 2) return;
            Begin(stack[stack.Count - 2], false, true);
        }

        void Begin(UIScreen target, bool push, bool pop)
        {
            // Rapid navigation replaces the pending target instead of queueing transitions.
            pending = target;
            pendingPush = push;
            pendingPop = pop;
            if (Current == null)
            {
                Swap();
                return;
            }
            if (Current.Group != null) Current.Group.interactable = false;
            if (transitionT < 0f)
            {
                transitionT = 0f;
                swapped = false;
                wipeRoot.gameObject.SetActive(!SignalTheme.ReducedMotion);
            }
        }

        void Swap()
        {
            UIScreen from = Current;
            if (from != null)
            {
                from.LastFocus = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                from.OnHide();
                from.Root.gameObject.SetActive(false);
            }
            if (pendingPop) stack.RemoveAt(stack.Count - 1);
            else if (pendingPush) stack.Add(pending);
            else
            {
                foreach (UIScreen s in stack) if (s != pending && s.Root.gameObject.activeSelf) s.Root.gameObject.SetActive(false);
                stack.Clear();
                stack.Add(pending);
            }
            UIScreen to = Current;
            to.Root.gameObject.SetActive(true);
            to.Group.interactable = true;
            to.Group.blocksRaycasts = true;
            to.Group.alpha = 1f;
            to.OnShow();
            app.OnScreenChanged(to);
            GameObject focus = to.LastFocus != null && to.LastFocus.activeInHierarchy ? to.LastFocus : to.DefaultFocus != null ? to.DefaultFocus.gameObject : null;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(focus);
            pending = null;
        }

        void Update()
        {
            // Menus hidden (a race or the Test Yard owns the screen): Back belongs to that session, not the menu stack.
            if (back.WasPressedThisFrame() && !Transitioning && app != null && app.Canvas != null && app.Canvas.gameObject.activeInHierarchy) Back();
            Current?.Tick();
            if (transitionT < 0f) return;

            float duration = SignalTheme.WipeSeconds;
            transitionT += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(transitionT / duration);
            if (SignalTheme.ReducedMotion)
            {
                // Short crossfade: fade out, swap at the midpoint, fade in.
                if (!swapped && t >= 0.5f) { Swap(); swapped = true; }
                if (Current != null) Current.Group.alpha = swapped ? Mathf.InverseLerp(0.5f, 1f, t) : 1f - t * 2f;
            }
            else
            {
                // Diagonal sector strips sweep across left → right, staggered; the swap happens while they cover.
                float w = ((RectTransform)wipeRoot.parent).rect.width + 900f;
                for (int i = 0; i < strips.Count; i++)
                {
                    float local = Mathf.Clamp01(t * 1.35f - i * 0.07f);
                    float eased = local * local * (3f - 2f * local);
                    strips[i].anchoredPosition = new Vector2(Mathf.Lerp(-w * 0.5f - 300f, w * 0.5f + 300f, eased) + (i - 2) * 170f, 0f);
                }
                if (!swapped && t >= 0.5f) { Swap(); swapped = true; }
            }
            if (t >= 1f)
            {
                if (!swapped) Swap();
                transitionT = -1f;
                wipeRoot.gameObject.SetActive(false);
                if (Current != null) Current.Group.alpha = 1f;
                if (pending != null) Begin(pending, pendingPush, pendingPop); // a newer request arrived mid-wipe
            }
        }
    }
}
