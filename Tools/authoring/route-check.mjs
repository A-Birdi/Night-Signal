#!/usr/bin/env node
// Route source checker for Assets/Content/Courses/<ID>/route.json (schema night-signal/route@1).
//
// The sampler below is a faithful JavaScript port of Assets/Game/Runtime/Track/RouteSampler.cs (centripetal
// Catmull-Rom, 48 dense steps per segment, mirrored end-point extrapolation for open routes, smoothstep attribute
// interpolation, 1 m arc-length resampling, frames, bank sign and curvature from tangents +/-3 samples) and of
// RouteIO.Measure. It runs in double precision; the C# runs in float, so values agree to within centimetres.
//
// Usage: node Tools/authoring/route-check.mjs [--quiet] [--no-overlap] [--only C05,C06]
//   Checks every Assets/Content/Courses/*/route.json against docs/COURSES.md and the Appendix A targets
//   (Assets/Content/Data/generated/courses.json; provisional targets for the Addendum 01 Freeplay courses): length
//   +/-5%, net elevation +/-3 m (0 for loops), max grade over 10 m windows, min radius and where, min width, the
//   twelve-slot grid zone, sector order, gate/landmark/section/area validity, the Appendix E challenge gates each
//   course must carry, the S29 contract layout on C24, plan-view self-intersection (with declared tunnel/bridge
//   crossings and their vertical clearance), loop closure, and a copy/reuse heuristic across courses. Also re-checks
//   the sampler port against the C# reference stats of C01 revision 1. Writes Evidence/courses/route-stats.json and
//   prints a table; exit code 1 if anything fails (--only runs without writing the evidence file).
//
// The module also exports sampleRoute/measure so authoring scripts can reuse the exact same sampler.

import { readFileSync, writeFileSync, readdirSync, existsSync, mkdirSync } from "node:fs";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const COURSES_DIR = join(ROOT, "Assets", "Content", "Courses");
const CATALOGUE = join(ROOT, "Assets", "Content", "Data", "generated", "courses.json");
const OUT = join(ROOT, "Evidence", "courses", "route-stats.json");

export const SCHEMA = "night-signal/route@1";
export const DENSE_STEPS_PER_SEGMENT = 48;

