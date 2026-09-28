# Night Signal — audio

Original music, engine and UI sound for *Night Signal: Mountain Circuit*, built as **deterministic runtime
synthesis driven by authored score data**. No samples, no rendered clips, no third-party music or sound
material: every sound comes from our own oscillators, filters, envelopes and drum models in
`Assets/Game/Audio`, and every note comes from the scores in `Assets/Content/Audio/Scores`.

Why synthesis instead of rendered WAV: 24 cues of 1.5–2.5 minutes would add hundreds of megabytes of audio
to the repository/LFS. The scores are ~10–20 KB of JSON each, and the synth renders them in real time
for about 2–4 % of one CPU core per cue (measured; see *Checks*).

> **Listening quality has not been verified by a person.** Everything below marked *measured* is an
> objective check (peak, loudness, loop seam, determinism, CPU, spectral distinctness, harmony audit).
> Whether the music is good, whether the engines are convincing, and the final mix balance in the game
> must be judged by ear in the editor.

## Layout

| Path | What |
|---|---|
| `Assets/Game/Audio/` | Engine-free synth assembly `NightSignal.AudioSynth` (`noEngineReferences: true`, no references). |
| `Assets/Game/Audio/Dsp/` | polyBLEP oscillators, ADSR, Simper SVF, one-pole/DC filters, Freeverb-style reverb, ping-pong delay, look-ahead limiter, lock-free SPSC ring, xorshift RNG, lookup tables. |
| `Assets/Game/Audio/Synth/` | Instrument presets (data), melodic voice (subtractive / supersaw / 2-op FM / Karplus-Strong pluck), drum voice, track channel. |
| `Assets/Game/Audio/Score/` | Minimal JSON reader, music theory (chords, voicings, progressions), score compiler, compiled score model. |
| `Assets/Game/Audio/Player/` | `ScorePlayer` (sequencer + mixer + FX + limiter), `MusicDeck` (crossfade/fade/duck/volume), `MusicCueIds`. |
| `Assets/Game/Audio/Vehicle/` | Engine families, `EngineVoice`, `RoadVoice` (tyres/kerbs/scrape/wind), `ImpactBank`, `VehicleSoundModel` (three buses). |
| `Assets/Game/Audio/Sfx/` | UI cue designs (`UiCueLibrary`) and `SfxEngine`. |
| `Assets/Game/Runtime/Audio/` | Thin Unity wrappers (namespace `NightSignal.GameAudio`, compiled in `NightSignal.Runtime`): `MusicPlayer`, `EngineAudio` + `VehicleBusFilter`, `SfxPlayer`, `GameAudioSettings`, `ProceduralAudio`. |
| `Assets/Content/Audio/Scores/` | `instruments.json` (32 presets) and 24 cue scores (`mus_*.json`). |
| `Assets/Content/Data/authored/music.cues.json` | Generated cue manifest: id, titles, category, allowed contexts, loop points, loudness. |
| `Tools/audio/` | `AudioSynth` (netstandard2.1 / C# 9 compile of the linked synth sources = Unity's API level) and `AudioTool` (`ns-audio` console: render + checks). Renders go to `Tools/audio/out/` (git-ignored). |

## Engine-free synth: rules

- **C# 9 / .NET Standard 2.1** — verified by `Tools/audio/AudioSynth` (warnings as errors). No file-scoped
  namespaces, global usings, records or init-only setters.
- **Allocation-free on the audio thread.** Players, voices, delay/reverb buffers and limiters are allocated
  on construction (main thread). `Render` never allocates, locks or throws. Commands from the game thread
  (play, stop, duck, impacts, UI cues, rev blips) cross through `SpscRing<T>`; plain per-frame inputs (rpm,
  throttle, speed…) are float fields that the audio side smooths.
- **Deterministic.** All noise is seeded xorshift; the sequencer renders fixed 32-sample internal blocks
  behind an output buffer, so output is bit-identical whatever the host's buffer size (*measured*: every
  cue renders identically with 1024- and 333-frame host buffers).
