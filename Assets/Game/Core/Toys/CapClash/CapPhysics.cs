using System;
using System.Collections.Generic;

namespace NightSignal.Core.Toys.CapClash
{
    /// <summary>
    /// Bounded planar disc physics shared by the authority and the client predictor: constant sliding friction plus light
    /// damping, equal-mass cap-cap impulses with restitution, segment contact against rails/bumpers/rubberized props, and
    /// drop-off wherever a tray edge has no rail. Fixed step (240 Hz); no randomness. The protocol is authoritative state
    /// plus correction, so bit-identical cross-platform floats are NOT assumed.
    /// </summary>
    public static class CapPhysics
    {
        public static void Launch(CapArrangementDef a, CapPhysicsDef ph, CapBody cap, double angleDeg, double power, double launchX)
        {
            double rad = angleDeg * ToyMath.Deg2Rad;
            cap.Pos = new Vec2(launchX, a.LaunchY);
            cap.Vel = new Vec2(Math.Sin(rad), Math.Cos(rad)) * (power * ph.MaxLaunchSpeed);
            cap.Moving = true;
        }

        public static void Step(CapArrangementDef a, CapPhysicsDef ph, List<CapBody> caps, ShotInFlight shot)
        {
            double dt = 1.0 / ph.StepHz;
            double r = ph.CapRadius;

            foreach (CapBody c in caps)
            {
                if (!c.Moving) continue;
                double speed = c.Vel.Length();
                double next = speed - (ph.SlideDeceleration + ph.LinearDamping * speed) * dt;
                if (next <= ph.StopSpeed) { c.Vel = Vec2.Zero; c.Moving = false; continue; }
                c.Vel = c.Vel * (next / speed);
                c.Pos = c.Pos + c.Vel * dt;
            }

            for (int iter = 0; iter < 2; iter++)
            {
                for (int i = 0; i < caps.Count; i++)
                    for (int j = i + 1; j < caps.Count; j++)
                        CollideCaps(ph, caps[i], caps[j], r, shot);
                foreach (CapBody c in caps)
                    foreach (CapSeg s in a.Segments)
                        CollideSegment(c, s, r, shot);
            }

            for (int i = caps.Count - 1; i >= 0; i--)
            {
                Vec2 p = caps[i].Pos;
                if (p.X < -a.Width / 2 || p.X > a.Width / 2 || p.Y < 0 || p.Y > a.Length)
                {
                    if (shot != null && caps[i].Owner == shot.Member) shot.FellOff = true;
                    caps.RemoveAt(i); // fell off: that cap is simply available to shoot again
                }
            }
        }

        static void CollideCaps(CapPhysicsDef ph, CapBody p, CapBody q, double r, ShotInFlight shot)
        {
            Vec2 d = p.Pos - q.Pos;
            double dist2 = d.LengthSquared();
            double min = 2 * r;
            if (dist2 >= min * min) return;
            double dist = Math.Sqrt(dist2);
            Vec2 n = dist > 1e-9 ? d / dist : new Vec2(1, 0);
            double overlap = min - dist;
            p.Pos = p.Pos + n * (overlap * 0.5);
            q.Pos = q.Pos - n * (overlap * 0.5);
            double rel = (p.Vel - q.Vel).Dot(n);
            if (rel < 0)
            {
                double j = -(1 + ph.CapRestitution) * rel * 0.5;
                p.Vel = p.Vel + n * j;
                q.Vel = q.Vel - n * j;
                p.Moving = p.Moving || p.Vel.Length() > ph.StopSpeed;
                q.Moving = q.Moving || q.Vel.Length() > ph.StopSpeed;
                if (shot != null && (p.Owner == shot.Member || q.Owner == shot.Member)) shot.CapContacts++;
            }
        }

        static Vec2 Closest(Vec2 a, Vec2 b, Vec2 p)
        {
            Vec2 ab = b - a;
            double len2 = ab.LengthSquared();
            double t = len2 > 0 ? ToyMath.Clamp((p - a).Dot(ab) / len2, 0, 1) : 0;
            return a + ab * t;
        }

        static void CollideSegment(CapBody c, CapSeg s, double r, ShotInFlight shot)
        {
            Vec2 q = Closest(s.A, s.B, c.Pos);
            Vec2 d = c.Pos - q;
            double dist2 = d.LengthSquared();
            if (dist2 >= r * r) return;
            double dist = Math.Sqrt(dist2);
            Vec2 n = dist > 1e-9 ? d / dist : (s.B - s.A).Perp().Normalized();
            c.Pos = q + n * r;
            double vn = c.Vel.Dot(n);
            if (vn < 0)
            {
                c.Vel = c.Vel - n * ((1 + s.Restitution) * vn);
                if (shot != null && c.Owner == shot.Member) shot.Banked = true;
            }
        }

        /// <summary>
        /// Client-side preview/prediction: simulates one shot on COPIES of the given caps until everything settles (or
        /// <paramref name="maxSeconds"/>) and scores the shooter's cap against the target. The authority's result wins.
        /// </summary>
        public static ShotOutcome Predict(CapArrangementDef a, CapPhysicsDef ph, CapTargetDef target, IEnumerable<CapBody> caps, string member,
                                          double angleDeg, double power, double launchX, double maxSeconds = 8)
        {
            var copy = new List<CapBody>();
            foreach (CapBody c in caps)
                if (c.Owner != member) copy.Add(new CapBody { CapId = c.CapId, Owner = c.Owner, Pos = c.Pos, Vel = c.Vel, Moving = c.Moving });
            var mine = new CapBody { CapId = "cap:" + member, Owner = member };
            copy.Add(mine);
            Launch(a, ph, mine, angleDeg, power, launchX);
            var shot = new ShotInFlight { ShotId = "preview", Member = member, TargetId = target.Id };
            int maxSteps = (int)(maxSeconds * ph.StepHz);
            for (int i = 0; i < maxSteps; i++)
            {
                Step(a, ph, copy, shot);
                bool moving = false;
                foreach (CapBody c in copy) moving |= c.Moving;
                if (!moving) break;
            }
            var outcome = new ShotOutcome { ShotId = "preview", Member = member, TargetId = target.Id, Banked = shot.Banked, CapContacts = shot.CapContacts };
            CapBody final = copy.Find(c => c.Owner == member);
            if (final != null && final.Pos.Y >= a.FoulLineY)
            {
                outcome.OnBoard = true;
                outcome.Distance = (final.Pos - new Vec2(target.X, target.Y)).Length();
                outcome.Points = target.PointsAt(outcome.Distance.Value);
            }
            return outcome;
        }
    }
}
