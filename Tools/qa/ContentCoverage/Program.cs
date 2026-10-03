using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightSignal.Core.Content;
using NightSignal.Core.Customization;
using NightSignal.Core.Rules;

// Content coverage (spec §17, R17.1): for every catalogue entry, whether the things that make it real exist — a course's
// route and scene, a stage's conditions, benchmark and story, a rival's look and story sheet, a car's body and tuning, a
// challenge reward's renderable definition, a music cue's score — and every cross-reference that does not resolve.
// A catalogue row is data, not delivered content (docs/DECISIONS.md): the report never counts rows as delivered.

string root = FindRoot(AppContext.BaseDirectory);
string Rel(params string[] parts) => Path.Combine(new[] { root }.Concat(parts).ToArray());
JsonNode Json(params string[] parts) => JsonNode.Parse(File.ReadAllText(Rel(parts)));
string Text(params string[] parts) => File.ReadAllText(Rel(parts));

// ---- the catalogue exactly as the game and the control plane load it
string generated = Rel("Assets", "Content", "Data", "generated"), authored = Rel("Assets", "Content", "Data", "authored");
var docs = ContentCatalogue.RequiredFiles.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(generated, f)));
foreach (string f in ContentCatalogue.AuthoredFiles)
    if (File.Exists(Path.Combine(authored, f))) docs[f] = File.ReadAllText(Path.Combine(authored, f));
ContentCatalogue cat = ContentCatalogue.Load(docs);

var unresolved = new List<string>();
var report = new JsonObject
{
    ["schema"] = "night-signal/content-coverage@1",
    ["generatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    ["revision"] = GitRevision(root),
    ["note"] = "Counts what exists beside the catalogue (files, entries, renderable definitions); a catalogue row alone is never 'delivered'.",
};
var categories = new JsonObject();
report["categories"] = categories;

// ---- courses: route.json and scene; landmarks in the route; the authored rival reference ghost
var stagesByCourse = cat.Stages.GroupBy(s => s.Course).ToDictionary(g => g.Key, g => g.Select(s => s.Id).ToList());
var trialsByCourse = cat.ChallengeTrials.Trials.GroupBy(t => t.Course).ToDictionary(g => g.Key, g => g.Select(t => t.Id).ToList());
var courseItems = new JsonArray();
int coursesDelivered = 0;
foreach (CourseDef c in cat.Courses)
{
    string dir = Rel("Assets", "Content", "Courses", c.Id);
    bool route = File.Exists(Path.Combine(dir, "route.json")), scene = File.Exists(Path.Combine(dir, c.Id + ".unity"));
    int routeLandmarks = 0, gates = 0;
    if (route)
    {
        JsonNode r = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "route.json")));
        routeLandmarks = r["landmarks"]?.AsArray().Count ?? 0;
        gates = r["gates"]?.AsArray().Count ?? 0;
    }
    bool ghost = File.Exists(Rel("Assets", "Content", "Resources", "RivalGhosts", c.Id + ".json"));
    bool delivered = route && scene;
    if (delivered) coursesDelivered++;
    courseItems.Add(new JsonObject
    {
        ["id"] = c.Id, ["kind"] = c.Kind, ["route"] = route, ["scene"] = scene, ["routeLandmarks"] = routeLandmarks,
        ["catalogueLandmarks"] = c.Landmarks?.Count ?? 0, ["gates"] = gates, ["rivalReferenceGhost"] = ghost,
        ["stages"] = new JsonArray((stagesByCourse.TryGetValue(c.Id, out var st) ? st : new List<string>()).Select(x => (JsonNode)x).ToArray()),
        ["trials"] = (trialsByCourse.TryGetValue(c.Id, out var tr) ? tr.Count : 0), ["delivered"] = delivered,
    });
}
categories["courses"] = Summary(cat.Courses.Count, coursesDelivered, "route.json and its scene exist", courseItems,
    ("withRivalReferenceGhost", courseItems.Count(i => (bool)i!["rivalReferenceGhost"]!)),
    ("withThreeRouteLandmarks", courseItems.Count(i => (int)i!["routeLandmarks"]! >= 3)));

