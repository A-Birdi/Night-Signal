using System.Collections.Generic;
using NightSignal.Art;
using UnityEngine;
using UnityEngine.Rendering;

namespace NightSignal.Characters
{
    /// <summary>
    /// A built character in the scene: the bone hierarchy (rest pose = identity rotations, model axes), the skinned body
    /// and its tinted materials. <see cref="CharacterMotion"/> poses the bones. Meshes are cached per look and level of
    /// detail; a cached rig carries three levels under a LODGroup (one skeleton, three skinned bodies), a preview one.
    /// </summary>
    public sealed class CharacterRig : MonoBehaviour
    {
        public CharacterLook Look;
        [System.NonSerialized] public CharacterBuilder.Skeleton Skeleton;
        public Transform[] Bones;
        public SkinnedMeshRenderer Body;
        /// <summary>The bodies by level of detail (index 0 = <see cref="Body"/>); one entry for an uncached preview rig.</summary>
        public SkinnedMeshRenderer[] Lods;

        /// <summary>
        /// Screen-relative heights below which the next level takes over (LODGroup): full detail to ≈ 8 m for a 1.75 m person
        /// at a 60° field of view, mid to ≈ 25 m, far to ≈ 190 m, then culled (the meet plateau is 150 × 110 m).
        /// </summary>
        public static readonly float[] LodHeights = { 0.18f, 0.06f, 0.008f };
        /// <summary>A mesh built for this rig alone (previews that change often), destroyed with it; cached meshes are shared.</summary>
        Mesh ownedMesh;

        static readonly Dictionary<string, Mesh> MeshCache = new Dictionary<string, Mesh>();

        public Transform this[Bone b] => Bones[(int)b];

        /// <summary>The look's mesh at a level of detail, built once per distinct look and level.</summary>
        public static Mesh MeshFor(CharacterLook look, CharacterBuilder.Skeleton sk, int lod = 0)
        {
            string key = JsonUtility.ToJson(look) + "#" + lod;
            if (MeshCache.TryGetValue(key, out Mesh m) && m != null) return m;
            m = CharacterBuilder.Build(look, sk, lod);
            m.hideFlags = HideFlags.DontSave;
            MeshCache[key] = m;
            return m;
        }

        /// <summary>
        /// The look as it is built: reward wardrobe items worn (customization.json "wardrobe") replace or add to the outfit.
        /// A look without a wardrobe — every rival, most players — is returned as it is.
        /// </summary>
        public static CharacterLook Dressed(CharacterLook look)
        {
            if (look?.Wardrobe == null || look.Wardrobe.Count == 0) return look;
            Core.Customization.WardrobeCatalogue wardrobe = Content.ContentLibrary.Load()?.Customization?.Wardrobe;
            return wardrobe != null ? wardrobe.Apply(look) : look;
        }

        public static CharacterRig Create(CharacterLook look, CharacterMaterialSet mats = null, Transform parent = null, string name = null, int layer = GameLayers.Avatar,
            bool cacheMesh = true)
        {
            look = Dressed(look);
            var go = new GameObject(name ?? $"Character_{look.Id}") { layer = layer };
            if (parent != null) go.transform.SetParent(parent, false);
            var rig = go.AddComponent<CharacterRig>();
            rig.Look = look;
            rig.Skeleton = CharacterBuilder.SkeletonFor(look);
            int n = (int)Bone.Count;
            rig.Bones = new Transform[n];
            for (int i = 0; i < n; i++)
            {
                var b = new GameObject(((Bone)i).ToString()) { layer = layer }.transform;
                Transform p = i == 0 ? go.transform : rig.Bones[(int)CharacterBuilder.Parent[i]];
                b.SetParent(p, false);
                Vector3 parentRest = i == 0 ? Vector3.zero : rig.Skeleton.Rest[(int)CharacterBuilder.Parent[i]];
                b.localPosition = rig.Skeleton.Rest[i] - parentRest;
                b.localRotation = Quaternion.identity;
                rig.Bones[i] = b;
            }
            Mesh mesh;
            if (cacheMesh) mesh = MeshFor(look, rig.Skeleton);
            else
            {
                mesh = CharacterBuilder.Build(look, rig.Skeleton);
                mesh.hideFlags = HideFlags.DontSave;
                rig.ownedMesh = mesh;
            }
            var bodyGo = new GameObject("Body") { layer = layer };
            bodyGo.transform.SetParent(go.transform, false);
            var smr = bodyGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = rig.Bones;
            smr.rootBone = rig.Bones[(int)Bone.Hips];
            smr.quality = SkinQuality.Bone1;
            smr.shadowCastingMode = ShadowCastingMode.On;
            smr.updateWhenOffscreen = false;
            Bounds bb = mesh.bounds;
            bb.center -= rig.Skeleton.Rest[(int)Bone.Hips];
            bb.Expand(new Vector3(1.2f, 0.8f, 1.2f)); // room for raised arms, crouches and dance steps
            smr.localBounds = bb;
            if (mats == null) mats = CharacterMaterialSet.Load();
            Material[] materials = mats != null ? mats.For(look) : null;
            if (materials != null) smr.sharedMaterials = materials;
            rig.Body = smr;
            rig.Lods = new[] { smr };
            if (cacheMesh)
            {
                // The mid and far bodies share the skeleton, the bounds and the materials; the LODGroup picks one by size on screen.
                rig.Lods = new SkinnedMeshRenderer[CharacterBuilder.LodCount];
                rig.Lods[0] = smr;
                var levels = new LOD[CharacterBuilder.LodCount];
                levels[0] = new LOD(LodHeights[0], new Renderer[] { smr });
                for (int lod = 1; lod < CharacterBuilder.LodCount; lod++)
                {
                    var lodGo = new GameObject($"Body_LOD{lod}") { layer = layer };
                    lodGo.transform.SetParent(go.transform, false);
                    var lr = lodGo.AddComponent<SkinnedMeshRenderer>();
                    lr.sharedMesh = MeshFor(look, rig.Skeleton, lod);
                    lr.bones = rig.Bones;
                    lr.rootBone = smr.rootBone;
                    lr.quality = SkinQuality.Bone1;
                    lr.shadowCastingMode = ShadowCastingMode.On;
                    lr.updateWhenOffscreen = false;
                    lr.localBounds = bb;
                    if (materials != null) lr.sharedMaterials = materials;
                    rig.Lods[lod] = lr;
                    levels[lod] = new LOD(LodHeights[lod], new Renderer[] { lr });
                }
                var group = go.AddComponent<LODGroup>();
                group.SetLODs(levels);
                group.localReferencePoint = new Vector3(0f, rig.Skeleton.H * 0.5f, 0f);
                group.size = rig.Skeleton.H * 1.05f;
            }
            return rig;
        }

        void OnDestroy()
        {
            if (ownedMesh != null) Destroy(ownedMesh);
        }

        /// <summary>Back to the rest pose.</summary>
        public void ResetPose()
        {
            for (int i = 0; i < Bones.Length; i++)
            {
                Vector3 parentRest = i == 0 ? Vector3.zero : Skeleton.Rest[(int)CharacterBuilder.Parent[i]];
                Bones[i].localPosition = Skeleton.Rest[i] - parentRest;
                Bones[i].localRotation = Quaternion.identity;
            }
        }
    }
}
