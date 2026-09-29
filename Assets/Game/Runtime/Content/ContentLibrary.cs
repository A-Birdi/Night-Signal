using System.Collections.Generic;
using NightSignal.Art;
using NightSignal.Core.Content;
using NightSignal.Vehicle;
using Newtonsoft.Json;
using UnityEngine;

namespace NightSignal.Content
{
    /// <summary>
    /// Build-safe bundle of every content document (generated + authored JSON as TextAssets). The client, the
    /// dedicated server and tools load the catalogue from here; its <see cref="ContentCatalogue.ContentHash"/>
    /// must match between server and clients.
    /// </summary>
    [CreateAssetMenu(menuName = "Night Signal/Content Library")]
    public sealed class ContentLibrary : ScriptableObject
    {
        public TextAsset[] Documents;
        public TextAsset CarBodies;
        /// <summary>While We Wait toy content (non-progression; not part of the race catalogue hash).</summary>
        public TextAsset[] ToyDocuments;
        /// <summary>Garage data (parts.json, build-recipes.json); also among <see cref="Documents"/>, which the content hash covers.</summary>
        public TextAsset PartsDocument;
        public TextAsset RecipesDocument;
        /// <summary>Appearance catalogue (customization.json): visual only, published with its own hash, not in the race hash.</summary>
        public TextAsset CustomizationDocument;
        /// <summary>Rival appearances (story/rivals.look.json): visual only, not in the race hash.</summary>
        public TextAsset CharacterLooks;
        /// <summary>Meet NPC appearances (story/npcs.look.json): the terrace host.</summary>
        public TextAsset NpcLooks;
        /// <summary>Meet text (story/meet.text.json): host dialogue, placards, timing board, quick chat.</summary>
        public TextAsset MeetText;
        /// <summary>The campaign story (story/stages.story, crews.diary, radio-records, rivals.story): presentation text, not in the race hash.</summary>
        public TextAsset StageStory, CrewDiary, StoryRecords, RivalStory, Endings;
        /// <summary>authored/tutorial/lessons.json — the T00 lessons (training, not part of the race content hash).</summary>
        public TextAsset TutorialLessonsDocument;
        Core.Tutorial.TutorialLessons tutorial;

        /// <summary>The T00 lessons and help index (null when the document is missing from this build).</summary>
        public Core.Tutorial.TutorialLessons Tutorial =>
            tutorial ?? (TutorialLessonsDocument != null ? tutorial = Core.Tutorial.TutorialLessons.Parse(TutorialLessonsDocument.text) : null);

        Core.Story.StoryText story;

        /// <summary>Stage intros, reactions and the race diary (null when the documents are missing from this build).</summary>
        public Core.Story.StoryText Story =>
            story ?? (StageStory != null ? story = Core.Story.StoryText.Load(StageStory.text, CrewDiary?.text, StoryRecords?.text, RivalStory?.text, Endings?.text) : null);

        ContentCatalogue catalogue;
        Dictionary<string, CarBodyDef> bodies;

        public ContentCatalogue Catalogue
        {
            get
            {
                if (catalogue != null) return catalogue;
                var docs = new Dictionary<string, string>();
                foreach (TextAsset t in Documents) docs[t.name + ".json"] = t.text;
                catalogue = ContentCatalogue.Load(docs);
                return catalogue;
            }
        }

        Dictionary<string, Characters.CharacterLook> looks;

        /// <summary>A rival's authored look by rival ID (null when unknown or the document is missing).</summary>
        public Characters.CharacterLook Look(string id)
        {
            if (looks == null)
            {
                looks = new Dictionary<string, Characters.CharacterLook>();
                foreach (TextAsset t in new[] { CharacterLooks, NpcLooks })
                    if (t != null)
                        foreach (Characters.CharacterLook l in JsonConvert.DeserializeObject<Characters.CharacterLookFile>(t.text).Looks) looks[l.Id] = l;
            }
            return looks.TryGetValue(id, out Characters.CharacterLook look) ? look : null;
        }

        public CarBodyDef Body(string carId)
        {
            if (bodies == null)
            {
                bodies = new Dictionary<string, CarBodyDef>();
                foreach (CarBodyDef b in JsonConvert.DeserializeObject<CarBodyFile>(CarBodies.text).Cars) bodies[b.Id] = b;
            }
            return bodies[carId];
        }

        /// <summary>Chassis parameters for a model; the physics wheel radius follows the authored body.</summary>
        public VehicleParams Params(string carId, AssistSettings assists)
        {
            ContentCatalogue c = Catalogue;
            return VehicleFactory.Build(c.Car(carId), c.CarTunings[carId], assists, Body(carId).WheelRadius);
        }

        Core.Toys.ToyContent toys;

        /// <summary>The five diversions' content, loaded and validated by Core (null when the documents are missing).</summary>
        public Core.Toys.ToyContent Toys
        {
            get
            {
                if (toys != null || ToyDocuments == null || ToyDocuments.Length == 0) return toys;
                var docs = new Dictionary<string, string>();
                foreach (TextAsset t in ToyDocuments) docs[t.name + ".json"] = t.text;
                toys = Core.Toys.ToyContent.Load(docs);
                return toys;
            }
        }

        Core.Builds.PartsCatalogue parts;
        Core.Builds.RecipeBook recipes;

        /// <summary>The performance parts catalogue — the hashed catalogue document (null when missing from this build).</summary>
        public Core.Builds.PartsCatalogue Parts => parts ?? (Text("parts.json", PartsDocument) is string t ? parts = Core.Builds.PartsCatalogue.Load(t) : null);

        /// <summary>Authored upgrade paths per car (null when missing).</summary>
        public Core.Builds.RecipeBook Recipes => recipes ?? (Text("build-recipes.json", RecipesDocument) is string t ? recipes = Core.Builds.RecipeBook.Load(t) : null);

        Core.Customization.CustomizationCatalogue customization;

        /// <summary>Body-kit families, rims, paints, lamps, plates and decals per chassis (null when missing from this build).</summary>
        public Core.Customization.CustomizationCatalogue Customization =>
            customization ?? (CustomizationDocument != null ? customization = Core.Customization.CustomizationCatalogue.Load(CustomizationDocument.text) : null);

        /// <summary>SHA-256 of the LF-normalised customization.json (Core's definition) — compared with the control plane's /healthz.</summary>
        public string CustomizationHash => Customization?.Hash;

        /// <summary>A document exactly as the content hash covers it; the separate TextAsset only for libraries built before.</summary>
        string Text(string name, TextAsset fallback) => Catalogue != null && Catalogue.TryDocument(name, out string text) ? text : fallback != null ? fallback.text : null;

        public static ContentLibrary Load() => Resources.Load<ContentLibrary>("ContentLibrary");
    }
}