// ---- stages: both sides' conditions, published benchmark targets, story and live opposition
JsonNode benchmarks = Json("Assets", "Content", "Data", "authored", "stage-benchmarks.json");
var bench = benchmarks["stages"]!.AsArray().Select(b => ((string)b!["stage"]!, (string)b["mode"]!, (int)b["targetMs"]!)).ToList();
JsonNode stageStory = Json("Assets", "Content", "Data", "authored", "story", "stages.story.json");
var storyStages = stageStory["stages"]!.AsArray().ToDictionary(s => (string)s!["id"]!, s => s!);
string oppositionText = docs["stages.opposition.json"];
var stageItems = new JsonArray();
int stagesDelivered = 0;
foreach (StageDef s in cat.Stages)
{
    bool course = cat.TryCourse(s.Course, out _);
    bool leads = cat.TryRival(s.Normal.Lead ?? "", out _) && cat.TryRival(s.Hard.Lead ?? "", out _);
    foreach (string id in s.Normal.Support.Concat(s.Hard.Support).Concat(new[] { s.Normal.Lead, s.Hard.Lead }))
        if (!string.IsNullOrEmpty(id) && !cat.TryRival(id, out _)) unresolved.Add($"stage {s.Id}: rival {id}");
    if (!course) unresolved.Add($"stage {s.Id}: course {s.Course}");
    bool conditions = cat.Conditions(s.Id, CampaignMode.Normal) != null && cat.Conditions(s.Id, CampaignMode.Hard) != null;
    bool benchNormal = bench.Any(b => b.Item1 == s.Id && b.Item2 == "normal" && b.Item3 > 0), benchHard = bench.Any(b => b.Item1 == s.Id && b.Item2 == "hard" && b.Item3 > 0);
    bool story = storyStages.TryGetValue(s.Id, out JsonNode st2) && st2["normal"] != null && st2["hard"] != null;
    bool live = oppositionText.Contains("\"" + s.Id + "\"");
    bool delivered = course && leads && conditions && benchNormal && benchHard && story && live;
    if (delivered) stagesDelivered++;
    stageItems.Add(new JsonObject
    {
        ["id"] = s.Id, ["course"] = s.Course, ["type"] = s.Type, ["leadsResolve"] = leads, ["conditionsBothSides"] = conditions,
        ["benchmarkNormal"] = benchNormal, ["benchmarkHard"] = benchHard, ["story"] = story, ["liveOpposition"] = live, ["delivered"] = delivered,
    });
}
categories["stages"] = Summary(cat.Stages.Count, stagesDelivered, "course and both leads resolve; Normal and Hard conditions, published benchmark targets, story and live opposition", stageItems);

// ---- rivals: an authored look (the avatar generator's input) and a story sheet with lines
JsonNode looks = Json("Assets", "Content", "Data", "authored", "story", "rivals.look.json");
JsonNode rivalStory = Json("Assets", "Content", "Data", "authored", "story", "rivals.story.json");
var lookIds = looks["looks"]!.AsArray().Select(l => (string)l!["id"]!).ToHashSet();
var storyById = rivalStory["rivals"]!.AsArray().ToDictionary(r => (string)r!["id"]!, r => r!);
var featured = cat.Stages.SelectMany(s => new[] { s.Normal.Lead, s.Hard.Lead }).Where(x => x != null).ToHashSet();
var anyStage = cat.Stages.SelectMany(s => s.Normal.Support.Concat(s.Hard.Support).Concat(new[] { s.Normal.Lead, s.Hard.Lead })).Where(x => x != null).ToHashSet();
var rivalItems = new JsonArray();
int rivalsDelivered = 0;
foreach (RivalDef r in cat.Rivals)
{
    bool look = lookIds.Contains(r.Id);
    bool sheet = storyById.TryGetValue(r.Id, out JsonNode sn);
    int lines = sheet && sn["lines"] is JsonObject lo ? lo.Sum(kv => kv.Value is JsonArray a ? a.Count : 1) : 0;
    bool car = cat.TryCar(r.PrimaryCar ?? "", out _);
    if (!car) unresolved.Add($"rival {r.Id}: car {r.PrimaryCar}");
    bool delivered = look && sheet && lines > 0 && car;
    if (delivered) rivalsDelivered++;
    rivalItems.Add(new JsonObject
    {
        ["id"] = r.Id, ["crew"] = r.Crew, ["look"] = look, ["storySheet"] = sheet, ["lines"] = lines, ["carResolves"] = car,
        ["featuredInAStage"] = featured.Contains(r.Id), ["inAnyStage"] = anyStage.Contains(r.Id), ["delivered"] = delivered,
    });
}
categories["rivals"] = Summary(cat.Rivals.Count, rivalsDelivered, "an authored look and a story sheet with lines; the car resolves", rivalItems,
    ("featuredInAStage", rivalItems.Count(i => (bool)i!["featuredInAStage"]!)), ("inAnyStage", rivalItems.Count(i => (bool)i!["inAnyStage"]!)));

