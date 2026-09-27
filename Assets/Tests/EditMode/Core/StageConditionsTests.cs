using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NightSignal.Race;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    /// <summary>
    /// Stage conditions (authored/stage-conditions.json, in the content hash): every stage side has a time of day and a
    /// dry/damp/wet surface; a campaign race uses its side's conditions (Hard sides their own damp/wet/night variations),
    /// anything else the course's defaults.
    /// </summary>
    public sealed class StageConditionsTests
    {
        [Test]
        public void EverySide_HasConditions_AndCampaignRacesUseThem()
        {
            ContentCatalogue cat = ContentFiles.LoadProjectCatalogue();
            int hardWet = 0, hardDamp = 0;
            for (int n = 1; n <= Limits.CampaignStages; n++)
            {
                string id = "S" + n.ToString("00");
                foreach (CampaignMode mode in new[] { CampaignMode.Normal, CampaignMode.Hard })
                {
                    StageConditions c = cat.Conditions(id, mode);
                    Assert.That(c, Is.Not.Null, $"{id} {mode}");
                    Assert.That(new[] { "dry", "damp", "wet" }, Does.Contain(c.Surface));
                    Assert.That(c.TimeOfDay, Is.Not.Empty);
                    Assert.That(RaceConditions.Surface(cat, "campaign", id, mode, null), Is.EqualTo(c.Surface));
                    Assert.That(RaceConditions.TimeOfDay(cat, "campaign", id, mode, null), Is.EqualTo(c.TimeOfDay));
                }
                if (cat.Conditions(id, CampaignMode.Hard).Surface == "wet") hardWet++;
                if (cat.Conditions(id, CampaignMode.Hard).Surface == "damp") hardDamp++;
            }
            Assert.That(hardWet, Is.EqualTo(6));
            Assert.That(hardDamp, Is.EqualTo(12));
            Assert.That(cat.Conditions("S14", CampaignMode.Hard).Surface, Is.EqualTo("wet"), "Rain Thread");
            Assert.That(cat.Conditions("S09", CampaignMode.Normal).Surface, Is.EqualTo("damp"));
            Assert.That(RaceConditions.Surface(cat, "freeplay", "S14", CampaignMode.Hard, null), Is.EqualTo("dry"), "freeplay: the course's surface");
        }
    }
}
