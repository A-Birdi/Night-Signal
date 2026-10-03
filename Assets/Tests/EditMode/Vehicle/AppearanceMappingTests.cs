using System.Collections.Generic;
using System.Linq;
using NightSignal.Art;
using NightSignal.Content;
using NightSignal.Core.Customization;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// Core customization → the renderer: a car's stock livery must draw exactly the car the body generator draws today (so
    /// the catalogue and the renderer cannot drift apart), and a livery survives the race roster's wire form unchanged.
    /// </summary>
    public sealed class AppearanceMappingTests
    {
        static ContentLibrary Lib => ContentLibrary.Load();

        static IEnumerable<string> CarIds() => ContentLibrary.Load().Customization.Chassis.Select(c => c.Car);

        [Test]
        public void StockLivery_DrawsTheBodyDefinitionsCar([ValueSource(nameof(CarIds))] string carId)
        {
            CarAppearance a = AppearanceMapping.ForLivery(Lib.Customization, carId, "");
            CarBodyDef body = Lib.Body(carId);
            Assert.AreEqual("stock", a.Front);
            Assert.AreEqual("stock", a.RearAero);
            Assert.AreEqual(body.RimFraction, a.RimFraction, 1e-4, "stock rim fraction");
            Assert.AreEqual(0f, a.WheelOffsetM, "stock wheels sit where the physics puts them");
            Assert.That(a.GlassTransmission, Is.GreaterThanOrEqualTo(0.89f), "stock glass keeps the shared material");
            Assert.IsEmpty(a.Decals);
        }

        [Test]
        public void Livery_SurvivesTheRosterWireForm()
        {
            CustomizationCatalogue cat = Lib.Customization;
            ChassisAppearanceDef chassis = cat.Chassis.First();
            var editor = new LiveryEditor(cat, chassis.Car, null);
            Assert.IsTrue(editor.SetVariant("front", chassis.Families.Of("front").Last()).Changed);
            Assert.IsTrue(editor.SetPlateText("NS 24").Changed);
            Assert.IsTrue(editor.AddDecal(cat.DecalShapes.First(s => string.IsNullOrEmpty(s.CosmeticId)).Id, chassis.Decals.Zones.First(), "#C8102E").Changed);
            Assert.IsTrue(editor.SetDecalOpacity(0, 0.5).Changed);
            string json = LiveryJson.ToCanonicalJson(editor.Draft);

            string wire = AppearanceMapping.WireOf(json);
            Assert.IsNotEmpty(wire);
            CarAppearance direct = AppearanceMapping.ForLivery(cat, chassis.Car, json), relayed = AppearanceMapping.ForWire(cat, chassis.Car, wire);
            Assert.IsNotNull(relayed);
            Assert.AreEqual(direct.GeometryKey, relayed.GeometryKey);
            Assert.AreEqual(direct.Primary, relayed.Primary);
            Assert.AreEqual("NS 24", relayed.PlateText);
            Assert.AreEqual(1, relayed.Decals.Count);
            Assert.AreEqual(0.5f, relayed.Decals[0].Opacity, 1e-4);
            Assert.AreEqual(new Color(0xC8 / 255f, 0x10 / 255f, 0x2E / 255f), relayed.Decals[0].Color);
        }

        /// <summary>
        /// A signature swatch's flip tint (the glancing-angle colour shift) reaches the renderer only when the swatch is owned,
        /// and is drawn by the Car Paint shader (Complex Lit with the clear coat kept); every other paint stays on the shared
        /// Complex Lit material.
        /// </summary>
        [Test]
        public void SignatureSwatch_FlipTint_OnlyWhenOwned_OnTheCarPaintShader()
        {
            CustomizationCatalogue cat = Lib.Customization;
            string car = cat.Chassis[0].Car;
            PaintSwatchDef swatch = cat.PaintSwatches.First(s => s.Id == "P-CH30");
            var editor = new LiveryEditor(cat, car, null);
            Assert.IsTrue(editor.ApplySwatch(swatch.Id).Changed);
            string json = LiveryJson.ToCanonicalJson(editor.Draft);
            CarAppearance owned = AppearanceMapping.ForLivery(cat, car, json, CosmeticOwnership.FromIds(new[] { swatch.CosmeticId }));
            CarAppearance notOwned = AppearanceMapping.ForLivery(cat, car, json, CosmeticOwnership.None);
            Assert.IsTrue(owned.FlipTint.HasValue);
            Assert.AreEqual(swatch.FlipTint, "#" + ColorUtility.ToHtmlStringRGB(owned.FlipTint.Value));
            Assert.IsFalse(notOwned.FlipTint.HasValue, "a swatch not owned shows its colour without the flip tint");

            CarMaterialSet mats = Resources.Load<CarMaterialSet>("CarMaterialSet");
            Assert.IsNotNull(mats.PaintFlip, "the Car Paint material ships in the material set (so the shader is in builds)");
            Assert.AreEqual("Night Signal/Car Paint", mats.PaintFlip.shader.name);
            Assert.IsTrue(mats.PaintFlip.shader.isSupported);
            Assert.IsTrue(mats.PaintFlip.IsKeywordEnabled("_CLEARCOAT"), "the clear coat of the shared paint is kept");
            var p = Lib.Params(car, NightSignal.Vehicle.AssistSettings.Default);
            NightSignal.Vehicle.VehicleView flip = NightSignal.Vehicle.VehicleView.Create("FlipCar", p, Lib.Body(car), mats, Color.grey, owned);
            NightSignal.Vehicle.VehicleView plain = NightSignal.Vehicle.VehicleView.Create("PlainCar", p, Lib.Body(car), mats, Color.grey, notOwned);
            try
            {
                Assert.AreEqual("Night Signal/Car Paint", flip.Paint.shader.name);
                Color c = flip.Paint.GetColor("_FlipColor");
                Assert.AreEqual(swatch.FlipTint, "#" + ColorUtility.ToHtmlStringRGB(c));
                Assert.That(c.a, Is.GreaterThan(0.5f), "a pearl turns most of the way at the silhouette");
                Assert.AreEqual(mats.Paint.shader.name, plain.Paint.shader.name, "no flip tint: the shared paint shader");
            }
            finally
            {
                Object.DestroyImmediate(flip.gameObject);
                Object.DestroyImmediate(plain.gameObject);
            }
        }

        [Test]
        public void WireForAnotherCar_ShowsThePaletteColour()
        {
            CustomizationCatalogue cat = Lib.Customization;
            string a = cat.Chassis[0].Car, b = cat.Chassis[1].Car;
            string wire = AppearanceMapping.WireOf(LiveryJson.ToCanonicalJson(new LiveryEditor(cat, a, null).Draft));
            Assert.IsNotNull(AppearanceMapping.ForWire(cat, a, wire));
            Assert.IsNull(AppearanceMapping.ForWire(cat, b, wire));
            Assert.IsNull(AppearanceMapping.ForWire(cat, a, "not a livery"));
            Assert.IsNull(AppearanceMapping.ForWire(cat, a, ""));
        }
    }
}
