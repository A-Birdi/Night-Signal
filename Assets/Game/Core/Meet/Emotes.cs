using System;

namespace NightSignal.Core.Meet
{
    /// <summary>
    /// The meet's emotes (spec: wave, polite bow, thumbs-up, clap, point, camera pose, stretch, cheer, shoulder shrug, nod,
    /// quick footwork dance, car-admiration crouch). Only the ID, start tick and bounded duration replicate; each client
    /// animates locally.
    /// </summary>
    public enum Emote : byte
    {
        None = 0,
        Wave = 1,
        Bow = 2,
        ThumbsUp = 3,
        Clap = 4,
        Point = 5,
        CameraPose = 6,
        Stretch = 7,
        Cheer = 8,
        Shrug = 9,
        Nod = 10,
        Footwork = 11,
        Admire = 12,
    }

    public static class Emotes
    {
        public const int Count = 12;

        /// <summary>Wheel order (clockwise from the top).</summary>
        public static readonly Emote[] Wheel =
        {
            Emote.Wave, Emote.Bow, Emote.ThumbsUp, Emote.Clap, Emote.Point, Emote.CameraPose,
            Emote.Stretch, Emote.Cheer, Emote.Shrug, Emote.Nod, Emote.Footwork, Emote.Admire,
        };

        /// <summary>Bounded duration in seconds (the server ends an emote after this; moving cancels it earlier).</summary>
        public static float Duration(Emote e)
        {
            switch (e)
            {
                case Emote.Wave: return 2.2f;
                case Emote.Bow: return 2.0f;
                case Emote.ThumbsUp: return 1.8f;
                case Emote.Clap: return 2.4f;
                case Emote.Point: return 2.0f;
                case Emote.CameraPose: return 2.6f;
                case Emote.Stretch: return 3.0f;
                case Emote.Cheer: return 2.2f;
                case Emote.Shrug: return 1.6f;
                case Emote.Nod: return 1.4f;
                case Emote.Footwork: return 3.2f;
                case Emote.Admire: return 3.4f;
                default: return 0f;
            }
        }

        public static string Label(Emote e)
        {
            switch (e)
            {
                case Emote.ThumbsUp: return "Thumbs-up";
                case Emote.CameraPose: return "Camera pose";
                case Emote.Footwork: return "Footwork";
                case Emote.Admire: return "Admire";
                case Emote.Bow: return "Bow";
                default: return e.ToString();
            }
        }

        public static bool TryParse(string id, out Emote e)
        {
            if (Enum.TryParse(id, true, out e) && e != Emote.None) return true;
            e = Emote.None;
            return false;
        }
    }
}
