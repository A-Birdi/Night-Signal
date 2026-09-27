using System.Collections.Generic;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;
using NUnit.Framework;

namespace NightSignal.Tests.Core
{
    /// <summary>
    /// Certified stage benchmarks (authored/stage-benchmarks.json, produced by the PlayMode certification run): every Normal
    /// stage has one, targets follow factor × P with the factor falling 1.18 → 1.05, the featured rival pace is never above
    /// its profile, the shared StageBenchmarks resolves to them (Local and the control plane alike), Hard stays provisional
    /// and labelled so, and the loader refuses broken entries.
    /// </summary>
    public sealed class CertifiedBenchmarkTests
    {
        [Test]
        public void EveryNormalStage_IsCertified_OnTheSpecCurve()
        {
            ContentCatalogue cat = ContentFiles.LoadProjectCatalogue();
            Assert.That(ContentLibrary.Load().Catalogue.ContentHash, Is.EqualTo(cat.ContentHash), "the built content library carries the same documents");
            double previousFactor = double.MaxValue;
            for (int n = 1; n <= Limits.CampaignStages; n++)
            {
                StageDef stage = cat.Stage("S" + n.ToString("00"));
                Assert.That(cat.TryCertifiedBenchmark(stage.Id, CampaignMode.Normal, out CertifiedBenchmark c), Is.True, stage.Id);
                Assert.That(c.Factor, Is.InRange(1.05 - 1e-9, 1.18 + 1e-9), stage.Id);
                Assert.That(c.Factor, Is.LessThanOrEqualTo(previousFactor), "the margin only tightens through the campaign");
                previousFactor = c.Factor;
                Assert.That(c.TargetMs, Is.EqualTo((long)System.Math.Round(c.ReferenceMs * c.Factor)), stage.Id);
                Assert.That(c.FeaturedRivalPace, Is.InRange(0.5, 1.0), stage.Id);

                StageBenchmark b = StageBenchmarks.For(cat, stage, CampaignMode.Normal);
                Assert.That(b.TargetTimeMs, Is.EqualTo(c.TargetMs));
                Assert.That(b.HardTimeoutMs, Is.GreaterThanOrEqualTo(StageOutcome.SupportEnvelopeMs(b.TargetTimeMs, CampaignMode.Normal)));
                Assert.That(StageBenchmarks.IsCertified(cat, stage, CampaignMode.Normal), Is.True);
                StringAssert.StartsWith("certified:", StageBenchmarks.Source(cat, stage, CampaignMode.Normal));

                Assert.That(StageBenchmarks.IsCertified(cat, stage, CampaignMode.Hard), Is.False, "Hard needs its own reference runs");
                StringAssert.StartsWith("provisional", StageBenchmarks.Source(cat, stage, CampaignMode.Hard));
            }
        }

        [Test]
        public void BrokenCertifiedEntries_AreRefused()
        {
            Dictionary<string, string> docs = ContentFiles.ReadFromProject();
            docs["stage-benchmarks.json"] = "{\"schema\":\"night-signal/stage-benchmarks@1\",\"stages\":[{\"stage\":\"S99\",\"mode\":\"normal\",\"referenceMs\":1,\"targetMs\":1,\"featuredRivalPace\":1}]}";
            Assert.Throws<ContentLoadException>(() => ContentCatalogue.Load(docs), "unknown stage");
            docs["stage-benchmarks.json"] = "{\"schema\":\"night-signal/stage-benchmarks@1\",\"stages\":[{\"stage\":\"S01\",\"mode\":\"normal\",\"referenceMs\":1000,\"targetMs\":1180,\"featuredRivalPace\":0}]}";
            Assert.Throws<ContentLoadException>(() => ContentCatalogue.Load(docs), "zero rival pace");
            docs["stage-benchmarks.json"] = "{\"schema\":\"night-signal/stage-benchmarks@1\",\"stages\":[{\"stage\":\"S01\",\"mode\":\"normal\",\"referenceMs\":1000,\"targetMs\":1180,\"featuredRivalPace\":1},{\"stage\":\"S01\",\"mode\":\"normal\",\"referenceMs\":1000,\"targetMs\":1180,\"featuredRivalPace\":1}]}";
            Assert.Throws<ContentLoadException>(() => ContentCatalogue.Load(docs), "duplicate");
        }
    }
}
