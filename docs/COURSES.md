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
| `startMetres` | start line distance; the six-slot grid sits behind it (needs ≥ 34 m before it) |
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
clearance ≥ 7 m); circuits close smoothly.

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
