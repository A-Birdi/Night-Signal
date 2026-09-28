using System.Collections.Generic;
using NightSignal.Content;
using NightSignal.Core.Content;
using NightSignal.Vehicle;
using NUnit.Framework;
using UnityEngine;

namespace NightSignal.Tests.Vehicle
{
    /// <summary>
    /// A record of the contact-prediction experiment of V-087 (the heavy-contact hitch of V-061 at ~190 ms RTT),
    /// deterministic and offline. A scripted server drives two cars into sustained side contact on flat ground and resolves
    /// the pair both ways each tick, as RaceSimulation does; an emulated client predicts its own car <see cref="Horizon"/>
    /// ticks ahead of the newest snapshot (20 Hz), reconciles and replays as RaceClient does. The same run is measured with
    /// the one-sided predictor RaceClient uses (the remote car extrapolated, never moved — reproduced here) and with a
    /// rejected alternative in which the remote proxies take their share of each contact. Result: the one-sided predictor
    /// stays within a few millimetres of the server on average (a few centimetres at worst); the alternative was no better.
    /// Kept explicit, as the drift tuning sweep is, to rerun the comparison.
    /// </summary>
    [Explicit("contact prediction experiment (V-087)")]
    public sealed class ContactPredictionTests
    {
        /// <summary>Ticks between a snapshot's tick and the tick the client is predicting when it arrives: ~95 ms each way plus the 2-tick input lead.</summary>
        const int Horizon = 14;
        const int SnapshotEvery = 3;
        const int Ticks = 480;
        const int MaxExtrapolationTicks = 15; // as RaceClient.Extrapolate

        static ContentCatalogue catalogue;
        static ContentCatalogue Catalogue => catalogue ?? (catalogue = ContentFiles.LoadProjectCatalogue());
        static VehicleParams Params(string carId) => VehicleFactory.Build(Catalogue.Car(carId), Catalogue.CarTunings[carId], AssistSettings.Default);

        /// <summary>The remote car as the client predicts it: extrapolated from its latest state; optionally taking its share of each contact.</summary>
        sealed class Proxy
        {
            public bool Reacts;
            public VehicleParams Params;
            public VehicleState Latest, State;
            public int LatestTick = -1, Tick = -1;

            public void Rebase()
            {
                State = Latest;
                Tick = LatestTick;
            }

            public void Advance(int tick)
            {
                if (Tick < 0 || Tick > tick) Rebase();
                while (Tick < tick)
                {
                    if (Tick - LatestTick < MaxExtrapolationTicks)
                    {
                        State.Position += State.Velocity * VehicleSimulation.TickDt;
                        State.Rotation = Quaternion.AngleAxis(State.AngularVelocity.y * VehicleSimulation.TickDt * Mathf.Rad2Deg, Vector3.up) * State.Rotation;
                    }
                    Tick++;
                }
            }

            public void Predict(ref VehicleState own, VehicleParams ownParams, int tick, VehicleSimulation sim)
            {
                if (LatestTick < 0) return;
                Advance(tick);
                Vector3 from = own.Position;
                bool touching;
                if (Reacts) touching = VehicleContact.Resolve(ref own, ownParams, ref State, Params, true, true).Touching;
                else
                {
                    VehicleState other = State;
                    touching = VehicleContact.Resolve(ref own, ownParams, ref other, Params, true, false).Touching;
                }
                if (touching) sim.ConstrainToBarriers(ref own, from);
            }

            public void Follow(in VehicleState own, VehicleParams ownParams, int tick)
            {
                Advance(tick);
                if (!Reacts) return;
                VehicleState ours = own;
                VehicleContact.Resolve(ref ours, ownParams, ref State, Params, false, true);
            }
        }

        struct Run
        {
            public int Snapshots, Corrections, ContactTicks;
            public float MeanError, MaxError, MeanVelocityError;
        }

        /// <summary>The scripted inputs: both cars accelerate side by side; A leans into B, later B leans back into A.</summary>
        static DriverInput InputA(int t) => DriverInput.Quantize(t >= 90 && t < 330 ? 0.22f : 0f, 1f, 0f, InputButtons.None);
        static DriverInput InputB(int t) => DriverInput.Quantize(t >= 180 && t < 300 ? -0.18f : 0f, 1f, 0f, InputButtons.None);

