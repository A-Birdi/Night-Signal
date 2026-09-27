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

        public static ContentLibrary Load() => Resources.Load<ContentLibrary>("ContentLibrary");
    }
}
