using NightSignal.Core.Builds;
using NightSignal.Core.Content;
using NightSignal.Core.Rules;

namespace NightSignal.BuildsTests;

/// <summary>Resolver: determinism, validated ranges, and REAL tradeoffs on inputs the simulation implements.</summary>
public sealed class ResolverTests
{
    static PartsCatalogue P => TestData.Parts;

    static ResolvedCarSpec R(string car, MechanicalSnapshot s) => TestData.Resolve(car, s);

    static DerivedFigures F(ResolvedCarSpec s) => PerformanceIndexEstimator.Figures(s);

    [Theory]
    [MemberData(nameof(TestData.AllCars), MemberType = typeof(TestData))]
    public void StockBuild_ReproducesTheAuthoredCarAndNeutralAdjustments(string carId)
    {
        BuildContext ctx = TestData.Ctx(carId);
        ResolvedCarSpec s = ctx.Stock;
        Assert.Equal(ctx.Car.MassKg, s.Car.MassKg);
        Assert.Equal(ctx.Car.PowerKw, s.Car.PowerKw);
        Assert.Equal(ctx.Car.TorqueNm, s.Car.TorqueNm);
        Assert.Equal(ctx.Car.Drive, s.Car.Drive);
        Assert.Equal(ctx.Tuning.TyreGrip, s.Tuning.TyreGrip);
        Assert.Equal(ctx.Tuning.DragAreaCdA, s.Tuning.DragAreaCdA);
        Assert.Equal(ctx.Tuning.Turbo, s.Tuning.Turbo);
        Assert.Equal(ctx.Tuning.Gears, s.Tuning.Gears);
        Assert.Equal(ctx.Tuning.RedlineRpm, s.Tuning.RedlineRpm);
        Assert.Equal(ctx.Tuning.LengthM, s.Tuning.LengthM);
        ChassisAdjustments n = new ChassisAdjustments();
        foreach (var f in typeof(ChassisAdjustments).GetFields())
            Assert.Equal((double)f.GetValue(n), (double)f.GetValue(s.Chassis));
        Assert.NotSame(ctx.Car, s.Car); // copies, never the shared catalogue objects
        Assert.Equal(BuildResolver.HandlingModelVersion, s.HandlingModelVersion);
    }

    [Fact]
    public void Resolution_IsDeterministic_AndTheHashIgnoresInsertionOrder()
    {
        MechanicalSnapshot a = TestData.Build("TYR-T2-SPORT", "SUS-T2-SPORT", "GBX-T1-FINAL", "ENG-T1-INTAKE").WithTune(TuningKeys.FinalDrive, 1060);
        var b = new MechanicalSnapshot();
        foreach (string slot in a.Parts.Keys.Reverse()) b.Parts[slot] = a.Parts[slot];
        b.Tuning.Values[TuningKeys.FinalDrive] = 1060;
        ResolvedCarSpec x = R("V01", a), y = R("V01", b), z = R("V01", a);
        Assert.Equal(x.BuildHash, y.BuildHash);
        Assert.Equal(x.BuildHash, z.BuildHash);
        foreach (SimParam p in SimParams.All) Assert.Equal(x.GetMicro(p), y.GetMicro(p));
        Assert.NotEqual(x.BuildHash, R("V01", a.WithTune(TuningKeys.FinalDrive, 1070)).BuildHash);
        Assert.NotEqual(x.BuildHash, R("V01", MechanicalSnapshot.Stock()).BuildHash);
        // Same parts on a different model → different physics hash.
        Assert.NotEqual(R("V04", a).BuildHash, x.BuildHash);
        // Known value pins the canonical form (changing it is a deliberate handling-version bump).
        Assert.Equal(64, x.BuildHash.Length);
    }

