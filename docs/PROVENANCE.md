# Asset provenance

Every shipped asset must have a lawful source and a record here (spec: "All assets/fonts require appropriate
permissions and bundled provenance"). Anything not listed is not cleared to ship.

| Asset | Location | Source | Licence / permission |
|---|---|---|---|
| Surface textures (asphalt, gravel, stone, wood, roof tiles, paper, grass, tea rows, soil, rock) | `Assets/Art/Textures/Generated/` | Generated in-project by `Assets/Game/Editor/Art/ProceduralTextures.cs` (seeded noise) | Original work of this project |
| Course and car materials | `Assets/Art/Materials/`, `Assets/Content/Resources/CarMaterialSet.asset` | Built by `MaterialLibrary` / `CarMaterialAuthoring` from the textures above and URP/Lit | Original work |
| Course geometry, terrain, landmarks | Generated at load from `Assets/Content/Courses/<ID>/route.json` (decision D-007) | Original route documents and generators | Original work |
| Car bodies | Generated from `Assets/Content/Data/authored/cars.body.json` by `CarBodyGenerator` | Original parametric bodies; no real vehicle is reproduced | Original work |
| Sky, lighting, post-processing | Unity `Skybox/Procedural`, URP Volume components | Unity built-ins configured by this project | Unity Editor licence |
| UI font: Liberation Sans (+ SDF atlas) | `Assets/TextMesh Pro/Fonts/`, `Assets/TextMesh Pro/Resources/Fonts & Materials/` | TMP Essential Resources shipped inside Unity's `com.unity.ugui` package | SIL Open Font License 1.1 — full text bundled at `Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt` |
| TMP shaders and settings | `Assets/TextMesh Pro/Shaders/`, `Assets/TextMesh Pro/Resources/` | TMP Essential Resources (Unity) | Unity Companion License (package) |

## Pending (needs owner approval before download)

- **Condensed heading face and tabular numeral face.** The style section asks for condensed racing headings and
  tabular timing figures. Candidate OFL families: Barlow Condensed (headings), JetBrains Mono or IBM Plex Mono
  (timing). Downloading font files needs explicit approval; until then the HUD uses Liberation Sans with TMP
  `<mspace>` monospaced digits, and the OFL text will be bundled next to any font that is added.
- **Music and sound.** Original synthesised audio is being produced in-project (no samples); it will be listed here
  when committed.
