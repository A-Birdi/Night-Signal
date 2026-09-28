using System;
using System.Collections.Generic;
using System.Linq;

namespace NightSignal.Core.Meet
{
    /// <summary>What a visitor did at the meet that a touring challenge counts (each already checked for place).</summary>
    public enum TouringAct
    {
        /// <summary>The arrival presentation completed or was skipped (the room confirmed the parking).</summary>
        Arrived,
        /// <summary>Inspected their own parked car (standing beside it).</summary>
        InspectOwnCar,
        /// <summary>Read a viewpoint placard (standing at it); the id is the placard's.</summary>
        ReadPlacard,
        /// <summary>Waved at the tutorial host (near the host spot).</summary>
        WaveAtHost,
        /// <summary>Bowed to the tutorial host (near the host spot).</summary>
        BowToHost,
        /// <summary>Read the host's emote help (near the host spot).</summary>
        ReadEmoteHelp,
        /// <summary>Took a photo at the overlook marker whose built-in composition check passed.</summary>
        PhotoComposed,
        /// <summary>Read the result slip at the timing board after completing an eligible event.</summary>
        ReadResultSlip,
        /// <summary>Read the Hard epilogue to its end with Shiori at the radio bench, after clearing the Hard finale (CH75).</summary>
        Epilogue,
    }

    /// <summary>One visitor's touring progress (kept for the account while the service runs; the grant is once ever).</summary>
    public sealed class MeetTouringProgress
    {
        public bool Arrived, OwnCar, Wave, Bow, EmoteHelp, Photo, Slip;
        /// <summary>The own car was inspected wearing a signed livery (CH48, a workshop challenge seen at the meet).</summary>
        public bool SignedCar;
        /// <summary>The Hard epilogue read to its end at the radio bench (CH75).</summary>
        public bool Epilogue;
        public readonly HashSet<string> Placards = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> PhotoPoints = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Challenges this progress has already reported complete (never reported twice).</summary>
        public readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The meet's touring challenges (Appendix E CH61–CH65, "touring" family): personal, achievable alone, validated where
    /// they happen — the meet room checks the visitor's server-held position for every act, so a claim from elsewhere on
    /// the terrace counts for nothing. CH61 First Parking Place: arrive and inspect your own parked car. CH62 Four Corners
    /// of the Terrace: read all four viewpoint placards at the placards. CH63 A Driver's Greeting: wave and bow at the
    /// tutorial host and read the emote help. CH64 A Picture With a Horizon: a photo at the overlook marker that passes the
    /// composition check (your car and the horizon in frame). CH65 Bring It Home: after completing an eligible event,
    /// read the result slip at the timing board. CH67 Photo Points (silver): read the three named non-race photo points —
    /// tea kiosk, radio bench, maintenance gate — standing at each.
    /// </summary>
    public static class MeetTouring
    {
        public const string FirstParking = "CH61", FourCorners = "CH62", Greeting = "CH63", Horizon = "CH64", BringItHome = "CH65",
            PhotoPoints = "CH67", AfterTheLastSignal = "CH75";
        public static readonly string[] Challenges = { FirstParking, FourCorners, Greeting, Horizon, BringItHome, PhotoPoints };

        /// <summary>How close (m, on the ground) the visitor must stand for each place.</summary>
        public const float OwnCarRange = 4.5f, PlacardRange = 4f, HostRange = 5f, PhotoRange = 3f, BoardRange = 5f, BenchRange = 4f;

