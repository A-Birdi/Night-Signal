using System;
using System.Collections.Generic;

namespace NightSignal.Art
{
    /// <summary>
    /// Authored silhouette parameters for one car model (Assets/Content/Data/authored/cars.body.json). Overall
    /// dimensions and axle positions come from the chassis tuning so visuals and physics always agree.
    /// Heights are metres above the ground; lengths are metres along the car.
    /// </summary>
    [Serializable]
    public sealed class CarBodyDef
    {
        public string Id;
        /// <summary>hatch | liftback | notchback | fastback | wagon | roadster | midship</summary>
        public string Style = "notchback";
        public float FrontOverhang = 0.85f;
        public float RearOverhang = 0.8f;
        public float NoseHeight = 0.62f;
        public float CowlHeight = 0.9f;
        public float DeckHeight = 0.92f;
        public float TailHeight = 0.86f;
        public float Clearance = 0.13f;
        /// <summary>Distance from the front axle back to the windshield base (negative = ahead of the axle).</summary>
        public float WindshieldBaseFromFrontAxle = 0.35f;
        public float WindshieldRakeDeg = 58f;
        public float RoofLength = 1.1f;
        public float RearWindowRakeDeg = 60f;
        public float RoofTaper = 0.8f;
        public float PlanTaperNose = 0.18f;
        public float PlanTaperTail = 0.1f;
        public float FenderFlare = 0.03f;
        public float Crown = 0.04f;
        public float Tumblehome = 0.08f;
        /// <summary>rect | round | oval | slim | stacked | triangle | wedge</summary>
        public string HeadLamps = "rect";
        /// <summary>divided | oval | twin-slot | bar | wrap | round | block</summary>
        public string TailLamps = "block";
        /// <summary>none | lip | ducktail | blade | wing</summary>
        public string Spoiler = "none";
        public List<string> Features = new List<string>();
        public float WheelRadius = 0.31f;
        public float RimFraction = 0.62f;
        public float TyreWidth = 0.21f;
        /// <summary>5 | 6 | mesh | dish | split</summary>
        public string RimStyle = "5";
    }

    [Serializable]
    public sealed class CarBodyFile
    {
        public string Schema;
        public List<CarBodyDef> Cars = new List<CarBodyDef>();
    }
}
