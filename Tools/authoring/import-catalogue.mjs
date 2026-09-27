#!/usr/bin/env node
// Converts the brief's authoring catalogue (docs/brief/Night_Signal_Content_Catalogue.json) into the game's
// typed content files under Assets/Content/Data/generated/. Output is deterministic; generated files are never
// hand-edited — authored additions live in Assets/Content/Data/authored/ and are merged by the game loader.
//
// Usage: node Tools/authoring/import-catalogue.mjs [--check]
//   --check  exit 1 if regenerating would change any generated file (drift detection for CI/validation).

import { readFileSync, writeFileSync, mkdirSync, existsSync } from "node:fs";
import { createHash } from "node:crypto";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const sourcePath = join(root, "docs", "brief", "Night_Signal_Content_Catalogue.json");
const outDir = join(root, "Assets", "Content", "Data", "generated");
const checkOnly = process.argv.includes("--check");

const sourceBytes = readFileSync(sourcePath);
const sourceSha256 = createHash("sha256").update(sourceBytes).digest("hex");
const src = JSON.parse(sourceBytes.toString("utf8"));

const REGIONS = {
  "Training campus": "training",
  "Mizuhana foothills": "mizuhana",
  "Kasumi forest": "kasumi",
  "Kurogawa reservoir": "kurogawa",
  "Akebono coast": "akebono",
  "Hoshimi uplands": "hoshimi",
  "Tsukishiro highland": "tsukishiro",
  "Finale mountain": "amanagi",
};
const CREW_IDS = {
  "Tea Hour Motor Club": "tea-hour",
  "Rainline Atelier": "rainline",
  "Reservoir Section": "reservoir",
  "Breakwater Union": "breakwater",
  "Datum Works": "datum",
  "Zero Frequency": "zero-frequency",
};
const CREW_REGIONS = { Mizuhana: "mizuhana", Kasumi: "kasumi", Kurogawa: "kurogawa", Akebono: "akebono", Hoshimi: "hoshimi", Tsukishiro: "tsukishiro" };
const ROLES = { R08: "lieutenant", R16: "lieutenant", R24: "lieutenant", R32: "lieutenant", R40: "normal-final", R48: "hard-final" };
const STARTERS = new Set(["V01", "V02", "V03"]);

const slug = (s) => s.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
const splitList = (s) => s.split(";").map((x) => x.trim()).filter(Boolean);
const fail = (msg) => { console.error("import-catalogue: " + msg); process.exit(2); };

// ---- courses ---------------------------------------------------------------------------------------------
const courses = src.courses.map((c) => {
  const region = REGIONS[c.region] ?? fail(`unknown region ${c.region}`);
  const isCircuit = /circuit/i.test(c.format);
  const kind = c.id === "T00" ? "tutorial" : c.id === "C25" ? "finale" : "regular";
  return {
    id: c.id,
    name: c.name,
    region,
    kind,
    format: kind === "tutorial" ? "training" : isCircuit ? "circuit" : "sprint",
    laps: isCircuit ? 2 : 1,
    formatDescription: c.format,
    targetLengthKm: c.length_km_target,
    targetNetElevationM: c.net_elevation_m_target,
    expectedSeconds: c.expected_seconds_for_economy,
    sectorsBrief: c.sectors,
    landmarks: splitList(c.landmarks),
    defaultConditions: c.default_conditions,
    designNote: c.design_note,
  };
});

// ---- cars ------------------------------------------------------------------------------------------------
const cars = src.cars.map((v) => {
  const m = /^(front-mid|front|mid) engine \/ (RWD|FWD|AWD)$/.exec(v.drive) ?? fail(`unparsed layout ${v.drive}`);
  const [maker, ...model] = v.name.split(" ");
  return {
    id: v.id,
    name: v.name,
    maker,
    model: model.join(" "),
    body: v.body,
    engineLayout: m[1],
    drive: m[2],
    basePI: v.base_PI_target,
    massKg: v.mass_kg_target,
    powerKw: v.power_kw_target,
    torqueNm: v.torque_nm_target,
    price: v.purchase_price,
    starter: STARTERS.has(v.id),
    silhouette: v.visual_identity,
    handling: v.handling_identity,
  };
});

