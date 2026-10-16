# Challenge trials — the fixed-loaner format (spec §11, Appendix E)

Spec §11: all 75 challenges are achievable by one player "through sanctioned races, ghosts, AI, the meet's built-in
interaction spots, and fixed loaners". 37 are judged from ordinary races, the meet and the diary (REQUIREMENTS R11.3).
The other 38 name a *supplied* car or build, a *fixed* reference, scripted AI, a fixed cup or a T00 drill — they need
an event whose conditions are fixed by the content, not by the player's garage. That event is a **challenge trial**.

## Model

- `Assets/Content/Data/authored/challenge-trials.json` (content-hashed like every authored file): one trial per
  challenge run —
  - `id` (`TR-CH55`), `challenge` (`CH55`), `course`, `laps` (circuits), `conditions` (`course` = the course's own, or
    an explicit surface such as `wet`);
  - `loaner`: `car` + `parts` (authored part ids resolved by Core Builds into a physics spec) — never the player's
    garage build; its PI is shown; a class cap where the predicate names one;
  - `rules`: `noReset`, `maxWallImpacts`, `noHandbrakeAfterStart`, …;
  - `targets`: `timeMs` and/or `driftRaw` at the tier the predicate names;
  - `group`: trials that must all pass for one challenge (CH54: two drive layouts, each against its own target).
  - racecraft trials (`kind: "race"`, V-125): a fixed AI `field` (each car's stock model, an optional rival identity, a
    role — field / pacing / pressure / merge — and a pace), `playerStartsLast`, and the rules `win`, `noCarContact`,
    `noCheckpointCut`, `cleanZonePass` (the course's overtake zone tagged with the challenge); no targets.
- Targets are **measured, not guessed**: the explicit PlayMode `ChallengeTrialReferenceTests` drives each loaner with the
  validator autopilot and writes the targets with the method stated in the file — time: Gold 1.02 ×, Silver 1.10 ×
  (as CH10), Bronze 1.20 × the autopilot's time; drift trials run at drift skills 0.95, 0.80 and 0.65 and use the
  cleanest run (fewest resets, then the most scored), time and drift from that one run, drift targets from the raw it
  scored in the zones (banked or earned, the larger — as V-113, so points lost to walls never soften a target): Gold ×
  1.00, Silver × 0.85, Bronze × 0.70; the reference's drift skill is published so a replay can reproduce it. Automation,
  not a human benchmark; rerun after physics, route, part or car changes (`Evidence/challenges/trials.txt`).
- Judged by Core `TrialJudge` (engine-free) from the run's facts (finish, time, resets, meaningful wall impacts,
  banked raw drift, handbrake after the start), with the reason for each failed condition.

## Running a trial

- **Offline** (first): Offline hub → Challenges → the list (name, tier, predicate text, loaner and PI, target, done or
  not) → Start: a solo, non-contact run on the trial's course with the loaner's spec and conditions; the result names
  each condition passed or failed; a pass grants the challenge in the Local profile through the ordinary Local event
  facts (`ChallengesCompleted`), once.
- **Online** (V-120): the Challenges intent offers the trials beside the Team Trials (`event.propose {challengeTrialId}`);
  a trial is a non-contact, AI-free Time Attack on its course that needs no sponsor; the start freezes the loaner into
  every entrant (resolved by the control plane like a garage build, re-resolved and verified by the game server); the game
  server judges each human with the same `TrialJudge`; settlement writes the verdict on the receipt, replays the account's
  settled passes and grants the challenge once — the existing one-time challenge ledger. Trials with their own conditions
  are refused online until the server supports them.

## Slices

1. **Fixed-loaner time and drift trials** (done offline, V-119, and online, V-120): CH55 (C04, stock V01, Silver), CH11 (C12, class-capped loaner,
   Gold, no reset, at most one meaningful wall impact), CH51 (C08, the supplied wet tune, Silver), CH54 (C09, two
   equal-PI drive layouts, Silver each), CH25 (C16 drift loaner, Silver drift, no handbrake after the start), CH28 (C23
   fixed build, time and raw drift targets in one run), CH30 (C25 drift route, raw target and a banked chain in every
   judged sector).
2. Geometry judges: the paved-road rule and a fixed Gold ghost (CH13 — done, V-121: a trial may race its reference run as a
   gold ghost from `Resources/TrialGhosts`; the validator's C21 run cuts two apexes by up to 0.13 m, so CH13 is not yet
   shown reachable within its rule); final-sector apex gates on C25 (CH15 — done, V-122: the gates were already in the route and judged); CH17, CH19, CH22 and
   CH27 — done, V-123, and **not as trials**: none names a loaner, so they are judged in any race on their course by Core
   `ZoneChainRun` (the routes' link, demonstration, clip and transition zones linked by one continuous slide, a bank gate,
   a 20–35° hold); the autopilot reaches CH19, not yet CH17, CH22, CH27.