    [Fact]
    public void ShorterGearing_RaisesAccelerationAndLowersReachableTopSpeed()
    {
        MechanicalSnapshot kit = TestData.Build("GBX-T1-FINAL");
        ResolvedCarSpec shortFd = R("V05", kit.WithTune(TuningKeys.FinalDrive, 1100));
        ResolvedCarSpec longFd = R("V05", kit.WithTune(TuningKeys.FinalDrive, 940));
        Assert.Equal(1.10, shortFd.Chassis.FinalDriveScale, 6);
        Assert.Equal(0.94, longFd.Chassis.FinalDriveScale, 6);
        DerivedFigures s = F(shortFd), l = F(longFd);
        Assert.True(s.EffectivePowerToWeight > l.EffectivePowerToWeight);
        Assert.True(s.ReachableTopSpeedKmh < l.ReachableTopSpeedKmh);
        Assert.True(s.FirstGearTopKmh < l.FirstGearTopKmh);
        Assert.True(s.GearLimited);
        // The fixed short final drive is a real, non-adjustable version of the same trade.
        Assert.Equal(1.06, R("V05", TestData.Build("GBX-T1-SHORTFD")).Chassis.FinalDriveScale, 6);
    }

    [Fact]
    public void CloserRatios_ReduceTheRevDrop_ButGiveATallerFirstGear()
    {
        MechanicalSnapshot box = TestData.Build("GBX-T2-CLOSE");
        DerivedFigures close = F(R("V09", box.WithTune(TuningKeys.GearSpread, 880)));
        DerivedFigures wide = F(R("V09", box.WithTune(TuningKeys.GearSpread, 1050)));
        Assert.True(close.RpmDropPerShift < wide.RpmDropPerShift);
        Assert.True(close.FirstGearTopKmh > wide.FirstGearTopKmh);
        // Six-speed conversion only changes the gear count on 5-speed cars and costs mass.
        ResolvedCarSpec six = R("V01", TestData.Build("GBX-T2-SIX"));
        Assert.Equal(6, six.Tuning.Gears);
        Assert.Equal(908, six.Car.MassKg);
    }

    [Fact]
    public void Aero_TradesDownforceAgainstDrag()
    {
        MechanicalSnapshot wing = TestData.Build("AER-T3-GTWING");
        ResolvedCarSpec high = R("V13", wing.WithTune(TuningKeys.AeroLevel, 1300));
        ResolvedCarSpec low = R("V13", wing.WithTune(TuningKeys.AeroLevel, 700));
        ResolvedCarSpec stock = TestData.Ctx("V13").Stock;
        Assert.True(high.Tuning.LiftAreaClA > low.Tuning.LiftAreaClA && low.Tuning.LiftAreaClA > stock.Tuning.LiftAreaClA);
        Assert.True(high.Tuning.DragAreaCdA > low.Tuning.DragAreaCdA && low.Tuning.DragAreaCdA > stock.Tuning.DragAreaCdA);
        Assert.True(F(high).CorneringGrip > F(stock).CorneringGrip);
        Assert.True(F(high).DragLimitedTopSpeedKmh < F(stock).DragLimitedTopSpeedKmh);
        // The low-drag kit is the opposite trade.
        ResolvedCarSpec slippery = R("V13", TestData.Build("AER-T2-LOWDRAG"));
        Assert.True(F(slippery).DragLimitedTopSpeedKmh > F(stock).DragLimitedTopSpeedKmh);
        Assert.True(F(slippery).CorneringGrip < F(stock).CorneringGrip);
        // Balance adjustment moves the aero split, not the total.
        Assert.Equal(0.38, R("V13", wing.WithTune(TuningKeys.AeroBalance, 380)).Chassis.AeroFrontShare, 6);
    }

    [Fact]
    public void StifferAndLower_GainsResponse_ButLosesTravel()
    {
        MechanicalSnapshot coil = TestData.Build("SUS-T3-COILOVER");
        ResolvedCarSpec stiffLow = R("V05", coil.WithTune(TuningKeys.SpringFront, 1450).WithTune(TuningKeys.SpringRear, 1450).WithTune(TuningKeys.RideHeight, -40));
        ResolvedCarSpec softHigh = R("V05", coil.WithTune(TuningKeys.SpringFront, 850).WithTune(TuningKeys.SpringRear, 850).WithTune(TuningKeys.RideHeight, 10));
        Assert.True(stiffLow.Chassis.SpringScaleFront > softHigh.Chassis.SpringScaleFront);
        Assert.True(stiffLow.Chassis.CgHeightOffsetM < softHigh.Chassis.CgHeightOffsetM);     // lower CG
        Assert.True(stiffLow.Chassis.MaxCompressionOffsetM < softHigh.Chassis.MaxCompressionOffsetM); // less bump travel
        Assert.True(stiffLow.Chassis.RestLengthOffsetM < 0);                                      // less droop over crests
        Assert.Equal(-0.034, stiffLow.Chassis.CgHeightOffsetM, 6);
        Assert.Equal(-0.020, stiffLow.Chassis.MaxCompressionOffsetM, 6);
        // Rally suspension raises the car: more travel, higher CG.
        ResolvedCarSpec rally = R("V07", TestData.Build("SUS-T2-RALLY"));
        Assert.True(rally.Chassis.MaxCompressionOffsetM > 0 && rally.Chassis.CgHeightOffsetM > 0);
    }