        /// <summary>Whether an act happened where it must (x, z = the visitor's position; ownBay = their bay, 0-based).</summary>
        public static bool InPlace(TouringAct act, string id, int ownBay, float x, float z)
        {
            switch (act)
            {
                case TouringAct.Arrived: return true;
                case TouringAct.InspectOwnCar:
                    if (ownBay < 0 || ownBay >= MeetLayout.Bays.Length) return false;
                    MeetBox f = MeetLayout.Bays[ownBay].Footprint;
                    return Near(f.X, f.Z, x, z, OwnCarRange + Math.Max(f.HalfW, f.HalfL));
                case TouringAct.ReadPlacard:
                    foreach (MeetBox p in MeetLayout.Placards)
                        if (p.Id == id) return Near(p.X, p.Z, x, z, PlacardRange);
                    foreach ((string pid, MeetPoint at) in MeetLayout.PhotoPoints)
                        if (pid == id) return Near(at.X, at.Z, x, z, PlacardRange);
                    return false;
                case TouringAct.WaveAtHost:
                case TouringAct.BowToHost:
                case TouringAct.ReadEmoteHelp:
                    return Near(MeetLayout.HostSpot.X, MeetLayout.HostSpot.Z, x, z, HostRange);
                case TouringAct.PhotoComposed:
                    return Near(MeetLayout.PhotoMarker.X, MeetLayout.PhotoMarker.Z, x, z, PhotoRange);
                case TouringAct.ReadResultSlip:
                    return Near(MeetLayout.TimingBoard.X, MeetLayout.TimingBoard.Z, x, z, BoardRange);
                case TouringAct.Epilogue:
                    return Near(MeetLayout.RadioBench.X, MeetLayout.RadioBench.Z, x, z, BenchRange);
            }
            return false;
        }

        static bool Near(float ax, float az, float bx, float bz, float range) =>
            (ax - bx) * (ax - bx) + (az - bz) * (az - bz) <= range * range;

        /// <summary>
        /// Records an act that was already checked for place (and, for the result slip, for an eligible completed event)
        /// and returns the challenges it newly completes — each at most once per progress. <paramref name="ownCarSigned"/>:
        /// the car inspected wears a signed livery (<see cref="Customization.LiveryChallenges.Signed"/>), as the room holds it.
        /// </summary>
        public static List<string> Record(MeetTouringProgress p, TouringAct act, string id = null, bool ownCarSigned = false)
        {
            switch (act)
            {
                case TouringAct.Arrived: p.Arrived = true; break;
                case TouringAct.InspectOwnCar:
                    p.OwnCar = true;
                    if (ownCarSigned) p.SignedCar = true;
                    break;
                case TouringAct.ReadPlacard:
                    if (MeetLayout.Placards.Any(x => x.Id == id)) p.Placards.Add(id);
                    else if (MeetLayout.PhotoPoints.Any(x => x.Id == id)) p.PhotoPoints.Add(id);
                    break;
                case TouringAct.WaveAtHost: p.Wave = true; break;
                case TouringAct.BowToHost: p.Bow = true; break;
                case TouringAct.ReadEmoteHelp: p.EmoteHelp = true; break;
                case TouringAct.PhotoComposed: p.Photo = true; break;
                case TouringAct.ReadResultSlip: p.Slip = true; break;
                case TouringAct.Epilogue: p.Epilogue = true; break;
            }
            var done = new List<string>();
            void Check(string challenge, bool met)
            {
                if (met && p.Reported.Add(challenge)) done.Add(challenge);
            }
            Check(FirstParking, p.Arrived && p.OwnCar);
            Check(FourCorners, MeetLayout.Placards.All(x => p.Placards.Contains(x.Id)));
            Check(Greeting, p.Wave && p.Bow && p.EmoteHelp);
            Check(Horizon, p.Photo);
            Check(BringItHome, p.Slip);
            Check(PhotoPoints, MeetLayout.PhotoPoints.All(x => p.PhotoPoints.Contains(x.Id)));
            // CH48 Sign Your Car (workshop): the signed livery seen on the parked car.
            Check(Customization.LiveryChallenges.SignYourCar, p.SignedCar);
            // CH75 After the Last Signal: the Hard epilogue finished with Shiori at the radio bench (the Hard finale cleared first).
            Check(AfterTheLastSignal, p.Epilogue);
            return done;
        }

        /// <summary>The act a request names (<c>own-car</c>, <c>placard</c>, <c>emote-help</c>, <c>photo</c>, <c>result-slip</c>).</summary>
        public static bool TryParse(string step, out TouringAct act)
        {
            switch (step)
            {
                case "own-car": act = TouringAct.InspectOwnCar; return true;
                case "epilogue": act = TouringAct.Epilogue; return true;
                case "placard": act = TouringAct.ReadPlacard; return true;
                case "emote-help": act = TouringAct.ReadEmoteHelp; return true;
                case "photo": act = TouringAct.PhotoComposed; return true;
                case "result-slip": act = TouringAct.ReadResultSlip; return true;
                default: act = TouringAct.Arrived; return false;
            }
        }
    }
}
