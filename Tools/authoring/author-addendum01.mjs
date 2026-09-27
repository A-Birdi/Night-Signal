#!/usr/bin/env node
// One-time authoring of the Addendum 01 overlays (docs/EFFECTIVE_RULES.md). Writes, only when absent unless --force:
//   Assets/Content/Data/authored/stages.opposition.json  live opponents per stage and mode (featured first)
//   Assets/Content/Data/authored/courses.addendum.json   FP01–FP03 rows and the course-access table
// Both files are authored data afterwards: edit them by hand, keep IDs stable. Re-running the catalogue importer never
// touches them, so a fresh import cannot reintroduce the superseded six-total grid or ghost-only rules.

import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const GEN = join(ROOT, "Assets", "Content", "Data", "generated");
const AUTH = join(ROOT, "Assets", "Content", "Data", "authored");
const force = process.argv.includes("--force");
const FINALE_ONLY = new Set(["R40", "R48"]);

const stages = JSON.parse(readFileSync(join(GEN, "stages.json"), "utf8")).stages;

function opponents(stage, side) {
  const lead = side.lead;
  const supports = (side.support || []).filter((r) => r !== lead && !FINALE_ONLY.has(r));
  let count;
  switch (stage.type) {
    case "finale": count = 1; break;                       // the final rival alone: a live H + 1 duel
    case "lieutenant":
    case "penultimate": count = 3; break;                  // featured + two supports
    default:
      if (stage.act === 1) count = stage.number % 2 === 1 ? 1 : 2;   // early: featured, sometimes one support
      else count = stage.number % 2 === 0 ? 3 : 2;                   // later: two or three opponents total
  }
  const list = [lead, ...supports].slice(0, count);
  if (list.length < count) throw new Error(`${stage.id}: not enough authored supports for ${count} opponents`);
  if (stage.type !== "finale" && list.some((r) => FINALE_ONLY.has(r))) throw new Error(`${stage.id}: finale-only rival outside the finale`);
  return list;
}

const opposition = {
  schema: "night-signal/stage-opposition@1",
  addendum: "Addendum 01 §1.2 / §12",
  rules: "Act I regular: featured alone on odd stages, featured + one support on even stages. Acts II–IV regular: two (odd) or three (even) opponents. Lieutenant and penultimate: featured + two supports. Finale: the final rival alone. R40/R48 appear only as their own finale lead. Opponents are live, solid, server-controlled cars; humans never displace them.",
  stages: stages.map((s) => ({ id: s.id, normal: opponents(s, s.normal), hard: opponents(s, s.hard) })),
};

const addendum = {
  schema: "night-signal/courses-addendum@1",
  addendum: "Addendum 01 §5 (D03)",
  courses: [
    {
      id: "FP01", name: "Kisaragi Dock Loop", region: "freeplay-venues", kind: "freeplay", format: "circuit", laps: 2,
      formatDescription: "Two-lap closed dockside circuit", targetLengthKm: 6.8, targetNetElevationM: 0, expectedSeconds: 260,
      sectorsBrief: "Container-yard switchback; broad freight-apron carousel; elevated loading-ramp crest; waterfront braking zone",
      landmarks: ["Retired harbor crane", "Illuminated ferry terminal", "Dry-dock gantry"],
      defaultConditions: "Night, dry",
      designNote: "Supports Circuit, Drift Attack zones and Time Attack. No shipping traffic, pedestrians or borrowed logos. Initial authoring target (3.4 km per lap).",
    },
    {
      id: "FP02", name: "Hoshino Switchback Park", region: "freeplay-venues", kind: "freeplay", format: "sprint", laps: 1,
      formatDescription: "Uphill sprint on a former service road", targetLengthKm: 5.1, targetNetElevationM: 240, expectedSeconds: 220,
      sectorsBrief: "Linked medium-radius uphill bends; short tunnel; technical five-hairpin climb; fast final ridge",
      landmarks: ["Timber cable-station shell", "Terraced lookout", "Illuminated retaining-wall mural"],
      defaultConditions: "Dusk, dry",
      designNote: "Supports Sprint, selected drift zones and Time Attack. Believable grading, barriers and safe runoff. Initial authoring target.",
    },
    {
      id: "FP03", name: "Aobane Airfield Circuit", region: "freeplay-venues", kind: "freeplay", format: "circuit", laps: 2,
      formatDescription: "Two-lap circuit at a closed regional airfield", targetLengthKm: 8.6, targetNetElevationM: 0, expectedSeconds: 285,
      sectorsBrief: "Runway braking zones; taxiway technical esses; banked maintenance-road turn; hangar-perimeter sequence",
      landmarks: ["Control tower", "Large numbered hangars", "Windsock and sculpture plaza"],
      defaultConditions: "Afternoon, dry",
      designNote: "Supports Circuit, large drift zones and Time Attack. Not cones on an empty plane. Initial authoring target (4.3 km per lap).",
    },
  ],
  access: {
    starterCourses: ["T00", "C01", "C02", "C03", "C04"],
    campaignCoursePrice: 45000,
    campaignCourseRange: ["C05", "C24"],
    rewardOnly: [{ course: "C25", stage: "S30", mode: "normal" }],
    purchaseOnly: [
      { course: "FP01", price: 45000 },
      { course: "FP02", price: 54000 },
      { course: "FP03", price: 63000 },
    ],
    priceBasis: "Initial reference ~9,000 Credits per ordinary completed race (≈5/6/7 races for FP01/02/03). Balancing assumption, not measured economy evidence.",
  },
};

function write(name, obj) {
  const path = join(AUTH, name);
  if (existsSync(path) && !force) { console.log(`${name}: exists, left unchanged (use --force to regenerate)`); return; }
  writeFileSync(path, JSON.stringify(obj, null, 2) + "\n");
  console.log(`${name}: written`);
}

write("stages.opposition.json", opposition);
write("courses.addendum.json", addendum);
for (const s of opposition.stages) console.log(s.id, s.normal.join(","), "|", s.hard.join(","));