- **Never clips.** Every output path ends in a look-ahead limiter whose gain is the box-filtered minimum
  of the required gains over the look-ahead window (a provable ceiling, default −1 dBFS) plus a
  −0.3 dBFS safety clamp. The music deck's crossfades use equal in/out rates so summed gains never exceed 1.
- **Seamless loops.** The form is `intro sections (once) → loop sections (repeat)`. At the loop end the
  sequencer jumps back in tick space; voices, reverb and delay tails keep running, so there is no seam.

## Score format — `night-signal/score@1`

```jsonc
{
  "schema": "night-signal/score@1",
  "id": "MUS_EXAMPLE", "title": "...", "description": "...", "style": "...", "key": "A minor",
  "tempo": 150, "timeSignature": [4, 4], "stepsPerBeat": 4, "swing": 0.0,      // 16th-note grid, quarter-note BPM
  "motifs": ["M1 Call Sign — ..."],
  "cue": { "displayTitle": "...", "category": "menu", "contexts": ["menu", "boombox"] },  // manifest metadata
  "mix": { "master": -4.0, "ceiling": -1,
           "reverb": { "size": 0.7, "damp": 0.45, "width": 1, "wet": 0.25 },
           "delay": { "beats": 0.75, "feedback": 0.3, "damp": 3500, "wet": 0.5, "toReverb": 0.3 },
           "duckRelease": 0.4 },                                  // sidechain release in beats
  "instruments": { },                                             // optional per-score preset overrides
  "tracks": [ { "id": "lead", "instrument": "lead_supersaw", "gain": 0, "pan": 0, "reverb": 0.2,
                "delay": 0.2, "duck": 0.1, "drive": 0, "driveTone": 5000, "sidechain": false } ],
  "patterns": { "name": { "track": "lead", "bars": 8, "<kind>": ... } },
  "sections": { "A": { "bars": 16, "transpose": 0, "play": [ "pattern", "pattern@8*1", "pattern*1^2" ] } },
  "form": ["intro", "A", "B"], "loopFrom": "A"
}
```

Pattern kinds (exactly one per pattern):

- `melody` — authored note by note: `"A4:2 A4:2 E5:4 | D5:8 r:8"` — `pitch:steps`, `r` rest, `-` tie,
  `|` bar line (**checked**: each bar must sum to the bar length), `t` suffix for triplets (`2t`),
  modifiers `>` accent, `'` soft, `~` glide into the note, `*` staccato.
- `notes` — `[[step, length, "C5" | midi, velocity?, "~"?], ...]`.
- `drums` — one step string per lane (`kick snare clap chh ohh ride crash tomh tomm toml rim shaker`):
  `x` hit, `X` accent, `o` ghost, `g` very soft, `.`/`-` rest.
- `chords` — a progression (`"Am | F G | C:12 G:4 | %"`) voiced with smooth voice-leading (`"voicing": "lead"`)
  or close position, struck by a rhythm string (`x` strike, `-` hold, `.` silence).
- `bass` — a progression plus a rhythm of chord degrees: `R` root/slash bass, `o`/`O` octaves, `5`, `f`,
  `3`, `7`, `4`, `6`, `2`, `L` (fifth below), `a`/`A` chromatic approach to the next chord; or a named
  style (`whole`, `half`, `root4`, `pulse8`, `octave8`, `offbeat`, `drive16`).
- `arp` — a progression plus an index pattern over the chord tones (`"0 1 2 3 2 1"`, `.` rest), rate in
  steps, reset per bar / per chord / never (the TT_DRIFT six-note cycle uses `never` so it rotates).

Placement strings: `name` (repeat to fill the section), `name@bar`, `name@bar*times`, `name*times`,
`^semitones` transposition. A section `transpose` shifts every pitched pattern (used for final-chorus key
changes). The compiler rejects unknown instruments/patterns, bad notes, wrong bar lengths and malformed
rhythms, and warns when two pitched placements overlap on one track.

Chord-based helpers are deterministic expansions of **authored progressions** — the harmony, bass rhythms,
arpeggio shapes and every melody are written in the scores. Nothing is random.

## Instrument presets — `night-signal/instruments@1`

