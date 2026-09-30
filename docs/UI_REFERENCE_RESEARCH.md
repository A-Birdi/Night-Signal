# UI reference research — "polished and good" versus "functional, AI-made"

Status: research notes (2026-09-30), the input to spec §15's short design decision record. Nothing here changes the game
yet; the owner asked for understanding first and will add their own references (videos, images) as feedback. Method:
still screenshots of shipped menus (the Game UI Database's per-screen captures), one long-form UI breakdown, and public
design writing. Limits: stills only — motion, sound and responsiveness were not measured from video; no user testing
(§15: reference research is not proof of user testing).

## What the references do (by screen job)

| Reference (screen) | What it does | Principle taken | Serves which Night Signal job | Not copied |
|---|---|---|---|---|
| Ridge Racer Type 4 (whole front end) — [breakdown](https://uxdesign.cc/ui-breakdown-ridge-racer-type-4-3df11df52aa6) | One palette, one bold type system, precise layouts; screens flow into each other with a rhythm; story told *inside* the UI space (portrait frames over real scenes); failure worded kindly | **Coherence and a single emotional motif carried into every screen**; the UI frames the content, it is not the content | Every screen; the story/rival intros; Results on a loss | The yellow/black palette, the typefaces, the team-select structure |
| Forza Horizon 5 — [home](https://www.gameuidatabase.com/uploads/Forza-Horizon-511112021-124739-18030.jpg), [event entry](https://www.gameuidatabase.com/uploads/Forza-Horizon-511112021-124733-94701.jpg), [finish](https://www.gameuidatabase.com/uploads/Forza-Horizon-511112021-124735-5101.jpg) | A slim status strip (level, car class + PI, car; credits, next goal); shoulder-button tabs with badges; one live hero tile; the event entry puts the car in the real place across two-thirds of the screen with a small option grid; locked options stay visible with a lock; the finish is one huge statement before any table | **The car/place is the focal object; menus are compact and sit beside it**; **stage the outcome first** (one beat), details after; show locked things with the reason | Home/Convoy, Freeplay setup, Results | The festival palette, tile grid wholesale, the "accolades" economy |
| Gran Turismo 7 — [world map](https://www.gameuidatabase.com/uploads/Gran-Turismo-709052026-013206-63471.jpg), [pre-race](https://www.gameuidatabase.com/uploads/Gran-Turismo-709052026-012949-56165.jpg) | A dense but calm status bar: small grey labels over white values (current goal, credits, current car with PP, level, clock); the map is the hero with a location card; a bottom drawer lists races with a track outline and PP limit. Pre-race: a full-bleed photo of the place, the course outline as a clean white line with a start marker, a small car credential card, one row of pill actions with the primary called out | **Label/value typography** (quiet labels, loud values); **the route line as the signature graphic**; **a persistent pinned goal**; one action row, one primary | Top bar, Campaign map, Freeplay/trial pre-race, Challenges' pinned goals | The Menu Book idea and naming, GT's layouts, logos, the photo-mode look |
| Need for Speed Unbound — [pre-race card](https://www.gameuidatabase.com/uploads/Need-for-Speed-Unbound11302022-125030-16244.jpg), [threat dossier](https://www.gameuidatabase.com/uploads/Need-for-Speed-Unbound11302022-124800-69199.jpg) | A street-print identity applied everywhere (crop marks, textured ground); one giant heading and one line of objective; ratings as icons (heat flames); a "dossier" for a threat: lit 3D subject left, form-style credential fields right, a boxed tactics list, one Continue | **An original motif used structurally, not as decoration**; **one message per moment**; **rival/threat as a credential dossier** | Rival intros and cards, pre-race objective, Challenges | The graffiti/print style, crop marks, heat icons, the typeface |
| Mario Kart World — [select](https://www.gameuidatabase.com/uploads/Mario-Kart-World07302025-094557-96392.jpg) | Big portraits; an unmistakable animated bracket focus; the focused choice previewed instantly and large in 3D; a motif-shaped nameplate; the live course blurred behind | **Focus is physical and immediate** (the preview reacts the same frame); the selection is the loudest thing on screen | Car select, Garage part choice, rival selection | The cartoon style, rounded tiles, tyre-tread plate |
| Wipeout (1995) — [mode select](https://www.gameuidatabase.com/uploads/Wipeout05312026-105318-21749.jpg); [The Designers Republic](https://www.thedesignersrepublic.com/project/wipeout) | One custom display face for everything; the craft rotates as the focal object; three choices; faint team emblems behind | **Type discipline** — one display face, a few sizes; identity from graphic design, not effects | Headings, the title screen | The typefaces, the future-sport identity |

## "Polished" versus "functional AI-UI" — the tells, and where Night Signal stands today

Honest reading of our own screens as of V-146 (Offline hub, Challenge Trials, Tune the Loaner, the braking-lane panel,
Results):

| Polished | Functional / AI-made (our current tells) |
|---|---|
| One focal object (car, route, rival) and the UI as its frame | Large dark panels full of stacked, equal-width, equal-weight buttons (the Offline hub's left column is ten identical buttons) — the "row of template buttons" §15 warns about |
| One primary action per screen, visibly dominant | "Start" is one more button among many |
| A clear type hierarchy: quiet labels, loud values, a few sizes, tabular numbers | Most text the same size and weight; sentences where a label + value would do |
| Information shown as graphics: route lines, ratings, gauges, portraits | Information shown as prose ("ok: … · MISSED: …" verdict strings read like a log) |
| Previews that react to focus (the car, the course line, the rival) | `< value >` steppers for course, car, format and opponents with no preview of what they change |
| Outcome staged as a beat, then detail, then itemised rewards | Everything at once on one panel |
| Focus that is physical: scale, brackets, light, sound, the same frame | A thin red left bar |
| Locked items visible with the reason | Mostly visible with a reason already (keep this) |
| One motif applied structurally across every screen | The Signal/Sector palette and type are consistent (a real strength), but the motif objects (timing slips, route charts, pit boards, start lights, radio labels, credentials) are barely used yet |
| The live world behind menus, meaningfully | A generic course view behind panels |

What already works and should survive: the palette and the reserved function colours (§15), the persistent top strip,
readable sizes at Text 150 % (bounds audits), controller prompts, honest locked reasons, no fake urgency.

## A direction to test later (hypotheses, not decisions)

These carry the principles above in Night Signal's own motif (§15: timing slips, route charts, tachometer markings, pit
boards, start lights, radio labels, driver credentials). None is to be built until the owner has reacted to it.

1. **The route line as signature.** Every course reference (hub, campaign, pre-race, results, trials) draws the course's
   real centreline as one clean line with the start marker and sector ticks — from the route data we already have — the way a
   timing sheet prints a circuit.
2. **Timing-slip results.** The finish is one beat ("CLEAR" / "P2 — 3:40.805" in large tabular type), then the slip prints:
   sector splits with deltas in cyan, incidents in amber, then itemised money/RP/unlocks — the challenge verdicts become ticked
   lines on the slip, not a sentence.
3. **Start lights as confirmation.** The primary action of a screen (Start, Ready, Apply) lights like a start gantry; holding it
   or the countdown-to-launch fills the lights — a motif that also says "committed".
4. **Pit-board headers.** Section titles as pit-board slabs (big condensed numerals/letters on graphite), used sparingly.
5. **Driver credentials.** Rival intros and the Player Card as laminated credential passes: portrait/3D left, fields right
   (tendency, car, class, home course, record against you), one line of voice.
6. **Radio labels.** Small uppercase tags (like the frequency labels in the brief's fiction) for status: CONVOY, READY, AWAY,
   LOCAL — replacing some sentences.
7. **The car as the hub's hero.** The Offline hub and Freeplay setup put the selected car (or the course) centre stage and move
   the menu into a compact column/strip beside it; steppers become previews (the course line, the car turning to face the
   camera).
8. **Motion with rhythm.** Keep the sector wipe; add a consistent enter order (header → hero → details → actions, ~40 ms
   apart), a crisp focus response in the same frame as input, and one sound per focus step and one per confirm. Reduced Motion
   keeps the order without the travel.

## What would help from the owner

- Two or three screens (any game, any medium) that feel right to you, and one or two that feel wrong — and a word on why.
- Whether the tone should lean warm/communal (the tea stalls, workshops and radio of the setting) or cool/technical
  (timing sheets, engineering) — the brief supports both; the menus should pick one lead.
- Any video you like for motion: I can reason about the enter order, timing and focus behaviour from specific moments you
  point to.

## Sources

- SYH, "UI Breakdown: Ridge Racer Type 4", UX Collective, 2024 — <https://uxdesign.cc/ui-breakdown-ridge-racer-type-4-3df11df52aa6>
- Game UI Database (Edd Coates), per-screen captures: Forza Horizon 5 <https://www.gameuidatabase.com/gameData.php?id=1225>,
  Gran Turismo 7 <https://www.gameuidatabase.com/gameData.php?id=2543>, Need for Speed Unbound
  <https://www.gameuidatabase.com/gameData.php?id=1586>, Mario Kart World <https://www.gameuidatabase.com/gameData.php?id=2104>,
  Wipeout <https://www.gameuidatabase.com/gameData.php?id=2415>
- The Designers Republic, Wipeout — <https://www.thedesignersrepublic.com/project/wipeout>; Creative Bloq, "How a PlayStation
  game inspired a generation of graphic designers" — <https://www.creativebloq.com/news/wipeout-design-inspiration>
- Gran Turismo Wiki, "Menu Styles" — <https://gran-turismo.fandom.com/wiki/Menu_Styles>
- GTPlanet, Tokyo Xtreme Racer 0.13 (toggleable UI, gauge layouts) — <https://www.gtplanet.net/tokyo-xtreme-racer-update-013-achievements-20250721/>

No reference images are stored in this repository; links point to the sources.
