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
- Targets are **measured, not guessed**: an explicit PlayMode run drives each loaner with the validator autopilot
  (as `ChallengeReferenceTests` does for V-113) and writes the targets with the method stated in the file — Gold
  1.02 ×, Silver 1.10 × (as CH10), Bronze 1.20 × the autopilot's time; drift targets from the autopilot's raw score
  (Gold at drift skill 0.95, as V-113). Automation, not a human benchmark; rerun after physics or route changes.
- Judged by Core `TrialJudge` (engine-free) from the run's facts (finish, time, resets, meaningful wall impacts,
  banked raw drift, handbrake after the start), with the reason for each failed condition.

## Running a trial

- **Offline** (first): Offline hub → Challenges → the list (name, tier, predicate text, loaner and PI, target, done or
  not) → Start: a solo, non-contact run on the trial's course with the loaner's spec and conditions; the result names
  each condition passed or failed; a pass grants the challenge in the Local profile through the ordinary Local event
  facts (`ChallengesCompleted`), once.
- **Online** (next): the Challenges intent lists the trials beside the Team Trials; the plan freezes the trial's loaner
  (no garage build), the game server judges with the same `TrialJudge`, settlement grants once — the existing
  one-time challenge ledger.

## Slices

1. **Fixed-loaner time and drift trials** (this slice): CH55 (C04, stock V01, Silver), CH11 (C12, class-capped loaner,
   Gold, no reset, at most one meaningful wall impact), CH51 (C08, the supplied wet tune, Silver), CH54 (C09, two
   equal-PI drive layouts, Silver each), CH25 (C16 drift loaner, Silver drift, no handbrake after the start), CH28 (C23
   fixed build, time and raw drift targets in one run), CH30 (C25 drift route, raw target and a banked chain in every
   judged sector).
2. Geometry judges: a paved-corridor judge (CH13, with C21's Gold ghost), final-sector apex gates on C25 (CH15), drift
   zones authored for C03, C05, C09 and C19 (CH17, CH19, CH22, CH27).
3. Scripted AI: pacing, pressure and merge cars (CH34, CH36, CH37, CH39, CH40, CH41, CH43, CH59).
4. Fixed cups on the Custom Cup table (CH14, CH42, CH69, CH72).
5. T00 challenge drills and workshop trials (CH02, CH07, CH23, CH46, CH47, CH49, CH52, CH53, CH56, CH57, CH58, CH60),
   and CH74 (the six story records, then the C24 reference trial).