        static Run Measure(bool remoteReacts, float gap)
        {
            VehicleParams pa = Params("V01"), pb = Params("V05");
            IVehicleWorld world = PlaneVehicleWorld.Flat;
            var simA = new VehicleSimulation(pa, world);
            var simB = new VehicleSimulation(pb, world);
            var serverA = new VehicleState[Ticks + 1];
            var serverB = new VehicleState[Ticks + 1];
            VehicleState a = VehicleState.AtRest(new Vector3(0f, 0.5f, 0f), Quaternion.identity);
            VehicleState b = VehicleState.AtRest(new Vector3((pa.WidthM + pb.WidthM) * 0.5f + gap, 0.5f, 0f), Quaternion.identity);
            serverA[0] = a;
            serverB[0] = b;
            var run = new Run();
            for (int t = 1; t <= Ticks; t++)
            {
                simA.Step(ref a, InputA(t));
                simB.Step(ref b, InputB(t));
                Vector3 fa = a.Position, fb = b.Position;
                if (VehicleContact.Resolve(ref a, pa, ref b, pb).Touching)
                {
                    run.ContactTicks++;
                    simA.ConstrainToBarriers(ref a, fa);
                    simB.ConstrainToBarriers(ref b, fb);
                }
                serverA[t] = a;
                serverB[t] = b;
            }

            // The client: its own simulation and inputs, the remote car only through snapshots.
            var clientSim = new VehicleSimulation(pa, world);
            var remote = new Proxy { Reacts = remoteReacts, Params = pb };
            var predicted = new VehicleState[Ticks + 1];
            predicted[0] = serverA[0];
            float sumError = 0f, sumVel = 0f;
            for (int t = 1; t <= Ticks; t++)
            {
                int T = t - Horizon;
                if (T > 0 && T % SnapshotEvery == 0)
                {
                    remote.Latest = serverB[T];
                    remote.LatestTick = T;
                    remote.Rebase();
                    float error = Vector3.Distance(predicted[T].Position, serverA[T].Position);
                    float velError = Vector3.Distance(predicted[T].Velocity, serverA[T].Velocity);
                    run.Snapshots++;
                    sumError += error;
                    sumVel += velError;
                    run.MaxError = Mathf.Max(run.MaxError, error);
                    if (error >= 0.03f || velError >= 0.2f)
                    {
                        run.Corrections++;
                        VehicleState s = serverA[T];
                        for (int k = T + 1; k < t; k++)
                        {
                            clientSim.Step(ref s, InputA(k));
                            remote.Predict(ref s, pa, k, clientSim);
                            predicted[k] = s;
                        }
                    }
                    else
                        for (int k = T + 1; k < t; k++) remote.Follow(predicted[k], pa, k);
                }
                VehicleState own = predicted[t - 1];
                clientSim.Step(ref own, InputA(t));
                remote.Predict(ref own, pa, t, clientSim);
                predicted[t] = own;
            }
            run.MeanError = sumError / Mathf.Max(1, run.Snapshots);
            run.MeanVelocityError = sumVel / Mathf.Max(1, run.Snapshots);
            return run;
        }

        static string Describe(Run r) =>
            $"contact ticks {r.ContactTicks}, snapshots {r.Snapshots}, corrections {r.Corrections}, position error mean {r.MeanError:F3} m / max {r.MaxError:F3} m, " +
            $"velocity error mean {r.MeanVelocityError:F2} m/s";

        [Test]
        public void SustainedContact_OneSidedAgainstRemoteProxies()
        {
            var lines = new List<string>();
            foreach (float gap in new[] { 0.05f, 0.3f, 0.8f })
            {
                Run oneSided = Measure(remoteReacts: false, gap);
                Run proxies = Measure(remoteReacts: true, gap);
                Assert.That(oneSided.ContactTicks, Is.GreaterThan(60), "the script must produce sustained contact");
                lines.Add($"gap {gap} m — one-sided (RaceClient): {Describe(oneSided)}; remote proxies: {Describe(proxies)}");
                Assert.That(oneSided.MaxError, Is.LessThan(0.1f), "the one-sided predictor stays within 10 cm of the server");
            }
            foreach (string l in lines)
            {
                TestContext.WriteLine(l);
                Debug.Log("[NightSignal.ContactPrediction] " + l);
            }
        }

        [Test]
        public void NoContact_PredictionIsExact()
        {
            // Far apart: nothing touches, and the client's own prediction equals the server's (same simulation, same inputs).
            Run r = Measure(false, gap: 30f);
            Assert.That(r.ContactTicks, Is.EqualTo(0));
            Assert.That(r.MaxError, Is.LessThan(1e-3f), Describe(r));
            Assert.That(r.Corrections, Is.EqualTo(0));
        }
    }
}
