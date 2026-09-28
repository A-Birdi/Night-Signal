using System;
using NightSignal.Core.Meet;
using UnityEngine;

namespace NightSignal.Meet
{
    public sealed partial class MeetSession
    {
        /// <summary>
        /// Offline: a touring act that happened where it must (the front end records it on the Local profile and grants what
        /// it completes). Online the meet room decides from the server-held position, so this stays unset there.
        /// </summary>
        public Action<TouringAct, string> TouringActed;

        /// <summary>
        /// A touring act (CH61–CH65). Online, the client-reported ones go to the room (arrival, wave and bow the room sees
        /// for itself); offline the local position is checked and the front end is told.
        /// </summary>
        void Touring(TouringAct act, string id = null)
        {
            if (Net != null)
            {
                string step = act == TouringAct.InspectOwnCar ? "own-car" : act == TouringAct.ReadPlacard ? "placard"
                    : act == TouringAct.ReadEmoteHelp ? "emote-help" : act == TouringAct.PhotoComposed ? "photo"
                    : act == TouringAct.ReadResultSlip ? "result-slip" : act == TouringAct.Epilogue ? "epilogue" : null;
                if (step != null) Net.Fire("meet.touring", new { step, id });
                return;
            }
            if (Player == null) return;
            Vector3 p = Player.transform.position;
            if (!MeetTouring.InPlace(act, id, PlayerBay, p.x, p.z)) return;
            TouringActed?.Invoke(act, id);
        }

        /// <summary>A touring challenge completed (offline grant or the room's push): the SIGNAL ribbon says so.</summary>
        public void ChallengeCompleted(string challengeId, string name, long cash)
        {
            Hud.Ribbon.Post(NoticeKind.Info, "challenge:" + challengeId, $"Challenge complete · {name} · +{cash:N0} cr");
            Note($"challenge {challengeId} completed ({name}, +{cash} cr)");
        }

        /// <summary>
        /// The photo mode's built-in composition check (CH64): standing at the overlook marker, your own car inside the frame
        /// and the camera level enough to keep the horizon in the picture.
        /// </summary>
        public bool PhotoComposition(out bool atMarker, out bool carInFrame, out bool horizon)
        {
            Vector3 p = Player != null ? Player.transform.position : Vector3.zero;
            atMarker = MeetTouring.InPlace(TouringAct.PhotoComposed, null, PlayerBay, p.x, p.z);
            carInFrame = false;
            Camera cam = Camera != null ? Camera.Cam : null;
            if (cam != null && PlayerCar != null)
            {
                Vector3 v = cam.WorldToViewportPoint(PlayerCar.transform.position + Vector3.up * 0.6f);
                carInFrame = v.z > 0.5f && v.z < 90f && v.x > 0.06f && v.x < 0.94f && v.y > 0.06f && v.y < 0.94f;
            }
            horizon = Camera != null && Mathf.Abs(Camera.Pitch) <= 12f;
            return atMarker && carInFrame && horizon;
        }

        string CompositionLine()
        {
            PhotoComposition(out bool marker, out bool car, out bool horizon);
            string Mark(bool ok) => ok ? "<color=#3EC6D8>yes</color>" : "<color=#9A968D>no</color>"; // (the UI font has no check-mark glyph)
            return $"Composition: overlook marker {Mark(marker)} · your car {Mark(car)} · horizon {Mark(horizon)}";
        }

        /// <summary>Automation: save a photo as the Interact control does in photo mode (with its composition check).</summary>
        public bool TakePhoto() => SavePhoto();
    }
}
