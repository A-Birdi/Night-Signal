using System.Collections.Generic;
using System.IO;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Vehicle;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace NightSignal.Editor.Handling
{
    /// <summary>Runs the handling harness for every car model and writes Evidence/handling/harness-report.json.</summary>
    public static class HandlingReportTool
    {
        public const string OutputPath = "Evidence/handling/harness-report.json";

        [MenuItem("Night Signal/Handling/Write Harness Report")]
        public static void WriteReport()
        {
            ContentCatalogue cat = ContentFiles.LoadProjectCatalogue();
            var rows = new List<object>();
            foreach (CarDef car in cat.Cars)
            {
                VehicleParams p = VehicleFactory.Build(car, cat.CarTunings[car.Id], AssistSettings.Default);
                HandlingReport r = HandlingHarness.Measure(p);
                rows.Add(new
                {
                    car = car.Id,
                    name = car.Name,
                    drive = car.Drive,
                    zeroTo100s = Round(r.ZeroTo100Seconds),
                    speedAfter1000mKmh = Round(r.SpeedAfter1000mKmh),
                    brake100To0m = Round(r.Brake100To0Metres),
                    skidpadLateralG = Round(r.SkidpadLateralG),
                    driftHoldSeconds = Round(r.DriftHoldSeconds),
                    driftMeanSlipDeg = Round(r.DriftMeanSlipDeg),
                    driftSpun = r.DriftSpun,
                });
            }
            var report = new
            {
                conditions = HandlingHarness.ConditionsText,
                contentHash = cat.ContentHash,
                unityVersion = Application.unityVersion,
                note = "Measured by the scripted harness driver; not a human playtest.",
                cars = rows,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            File.WriteAllText(OutputPath, JsonConvert.SerializeObject(report, Formatting.Indented) + "\n");
            Debug.Log($"[NightSignal.Handling] Wrote {OutputPath} for {rows.Count} cars.");
        }

        static double Round(float v) => System.Math.Round(v, 2);
    }
}