// ---- cars: a body definition (the body generator's input), tuning, an upgrade recipe
var bodyIds = Json("Assets", "Content", "Data", "authored", "cars.body.json")["cars"]!.AsArray().Select(b => (string)b!["id"]!).ToHashSet();
var recipeIds = Json("Assets", "Content", "Data", "authored", "build-recipes.json")["cars"]!.AsArray().Select(b => (string)b!["car"]!).ToHashSet();
var carItems = new JsonArray();
int carsDelivered = 0;
foreach (CarDef c in cat.Cars)
{
    bool body = bodyIds.Contains(c.Id), tuning = cat.CarTunings.ContainsKey(c.Id), recipe = recipeIds.Contains(c.Id);
    bool delivered = body && tuning;
    if (delivered) carsDelivered++;
    carItems.Add(new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["body"] = body, ["tuning"] = tuning, ["upgradeRecipe"] = recipe, ["delivered"] = delivered });
}
categories["cars"] = Summary(cat.Cars.Count, carsDelivered, "a body definition and tuning data exist", carItems, ("withUpgradeRecipe", carItems.Count(i => (bool)i!["upgradeRecipe"]!)));

// ---- challenges and their rewards: a renderable definition in customization.json; trial-backed or not
// The validated appearance catalogue says what draws each reward: a decal shape, a paint swatch, a card item (background,
// frame, motif, title, layout, avatar) or a wardrobe item worn on the driver. An id merely mentioned in the file is not one.
CustomizationCatalogue appearance = CustomizationCatalogue.Load(Text("Assets", "Content", "Data", "authored", "customization.json"));
var drawnBy = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (DecalShapeDef s in appearance.DecalShapes.Where(s => s.CosmeticId != null)) drawnBy[s.CosmeticId] = "decal shape " + s.Id;
foreach (PaintSwatchDef s in appearance.PaintSwatches.Where(s => s.CosmeticId != null)) drawnBy[s.CosmeticId] = "paint swatch " + s.Id;
foreach (var i in appearance.Card.Items().Where(i => i.CosmeticId != null)) drawnBy[i.CosmeticId] = $"card {i.Kind} {i.Id}";
foreach (WardrobeItemDef w in appearance.Wardrobe.Items) drawnBy[w.CosmeticId] = $"wardrobe {w.Slot} {w.Id}";
var trialsByChallenge = cat.ChallengeTrials.Trials.GroupBy(t => t.Challenge).ToDictionary(g => g.Key, g => g.ToList());
var challengeItems = new JsonArray();
int rewardsRenderable = 0;
foreach (ChallengeDef ch in cat.Challenges)
{
    bool rewardDefined = !string.IsNullOrEmpty(ch.Reward) && cat.Cosmetics.Any(x => x.Id == ch.Reward);
    if (!string.IsNullOrEmpty(ch.Reward) && !rewardDefined) unresolved.Add($"challenge {ch.Id}: reward {ch.Reward}");
    bool renderable = rewardDefined && drawnBy.ContainsKey(ch.Reward);
    if (renderable) rewardsRenderable++;
    trialsByChallenge.TryGetValue(ch.Id, out var trials);
    challengeItems.Add(new JsonObject
    {
        ["id"] = ch.Id, ["family"] = ch.Family, ["tier"] = ch.Tier, ["reward"] = ch.Reward,
        ["rewardCategory"] = cat.Cosmetics.FirstOrDefault(x => x.Id == ch.Reward)?.Category, ["rewardRenderable"] = renderable,
        ["rewardDrawnBy"] = renderable ? drawnBy[ch.Reward] : null,
        ["trials"] = trials?.Count ?? 0, ["trialsPublished"] = trials?.Count(t => t.Published) ?? 0,
    });
}
categories["challenges"] = Summary(cat.Challenges.Count, rewardsRenderable, "the reward cosmetic is drawn by a validated customization.json item (decal shape, paint swatch, card item or worn wardrobe item)", challengeItems,
    ("trialBacked", challengeItems.Count(i => (int)i!["trials"]! > 0)),
    ("rewardsDataOnly", challengeItems.Count(i => !(bool)i!["rewardRenderable"]!)));