`instruments.json` defines 32 presets with single inheritance (`"extends"`): supersaw leads, mono saw and
square leads, FM brass, soft triangle lead, breathy whistle, saw/FM plucks, FM bell, FM electric piano,
organ, FM marimba, warm/air/glass pads, strings, supersaw stabs, octave/sub/reese/picked/acid/funk basses,
Karplus-Strong power and clean guitars (overdrive is a track insert), a noise riser, and five drum kits
(`kit_euro`, `kit_breaks`, `kit_rock`, `kit_soft`, `kit_electro`) whose pieces are synthesized (pitch-swept
sine kicks and toms, tone+noise snares, multi-burst claps, six-oscillator metallic hats/cymbals).

## The cues (24)

Stable IDs are in `MusicCueIds` and the manifest. *Intro* plays once; *Loop* is the repeating body (all
main loops ≥ 93 s; the two results treatments are shorter by design). Durations are exact score lengths.

| ID | Title | Spoiler-safe display title | Category | BPM | Key | Intro s | Loop s | Form | Allowed contexts |
|---|---|---|---|---|---|---|---|---|---|
| `MUS_TITLE` | Night Signal (Main Title) | Night Signal — Main Title | title | 118 | A minor | 16.3 | 130.2 | (intro:8) A:16 B:8 C:16 D:8 C2:16 | title, boombox |
| `MUS_MENU_A` | Convoy Frequency | Convoy Frequency | menu | 148 | F# minor | 13.0 | 103.8 | (intro:8) A:16 B:8 C:16 D:8 C2:16 | menu, campaign-map, freeplay-lobby, challenges, boombox |
| `MUS_MENU_B` | Map Room Overdrive | Map Room Overdrive | menu | 140 | D minor | 13.7 | 109.7 | (intro:8) A:16 B:8 C:16 D:8 C2:16 | menu, campaign-map, freeplay-lobby, challenges, boombox |
| `MUS_GARAGE` | Workbench Hours | Workbench Hours | garage | 100 | F major | 9.6 | 115.2 | (intro:4) A:16 B:8 A3:8 C:8 A4:8 | garage, boombox |
| `MUS_MEET` | Cedar Lantern Terrace | Cedar Lantern Terrace | meet | 84 (3/4) | E major | 8.6 | 102.9 | (intro:4) A:8 A2:8 B:8 C:8 A3:8 B2:8 | meet, boombox |
| `MUS_TUTORIAL` | Hinode Practice Loop | Hinode Practice Loop | tutorial | 110 | C major | 8.7 | 122.2 | (intro:4) A:8 A2:8 B:8 C:8 A3:8 B2:8 C2:8 | tutorial, boombox |
| `MUS_RESULTS_WIN` | Timing Slip (Victory) | Timing Slip — Victory | results | 124 | D major | 7.7 | 46.5 | (fanfare:4) A:8 B:8 A2:8 | results-win, boombox |
| `MUS_RESULTS_LOSS` | Timing Slip (Next Time) | Timing Slip — Next Time | results | 92 | B minor | 5.2 | 41.7 | (open:2) A:8 B:8 | results-loss, boombox |
| `MUS_RACE_MIZUHANA` | Lantern Rush | Lantern Rush | race-region | 154 | F# minor / A major | 12.5 | 99.7 | (intro:8) A:16 B:8 C:16 D:8 C2:16 | race-region-mizuhana, boombox |
| `MUS_RACE_KASUMI` | Mistglass Breaks | Mistglass Breaks | race-region | 172 | F minor (Dorian) | 11.2 | 100.5 | (intro:8) A:16 B:16 C:8 D:16 E:16 | race-region-kasumi, boombox |
| `MUS_RACE_KUROGAWA` | Spillway Drive | Spillway Drive | race-region | 146 | E minor | 13.2 | 105.2 | (intro:8) A:16 B:8 C:16 D:8 C2:16 | race-region-kurogawa, boombox |
| `MUS_RACE_AKEBONO` | Tide Lantern Coast | Tide Lantern Coast | race-region | 128 | C# minor / E major | 15.0 | 120.0 | (intro:8) A:16 B:8 C:16 D:8 C2:16 | race-region-akebono, boombox |
| `MUS_RACE_HOSHIMI` | Datum Line | Datum Line | race-region | 136 | G minor (Phrygian colour) | 14.1 | 112.9 | (intro:8) A:16 B:8 C:16 D:8 E:16 | race-region-hoshimi, boombox |
| `MUS_RACE_TSUKISHIRO` | Highland Relay | Highland Relay | race-region | 160 | D minor → E minor | 12.0 | 99.0 | (intro:8) A:16 B:8 C:16 D:8 C2:16 turn:2 | race-region-tsukishiro, boombox |
| `MUS_LT_DAIGO` | Brass Stopwatch | Mizuhana Lieutenant | lieutenant | 142 | G minor / Bb major | 13.5 | 108.2 | (intro:8) A:16 B:8 C:16 D:8 C2:16 | encounter-S07, boombox |
| `MUS_LT_EMI` | Barometer | Kasumi Lieutenant | lieutenant | 174 | C minor (Dorian) | 11.0 | 99.3 | (intro:8) A:16 B:16 C:8 D:16 E:16 | encounter-S14, boombox |
| `MUS_LT_JUN` | Measure Twice | Kurogawa Lieutenant | lieutenant | 150 | B minor | 12.8 | 102.4 | (intro:8) A:16 B:8 C:16 D:8 C2:16 | encounter-S21, boombox |
| `MUS_LT_MAKO` | Breakwater Bravado | Akebono Lieutenant | lieutenant | 158 | G# minor → A minor | 12.2 | 97.2 | (intro:8) A:16 B:8 C:16 D:8 C2:16 | encounter-S28, boombox |
| `MUS_PENULTIMATE` | Four Signals | Mastery Trial | penultimate | 152 | E minor | 12.6 | 101.1 | (intro:8) entry:16 arc:16 descent:8 horizon:16 coda:8 | encounter-S29, boombox |
| `MUS_FINAL_REINA` | The Surveyor | Final Encounter (Normal) | final-normal | 156 | C# minor (Phrygian) | 12.3 | 110.8 | (intro:8) A:16 B:8 C:16 D:8 E:8 C2:16 | encounter-S30-normal, boombox |
| `MUS_FINAL_SHIORI` | Zero Signal | Final Encounter (Hard) | final-hard | 132 | D major (Lydian) | 29.1 | 101.8 | (intro:16) A:16 B:8 C:16 D:8 E:8 | encounter-S30-hard, boombox |
| `MUS_TT_MEAN` | Six Abreast | Six Abreast | team-trial | 136 | G major | 14.1 | 112.9 | (intro:8) A:16 B:8 C:16 D:8 C2:16 | trial-TT_MEAN, boombox |
| `MUS_TT_BEST` | One Clean Lap | One Clean Lap | team-trial | 164 | A minor (Dorian) | 11.7 | 93.7 | (intro:8) A:16 B:8 C:16 D:8 C2:16 | trial-TT_BEST, boombox |
| `MUS_TT_DRIFT` | Angle of Six | Angle of Six | team-trial | 124 | Bb minor (Dorian) | 7.7 | 108.4 | (intro:4) A:16 B:8 C:16 D:8 E:8 | trial-TT_DRIFT, boombox |