3. Scripted AI: pacing, pressure and merge cars (CH36, CH37, CH39, CH40, CH41, CH43, CH59). CH34 needs no fixed field —
   done in ordinary C02 races (V-124): a pass inside the marked hairpin exit zone, held to the retain gate. CH40 (C17's
   fixed race) and CH41 (six identical V07s on C18, from last) — done offline as racecraft trials (V-125); CH36 (a pacing
   rival that keeps C10's outside lane open) and CH39 (a pressure car within 1 s through C14's second sector at a measured
   Silver pace) — V-126. CH37 (the T00 merge beside a "merge" pace car, each in its own MERGE lane, no touch) — V-134.
4. Fixed cups (CH14, CH42, CH69, CH72) — done offline (V-127): the "cup" kind — three legs in the loaner, one continuous
   session through the Custom Cup page, judged as a whole (every leg finished in order, each inside its measured time ×
   the cup's published factor, walls and resets summed); leaving ends the cup.
5. T00 challenge drills and workshop trials (CH02, CH07, CH23, CH46, CH47, CH49, CH52, CH53, CH56, CH57, CH58, CH60),
   and CH74 (the six story records, then the C24 reference trial). Done: CH52/CH58 sections (V-129), CH53 (V-130), CH07
   (V-131), CH23 (V-132, not reached), CH74 (V-133), CH49 with a manual gearbox (V-135), CH46 (V-136), CH57 (V-137), CH56
   (V-138). CH59 is a racecraft trial with a tunable loaner (V-139). CH02 and CH47 are braking-lane lessons (kind "lane",
   V-140): T00's braking lane with the Test Yard's lane measurement, judged from every start and kept without an event —
   the Test Yard itself grants nothing (Addendum 02 §10.1). CH60 is a group of ten tunable T00 trials, one per system,
   each with `changedControls` and a marked-section driving check at 1.10 × (V-141).

### Tunable loaners (V-136)

A loaner may carry `choices` (free alternative parts by slot; the supplied part stays allowed), `tunable` (the installed
parts' tuning is the player's) and `piBudget` (0 = none). The player sets it up on **Tune the Loaner** and saves it with the
Local profile (`TrialSetups`, one per trial, no money); every run races the saved setup, resolved by Core
`TrialLoaners.ResolveSetup` exactly like a garage build. The judge adds "your setup legal (within the budget)", and the rules
`finalDriveChanged` (CH46) and `aeroNotAtExtreme` (CH57: the wing level below its top and the balance inside its range).
Tunable trials are offline only until the game server can take a player's setup.

## Judged but not yet shown reachable (11)

All 75 challenges are judged; these eleven have not been reached by the validator autopilot in a built player (Gate 5
"all 75 challenges proven achievable" stays open for them). Not reaching one is an automation limit, not proof it cannot be
done — a person's run would count. Grouped by the automation gap behind them (compiled 2026-10-16 from the entries cited):

| Gap | Challenges | What the runs showed | The work that would close it |
|---|---|---|---|
| Sustained drift that manages the road edge | CH17, CH22, CH27 (zone chains), CH28 (drift trial), CH23 (recovery drill) | CH17 banks LINK-1 + LINK-2 then runs out of road before LINK-3; CH22's slide meets the barrier at the first clip; CH27's slide ends in the first transition (V-123). CH28: best 4:28.657 inside the time but 740 of the 2,000 raw banked, at every skill (V-119). CH23: 10 / 19 / 11 recoveries at three skills, never four alternating without a break (V-132) | a drift controller that holds angle and line to the clip and plans the exit before the edge (HANDOFF item 3); for CH23 a deliberate slide-and-catch drill mode |
| Overtaking racecraft | CH34 (a pass held through C02's hairpin exit zone), CH36 (a pass in C10's open outside lane), CH40, CH41 (fixed AI fields) | the autopilot makes no pass in C02's marked zone (V-124) or C10's lane (V-126); CH40: no marked overtake and one car contact; CH41: P5 with two contacts (V-125) | pass planning (pick the zone, set up the exit, commit) with contact avoidance in the validator autopilot |
| Line precision on the paved rule | CH13 | the Gold-pace run leaves the paved road for 0.5 s at two corners (≈4,620 m, ≈4,830 m; up to 0.13 m over); a slower pace, a narrower apex line and a wider edge margin were all measured and all slower (V-121) | an apex line that respects the paved half-width at those two corners without the measured time loss |
| Pace against a rival's own run | CH43 | both defence gates driven inside the corridor, but 2:55.140 — 7.9 s slower than R32's own run in the same car on damp C20 (V-128) | a faster validator in the damp (line, braking) — R32's profile out-drives it |