// ---------------------------------------------------------------------------------------------- vector helpers
const V = (x, y, z) => ({ x, y, z });
const add = (a, b) => V(a.x + b.x, a.y + b.y, a.z + b.z);
const sub = (a, b) => V(a.x - b.x, a.y - b.y, a.z - b.z);
const mul = (a, s) => V(a.x * s, a.y * s, a.z * s);
const dot = (a, b) => a.x * b.x + a.y * b.y + a.z * b.z;
const cross = (a, b) => V(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
const mag = (a) => Math.sqrt(dot(a, a));
const dist = (a, b) => mag(sub(a, b));
const lerpV = (a, b, t) => V(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
const lerp = (a, b, t) => a + (b - a) * t;
const clamp01 = (t) => (t < 0 ? 0 : t > 1 ? 1 : t);
// Unity Vector3.normalized: zero vector below 1e-5 magnitude.
const norm = (a) => {
  const m = mag(a);
  return m > 1e-5 ? mul(a, 1 / m) : V(0, 0, 0);
};
// Unity Mathf.SmoothStep(0, 1, t).
const smoothStep01 = (t) => {
  t = clamp01(t);
  return -2 * t * t * t + 3 * t * t;
};
// Unity Vector3.Angle / SignedAngle (degrees).
function angleDeg(a, b) {
  const den = Math.sqrt(dot(a, a) * dot(b, b));
  if (den < 1e-15) return 0;
  let d = dot(a, b) / den;
  d = d < -1 ? -1 : d > 1 ? 1 : d;
  return (Math.acos(d) * 180) / Math.PI;
}
function signedAngleDeg(a, b, axis) {
  const u = angleDeg(a, b);
  const s = dot(axis, cross(a, b));
  return u * (s >= 0 ? 1 : -1); // Mathf.Sign(0) == 1
}
// Quaternion.AngleAxis(deg, axis) * v (component maths are the same as the right-handed Rodrigues formula).
function rotateAround(v, axis, deg) {
  const k = norm(axis);
  const th = (deg * Math.PI) / 180;
  const c = Math.cos(th), s = Math.sin(th);
  return add(add(mul(v, c), mul(cross(k, v), s)), mul(k, dot(k, v) * (1 - c)));
}

// ---------------------------------------------------------------------------------------------- sampler port
const cpPos = (cp) => V(cp.p[0], cp.p[1], cp.p[2]);

function point(cps, i, loop) {
  const n = cps.length;
  if (loop) return cpPos(cps[((i % n) + n) % n]);
  if (i < 0) return sub(mul(cpPos(cps[0]), 2), cpPos(cps[1]));
  if (i >= n) return sub(mul(cpPos(cps[n - 1]), 2), cpPos(cps[n - 2]));
  return cpPos(cps[i]);
}

export function centripetalCatmullRom(p0, p1, p2, p3, t) {
  const t0 = 0;
  const t1 = t0 + Math.pow(dist(p0, p1), 0.5) + 1e-4;
  const t2 = t1 + Math.pow(dist(p1, p2), 0.5) + 1e-4;
  const t3 = t2 + Math.pow(dist(p2, p3), 0.5) + 1e-4;
  const tt = lerp(t1, t2, t);
  const a1 = add(mul(p0, (t1 - tt) / (t1 - t0)), mul(p1, (tt - t0) / (t1 - t0)));
  const a2 = add(mul(p1, (t2 - tt) / (t2 - t1)), mul(p2, (tt - t1) / (t2 - t1)));
  const a3 = add(mul(p2, (t3 - tt) / (t3 - t2)), mul(p3, (tt - t2) / (t3 - t2)));
  const b1 = add(mul(a1, (t2 - tt) / (t2 - t0)), mul(a2, (tt - t0) / (t2 - t0)));
  const b2 = add(mul(a2, (t3 - tt) / (t3 - t1)), mul(a3, (tt - t1) / (t3 - t1)));
  return add(mul(b1, (t2 - tt) / (t2 - t1)), mul(b2, (tt - t1) / (t2 - t1)));
}

/**
 * Port of RouteSampler.Sample. Returns { samples, total, cpDistances } where samples[i] =
 * { pos, distance, width, bank, shoulderLeft, shoulderRight, tangent, right, curvature }.
 */
export function sampleRoute(route, spacing = 1) {
  const cps = route.controlPoints;
  if (!cps || cps.length < 3) throw new Error("A route needs at least three control points");
  const loop = !!route.closedLoop;
  const dense = [];
  const attr = []; // [width, bank, shoulderL, shoulderR]
  const segments = loop ? cps.length : cps.length - 1;
  for (let seg = 0; seg < segments; seg++) {
    const p0 = point(cps, seg - 1, loop), p1 = point(cps, seg, loop), p2 = point(cps, seg + 1, loop), p3 = point(cps, seg + 2, loop);
    const a = cps[seg % cps.length], b = cps[(seg + 1) % cps.length];
    for (let k = 0; k < DENSE_STEPS_PER_SEGMENT; k++) {
      const t = k / DENSE_STEPS_PER_SEGMENT;
      dense.push(centripetalCatmullRom(p0, p1, p2, p3, t));
      const s = smoothStep01(t);
      attr.push([lerp(a.width, b.width, s), lerp(a.bank, b.bank, s), lerp(a.shoulderLeft, b.shoulderLeft, s), lerp(a.shoulderRight, b.shoulderRight, s)]);
    }
  }
  const last = loop ? cps[0] : cps[cps.length - 1];
  dense.push(cpPos(last));
  attr.push([last.width, last.bank, last.shoulderLeft, last.shoulderRight]);

  const cum = new Float64Array(dense.length);
  for (let i = 1; i < dense.length; i++) cum[i] = cum[i - 1] + dist(dense[i - 1], dense[i]);
  const total = cum[cum.length - 1];

  const count = Math.floor(total / spacing) + 1;
  const samples = new Array(count);
  let j = 0;
  for (let i = 0; i < count; i++) {
    const d = Math.min(i * spacing, total);
    while (j < cum.length - 2 && cum[j + 1] < d) j++;
    const segLen = Math.max(1e-5, cum[j + 1] - cum[j]);
    const u = clamp01((d - cum[j]) / segLen);
    const A = attr[j], B = attr[j + 1];
    samples[i] = {
      pos: lerpV(dense[j], dense[j + 1], u),
      distance: d,
      width: lerp(A[0], B[0], u),
      bank: lerp(A[1], B[1], u),
      shoulderLeft: lerp(A[2], B[2], u),
      shoulderRight: lerp(A[3], B[3], u),
    };
  }

  const up = V(0, 1, 0);
  for (let i = 0; i < count; i++) {
    let prev = samples[Math.max(0, i - 1)].pos;
    let next = samples[Math.min(count - 1, i + 1)].pos;
    if (loop && (i === 0 || i === count - 1)) {
      prev = samples[i === 0 ? count - 2 : i - 1].pos;
      next = samples[i === count - 1 ? 1 : i + 1].pos;
    }
    const tangent = norm(sub(next, prev));
    const flatRight = norm(cross(up, tangent));
    const right = rotateAround(flatRight, tangent, -samples[i].bank);
    samples[i].tangent = tangent;
    samples[i].right = norm(right);
  }
  for (let i = 0; i < count; i++) {
    const ia = Math.max(0, i - 3), ib = Math.min(count - 1, i + 3);
    const t0 = samples[ia].tangent, t1 = samples[ib].tangent;
    const ds = samples[ib].distance - samples[ia].distance;
    const ang = (signedAngleDeg(V(t0.x, 0, t0.z), V(t1.x, 0, t1.z), up) * Math.PI) / 180;
    samples[i].curvature = ds > 0 ? ang / ds : 0;
  }

  // Distance of every control point along the dense polyline (authoring aid, not part of the C# output).
  const cpDistances = cps.map((_, i) => (i < segments ? cum[i * DENSE_STEPS_PER_SEGMENT] : total));
  return { samples, total, cpDistances };
}

/** Port of RouteIO.Measure plus where the extremes occur. */
export function measure(s) {
  const st = {
    lengthMetres: s[s.length - 1].distance,
    netElevationMetres: s[s.length - 1].pos.y - s[0].pos.y,
    minRadiusMetres: Infinity,
    minRadiusAt: 0,
    maxGradePercent: 0,
    maxGradeAt: 0,
    minWidthMetres: Infinity,
    minWidthAt: 0,
  };
  for (let i = 0; i < s.length; i++) {
    const k = Math.abs(s[i].curvature);
    if (k > 1e-5 && 1 / k < st.minRadiusMetres) { st.minRadiusMetres = 1 / k; st.minRadiusAt = s[i].distance; }
    if (s[i].width < st.minWidthMetres) { st.minWidthMetres = s[i].width; st.minWidthAt = s[i].distance; }
    if (i >= 10) {
      const rise = s[i].pos.y - s[i - 10].pos.y;
      const run = s[i].distance - s[i - 10].distance;
      const g = Math.abs(rise / run) * 100;
      if (g > st.maxGradePercent) { st.maxGradePercent = g; st.maxGradeAt = s[i].distance; }
    }
  }
  return st;
}

// ---------------------------------------------------------------------------------------------- contract tables
export const BIOMES = ["mizuhana-foothills", "kasumi-forest", "kurogawa-reservoir", "akebono-coast", "hoshimi-uplands", "tsukishiro-highland", "amanagi-finale", "hinode-campus"];
export const TIMES = ["day", "late-afternoon", "sunset", "dusk", "blue-hour", "evening", "night", "pre-dawn", "dawn", "first-light"];
export const SURFACES = ["dry", "damp", "wet"];
export const GATE_KINDS = ["apex", "precision", "drift-zone", "clip-zone", "transition-zone", "brake-zone", "exit-speed", "lane", "overtake-zone", "defence", "demo-zone", "contract", "timing"];
export const SECTION_KINDS = ["tunnel", "viaduct", "bridge"];
export const AREA_KINDS = ["skid-pad", "braking-lane", "training-bay", "apron", "recovery-bay"];
export const KITS = {
  "tea-shed": null,
  "stone-bridge": null,
  "lantern-row": null,
  structure: { type: ["workshop", "shed", "shelter", "station", "pavilion", "office", "hut", "machine-hall", "market", "terminal", "kiosk", "house", "shrine-roof", "pump-house", "switchyard", "cabin", "gallery"], roof: ["gable", "hip", "flat", "curved", "sawtooth"], material: ["timber", "concrete", "brick", "steel", "stone"], num: ["width", "depth", "height"] },
  tower: { type: ["water-tank", "mast", "lighthouse", "radio-dish", "chimney", "cooling-tower", "beacon", "pylon-line", "crane", "spillway-tower", "observatory", "transmitter", "antenna"], num: ["height", "count"] },
  crossing: { type: ["steel-truss", "lattice", "covered-footbridge", "overpass", "two-level", "maintenance-bridge", "pipeline-arch", "relay-arch", "rail-trestle"], num: ["span"] },
  wall: { type: ["retaining", "stone-stair", "split-retaining", "quarry-steps", "snow-fence", "sea-wall", "breakwater", "flood-marks", "avalanche-gallery"], num: ["length", "height"] },
  water: { type: ["canal", "reservoir", "sea", "creek", "waterfall", "spillway", "inlet"], num: ["extent"] },
  field: { type: ["tea-terraces", "greenhouses", "orchard", "cedar-grove", "pine-grove", "white-pine-grove", "rice-terraces"], num: ["extent"] },
  rail: { type: ["trestle", "funicular", "coast-railway", "conveyor", "cable-station"], num: ["length"] },
  sign: { type: ["flood-marker", "memorial", "placard", "marshal-beacons", "timing-board", "mural", "tunnel-marker"], num: ["count"] },
  gate: { type: ["cedar-gate", "storm-gate", "relay-arch", "maintenance-gate"], num: [] },
};

// Appendix E challenge gates each course must carry: [course, challenge, kind, minimum count, where].
// where = "route" (gates[]), "area" (areas[].gates[] on T00), "overlay" (a gates.overlay.json beside route.json)
// or "route+overlay" (either).
export const REQUIRED_GATES = [
  ["T00", "CH02", "brake-zone", 1, "area"],
  ["T00", "CH07", "brake-zone", 1, "route"],
  ["T00", "CH16", "drift-zone", 2, "route"],
  ["T00", "CH23", "transition-zone", 4, "route"],
  ["T00", "CH37", "lane", 2, "route"],
  ["T00", "CH46", "timing", 2, "route"],
  ["T00", "CH47", "brake-zone", 1, "area"],
  ["T00", "CH49", "timing", 3, "route"],
  ["T00", "CH52", "timing", 4, "route"],
  ["T00", "CH58", "timing", 6, "route"],
  ["C01", "CH20", "drift-zone", 3, "route+overlay"],
  ["C02", "CH04", "exit-speed", 3, "route"],
  ["C02", "CH34", "overtake-zone", 1, "route"],
  ["C03", "CH03", "apex", 3, "route"],
  ["C03", "CH17", "transition-zone", 3, "route"],
  ["C03", "CH53", "apex", 2, "route"],
  ["C03", "CH53", "exit-speed", 2, "route"],
  ["C04", "CH18", "drift-zone", 2, "route"],
  ["C05", "CH06", "precision", 6, "route"],
  ["C05", "CH19", "demo-zone", 1, "route"],
  ["C08", "CH08", "brake-zone", 2, "route"],
  ["C08", "CH21", "drift-zone", 2, "route"],
  ["C09", "CH22", "clip-zone", 3, "route"],
  ["C10", "CH36", "lane", 1, "route"],
  ["C11", "CH10", "timing", 1, "route"],
  ["C12", "CH26", "drift-zone", 2, "route"],
  ["C13", "CH09", "lane", 2, "route"],
  ["C14", "CH39", "defence", 1, "route"],
  ["C15", "CH24", "drift-zone", 3, "route"],
  ["C16", "CH25", "drift-zone", 3, "route"],
  ["C17", "CH40", "overtake-zone", 1, "route"],
  ["C19", "CH27", "transition-zone", 6, "route"],
  ["C20", "CH12", "brake-zone", 4, "route"],
  ["C20", "CH43", "defence", 2, "route"],
  ["C21", "CH13", "lane", 1, "route"],
  ["C23", "CH28", "drift-zone", 2, "route"],
  ["C24", "CH29", "drift-zone", 3, "route"],
  ["C25", "CH15", "apex", 3, "route"],
  ["C25", "CH30", "drift-zone", 5, "route"],
];

const MIN_SEPARATION = 12; // plan distance between route parts...
const MIN_ALONG = 60; // ...that are further apart than this along the route
const MIN_CLEARANCE = 7; // vertical clearance at declared tunnel/bridge crossings
const MAX_GRADE = 12;
const MIN_RADIUS = 12;
const MIN_WIDTH = 6;
const GRID_SLOTS = 12; // Addendum 01: up to 12 vehicles
const GRID_MIN_START = 62;
const GRID_ZONE_BEHIND = 57; // last slot ~54.5 m behind the line plus half a car

// ---------------------------------------------------------------------------------------------- helpers
function sampleAt(samples, d, loop, total) {
  if (loop) d = ((d % total) + total) % total;
  const f = Math.max(0, Math.min(d, samples[samples.length - 1].distance));
  const i = Math.min(Math.floor(f), samples.length - 2);
  const u = f - samples[i].distance;
  const a = samples[i], b = samples[i + 1];
  return { ...a, pos: lerpV(a.pos, b.pos, u), width: lerp(a.width, b.width, u), shoulderLeft: lerp(a.shoulderLeft, b.shoulderLeft, u), shoulderRight: lerp(a.shoulderRight, b.shoulderRight, u), distance: f };
}

function inRanges(d, ranges) {
  for (const r of ranges) if (d >= r.fromMetres && d <= r.toMetres) return true;
  return false;
}

/** Plan-view proximity of non-adjacent route parts, using a 12 m spatial hash over the 1 m samples. */
function separation(samples, loop, total, sections) {
  const cell = 16;
  const grid = new Map();
  const key = (x, z) => `${x},${z}`;
  for (let i = 0; i < samples.length; i++) {
    const p = samples[i].pos;
    const k = key(Math.floor(p.x / cell), Math.floor(p.z / cell));
    if (!grid.has(k)) grid.set(k, []);
    grid.get(k).push(i);
  }
  const structural = sections.filter((s) => SECTION_KINDS.includes(s.kind));
  let minSep = Infinity, minSepAt = null;
  const violations = [];
  const crossings = [];
  const REPORT = 40; // report the closest non-adjacent approach up to this distance
  for (let i = 0; i < samples.length; i++) {
    const p = samples[i].pos;
    const cx = Math.floor(p.x / cell), cz = Math.floor(p.z / cell);
    for (let dx = -3; dx <= 3; dx++)
      for (let dz = -3; dz <= 3; dz++) {
        const list = grid.get(key(cx + dx, cz + dz));
        if (!list) continue;
        for (const j of list) {
          if (j <= i) continue;
          let along = Math.abs(samples[j].distance - samples[i].distance);
          if (loop) along = Math.min(along, total - along);
          if (along <= MIN_ALONG) continue;
          const q = samples[j].pos;
          const d2 = (p.x - q.x) ** 2 + (p.z - q.z) ** 2;
          if (d2 > REPORT * REPORT) continue;
          const d = Math.sqrt(d2);
          const dy = Math.abs(p.y - q.y);
          const declared = inRanges(samples[i].distance, structural) || inRanges(samples[j].distance, structural);
          if (declared && dy >= MIN_CLEARANCE) {
            if (d < MIN_SEPARATION) crossings.push({ a: samples[i].distance, b: samples[j].distance, plan: d, clearance: dy });
            continue;
          }
          if (d < minSep) { minSep = d; minSepAt = [Math.round(samples[i].distance), Math.round(samples[j].distance)]; }
          if (d < MIN_SEPARATION) violations.push({ a: samples[i].distance, b: samples[j].distance, plan: d, vertical: dy, declared });
        }
      }
  }
  // Collapse crossings into distinct events (one per ~30 m).
  crossings.sort((u, w) => u.a - w.a);
  const events = [];
  for (const c of crossings) {
    const e = events.find((ev) => Math.abs(ev.a - c.a) < 30 && Math.abs(ev.b - c.b) < 30);
    if (e) { if (c.plan < e.plan) Object.assign(e, { plan: c.plan }); e.clearance = Math.min(e.clearance, c.clearance); }
    else events.push({ ...c });
  }
  return { minSep, minSepAt, violations, crossings: events.map((e) => ({ a: Math.round(e.a), b: Math.round(e.b), clearance: +e.clearance.toFixed(1) })) };
}

function rectCorners(c, size, headingDeg) {
  const h = (headingDeg * Math.PI) / 180;
  const f = { x: Math.sin(h), z: Math.cos(h) }; // heading 0 = +z (north), 90 = +x (east)
  const r = { x: Math.cos(h), z: -Math.sin(h) };
  const hw = size[0] / 2, hl = size[1] / 2;
  return [[-1, -1], [1, -1], [1, 1], [-1, 1]].map(([a, b]) => ({ x: c[0] + r.x * a * hw + f.x * b * hl, z: c[2] + r.z * a * hw + f.z * b * hl }));
}
function rectsOverlap(A, B) {
  const axes = [];
  for (const P of [A, B]) for (let i = 0; i < 4; i++) { const p = P[i], q = P[(i + 1) % 4]; axes.push({ x: q.z - p.z, z: p.x - q.x }); }
  for (const ax of axes) {
    const pa = A.map((p) => p.x * ax.x + p.z * ax.z), pb = B.map((p) => p.x * ax.x + p.z * ax.z);
    if (Math.max(...pa) < Math.min(...pb) || Math.max(...pb) < Math.min(...pa)) return false;
  }
  return true;
}
function pointInRect(p, R) {
  let sign = 0;
  for (let i = 0; i < 4; i++) {
    const a = R[i], b = R[(i + 1) % 4];
    const c = (b.x - a.x) * (p.z - a.z) - (b.z - a.z) * (p.x - a.x);
    if (c !== 0) { if (sign === 0) sign = Math.sign(c); else if (Math.sign(c) !== sign) return false; }
  }
  return true;
}

function checkGate(g, where, lengthLimit, errors, sampleFn) {
  const tag = `gate ${g.id}`;
  if (!g.id) errors.push(`${where}: gate without id`);
  if (!GATE_KINDS.includes(g.kind)) errors.push(`${tag}: unknown kind '${g.kind}'`);
  if (!(g.startMetres >= 0 && g.endMetres <= lengthLimit)) errors.push(`${tag}: ${g.startMetres}..${g.endMetres} outside 0..${lengthLimit.toFixed(0)}`);
  if (g.endMetres < g.startMetres) errors.push(`${tag}: endMetres before startMetres`);
  if ((g.kind === "apex" || g.kind === "precision") && g.startMetres !== g.endMetres) errors.push(`${tag}: apex/precision gates need startMetres == endMetres`);
  const lineKinds = ["apex", "precision", "timing", "exit-speed"]; // may be a single line across the road
  if (!lineKinds.includes(g.kind) && g.endMetres - g.startMetres < 5) errors.push(`${tag}: zone shorter than 5 m`);
  if (!(g.lineTolerance > 0)) errors.push(`${tag}: lineTolerance must be > 0`);
  if (!(g.targetSpeedKmh >= 0)) errors.push(`${tag}: targetSpeedKmh must be >= 0`);
  if (sampleFn && g.kind !== "contract" && g.kind !== "timing") {
    const s = sampleFn((g.startMetres + g.endMetres) / 2);
    if (Math.abs(g.lineOffset) > s.width / 2) errors.push(`${tag}: lineOffset ${g.lineOffset} beyond the paved half-width ${(s.width / 2).toFixed(1)}`);
  }
}

// ---------------------------------------------------------------------------------------------- course check
export function checkCourse(route, meta, overlay) {
  const errors = [];
  const warnings = [];
  const loop = !!route.closedLoop;
  if (route.schema !== SCHEMA) errors.push(`schema must be ${SCHEMA}`);
  if (meta && route.course !== meta.id) errors.push(`course id ${route.course} != catalogue ${meta.id}`);
  if (!Number.isInteger(route.revision) || route.revision < 1) errors.push("revision must be a positive integer");
  if (!BIOMES.includes(route.biome)) errors.push(`unknown biome '${route.biome}'`);
  if (route.timeOfDay !== undefined && !TIMES.includes(route.timeOfDay)) errors.push(`unknown timeOfDay '${route.timeOfDay}'`);
  if (route.surface !== undefined && !SURFACES.includes(route.surface)) errors.push(`unknown surface '${route.surface}'`);
  if (meta && route.course !== "C01" && (route.timeOfDay === undefined || route.surface === undefined)) errors.push("timeOfDay/surface missing");
  if (meta) {
    const wantLoop = meta.format === "circuit" || meta.format === "training";
    if (wantLoop !== loop) errors.push(`closedLoop ${loop} does not match format '${meta.format}'`);
  }
  const ids = new Set();
  route.controlPoints.forEach((cp, i) => {
    const want = `${route.course.toLowerCase()}-p${String(i).padStart(2, "0")}`;
    if (cp.id !== want) errors.push(`control point ${i} id '${cp.id}' (expected ${want})`);
    if (ids.has(cp.id)) errors.push(`duplicate control point id ${cp.id}`);
    ids.add(cp.id);
    if (cp.shoulderLeft < 1 || cp.shoulderLeft > 4 || cp.shoulderRight < 1 || cp.shoulderRight > 4) errors.push(`${cp.id}: shoulders must be 1-4 m`);
    if (cp.width > 12.5) errors.push(`${cp.id}: width ${cp.width} above 12 m`);
  });

  const { samples, total } = sampleRoute(route, 1);
  const st = measure(samples);
  const sAt = (d) => sampleAt(samples, d, loop, total);
  const length = st.lengthMetres;

  const target = meta ? meta.targetLengthKm * 1000 : null;
  const lenErrPct = target ? ((length - target) / target) * 100 : 0;
  if (target && Math.abs(lenErrPct) > 5) errors.push(`length ${length.toFixed(0)} m is ${lenErrPct.toFixed(1)}% from target ${target} m`);
  const targetNet = meta ? (loop ? 0 : meta.targetNetElevationM) : null;
  const netErr = targetNet === null ? 0 : st.netElevationMetres - targetNet;
  if (targetNet !== null && Math.abs(netErr) > (loop ? 0.05 : 3)) errors.push(`net elevation ${st.netElevationMetres.toFixed(1)} m vs target ${targetNet} m`);
  if (st.maxGradePercent > MAX_GRADE) errors.push(`max grade ${st.maxGradePercent.toFixed(1)}% at ${st.maxGradeAt} m exceeds ${MAX_GRADE}%`);
  if (st.minRadiusMetres < MIN_RADIUS) errors.push(`min radius ${st.minRadiusMetres.toFixed(1)} m at ${st.minRadiusAt} m below ${MIN_RADIUS} m`);
  if (st.minWidthMetres < MIN_WIDTH - 1e-6) errors.push(`min width ${st.minWidthMetres.toFixed(2)} m below ${MIN_WIDTH} m`);

  // Closure (circuits): the spline wraps; the last 1 m sample must meet the first without a gap or kink.
  let closure = null;
  if (loop) {
    const a = samples[samples.length - 1], b = samples[0];
    const gap = dist(a.pos, b.pos) - (total - a.distance);
    const kink = angleDeg(V(a.tangent.x, 0, a.tangent.z), V(b.tangent.x, 0, b.tangent.z));
    closure = { gapMetres: +gap.toFixed(3), kinkDeg: +kink.toFixed(2), deltaY: +(a.pos.y - b.pos.y).toFixed(3) };
    if (Math.abs(gap) > 0.05 || kink > 2) errors.push(`loop closure gap ${gap.toFixed(3)} m / kink ${kink.toFixed(2)} deg`);
  }

  // Start grid (Addendum 01): twelve staggered slots, 2 columns x 6 rows, 9 m rows, 4.5 m column stagger, first slot
  // 5 m behind the start line, so the last slot sits ~54.5 m behind it. startMetres >= 62; every slot must be on a
  // >= 10 m wide road (or its lateral offset min(2.4, width/4) must clear the paved edge by >= 1.3 m); the grid zone
  // must be straight (radius >= 150 m) with no crest/dip (grade <= 4%, grade variation <= 2%).
  if (!(route.startMetres >= GRID_MIN_START)) errors.push(`startMetres ${route.startMetres} < ${GRID_MIN_START} (twelve-slot grid)`);
  const gridSlots = [];
  for (let k = 0; k < GRID_SLOTS; k++) {
    const row = Math.floor(k / 2), col = k % 2;
    const d = route.startMetres - 5 - row * 9 - col * 4.5;
    const s = sAt(d);
    const lateral = Math.min(2.4, s.width * 0.25);
    const clearance = s.width / 2 - lateral;
    gridSlots.push({ slot: k + 1, d, width: s.width, clearance });
    if (s.width < 10 - 1e-6 && clearance < 1.3) errors.push(`grid slot ${k + 1} at ${d} m: width ${s.width.toFixed(1)} m, edge clearance ${clearance.toFixed(2)} m`);
    else if (s.width < 10 - 1e-6) warnings.push(`grid slot ${k + 1} at ${d} m narrower than 10 m (${s.width.toFixed(1)} m)`);
  }
  let gridMinWidth = Infinity, gridMaxK = 0, gridMaxGrade = 0, gridMinGrade = Infinity;
  for (let d = route.startMetres - GRID_ZONE_BEHIND; d <= route.startMetres + 5; d += 1) {
    const s = sAt(d);
    gridMinWidth = Math.min(gridMinWidth, s.width);
    gridMaxK = Math.max(gridMaxK, Math.abs(s.curvature));
    const g = ((sAt(d + 5).pos.y - sAt(d - 5).pos.y) / 10) * 100;
    gridMaxGrade = Math.max(gridMaxGrade, g);
    gridMinGrade = Math.min(gridMinGrade, g);
  }
  if (gridMaxK > 1 / 150) errors.push(`grid zone curves (radius ${(1 / gridMaxK).toFixed(0)} m < 150 m)`);
  if (Math.max(Math.abs(gridMaxGrade), Math.abs(gridMinGrade)) > 4 || gridMaxGrade - gridMinGrade > 2) errors.push(`grid zone not level enough (grade ${gridMinGrade.toFixed(1)}..${gridMaxGrade.toFixed(1)}%)`);
  if (!loop && route.startMetres - GRID_ZONE_BEHIND < 0) errors.push("grid zone starts before the route start");

  // Sectors.
  const sectors = route.sectors || [];
  let sectorsOk = sectors.length >= 3 && sectors[0].startMetres === 0;
  for (let i = 1; i < sectors.length; i++) if (!(sectors[i].startMetres > sectors[i - 1].startMetres + 150)) sectorsOk = false;
  if (sectors.length && sectors[sectors.length - 1].startMetres > length - 150) sectorsOk = false;
  if (!sectorsOk) errors.push("sectors must be >= 3, start at 0, increase by > 150 m and end before the finish");
  if (route.course === "C24") {
    const names = sectors.map((s) => s.name).join(",");
    if (names !== "Entry,Arc,Descent,Horizon") errors.push(`C24 sectors must be Entry,Arc,Descent,Horizon (got ${names})`);
  }

  // Gates.
  const gates = route.gates || [];
  const gateIds = new Set();
  for (const g of gates) {
    if (gateIds.has(g.id)) errors.push(`duplicate gate id ${g.id}`);
    gateIds.add(g.id);
    checkGate(g, route.course, loop ? length : length - 20, errors, sAt); // sprints finish 20 m before the end
  }
  if (route.course === "C24") {
    const secStart = (n) => sectors.find((s) => s.name === n)?.startMetres ?? NaN;
    const finish = loop ? length : length - 20; // CourseGenerator.FinishMetres
    const secEnd = (n) => { const i = sectors.findIndex((s) => s.name === n); return i >= 0 && i + 1 < sectors.length ? sectors[i + 1].startMetres : finish; };
    const inSec = (g, n) => g.startMetres >= secStart(n) && g.endMetres <= secEnd(n);
    for (const n of ["Entry", "Arc", "Descent", "Horizon"]) {
      const c = gates.find((g) => g.kind === "contract" && g.id === n);
      if (!c) errors.push(`C24 contract gate '${n}' missing`);
      else if (Math.abs(c.startMetres - secStart(n)) > 1 || Math.abs(c.endMetres - secEnd(n)) > 1) errors.push(`C24 contract '${n}' must span its sector`);
    }
    if (gates.filter((g) => g.kind === "apex" && inSec(g, "Entry")).length !== 2) errors.push("C24 Entry needs exactly two apex gates");
    if (gates.filter((g) => g.kind === "drift-zone" && inSec(g, "Arc")).length !== 3) errors.push("C24 Arc needs exactly three marked drift corners");
    if (gates.filter((g) => g.kind === "brake-zone" && inSec(g, "Descent")).length < 1) errors.push("C24 Descent needs a brake-release target zone");
    if (gates.filter((g) => g.kind === "exit-speed" && inSec(g, "Horizon")).length < 1) errors.push("C24 Horizon needs exit-speed gates");
  }

  // Landmarks.
  const landmarks = route.landmarks || [];
  if (landmarks.length < 3) errors.push(`only ${landmarks.length} landmarks (need >= 3)`);
  for (const lm of landmarks) {
    const tag = `landmark ${lm.id}`;
    if (!(lm.atMetres >= 0 && lm.atMetres <= length)) errors.push(`${tag}: atMetres ${lm.atMetres} outside route`);
    if (!["left", "right", "over"].includes(lm.side)) errors.push(`${tag}: side '${lm.side}'`);
    if (!(lm.kit in KITS)) { errors.push(`${tag}: unknown kit '${lm.kit}'`); continue; }
    const spec = KITS[lm.kit];
    if (spec) {
      const p = lm.params || {};
      if (!spec.type.includes(p.type)) errors.push(`${tag}: ${lm.kit} type '${p.type}' not in the kit vocabulary`);
      for (const f of ["roof", "material"]) if (spec[f] && p[f] !== undefined && !spec[f].includes(p[f])) errors.push(`${tag}: ${f} '${p[f]}'`);
      for (const f of spec.num) if (p[f] !== undefined && !(typeof p[f] === "number" && p[f] > 0)) errors.push(`${tag}: ${f} must be a positive number`);
    }
    if (lm.side === "over") {
      if (lm.kit !== "crossing" && lm.kit !== "gate" && lm.kit !== "stone-bridge" && lm.kit !== "wall" && lm.kit !== "sign") warnings.push(`${tag}: side over for kit ${lm.kit}`);
    } else if (lm.kit !== "stone-bridge" && lm.kit !== "lantern-row") {
      const s = sAt(lm.atMetres);
      const fr = norm(V(s.right.x, 0, s.right.z));
      const sign = lm.side === "left" ? -1 : 1;
      const edge = s.width / 2 + (lm.side === "left" ? s.shoulderLeft : s.shoulderRight);
      if (lm.offsetMetres < edge + 2) errors.push(`${tag}: offset ${lm.offsetMetres} m is inside the barrier line (${edge.toFixed(1)} m)`);
      const P = add(s.pos, mul(fr, sign * lm.offsetMetres));
      // Must not sit on another part of the route.
      let worst = Infinity, worstAt = 0;
      for (const q of samples) {
        const d = Math.hypot(q.pos.x - P.x, q.pos.z - P.z) - (q.width / 2 + Math.max(q.shoulderLeft, q.shoulderRight));
        if (d < worst) { worst = d; worstAt = q.distance; }
      }
      if (worst < 2) errors.push(`${tag}: anchor lies on/near the road at ${worstAt} m`);
    }
  }

  // Sections.
  const sections = route.sections || [];
  const sortedSec = [...sections].sort((a, b) => a.fromMetres - b.fromMetres);
  for (const s of sections) {
    if (!SECTION_KINDS.includes(s.kind)) errors.push(`section kind '${s.kind}'`);
    if (!(s.fromMetres >= 0 && s.toMetres <= length && s.toMetres - s.fromMetres >= 20)) errors.push(`section ${s.kind} ${s.fromMetres}..${s.toMetres} invalid`);
  }
  for (let i = 1; i < sortedSec.length; i++) if (sortedSec[i].fromMetres < sortedSec[i - 1].toMetres) errors.push("sections overlap");

  // Self-intersection / proximity.
  const sep = separation(samples, loop, total, sections);
  if (sep.violations.length) {
    const v0 = sep.violations[0];
    errors.push(`${sep.violations.length} sample pairs closer than ${MIN_SEPARATION} m (e.g. ${v0.a.toFixed(0)} m / ${v0.b.toFixed(0)} m: plan ${v0.plan.toFixed(1)} m, vertical ${v0.vertical.toFixed(1)} m${v0.declared ? "" : ", no declared structure"})`);
  }

  // Areas (T00).
  const areas = route.areas || [];
  const rects = [];
  for (const a of areas) {
    const tag = `area ${a.id}`;
    if (!AREA_KINDS.includes(a.kind)) errors.push(`${tag}: kind '${a.kind}'`);
    if (!(a.size?.[0] > 0 && a.size?.[1] > 0)) { errors.push(`${tag}: bad size`); continue; }
    const R = rectCorners(a.centre, a.size, a.headingDeg || 0);
    rects.push([a, R]);
    // Off-route: no road sample (paved + shoulder) inside the rectangle.
    let hit = null;
    for (const q of samples) {
      const probe = [q.pos, add(q.pos, mul(norm(V(q.right.x, 0, q.right.z)), q.width / 2 + q.shoulderRight)), add(q.pos, mul(norm(V(q.right.x, 0, q.right.z)), -(q.width / 2 + q.shoulderLeft)))];
      if (probe.some((p) => pointInRect(p, R))) { hit = q.distance; break; }
    }
    if (hit !== null) errors.push(`${tag}: overlaps the loop road at ${hit} m`);
    if (a.kind === "braking-lane" && a.size[1] < 300) errors.push(`${tag}: braking lane shorter than 300 m`);
    for (const g of a.gates || []) {
      if (gateIds.has(g.id)) errors.push(`duplicate gate id ${g.id}`);
      gateIds.add(g.id);
      checkGate(g, tag, a.size[1], errors, null);
      if (Math.abs(g.lineOffset) > a.size[0] / 2) errors.push(`gate ${g.id}: lineOffset outside ${tag}`);
    }
  }
  for (let i = 0; i < rects.length; i++) for (let j = i + 1; j < rects.length; j++) if (rectsOverlap(rects[i][1], rects[j][1])) errors.push(`areas ${rects[i][0].id} and ${rects[j][0].id} overlap`);
  if (route.course === "T00") {
    const count = (k) => areas.filter((a) => a.kind === k).length;
    if (count("skid-pad") < 1 || count("braking-lane") < 1 || count("recovery-bay") < 1 || count("training-bay") !== 6) errors.push("T00 needs a skid pad, a braking lane, a recovery bay and six training bays");
  }

  // Overlay gates (C01).
  const overlayGates = overlay?.gates || [];
  if (overlay) {
    if (overlay.schema !== "night-signal/route-gates@1" || overlay.course !== route.course) errors.push("gates overlay header invalid");
    for (const g of overlayGates) {
      if (gateIds.has(g.id)) errors.push(`duplicate gate id ${g.id}`);
      gateIds.add(g.id);
      checkGate(g, "overlay", loop ? length : length - 20, errors, sAt);
    }
  }

  // Required challenge gates.
  const required = REQUIRED_GATES.filter((r) => r[0] === route.course);
  const areaGates = areas.flatMap((a) => a.gates || []);
  const pool = { route: gates, area: areaGates, overlay: overlayGates, "route+overlay": [...gates, ...overlayGates] };
  const challenges = {};
  for (const [, ch, kind, n, where] of required) {
    const have = pool[where].filter((g) => g.challenge === ch && g.kind === kind).length;
    challenges[`${ch}:${kind}`] = `${have}/${n}`;
    if (have < n) errors.push(`${ch} needs ${n} ${kind} gate(s) in ${where}, found ${have}`);
  }

  // Bounding box (terrain sizing) and elevation span.
  let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity, minY = Infinity, maxY = -Infinity;
  for (const q of samples) {
    minX = Math.min(minX, q.pos.x); maxX = Math.max(maxX, q.pos.x);
    minZ = Math.min(minZ, q.pos.z); maxZ = Math.max(maxZ, q.pos.z);
    minY = Math.min(minY, q.pos.y); maxY = Math.max(maxY, q.pos.y);
  }
  if (sep.minSep < 20) warnings.push(`closest non-adjacent approach ${sep.minSep.toFixed(1)} m`);

  return {
    course: route.course,
    name: meta?.name ?? route.course,
    format: meta?.format ?? (loop ? "loop" : "sprint"),
    provisionalTargets: !!meta?.provisional,
    closedLoop: loop,
    revision: route.revision,
    controlPoints: route.controlPoints.length,
    samples: samples.length,
    lengthMetres: +length.toFixed(1),
    targetLengthMetres: target,
    lengthErrorPercent: +lenErrPct.toFixed(2),
    netElevationMetres: +st.netElevationMetres.toFixed(2),
    targetNetElevationMetres: targetNet,
    maxGradePercent: +st.maxGradePercent.toFixed(2),
    maxGradeAtMetres: st.maxGradeAt,
    minRadiusMetres: +st.minRadiusMetres.toFixed(2),
    minRadiusAtMetres: st.minRadiusAt,
    minWidthMetres: +st.minWidthMetres.toFixed(2),
    gridMinWidthMetres: +gridMinWidth.toFixed(2),
    gridSlots: gridSlots.length,
    gridMinSlotClearanceMetres: +Math.min(...gridSlots.map((g) => g.clearance)).toFixed(2),
    gridZoneMinRadiusMetres: gridMaxK > 0 ? +(1 / gridMaxK).toFixed(0) : null,
    elevationSpanMetres: +(maxY - minY).toFixed(1),
    boundingBoxMetres: [+(maxX - minX).toFixed(0), +(maxZ - minZ).toFixed(0)],
    closestApproachMetres: Number.isFinite(sep.minSep) ? +sep.minSep.toFixed(1) : null,
    closestApproachAt: sep.minSepAt,
    crossings: sep.crossings,
    closure,
    sectors: sectors.map((s) => `${s.name}@${s.startMetres}`),
    sectorsValid: sectorsOk,
    gates: gates.length,
    areaGates: areaGates.length,
    overlayGates: overlayGates.length,
    landmarks: landmarks.length,
    sections: sections.map((s) => `${s.kind} ${s.fromMetres}-${s.toMetres}`),
    areas: areas.length,
    challengeGates: challenges,
    timeOfDay: route.timeOfDay ?? null,
    surface: route.surface ?? null,
    errors,
    warnings,
    pass: errors.length === 0,
    _samples: samples,
  };
}

// ---------------------------------------------------------------------------------------------- uniqueness
// Heuristic copy/reuse detector (spec §14: >= 70% of a counted course's centreline exclusive). Courses live in
// separate scenes, so literal overlap is impossible; what matters is re-using the same corner sequence. Each course's
// signed curvature is sampled every 5 m; every window (default 400 m, stepped 25 m) with real cornering (mean
// |k| >= 1/400 m) is compared with every window of every other course at 5 m offsets, forward, mirrored, reversed
// and reversed-mirrored. A window "matches" when the RMS curvature difference is under THRESHOLD of its own RMS
// curvature (default 20%) and the mean grade difference is under 1.5 %. Exclusive % = share of active windows with no
// match anywhere else. It flags a copied, mirrored or reversed stretch of road, not merely similar single corners.
function curvatureProfile(samples, step = 5) {
  const k = [], g = [];
  for (let i = 0; i + step < samples.length; i += step) {
    let s = 0;
    for (let j = 0; j < step; j++) s += samples[i + j].curvature;
    k.push(s / step);
    g.push(((samples[i + step].pos.y - samples[i].pos.y) / step) * 100);
  }
  return { k: Float64Array.from(k), g: Float64Array.from(g) };
}

export const UNIQUE = { windowMetres: 400, threshold: 0.2, gradeTolerance: 1.5 };
export function uniqueness(results, opts = UNIQUE) {
  const W = Math.round(opts.windowMetres / 5), STRIDE = 5; // windows every 25 m
  const profs = results.map((r) => {
    const p = curvatureProfile(r._samples);
    const n = p.k.length;
    const rev = { k: new Float64Array(n), g: new Float64Array(n) };
    for (let i = 0; i < n; i++) { rev.k[i] = -p.k[n - 1 - i]; rev.g[i] = -p.g[n - 1 - i]; }
    return { id: r.course, fwd: p, rev };
  });
  const out = {};
  for (const A of profs) {
    const kA = A.fwd.k, gA = A.fwd.g;
    let active = 0, shared = 0;
    const matches = {};
    for (let s = 0; s + W <= kA.length; s += STRIDE) {
      let e = 0, act = 0;
      for (let i = 0; i < W; i++) { e += kA[s + i] * kA[s + i]; act += Math.abs(kA[s + i]); }
      if (act / W < 1 / 400) continue;
      active++;
      const limit = opts.threshold * opts.threshold * e;
      let found = null;
      outer: for (const B of profs) {
        if (B === A) continue;
        for (const variant of [B.fwd, B.rev]) {
          const kB = variant.k, gB = variant.g;
          for (const mir of [1, -1]) {
            for (let o = 0; o + W <= kB.length; o++) {
              let acc = 0, gd = 0, i = 0;
              for (; i < W; i++) {
                const d = kA[s + i] - mir * kB[o + i];
                acc += d * d;
                if (acc > limit) break;
              }
              if (i < W) continue;
              for (i = 0; i < W; i++) gd += Math.abs(gA[s + i] - gB[o + i]);
              if (gd / W > opts.gradeTolerance) continue;
              found = B.id;
              break outer;
            }
          }
        }
      }
      if (found) { shared++; matches[found] = (matches[found] || 0) + 1; }
    }
    out[A.id] = { activeWindows: active, sharedWindows: shared, exclusivePercent: active ? +(100 * (1 - shared / active)).toFixed(1) : 100, matches };
  }
  return out;
}

// ---------------------------------------------------------------------------------------------- main
// Addendum 01 Freeplay-only courses: provisional targets used until catalogue rows exist (FP02's net elevation is
// not given by the addendum; +230 m is the authored value and should be copied into its catalogue row).
export const PROVISIONAL = {
  FP01: { id: "FP01", name: "Kisaragi Dock Loop", format: "circuit", targetLengthKm: 3.4, targetNetElevationM: 0, provisional: true },
  FP02: { id: "FP02", name: "Hoshino Switchback Park", format: "sprint", targetLengthKm: 5.1, targetNetElevationM: 230, provisional: true },
  FP03: { id: "FP03", name: "Aobane Airfield Circuit", format: "circuit", targetLengthKm: 4.3, targetNetElevationM: 0, provisional: true },
};

function loadAll(only) {
  const catalogue = JSON.parse(readFileSync(CATALOGUE, "utf8")).courses;
  const list = [];
  for (const dir of readdirSync(COURSES_DIR, { withFileTypes: true })) {
    if (!dir.isDirectory()) continue;
    const file = join(COURSES_DIR, dir.name, "route.json");
    if (!existsSync(file)) continue;
    if (only && !only.includes(dir.name)) continue;
    const route = JSON.parse(readFileSync(file, "utf8"));
    const ovFile = join(COURSES_DIR, dir.name, "gates.overlay.json");
    const overlay = existsSync(ovFile) ? JSON.parse(readFileSync(ovFile, "utf8")) : null;
    list.push({ route, meta: catalogue.find((c) => c.id === route.course) ?? PROVISIONAL[route.course], overlay });
  }
  const order = [...catalogue.map((c) => c.id), ...Object.keys(PROVISIONAL).filter((k) => !catalogue.some((c) => c.id === k))];
  const rank = (id) => (order.includes(id) ? order.indexOf(id) : order.length);
  list.sort((a, b) => rank(a.route.course) - rank(b.route.course) || a.route.course.localeCompare(b.route.course));
  return { list, catalogue };
}

function parityFixture() {
  // [x, y, z, width, bank, shoulderLeft, shoulderRight] of C01 revision 1.
  const P = [[0,80,0,11.5,0,2.5,2.5],[0,79.5,90,10,0,2.5,2.5],[-5,78,190,9,-1,2.5,2.5],[-40,76,300,9,-3,2.5,3],[-110,74,380,9,-3.5,2.5,3],
    [-210,71.5,430,8.5,-1.5,2.5,2.5],[-330,68,455,8.5,0,2.5,2.5],[-450,65,490,8.5,1.5,2.5,2.5],[-550,62,570,8.5,2,2.5,2.5],[-610,59,680,8.5,-1,2.5,2.5],
    [-690,56,780,8.5,-2,2.5,2.5],[-800,53,830,8,0,1.8,1.8],[-920,50,850,8,0,1.8,1.8],[-1030,47,900,8.5,1.5,2.5,2.5],[-1100,44,990,8.5,2,2.5,2.5],
    [-1130,41,1110,8.5,0.5,2.5,2.5],[-1140,37,1250,8.5,0,2.5,2.5],[-1140,34,1340,9,0,3,3],[-1130,32,1395,9.5,3,3.5,3],[-1105,31,1418,10,5,4,3],
    [-1078,30,1412,10,5,4,3],[-1065,29,1385,9.5,3,3.5,3],[-1050,26,1320,9,0.5,2.5,2.5],[-1010,20,1240,8.5,-1,2.5,2.5],[-940,14,1180,8.5,-1,2.5,2.5],
    [-850,9,1150,8.5,0,2.5,2.5],[-740,5,1130,9,0,2.5,2.5],[-620,2,1125,9,0,2.5,2.5],[-500,0.5,1120,9,0,2.5,2.5],[-380,0,1120,10,0,2.5,2.5],[-260,0,1118,11,0,2.5,2.5]];
  return { closedLoop: false, controlPoints: P.map((q, i) => ({ id: `c01-p${String(i).padStart(2, "0")}`, p: [q[0], q[1], q[2]], width: q[3], bank: q[4], shoulderLeft: q[5], shoulderRight: q[6] })) };
}

function pad(s, n, right = false) {
  s = String(s);
  return right ? s.padStart(n) : s.padEnd(n);
}

function main() {
  const args = process.argv.slice(2);
  const quiet = args.includes("--quiet");
  const onlyIdx = args.indexOf("--only");
  const only = onlyIdx >= 0 ? args[onlyIdx + 1].split(",") : null;
  const { list, catalogue } = loadAll(only);
  const results = list.map(({ route, meta, overlay }) => checkCourse(route, meta, overlay));

  // Sampler parity: C01 revision 1 (fixture below) measured in Unity by RouteSampler/RouteIO.Measure gave
  // length 3079 m, net -80.0 m, min radius ~19.8 m, max grade ~7.0%. The fixture is fixed so later C01 revisions
  // (e.g. the twelve-slot grid rollout) do not disturb the parity check.
  const fixture = parityFixture();
  const fs0 = measure(sampleRoute(fixture, 1).samples);
  const parity = {
    fixture: "C01 revision 1 control points (embedded)",
    reference: { lengthMetres: 3079, netElevationMetres: -80.0, minRadiusMetres: 19.8, maxGradePercent: 7.0 },
    port: { lengthMetres: +fs0.lengthMetres.toFixed(1), netElevationMetres: +fs0.netElevationMetres.toFixed(2), minRadiusMetres: +fs0.minRadiusMetres.toFixed(2), maxGradePercent: +fs0.maxGradePercent.toFixed(2) },
  };
  parity.pass = Math.abs(fs0.lengthMetres - 3079) <= 1 && Math.abs(fs0.netElevationMetres + 80) <= 0.05 && Math.abs(fs0.minRadiusMetres - 19.8) <= 0.1 && Math.abs(fs0.maxGradePercent - 7.0) <= 0.1;
  if (!parity.pass) process.exitCode = 1;

  let uniq = null;
  if (!args.includes("--no-overlap") && results.length > 1) {
    uniq = uniqueness(results);
    for (const r of results) {
      r.exclusivePercent = uniq[r.course].exclusivePercent;
      r.overlapMatches = uniq[r.course].matches;
      if (r.exclusivePercent < 70) r.errors.push(`only ${r.exclusivePercent}% of cornering windows are exclusive (need >= 70%)`);
    }
  }
  for (const r of results) r.pass = r.errors.length === 0;

  const missing = catalogue.filter((c) => !results.some((r) => r.course === c.id)).map((c) => c.id);
  const header = ["ID", "fmt", "len m", "tgt m", "err%", "net m", "tgt", "maxG%", "minR m", "@m", "minW", "sep m", "xing", "sec", "gate", "lmk", "excl%", "result"];
  const widths = [4, 7, 7, 6, 6, 7, 5, 6, 7, 6, 5, 6, 5, 4, 5, 4, 6, 6];
  const rows = results.map((r) => [
    r.course,
    r.closedLoop ? (r.course === "T00" ? "loop" : "circ") : "sprint",
    r.lengthMetres.toFixed(0),
    r.targetLengthMetres,
    (r.lengthErrorPercent >= 0 ? "+" : "") + r.lengthErrorPercent.toFixed(1),
    r.netElevationMetres.toFixed(1),
    r.targetNetElevationMetres,
    r.maxGradePercent.toFixed(1),
    r.minRadiusMetres.toFixed(1),
    r.minRadiusAtMetres,
    r.minWidthMetres.toFixed(1),
    r.closestApproachMetres === null ? "-" : r.closestApproachMetres.toFixed(0),
    r.crossings.length ? r.crossings.map((c) => c.clearance.toFixed(0)).join("/") : "-",
    r.sectors.length,
    r.gates + r.areaGates + r.overlayGates,
    r.landmarks,
    r.exclusivePercent === undefined ? "-" : r.exclusivePercent.toFixed(0),
    r.pass ? "PASS" : "FAIL",
  ]);
  const line = (cells) => cells.map((c, i) => pad(c, widths[i], i > 1 && i < 17)).join(" ");
  console.log(line(header));
  console.log(widths.map((w) => "-".repeat(w)).join(" "));
  for (const row of rows) console.log(line(row));
  if (parity) console.log(`\nC# parity (C01 revision-1 fixture): length ${parity.port.lengthMetres} m (ref 3079), net ${parity.port.netElevationMetres} m (ref -80.0), min radius ${parity.port.minRadiusMetres} m (ref ~19.8), max grade ${parity.port.maxGradePercent}% (ref ~7.0) -> ${parity.pass ? "MATCH" : "MISMATCH"}`);
  if (missing.length) console.log(`Missing route.json: ${missing.join(", ")}`);
  for (const r of results) {
    if (r.errors.length) for (const e of r.errors) console.log(`  ${r.course} ERROR: ${e}`);
    if (!quiet) for (const w of r.warnings) console.log(`  ${r.course} warn: ${w}`);
  }
  const allPass = results.every((r) => r.pass) && missing.length === 0 && parity.pass;
  console.log(`\n${results.filter((r) => r.pass).length}/${results.length} courses pass${missing.length ? `, ${missing.length} missing` : ""}.`);

  if (!only) {
    mkdirSync(dirname(OUT), { recursive: true });
    const doc = {
      generator: "Tools/authoring/route-check.mjs",
      schema: "night-signal/route-stats@1",
      note: "Measured from route.json with a double-precision port of RouteSampler.cs / RouteIO.Measure (1 m samples). Lengths are centreline metres; circuits are per lap.",
      samplerParity: parity,
      rules: { lengthTolerancePercent: 5, netElevationToleranceMetres: 3, maxGradePercent: MAX_GRADE, minRadiusMetres: MIN_RADIUS, minWidthMetres: MIN_WIDTH, minSeparationMetres: MIN_SEPARATION, minAlongMetres: MIN_ALONG, minCrossingClearanceMetres: MIN_CLEARANCE, exclusiveWindowPercent: 70, overlapHeuristic: UNIQUE, gridSlots: GRID_SLOTS, gridMinStartMetres: GRID_MIN_START, gridMinWidthMetres: 10, gridSlotEdgeClearanceMetres: 1.3 },
      allPass,
      courses: results.map(({ _samples, ...r }) => r),
    };
    writeFileSync(OUT, JSON.stringify(doc, null, 2) + "\n");
    console.log(`Wrote ${OUT.replace(ROOT + "\\", "").replace(ROOT + "/", "")}`);
  }
  process.exitCode = allPass ? 0 : 1;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