Context strings are proposals for the coordinator's music-context router; unlock rules are deliberately
**not** in the manifest. Lieutenant/penultimate/final cues list only their encounter context plus
`boombox`, and the two finals are separate contexts (`encounter-S30-normal`, `encounter-S30-hard`) so a
course-ID lookup can never select a final theme. When spoiler protection applies, show `displayTitle`.

### Cue notes (style, instrumentation, motifs)

- **MUS_TITLE — Night Signal (Main Title).** Exclusive to the pre-login Main Menu. Synthwave anthem: bells
  alone state the Call Sign over pads, then supersaw verse, arpeggio pre-chorus, anthem chorus, bell
  breakdown, and a final chorus with a rising string counter-line. Motif: **M1 Call Sign**, full statement.
- **MUS_MENU_A — Convoy Frequency.** Signed-in menu/map/freeplay. Eurobeat-inspired hooks over a pumping
  four-on-the-floor: syncopated 3-3-2 verse on a portamento saw, leaping supersaw chorus, EP breakdown
  with bells, off-beat FM brass in the last chorus. M1 head as the intro stab hook.
- **MUS_MENU_B — Map Room Overdrive.** Contrasting menu theme with a rock arrangement: two overdriven
  Karplus-Strong guitars, picked bass, rock kit, organ, square lead; half-time bridge with the M1 head
  in G minor.