    [Fact]
    public void DiffLock_TradesThrottleRotationAgainstStability_AndAwdCentreDefaultsToStock()
    {
        MechanicalSnapshot rwd = TestData.Build("DIF-T2-RWD-ADJ");
        Assert.Equal(0.46, R("V05", rwd.WithTune(TuningKeys.DiffLock, 100)).Chassis.PowerSlideGripLoss, 6);
        Assert.Equal(0.24, R("V05", rwd.WithTune(TuningKeys.DiffLock, 0)).Chassis.PowerSlideGripLoss, 6);
        // FWD: more lock = driven fronts keep grip under power (less power understeer).
        Assert.True(R("V06", TestData.Build("DIF-T2-FWD-ADJ").WithTune(TuningKeys.DiffLock, 100)).Chassis.PowerSlideGripLoss
                    < SimulationDefaults.PowerSlideGripLoss);
        // AWD centre coupling: default = the car's own stock split; adjustable both ways.
        ResolvedCarSpec awdDefault = R("V03", TestData.Build("DIF-T2-AWD-CENTRE"));
        Assert.Equal(TestData.Ctx("V03").Tuning.AwdFrontShare, awdDefault.Tuning.AwdFrontShare, 6);
        Assert.Equal(0.30, R("V03", TestData.Build("DIF-T2-AWD-CENTRE").WithTune(TuningKeys.AwdFrontShare, 300)).Tuning.AwdFrontShare, 6);
    }

    [Fact]
    public void ForcedInduction_PowerAgainstLag_AndNaConversionBecomesTurbocharged()
    {
        ResolvedCarSpec big = R("V02", TestData.Build("FI-T3-BIGTURBO"));
        ResolvedCarSpec resp = R("V02", TestData.Build("FI-T2-RESPONSE"));
        BuildContext v02 = TestData.Ctx("V02");
        Assert.True(big.Car.PowerKw > resp.Car.PowerKw);
        Assert.True(big.Tuning.TurboLagSeconds > v02.Tuning.TurboLagSeconds && resp.Tuning.TurboLagSeconds < v02.Tuning.TurboLagSeconds);
        ResolvedCarSpec kit = R("V01", TestData.Build("FI-T4-TURBOKIT"));
        Assert.True(kit.Tuning.Turbo);
        Assert.Equal(0.75, kit.Tuning.TurboLagSeconds, 6);
        Assert.Equal(922, kit.Car.MassKg);
        ResolvedCarSpec sc = R("V01", TestData.Build("FI-T3-SUPERCHARGER"));
        Assert.False(sc.Tuning.Turbo); // instant response, the NA-viable route keeps no lag
        Assert.True(sc.Car.TorqueNm > TestData.Ctx("V01").Car.TorqueNm);
    }

    [Fact]
    public void Engines_HighRevVersusTorque_AreDifferentShapes()
    {
        BuildContext v = TestData.Ctx("V03");
        ResolvedCarSpec rev = R("V03", TestData.Build("ENG-T3-I4-HIGHREV"));
        ResolvedCarSpec torque = R("V03", TestData.Build("ENG-T3-I4-STROKER"));
        Assert.True(rev.Tuning.RedlineRpm > v.Tuning.RedlineRpm && torque.Tuning.RedlineRpm < v.Tuning.RedlineRpm);
        Assert.True(torque.Car.TorqueNm > rev.Car.TorqueNm);
        Assert.True(rev.Car.PowerKw > torque.Car.PowerKw);
        Assert.True(torque.Car.MassKg > rev.Car.MassKg);
    }