// ---- crews, tendencies, rivals ---------------------------------------------------------------------------
const crews = src.crews.map((c) => ({
  id: CREW_IDS[c.name] ?? fail(`unknown crew ${c.name}`),
  name: c.name,
  region: CREW_REGIONS[c.region] ?? fail(`unknown crew region ${c.region}`),
  identity: c.identity,
}));

const tendencyNames = [...new Set(src.rivals.map((r) => r.archetype))];
const tendencies = tendencyNames.map((name) => ({ id: slug(name), name }));

const rivals = src.rivals.map((r) => {
  const m = /^(\d+), (.+)$/.exec(r.age_pronouns) ?? fail(`unparsed age/pronouns ${r.age_pronouns}`);
  return {
    id: r.id,
    name: r.name,
    age: Number(m[1]),
    pronouns: m[2],
    crew: CREW_IDS[r.crew] ?? fail(`unknown crew ${r.crew}`),
    role: ROLES[r.id] ?? "crew",
    appearance: r.appearance,
    personality: r.personality,
    primaryCar: r.primary_car,
    tendency: slug(r.archetype),
    strength: r.strength,
    weakness: r.weakness,
    introSample: r.intro_sample,
  };
});

// ---- stages ----------------------------------------------------------------------------------------------
// Hard support pools are not in the brief. Rule (spec Appendix B: "fill support from that crew's remaining
// members and the current stage's previously introduced rivals"): start from the Normal support pool, replace
// the Hard lead (if present) with the Normal lead, never include the Hard lead itself, and keep order.
const stages = src.campaign_stages.map((s, i) => {
  const hardSupport = s.normal_support.map((r) => (r === s.hard_lead ? s.normal_lead : r))
    .filter((r, idx, arr) => r !== s.hard_lead && arr.indexOf(r) === idx);
  return {
    id: s.id,
    number: i + 1,
    type: s.type,
    course: s.course,
    act: s.act,
    maxPI: s.car_cap,
    normal: { lead: s.normal_lead, support: s.normal_support, storyBeat: s.normal_story_beat },
    hard: { lead: s.hard_lead, support: hardSupport, supportDerivation: "swap-rule-v1", variation: s.hard_variation },
  };
});

// ---- challenges and cosmetics ----------------------------------------------------------------------------
const challenges = src.challenges.map((c) => ({
  id: c.id,
  family: c.family.toLowerCase(),
  tier: c.tier.toLowerCase(),
  name: c.name,
  predicateText: c.predicate,
  reward: c.reward_id,
  rankPoints: c.rank_points,
  cash: c.cash,
}));
const cosmetics = src.challenges.map((c) => ({
  id: c.reward_id,
  name: c.reward_name,
  category: c.reward_category,
  source: c.id,
}));

// ---- write -----------------------------------------------------------------------------------------------
const header = (kind, count) => ({
  generated: true,
  generator: "Tools/authoring/import-catalogue.mjs",
  sourceFile: "docs/brief/Night_Signal_Content_Catalogue.json",
  sourceSha256,
  schema: `night-signal/${kind}@1`,
  count,
});
const files = {
  "courses.json": { ...header("courses", courses.length), courses },
  "cars.json": { ...header("cars", cars.length), cars },
  "crews.json": { ...header("crews", crews.length), crews, tendencies },
  "rivals.json": { ...header("rivals", rivals.length), rivals },
  "stages.json": { ...header("stages", stages.length), stages },
  "challenges.json": { ...header("challenges", challenges.length), challenges },
  "cosmetics.json": { ...header("cosmetics", cosmetics.length), cosmetics },
};

mkdirSync(outDir, { recursive: true });
let changed = 0;
for (const [name, data] of Object.entries(files)) {
  const path = join(outDir, name);
  const text = JSON.stringify(data, null, 2) + "\n";
  const current = existsSync(path) ? readFileSync(path, "utf8").replace(/\r\n/g, "\n") : null;
  if (current === text) continue;
  changed++;
  if (checkOnly) console.error(`would change: ${name}`);
  else writeFileSync(path, text, { encoding: "utf8" });
}
console.log(`${checkOnly ? "checked" : "wrote"} ${Object.keys(files).length} files, ${changed} ${checkOnly ? "differ" : "changed"}; source sha256 ${sourceSha256}`);
if (checkOnly && changed > 0) process.exit(1);
