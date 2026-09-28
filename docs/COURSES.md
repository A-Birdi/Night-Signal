# Course authoring contract

Each course is `Assets/Content/Courses/<ID>/route.json` (schema `night-signal/route@1`). It is the versioned
source: `CourseRuntime` generates geometry from it deterministically (decision D-007). Coordinates are metres,
x east, y up, z north (Unity, left-handed). Every course lives in its own scene, so coordinates may overlap
between courses.

## Top-level fields

| field | meaning |
|---|---|
| `schema`, `course`, `revision` | `night-signal/route@1`, course ID, integer revision (bump on any geometry change) |
| `closedLoop` | `true` for circuits (two laps; start = finish line) and the T00 loop |
| `startMetres` | start line distance; the twelve-slot grid (up to 12 vehicles: 2 columns × 6 rows, 9 m rows, 4.5 m column stagger, first slot 5 m behind the line, last ≈ 55 m behind) sits behind it, so `startMetres` ≥ 62. The grid zone must be straight (no hairpin, radius ≥ 150 m), level (no crest/dip) and 10–12 m wide at every slot |
| `checkpointSpacingMetres` | normally 100 |
| `biome` | region/biome key: `mizuhana-foothills`, `kasumi-forest`, `kurogawa-reservoir`, `akebono-coast`, `hoshimi-uplands`, `tsukishiro-highland`, `amanagi-finale`, `hinode-campus` |
| `timeOfDay`, `surface` | Normal conditions: LightingPresets id (`day, late-afternoon, sunset, dusk, blue-hour, evening, night, pre-dawn, dawn, first-light`) and `dry/damp/wet` |
| `controlPoints[]` | `{id, p:[x,y,z], width, bank, shoulderLeft, shoulderRight}` — centripetal Catmull-Rom through the points. `bank` degrees, + raises the LEFT edge (bank into right turns). Widths: 7–9 m ordinary, 6 m telegraphed technical, 10–12 m grids/passing. Shoulders 1–4 m. |
| `sectors[]` | `{id, name, startMetres}` — at least three; first starts at 0 |
| `gates[]` | judged zones, see below |
| `landmarks[]` | `{id, name, atMetres, side: left/right/over, offsetMetres, kit, params}` — at least three |
| `sections[]` | `{kind: tunnel/viaduct/bridge, fromMetres, toMetres, style}` — tunnels get terrain above and a lining; viaducts/bridges get piers and no terrain fill underneath |
| `areas[]` | off-route flat areas `{id, kind: skid-pad/braking-lane/training-bay/apron/recovery-bay, centre:[x,y,z], size:[w,l], headingDeg}` (T00) |

Geometry rules checked by the validator: length within ±5% of the brief target; net elevation within ±3 m
(0 for closed loops); max sustained grade ≤ 12%; minimum centreline radius ≥ 12 m (hairpins) unless the brief
asks for less; minimum width ≥ 6 m; no self-intersection except at declared tunnel/bridge crossings (vertical
clearance ≥ 7 m); circuits close smoothly; `startMetres` ≥ 62 with each of the twelve grid slots on ≥ 10 m of paved
width (or its lateral offset min(2.4, width/4) clearing the edge by ≥ 1.3 m) inside a straight, level grid zone.
`node Tools/authoring/route-check.mjs` runs these checks and writes `Evidence/courses/route-stats.json`.

## Gate kinds

| kind | meaning (distances along the centreline, lateral offset + = right) |
|---|---|
| `apex` | pass within `lineTolerance` of `lineOffset` at `startMetres` (= `endMetres`) |
| `precision` | alternating precision gate, same as apex; used in sets |
| `drift-zone` | judged drift zone `startMetres..endMetres` with intended arc `lineOffset` ± `lineTolerance` |
| `clip-zone` | outer clip zone for drift judging (`lineOffset` usually toward the outside) |
| `transition-zone` | linked left/right transition for chains |
| `brake-zone` | braking target: reach `targetSpeedKmh` (± window) by `endMetres`, within the lane |
| `exit-speed` | speed at `endMetres` must be ≥ `targetSpeedKmh` |
| `lane` | pass through a lane (`lineOffset` ± `lineTolerance`) between start/end — e.g. outside-lane overtake |
| `overtake-zone`, `defence` | racecraft zones for challenges |
| `demo-zone` | teaching/demonstration zone (tutorial, CH19) |
| `contract` | S29 Four Signals contract sector (`id` Entry/Arc/Descent/Horizon) |
| `timing` | extra timing split |

Target speeds that need measured reference runs are written as `0` and filled by calibration later (never guessed).

## Landmark kits