categories["challenges"]!["judgedNote"] = "Which challenges are judged where (races, the meet, the diary, trials) is runtime code, not data — see REQUIREMENTS R11.3.";

// ---- music cues: the score file each cue names
JsonNode cues = Json("Assets", "Content", "Data", "authored", "music.cues.json");
var cueItems = new JsonArray();
int cuesDelivered = 0;
foreach (JsonNode q in cues["cues"]!.AsArray())
{
    string score = (string)q!["score"]!;
    bool exists = File.Exists(Rel(score.Split('/')));
    if (exists) cuesDelivered++;
    cueItems.Add(new JsonObject { ["id"] = (string)q["id"]!, ["score"] = score, ["scoreExists"] = exists, ["delivered"] = exists });
}
categories["musicCues"] = Summary(cueItems.Count, cuesDelivered, "the cue's score file exists", cueItems);

// ---- challenge trials and lessons
var problems = TrialJudge.Problems(cat.ChallengeTrials, id => cat.TryCourse(id, out _), id => cat.Challenges.Any(x => x.Id == id), id => cat.TryCar(id, out _));
categories["challengeTrials"] = new JsonObject
{
    ["defined"] = cat.ChallengeTrials.Trials.Count, ["published"] = cat.ChallengeTrials.Trials.Count(t => t.Published),
    ["challengesCovered"] = cat.ChallengeTrials.Trials.Select(t => t.Challenge).Distinct().Count(),
    ["problems"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray()),
};
JsonNode lessons = Json("Assets", "Content", "Data", "authored", "tutorial", "lessons.json");
categories["lessons"] = new JsonObject
{
    ["defined"] = lessons["lessons"]!.AsArray().Count,
    ["drive"] = lessons["lessons"]!.AsArray().Count(l => (string)l!["kind"]! == "drive"),
    ["demonstrationGhost"] = File.Exists(Rel("Assets", "Content", "Resources", "LessonGhosts", "T00.json")),
};

// ---- the catalogue validator (Appendix G data assertions), and every reference that did not resolve
ValidationReport v = CatalogueValidator.Validate(cat);
report["validator"] = new JsonObject
{
    ["passed"] = v.Passed, ["errors"] = v.Errors.Count(),
    ["issues"] = new JsonArray(v.Issues.Select(i => (JsonNode)$"{i.Severity} {i.Code}: {i.Message}").ToArray()),
};
report["unresolvedReferences"] = new JsonArray(unresolved.Distinct().Select(u => (JsonNode)u).ToArray());

