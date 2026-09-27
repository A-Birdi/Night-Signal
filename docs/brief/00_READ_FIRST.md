# NIGHT SIGNAL: MOUNTAIN CIRCUIT — prompt pack

Created for Robin • 26 September 2026 • revision 1

This is a new-project design/implementation brief, not a generated game or a Unity project. No repository, local editor, paid service or player account has been modified.

## What to use

1. Read `02_Unity_and_Claude_Setup.md` for the Windows/Unity/Claude connection and backend preparation.
2. Attach `01_Night_Signal_Master_Specification.txt` to the NEW Claude Code project. The Markdown version contains exactly the same text; use one, not both.
3. `Night_Signal_Content_Catalogue.json` is an optional structured duplicate of the track, car, rival, stage and challenge appendices. It is useful for importing typed content but is not game data already proven playable.
4. Paste `03_Claude_Launch_Prompt.txt` as the execution message alongside the specification.
5. Use `04_Resume_After_Interruption.txt` in the existing session after a usage interruption. It preserves the full assignment rather than restarting.

The master contains approximately 24,987 whitespace-delimited words: core rules plus all 26 course briefs, 30-stage mapping, 18 car definitions, 48 rivals and 75 challenges/rewards. The exact static check is in `05_Brief_Consistency_Check.json`. Counts/references/arithmetic were checked; no Unity/runtime/network test is implied.

## Deliberate choices you should know about

- Unity-first native Windows release; optional Unity Web build later. GitHub Pages can host static delivery, not the multiplayer authority/backend.
- Dedicated authoritative simulation, separate convoy/control service, persistent authenticated accounts and server-owned money/unlocks.
- Six HUMAN racers are supported. Six humans leave no room for a live AI under the six-entrant cap: full-convoy campaign uses a visibly labelled recorded rival benchmark, not a seventh live car.
- 30 competitive stages per mode: 24 regular +4 lieutenant bosses +1 penultimate +1 finale. Tutorial is additional. There are 26 distinct base courses; rematches/variants are not counted as new routes.
- A 12-person car meet can contain multiple six-person convoys. It does not increase race capacity.
- Hard has a distinct fixed legendary final rival and authored changes, not merely inflated AI speed.
- Rank Points have a finite 15,000-point budget. All75 challenges have unique cosmetic rewards and can be achieved without requiring another human.
- The previous RPG's single-file/all-procedural-art constraints do not apply. Authored reusable 3D assets are encouraged; no paid provider or external asset purchase is implicitly authorized.

The title, setting, characters, cars, routes and rules are original proposals. Initial D and KH2 references describe inspiration, not permission to copy protected assets or a claim of affiliation. The working title has not undergone a trademark search.

## Scope boundaries

One submitted assignment may require multiple agent work cycles and usage windows. This pack neither guarantees one-session completion nor silently lowers the requested content. A good first deliverable is a verified networked production slice; it is a checkpoint toward the full game, not an alternative final scope.

No exact hosting bill is promised. Provisioning, credentials, paid services, public deployment, firewall changes and system installations need your approval. Keep the Unity bridge local and verify it controls the intended new checkout before sending the full assignment.