| kit | params |
|---|---|
| `tea-shed`, `stone-bridge`, `lantern-row` | none (existing bespoke kits) |
| `structure` | `type` (workshop, shed, shelter, station, pavilion, office, hut, machine-hall, market, terminal, kiosk, house, shrine-roof, pump-house, switchyard, cabin, gallery), `width`, `depth`, `height`, `roof` (gable, hip, flat, curved, sawtooth), `material` (timber, concrete, brick, steel, stone) |
| `tower` | `type` (water-tank, mast, lighthouse, radio-dish, chimney, cooling-tower, beacon, pylon-line, crane, spillway-tower, observatory, transmitter, antenna), `height`, `count` |
| `crossing` | a structure the road passes over or under: `type` (steel-truss, lattice, covered-footbridge, overpass, two-level, maintenance-bridge, pipeline-arch, relay-arch, rail-trestle), `span` |
| `wall` | `type` (retaining, stone-stair, split-retaining, quarry-steps, snow-fence, sea-wall, breakwater, flood-marks, avalanche-gallery), `length`, `height` |
| `water` | `type` (canal, reservoir, sea, creek, waterfall, spillway, inlet), `extent` |
| `field` | `type` (tea-terraces, greenhouses, orchard, cedar-grove, pine-grove, white-pine-grove, rice-terraces), `extent` |
| `rail` | `type` (trestle, funicular, coast-railway, conveyor, cable-station), `length` |
| `sign` | `type` (flood-marker, memorial, placard, marshal-beacons, timing-board, mural, tunnel-marker), `count` |
| `gate` | `type` (cedar-gate, storm-gate, relay-arch, maintenance-gate, torii-style is NOT allowed — use original forms) |

Kits are original geometry recipes; landmarks must read as the named place, not as generic boxes.

Implementation (`Assets/Game/Runtime/Track/Generation/LandmarkKits*.cs`, V-071): every kit above is built from these
parameters. Rules the kits keep, checked for all courses by EditMode `LandmarkKitTests`:

- A landmark beside the road is pushed outward (never inward) until its footprint clears the **whole** course by the
  road's half width + shoulder + margin — hairpins and loops that pass behind it included. `offsetMetres` is from the
  road centre to the landmark centre.
- Nothing is built over the paved road or shoulders unless it clears the road's highest point there by
  `LandmarkKits.OverheadClearance` (6.5 m) — crossings, the tunnel-marker gantry, a funicular or conveyor that has to
  pass over another leg of the road — or lies below it (open water).
- Colliders are on the Scenery layer only (the cameras see them, the cars never do); far, purely visual pieces have none,
  and the dedicated server (collision-only generation) skips water and fields entirely. The bespoke C01 stone bridge is
  the one exception: its parapets are the road's barrier there.
- A `crossing` inside a `bridge`/`viaduct` section, or an `avalanche-gallery` wall inside a `tunnel` section, is the road's
  own structure: the bridge/tunnel builders draw it from the section style and the kit adds nothing.
- Open water (`sea`, `reservoir`) is a plane that starts at the authored offset and runs away from the road, at a level
  below every road inside it; channels beside the road (`canal`, `creek`, `spillway`, a waterfall's pool) are carved into
  the terrain at planning time, and skipped where the course doubles back to within reach.
- Everything is deterministic per course and landmark id. Editor: *Night Signal → Art → Render Landmark Sheets*
  photographs every landmark from its road (`Builds/Screenshots/landmarks/`).

## Regional kits

Each route's `biome` selects a regional kit (`LandmarkKits.Regional.cs`) scattered along the whole course after the
landmarks, both sides, in rows outward from the barrier:

| biome | region | kit |
|---|---|---|
| `mizuhana-foothills` | C01–C04 | broadleaf trees, bamboo clumps, clipped hedges (plus the terrain's tea rows) |
| `kasumi-forest` | C05–C08, FP02 | dense cedar forest, the back rows simplified |
| `kurogawa-reservoir` | C09–C12 | conifers and rock outcrops |
| `akebono-coast` | C13–C16, FP01 | wind-bent coastal pines, shore rock |
| `hoshimi-uplands` | C17–C20, FP03 | rock outcrops, sparse shrubs, a timber pole line with wires |
| `tsukishiro-highland`, `amanagi-finale` | C21–C25 | firs, outcrops, lingering snow, red-and-white snow poles at the road edge |
| `hinode-campus` | T00 | lamp posts over the road edge (heads above the clearance), hedges |

Rules: visual only (no colliders; not built for the collision-only server); every prop keeps its own reach (crown radius,
clump, hedge length) clear of every leg of the road, stays out of landmark footprints and open water, and nothing grows
under a bridge or viaduct deck; wires are not strung across a road; built in 250 m chunks so the cameras cull them.
`LandmarkKitTests` holds the scatter to the same no-intrusion rule as the landmarks. *Render Landmark Sheets* also has
`LandmarkSheet.RenderRegions` — three road views per course, one sheet per biome — for the regional comparison.