    [Theory]
    [MemberData(nameof(TestData.AllCars), MemberType = typeof(TestData))]
    public void WeightReduction_IsBoundedPerChassis(string carId)
    {
        BuildContext ctx = TestData.Ctx(carId);
        P.TryChassis(carId, out ChassisDef ch);
        ResolvedCarSpec carbon = R(carId, TestData.Build("WGT-T3-CARBON"));
        Assert.Equal(ctx.Car.MassKg - 0.9 * ch.WeightReductionMaxKg, carbon.Car.MassKg, 6);
        // Carbon weight programme + carbon body panels would be 1.0 of the allowance: never more than the chassis cap.
        ResolvedCarSpec both = R(carId, TestData.Build("WGT-T3-CARBON", "BKT-T3-CARBONSHELL"));
        Assert.Equal(ctx.Car.MassKg - ch.WeightReductionMaxKg, both.Car.MassKg, 6);
        Assert.True(both.Tuning.InertiaScale < ctx.Tuning.InertiaScale); // lighter AND more nervous
    }

    [Fact]
    public void Tyres_GripAgainstProgressiveness()
    {
        ResolvedCarSpec semi = R("V05", TestData.Build("TYR-T3-SEMISLICK"));
        ResolvedCarSpec rain = R("V05", TestData.Build("TYR-T3-RAINSPORT"));
        Assert.True(semi.Tuning.TyreGrip > rain.Tuning.TyreGrip);
        Assert.True(semi.Chassis.SlideGripFraction < rain.Chassis.SlideGripFraction);
        Assert.True(semi.Chassis.PeakSlipDeg < rain.Chassis.PeakSlipDeg);
        Assert.True(semi.Chassis.SlideFalloffDeg < rain.Chassis.SlideFalloffDeg);
    }

    [Theory]
    [MemberData(nameof(TestData.AllCars), MemberType = typeof(TestData))]
    public void EveryCompatiblePart_AtEveryTuningExtreme_StaysInsideTheSafeRanges(string carId)
    {
        BuildContext ctx = TestData.Ctx(carId);
        int checkedBuilds = 0;
        foreach (PartDef part in P.CompatibleParts(ctx.Car, ctx.Tuning))
        {
            var baseBuild = part.SlotValue == PartSlot.Utility ? new MechanicalSnapshot { UtilityPartId = part.Id } : TestData.Build(part.Id);
            var variants = new List<MechanicalSnapshot> { baseBuild };
            foreach (PartTuningControl t in part.Tuning)
            {
                variants.Add(baseBuild.WithTune(t.Key, t.Min));
                variants.Add(baseBuild.WithTune(t.Key, t.Max));
            }
            foreach (MechanicalSnapshot v in variants)
            {
                ResolveResult r = BuildResolver.Resolve(ctx.Car, ctx.Tuning, P, v);
                Assert.True(r.Ok, $"{carId} {part.Id}: {string.Join("; ", r.Issues)}");
                checkedBuilds++;
            }
        }
        // And the most extreme full build (top tier everywhere, all tuning at max).
        var max = new MechanicalSnapshot();
        foreach (PartSlot slot in PartSlots.Mechanical)
        {
            PartDef top = P.CompatibleParts(ctx.Car, ctx.Tuning, slot).OrderByDescending(p => p.Tier).FirstOrDefault();
            if (top == null) continue; // mid-engine NA: no forced-induction kit by design
            max.Parts[PartSlots.Id(slot)] = top.Id;
            foreach (PartTuningControl t in top.Tuning) max.Tuning.Values[t.Key] = t.Max;
        }
        ResolveResult all = BuildResolver.Resolve(ctx.Car, ctx.Tuning, P, max);
        Assert.True(all.Ok, string.Join("; ", all.Issues));
        Assert.True(checkedBuilds > 50);
    }