- **MUS_GARAGE — Workbench Hours.** Relaxed but awake jazz-funk with light swing: FM electric piano
  comping, walking funk bass with chromatic approaches, muted clean-guitar off-beats, soft kit, triangle
  lead playing M1 in F major, a Bb-minor-colour bridge and an EP solo chorus.
- **MUS_MEET — Cedar Lantern Terrace.** Inviting 3/4 dusk waltz: air pads, plucked FM arpeggio, sub bass,
  soft melody, rim/shaker only; bells slow M1 to a lullaby and first hint at **M3 Dawn Line**; Lydian
  bridge (Amaj7#11, borrowed Cmaj7).
- **MUS_TUTORIAL — Hinode Practice Loop.** Bright, uncluttered C-major groove (FM marimba eighths, light
  funk bass, soft kit); the M1-based tune drops out in alternate sections so lesson text stays readable.
- **MUS_RESULTS_WIN — Timing Slip (Victory).** Four-bar brass + supersaw fanfare (M1 in D major), then a
  relaxed house bed with an EP melody on alternate passes.
- **MUS_RESULTS_LOSS — Timing Slip (Next Time).** Warm, encouraging, non-punitive B-minor groove; M1 sung
  slowly in the minor, lifting toward G and A major. No buzzers.
- **MUS_RACE_MIZUHANA — Lantern Rush** (C01–C04). Bright eurobeat; brass **M4 Lantern Step** hook
  (bouncing major-pentatonic figure), F#-minor verses, A-major chorus, final chorus up a whole step.
- **MUS_RACE_KASUMI — Mistglass Breaks** (C05–C08). 172 BPM late-night breakbeat, half-time feel, reese
  bass, FM **M5 Rain Thread** cascades, breathy lead, second drop with a different break.
- **MUS_RACE_KUROGAWA — Spillway Drive** (C09–C12). Synth-rock: **M6 Spillway** riff (root-root-third-root-
  fourth-root-third-second) in bass and guitars, square-lead verse, supersaw chorus, square solo bridge.
- **MUS_RACE_AKEBONO — Tide Lantern Coast** (C13–C16). EDM / italo-disco house at 128: off-beat bass,
  EP stabs, sidechained pads, FM arps; **M7 Tide Swing** 3-3-2 major-seventh figure; organ in the recap.
- **MUS_RACE_HOSHIMI — Datum Line** (C17–C20). Electro-breaks: broken beat, resonant acid bass, gated
  arps, Phrygian colour; first appearance of **M2 Survey Grid** (rising fourths, semitone fall).
- **MUS_RACE_TSUKISHIRO — Highland Relay** (C21–C24). Largest regular anthem, 160 BPM eurobeat; M1 in the
  intro hook and chorus tag; string bridge with the first full **M3 Dawn Line**; last chorus in E minor,
  then a two-bar turnaround back to D minor for a clean loop.
- **MUS_LT_DAIGO — Brass Stopwatch** (S07, Daigo Ibuki). Stopwatch-tick intro, minor M4 brass fanfare,
  square-lead "lesson" verse, broad Bb-major chorus, braking-lesson breakdown with hard stops.
- **MUS_LT_EMI — Barometer** (S14, Emi Takanashi). 174 BPM breakbeat; M5 inverted into rising "pressure"
  cascades; calm, exact melody over Cm9–Abmaj7–Fm9; higher counter-line in the second verse.
- **MUS_LT_JUN — Measure Twice** (S21, Jun Saegusa). Electro-rock where kick, arpeggio, guitar and verse
  cells are grouped 3+3+2; M6 regrouped; Neapolitan C major in the bridge.
- **MUS_LT_MAKO — Breakwater Bravado** (S28, Mako Hoshino). Flashy eurobeat with brass and strings, minor
  M7 hook, stop-time breakdown with a harmonic-minor scale run, final chorus up a semitone.
- **MUS_PENULTIMATE — Four Signals** (S29). Suite matching the four judged contracts: Entry (M2,
  gated precision), Arc (swung circle of fifths + M5 cascades), Descent (half-time lament bass),
  Horizon (M1 + M3 anthem), coda layering the motifs. Intro rings each motif head once.
- **MUS_FINAL_REINA — The Surveyor** (Normal S30). Precise, cold, driving: C# Phrygian, machine-steady
  sixteenth bass, electro drums, gated stabs, glassy FM; M2 as her full hook; a lonely bell breakdown of
  stacked fourths; eight-bar build; final chorus with strings.
- **MUS_FINAL_SHIORI — Zero Signal** (Hard S30). A different full composition (not a variant of Reina's):
  restrained dawn theme in D Lydian — whistle states M3 over clean-guitar arpeggios, a half-time pulse
  gathers momentum, strings widen it, the full section answers M3 with a major-key M1; bell-only dawn break;
  final climb. Tempo, key, mode, instrumentation, harmony and melody all differ from The Surveyor.
- **MUS_TT_MEAN — Six Abreast.** House-rock; **M8 Convoy Six** (1-2-3-5-6-8) relayed between two leads,
  unison in the chorus ("every position counts").
- **MUS_TT_BEST — One Clean Lap.** Urgent 164 BPM sprint; continuous sixteenth bass; Dorian Convoy Six
  runs; triplet pre-chorus sweeps; hats never stop in the breakdown.
- **MUS_TT_DRIFT — Angle of Six.** Swung electro-house/funk in Bb Dorian; a six-note arpeggio cycles
  against the 4/4 bar so its accents rotate; swung Convoy Six hook.

### Motif catalogue (all original)

| Motif | Shape | Where |
|---|---|---|
| M1 Call Sign | 1-1-5 (short-short-long), falling answer 4-3-2-3 | Title (full), menus, garage, meet, tutorial, results, Tsukishiro, penultimate, Shiori (major answer) |
| M2 Survey Grid | rising perfect fourths in 16ths, then a semitone fall | Hoshimi (first), penultimate (Entry), Reina (full hook) |
| M3 Dawn Line | 5 → 1-2 → 3 (long-short-short-long), Lydian colour | Meet (hint), Tsukishiro bridge, penultimate (Horizon/coda), Shiori (full theme) |
| M4 Lantern Step | bouncing major-pentatonic 1-2-3-5-6-5-3-2 | Mizuhana; minor variant for Daigo |
| M5 Rain Thread | descending pentatonic 16th cascades | Kasumi; inverted (rising) for Emi; penultimate Arc |
| M6 Spillway | root-root-3rd-root-4th-root-3rd-2nd eighths | Kurogawa; 3+3+2 regrouping for Jun |
| M7 Tide Swing | syncopated 3-3-2 figure over major sevenths | Akebono; minor variant for Mako |
| M8 Convoy Six | six-note 1-2-3-5-6-8 figure | The three Team Trials |

Motifs recur as *re-harmonised, re-voiced, re-orchestrated arrangements*, never as copied tracks: no cue
reuses another cue's patterns.

## Engine and vehicle audio

`VehicleSoundModel` (one per car) has three independent buses, each with its own gain and limiter:

1. **Engine** — `EngineVoice`. One crank-cycle phase drives three layers so pitch tracks rpm with no drift:
   (a) 24 additive half-order partials (0.5–12 × crank frequency) via a Chebyshev recurrence, shaped by the
   family's order table and blended between **idle / load / coast** bands (spectral tilt, sub-order gain);
   (b) a raised-cosine exhaust pulse per combustion event with per-event amplitude jitter (roughness) and
   pulse-gated noise (rasp); (c) two exhaust formant resonators and a load-dependent tone filter.
   Plus intake roar with throttle, **turbo** whistle and spool noise scaled by boost, **flutter** or
   **blow-off valve** on lift, **gearshift** torque-cut and mechanical clack, **rev-limiter** fuel-cut gating
   with pops, overrun **crackle**, idle **lope**, and a **meet rev blip** (rate-limited in `EngineAudio`).
2. **Road** — `RoadVoice`: tonal tyre squeal on paved surfaces scaled by slip, granular crunch on
   shoulder/grass, rolling rumble by speed, kerb rumble-strip buzz, continuous wall scrape, and wind (its
   own gain, mapped to the Ambient setting).
3. **Impacts** — `ImpactBank`: suspension/kerb thumps and collisions whose loudness, body weight, metallic
   ring and debris scale with severity.

Families (`EngineFamilyKind` mirrors `NightSignal.Vehicle.EngineFamily`):

| Family | Firing order | Character (order content) |
|---|---|---|
| Inline-four | 2nd | strong 2nd/4th/6th, some 1st-order imbalance; buzzy |
| Six-cylinder | 3rd | 3rd/6th/9th, almost no low sub-orders; smooth, silky; valve blow-off when turbo |
| Compact triple | 1.5th | strong 0.5/1st orders under the 1.5th — off-beat thrum; rough |
| Rotary-like (2 rotors) | 2nd | nearly flat harmonic series, sharp exhaust pulses — buzzy rasp; lumpy idle, high limiter rate |

Each car's seed shifts formants ±7 %, so two cars of one family are not identical.

## UI / SFX

`UiCueLibrary` (all original, gentle): **SignalRibbon** — quiet rising fifth (A5 → E6 FM bells with a soft
octave shimmer, peak ≈ −17 dBFS); **MenuMove** tick; **MenuConfirm** / **MenuBack** two-note blips;
**ReadyChime** four-note rising arpeggio; **Countdown3/2/1** (A5 beeps) and **CountdownGo** (A6 + fifth);
**ResultsWin** / **ResultsLoss** short stings. `SfxPlayer.CaptionRaised` supplies caption text for the
important non-verbal cues (spec §16).

## Unity integration (for the coordinator)

**Required asmdef change (not made by me):** add `"NightSignal.AudioSynth"` to the `references` of
`Assets/Game/Runtime/NightSignal.Runtime.asmdef`. Unity must also create `.meta` files for the new folders
and files on import.

- **MusicPlayer** (one per game, persists across scenes): assign `instrumentLibrary` =
  `Assets/Content/Audio/Scores/instruments.json` and `cues` = the 24 `mus_*.json` TextAssets. Call
  `Play(cueId, crossfadeSeconds)` from the context router; replaying the current cue is a no-op.
  `Stop(fade)`, `Duck(db, seconds)` / `Unduck()`, `Preload(cueId)` (compile ahead to avoid a hitch).
  Route its AudioSource to a Music mixer group if a mixer is used.
- **EngineAudio** (one per car): `Configure(vehicleParams, carModelId)` once, then each frame
  `Feed(in state, in telemetry, throttle, Time.deltaTime)` for simulated cars or `SetInputs(...)` for
  interpolated remote cars. Creates `EngineBus`, `RoadBus`, `ImpactBus` child AudioSources (optional mixer
  groups in the inspector; 3D, log roll-off 6–220 m). `TryMeetRev()` enforces a 3 s cooldown.
  Uses the constant-1 carrier-clip technique so Unity's spatialisation multiplies into the synthesized
  signal.
- **SfxPlayer** (one per game): `SfxPlayer.PlayUi(UiCue.SignalRibbon)` etc.
- **GameAudioSettings**: `Master, Music, Engine, Tyres, Impacts, Ui, Ambient (wind), Voice` (0..1, squared
  fader law). The settings screen writes them; persistence is up to the settings system.

## Tools and checks

```
dotnet build Tools/audio/AudioTool -c Release
dotnet run --project Tools/audio/AudioTool -c Release --no-build -- check      # everything; exit 1 on failure (--wav adds music previews)
dotnet run --project Tools/audio/AudioTool -c Release --no-build -- cue MUS_TITLE
dotnet run --project Tools/audio/AudioTool -c Release --no-build -- stems MUS_TITLE   # per-track levels
dotnet run --project Tools/audio/AudioTool -c Release --no-build -- harmony      # semitone-clash audit
dotnet run --project Tools/audio/AudioTool -c Release --no-build -- normalize    # set mix.master toward −16 LUFS
dotnet run --project Tools/audio/AudioTool -c Release --no-build -- manifest     # regenerate music.cues.json
```

`check` renders every cue for its full length plus one more loop (48 kHz stereo) and fails on: compile
errors; missing required cues; peak above −0.3 dBFS; a loop-seam sample step larger than any step in the
surrounding ±50 ms; > 2 s of silence inside a loop; host-buffer-size-dependent output; main loop < 88 s
(results < 30 s); integrated loudness outside −16 ± 1.5 LUFS; > 5 % of a core; stale manifest. It also
renders each engine family (NA and turbo) through idle → redline sweep → limiter → coast → gear pulls →
lift, verifies the firing order dominates the spectrum at 3000 rpm and that families are spectrally
distinct, renders road/impact scenarios and all UI cues, and measures the whole in-race stack (music + six
cars × three buses + UI). WAV previews go to `Tools/audio/out/` (git-ignored): engine/road/impact/UI
renders on every `check`; music previews (the whole piece once plus 6 s past the loop seam, ~25 MB each)
from `cue ID` / `cues`, or `check --wav`.

Harmony audit: four remaining flags are deliberate non-chord tones (Daigo verse bar 2 A→Bb appoggiatura;
meet bell chorus bar 2 A→G# suspension; penultimate Descent line F# ninth over Em).

### Latest measured results (2026-09-27, `check`, .NET 10 on the development PC)

- Scores: 24/24 compile, 0 warnings; required inventory 24/24; manifest 24/24 in sync.
- Music (every cue, full length + one loop, 48 kHz stereo): peak −3.6 … −1.0 dBFS (limit −0.3);
  integrated loudness −16.2 … −15.9 LUFS (target −16 ± 1.5); loop-seam step ratio 0.02 … 0.26 (≤ 1.00 means
  no discontinuity); loop pass-2 spectral match 0.995 … 1.000; no silent gaps; bit-identical across host
  buffer sizes; CPU 15.6 … 30.6 ms per second of audio (1.6–3.1 % of one core), average 2.3 %.
- Engines (idle → redline → limiter → coast → gear pulls → lift): peak −8.4 … −5.1 dBFS, 4.7–9.5 ms/s per
  engine bus; firing frequency within 3.5 dB of the strongest partial for every family; pairwise timbre
  distance 6.9–15.1 dB between families.
- Road bus peak −12.1 dBFS; impacts −18 dBFS (thump 0.3) … −1.0 dBFS (collision 0.95); UI cues −25.6 …
  −14.5 dBFS peak, 0.35–1.5 s.
- Full in-race stack (one race cue + six cars × three buses + UI): 63 ms/s = 6.3 % of one core
  (music 1.8 %, cars 0.7 % each).

## In the game (V-078)

- Every drawn race car carries `EngineAudio` through `CarAudio` (fed from `VehicleView.Render`); your car plus the
  nearest cars within 170 m synthesize, at most six at once. Race cues come from Core `RaceMusic` (stage → encounter
  theme, trial kind → trial theme, otherwise the course region), started at the countdown; results play
  `MUS_RESULTS_WIN`/`MUS_RESULTS_LOSS`. Built-player levels at the listener (S07): cars alone −16.4 dBFS RMS, music alone
  −19.1, together −14.3.

## Known limitations / not verified

- **No human listening.** Musical quality, engine realism, the balance between music and engine audio
  in the game, and SFX loudness in context are unverified.
- **Not run in Unity by me.** The wrappers compile against Unity 6000.6.3f1's module assemblies plus the
  real `Vehicle` sources (scratch compile check), but have not run in the editor. In particular the
  constant-1 carrier-clip spatialisation technique and `OnAudioFilterRead` behaviour should be confirmed in
  play mode, and CPU should be profiled under Mono and IL2CPP (the measured figures are .NET 10 JIT).
- **First-call JIT**: under Mono the first `Render` of a new code path is JIT-compiled on the audio thread;
  a pre-warm render at load may be worth adding if a hitch is heard.
- **Ambience beds and voice/TTS are not part of this work**; `GameAudioSettings.Ambient/Voice` exist for them.
- Score compile allocations happen on `Play`/`Preload` (main thread) — call `Preload` during loading.
