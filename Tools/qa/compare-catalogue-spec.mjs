#!/usr/bin/env node
// Cross-checks the brief's structured catalogue against the authoritative appendices in SPECIFICATION.md.
// Reports every field that differs. Usage: node Tools/qa/compare-catalogue-spec.mjs  (exit 1 on discrepancies)

import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const spec = readFileSync(join(root, "SPECIFICATION.md"), "utf8").replace(/\r\n/g, "\n");
const cat = JSON.parse(readFileSync(join(root, "docs", "brief", "Night_Signal_Content_Catalogue.json"), "utf8"));
const issues = [];
const numeric = (v) => /^[+-]?\d+(\.\d+)?$/.test(String(v).trim());
const diff = (where, field, specVal, catVal) => {
  const same = numeric(specVal) && numeric(catVal)
    ? Number(specVal) === Number(catVal)
    : String(specVal).trim() === String(catVal).trim();
  if (!same) issues.push(`${where} ${field}: spec="${specVal}" catalogue="${catVal}"`);
};
const section = (id) => {
  const start = spec.indexOf(`\n## ${id} — `) >= 0 ? spec.indexOf(`\n## ${id} — `) : spec.indexOf(`\n### ${id} — `);
  if (start < 0) return null;
  const rest = spec.slice(start + 1);
  const next = rest.slice(3).search(/\n#{2,3} /);
  return next < 0 ? rest : rest.slice(0, next + 3);
};
const field = (text, label) => { const m = new RegExp(`${label}:\\s*(.+?)(?:\\.\\s|\\n|$)`).exec(text); return m ? m[1].trim() : null; };

for (const c of cat.courses) {
  const s = section(c.id);
  if (!s) { issues.push(`${c.id}: missing in spec`); continue; }
  diff(c.id, "name", /— (.+)\n/.exec(s)[1], c.name);
  diff(c.id, "region", field(s, "Region"), c.region);
  diff(c.id, "length", /Target length ([\d.]+) km/.exec(s)[1], c.length_km_target);
  diff(c.id, "elevation", /net elevation ([+-]?[\d]+) m/.exec(s)[1].replace("+", ""), c.net_elevation_m_target);
  diff(c.id, "expected", /economy (\d+) s/.exec(s)[1], c.expected_seconds_for_economy);
  diff(c.id, "landmarks", /Required landmarks: (.+)\./.exec(s)[1], c.landmarks);
}
for (const st of cat.campaign_stages) {
  const s = section(st.id);
  if (!s) { issues.push(`${st.id}: missing in spec`); continue; }
  const head = /## S\d\d — (\w+) \/ (\w+) \/ Act (\d)/.exec(s);
  diff(st.id, "type", head[1].toLowerCase(), st.type);
  diff(st.id, "course", head[2], st.course);
  diff(st.id, "act", head[3], st.act);
  diff(st.id, "normal lead", /Normal featured rival: (R\d\d)/.exec(s)[1], st.normal_lead);
  diff(st.id, "support", /support pool: ([R\d, ]+)\./.exec(s)[1].replace(/\s/g, ""), st.normal_support.join(","));
  diff(st.id, "hard lead", /Hard featured rival: (R\d\d)/.exec(s)[1], st.hard_lead);
  diff(st.id, "cap", /Maximum PI: (\d+)/.exec(s)[1], st.car_cap);
}
for (const v of cat.cars) {
  const s = section(v.id);
  if (!s) { issues.push(`${v.id}: missing in spec`); continue; }
  diff(v.id, "name", /— (.+)\n/.exec(s)[1], v.name);
  diff(v.id, "layout", /Layout: (.+?)\. Base/.exec(s)[1], v.drive);
  diff(v.id, "PI", /PI (\d+)/.exec(s)[1], v.base_PI_target);
  diff(v.id, "mass", /mass (\d+) kg/.exec(s)[1], v.mass_kg_target);
  diff(v.id, "power", /power (\d+) kW/.exec(s)[1], v.power_kw_target);
  diff(v.id, "torque", /torque (\d+) Nm/.exec(s)[1], v.torque_nm_target);
  diff(v.id, "price", /Price: ([\d,]+) credits/.exec(s)[1].replace(/,/g, ""), v.purchase_price);
}
for (const r of cat.rivals) {
  const s = section(r.id);
  if (!s) { issues.push(`${r.id}: missing in spec`); continue; }
  diff(r.id, "name", /— (.+?) \(/.exec(s)[1], r.name);
  diff(r.id, "age/pronouns", /\((.+)\)\n/.exec(s)[1], r.age_pronouns);
  diff(r.id, "car", /Primary car: (V\d\d)/.exec(s)[1], r.primary_car);
  diff(r.id, "tendency", /Driving tendency: (.+?)\. Strength/.exec(s)[1], r.archetype);
}
for (const ch of cat.challenges) {
  const s = section(ch.id);
  if (!s) { issues.push(`${ch.id}: missing in spec`); continue; }
  const head = /— (.+) \[(\w+)\]/.exec(s);
  diff(ch.id, "name", head[1], ch.name);
  diff(ch.id, "tier", head[2], ch.tier);
  const rew = /Reward: (COS-CH\d\d) — (.+) \((\w+)\); (\d+) RP; ([\d,]+) credits/.exec(s);
  diff(ch.id, "reward id", rew[1], ch.reward_id);
  diff(ch.id, "reward name", rew[2], ch.reward_name);
  diff(ch.id, "category", rew[3], ch.reward_category);
  diff(ch.id, "rp", rew[4], ch.rank_points);
  diff(ch.id, "cash", rew[5].replace(/,/g, ""), ch.cash);
  diff(ch.id, "predicate", /Completion: (.+)\n/.exec(s)[1], ch.predicate);
}
const counts = `courses ${cat.courses.length}, stages ${cat.campaign_stages.length}, cars ${cat.cars.length}, rivals ${cat.rivals.length}, challenges ${cat.challenges.length}`;
if (issues.length) { console.log(`DISCREPANCIES (${issues.length}) — ${counts}`); issues.forEach((i) => console.log("  " + i)); process.exit(1); }
console.log(`catalogue matches SPECIFICATION.md appendices field-by-field — ${counts}`);