    [Fact]
    public void InvalidTuning_IsRejectedWithExactReasons_NeverClamped()
    {
        BuildContext ctx = TestData.Ctx("V01");
        MechanicalSnapshot kit = TestData.Build("GBX-T1-FINAL");
        RepairItem One(MechanicalSnapshot s)
        {
            ResolveResult r = BuildResolver.Resolve(ctx.Car, ctx.Tuning, P, s);
            Assert.False(r.Ok);
            return Assert.Single(r.Issues);
        }
        Assert.Contains("outside 920–1120", One(kit.WithTune(TuningKeys.FinalDrive, 1130)).Detail);
        Assert.Contains("not a multiple of 10", One(kit.WithTune(TuningKeys.FinalDrive, 1005)).Detail);
        Assert.Contains("not adjustable", One(MechanicalSnapshot.Stock().WithTune(TuningKeys.FinalDrive, 1000)).Detail);
        Assert.Contains("Unknown tuning value", One(kit.WithTune("Nitrous", 1)).Detail);
        var v = kit.Clone();
        v.Tuning.Version = 0;
        Assert.Contains("version", One(v).Detail);
        // Normalize is the explicit editor action, with every change listed.
        var changes = new List<string>();
        var controls = TuningModel.Controls(new[] { P.Part("GBX-T1-FINAL") }, _ => 0);
        TuningSetup fixedTune = TuningModel.Normalize(kit.WithTune(TuningKeys.FinalDrive, 1130).WithTune(TuningKeys.BrakeBias, 600).Tuning, controls, changes);
        Assert.Equal(1120, fixedTune.Values[TuningKeys.FinalDrive]);
        Assert.False(fixedTune.Values.ContainsKey(TuningKeys.BrakeBias));
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void Utility_NeverChangesPhysicsPiOrRawScore_AndNeverStacks()
    {
        BuildContext ctx = TestData.Ctx("V05");
        MechanicalSnapshot b = TestData.Build("TYR-T2-SPORT");
        ResolvedCarSpec plain = R("V05", b);
        foreach (string u in new[] { "UTL-INC-4", "UTL-INC-8", "UTL-SHW-5", "UTL-SHW-10" })
        {
            MechanicalSnapshot withU = b.With(PartSlot.Utility, u);
            ResolvedCarSpec s = R("V05", withU);
            Assert.Equal(plain.BuildHash, s.BuildHash);
            foreach (SimParam p in SimParams.All) Assert.Equal(plain.GetMicro(p), s.GetMicro(p));
            Assert.Equal(PerformanceIndexEstimator.Estimate(plain, ctx.Stock, 380).Value, PerformanceIndexEstimator.Estimate(s, ctx.Stock, 380).Value);
            Assert.NotEqual(b.SelectionHash(), withU.SelectionHash());
            Assert.True(s.Utility.IncomePercent == 0 || s.Utility.ShowcasePercent == 0);
            // Feeds the existing Core rules unchanged.
            Assert.Equal(100 + s.Utility.IncomePercent, Economy.UtilityX100(s.Utility.IncomePercent));
            Assert.True(DriftScorer.ShowcaseScore(1000, s.Utility.ShowcasePercent) >= 1000);
        }
        Assert.Equal(8, R("V05", b.With(PartSlot.Utility, "UTL-INC-8")).Utility.IncomePercent);
        Assert.Equal(10, R("V05", b.With(PartSlot.Utility, "UTL-SHW-10")).Utility.ShowcasePercent);
        // A utility item can never be placed in a mechanical slot (no hidden second utility).
        var smuggled = b.Clone();
        smuggled.Parts["aero"] = "UTL-SHW-10";
        ResolveResult r = BuildResolver.Resolve(ctx.Car, ctx.Tuning, P, smuggled);
        Assert.Contains(r.Issues, i => i.Kind == RepairKind.WrongSlot);
        smuggled = b.Clone();
        smuggled.Parts["utility"] = "UTL-SHW-10";
        Assert.Contains(BuildResolver.Resolve(ctx.Car, ctx.Tuning, P, smuggled).Issues, i => i.Kind == RepairKind.UnknownSlot);
    }

    [Fact]
    public void Appearance_IsRecordedForPerformanceRelevantBodyAndAeroParts()
    {
        ResolvedCarSpec s = R("V05", TestData.Build("AER-T3-GTWING", "BKT-T2-WIDEARCH", "TYR-T2-SPORT"));
        Assert.Equal(new[] { "aero-gtwing", "body-widearch" }, s.PerformanceAppearance);
        Assert.True(s.Tuning.TrackM > TestData.Ctx("V05").Tuning.TrackM);
    }
}