// ---- write
string outDir = Rel("Evidence", "coverage");
Directory.CreateDirectory(outDir);
File.WriteAllText(Path.Combine(outDir, "content-coverage.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
var md = new StringBuilder();
md.AppendLine("# Content coverage").AppendLine()
  .AppendLine($"Generated by `dotnet run --project Tools/qa/ContentCoverage` at revision `{report["revision"]}` ({report["generatedUtc"]}). " +
              "A catalogue row is data, not delivered content: each line counts what exists beside it. Full detail: `content-coverage.json`.").AppendLine()
  .AppendLine("| Category | Defined | Delivered | Delivered means | Also |").AppendLine("|---|---|---|---|---|");
foreach (var kv in categories)
{
    JsonObject o = kv.Value!.AsObject();
    if (!o.ContainsKey("delivered")) continue;
    string also = string.Join("; ", o.Where(p => p.Key.StartsWith("with") || p.Key is "featuredInAStage" or "inAnyStage" or "trialBacked" or "rewardsDataOnly")
        .Select(p => $"{p.Key} {p.Value}"));
    md.AppendLine($"| {kv.Key} | {o["defined"]} | {o["delivered"]} | {o["deliveredMeans"]} | {also} |");
}
JsonObject ct = categories["challengeTrials"]!.AsObject();
md.AppendLine().AppendLine($"Challenge trials: {ct["defined"]} defined, {ct["published"]} published, covering {ct["challengesCovered"]} challenges; " +
                           $"{((JsonArray)ct["problems"]!).Count} problem(s).");
JsonObject ls = categories["lessons"]!.AsObject();
md.AppendLine($"Lessons: {ls["defined"]} ({ls["drive"]} driven); demonstration ghost {(((bool)ls["demonstrationGhost"]!) ? "present" : "missing")}.");
md.AppendLine($"Catalogue validator: {(v.Passed ? "passed" : "FAILED")}, {v.Errors.Count()} error(s), {v.Issues.Count - v.Errors.Count()} warning(s).");
md.AppendLine($"Unresolved references: {unresolved.Distinct().Count()}.");
foreach (string u in unresolved.Distinct()) md.AppendLine($"- {u}");
var notDelivered = categories.Where(kv => kv.Value!["items"] is JsonArray).SelectMany(kv => kv.Value!["items"]!.AsArray()
    .Where(i => i!["delivered"] is JsonNode d && !(bool)d).Select(i => $"{kv.Key}: {(string)i!["id"]!}")).ToList();
if (notDelivered.Count > 0)
{
    md.AppendLine().AppendLine("## Not delivered");
    foreach (string n in notDelivered) md.AppendLine($"- {n}");
}
var dataOnly = challengeItems.Where(i => !(bool)i!["rewardRenderable"]!).Select(i => $"{(string)i!["id"]!} → {(string)i["reward"]!} ({(string)i["rewardCategory"]})").ToList();
if (dataOnly.Count > 0)
{
    md.AppendLine().AppendLine("## Challenge rewards that are catalogue rows only (no renderable definition yet)");
    foreach (string d in dataOnly) md.AppendLine($"- {d}");
}
File.WriteAllText(Path.Combine(outDir, "content-coverage.md"), md.ToString());
Console.Write(md.ToString());
return v.Passed && unresolved.Count == 0 && problems.Count == 0 ? 0 : 1;

static JsonObject Summary(int defined, int delivered, string means, JsonArray items, params (string Key, int Value)[] also)
{
    var o = new JsonObject { ["defined"] = defined, ["delivered"] = delivered, ["deliveredMeans"] = means };
    foreach (var (k, val) in also) o[k] = val;
    o["items"] = items;
    return o;
}

static string FindRoot(string start)
{
    for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "Assets", "Content", "Data", "generated", "courses.json"))) return d.FullName;
    throw new InvalidOperationException("Run inside the Night Signal repository.");
}

static string GitRevision(string root)
{
    try
    {
        string head = File.ReadAllText(Path.Combine(root, ".git", "HEAD")).Trim();
        if (!head.StartsWith("ref: ")) return head[..Math.Min(7, head.Length)];
        string refFile = Path.Combine(root, ".git", head[5..].Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(refFile)) return File.ReadAllText(refFile).Trim()[..7];
        string packed = Path.Combine(root, ".git", "packed-refs");
        string line = File.Exists(packed) ? File.ReadLines(packed).FirstOrDefault(l => l.EndsWith(" " + head[5..])) : null;
        return line?[..7] ?? "unknown";
    }
    catch (IOException) { return "unknown"; }
}
