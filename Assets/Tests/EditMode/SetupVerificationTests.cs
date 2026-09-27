using System.IO;
using System.Linq;
using NightSignal.Diagnostics;
using NightSignal.Editor.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace NightSignal.Tests
{
    /// <summary>Guards the approved project baseline (see docs/DECISIONS.md, D-001 and D-002).</summary>
    public sealed class SetupVerificationTests
    {
        const string PcPipelineAsset = "Assets/Settings/PC_RPAsset.asset";

        [Test]
        public void EditorVersion_MatchesPinnedProjectVersion()
        {
            string versionFile = File.ReadAllText("ProjectSettings/ProjectVersion.txt");
            string pinned = versionFile.Split('\n')
                .First(l => l.StartsWith("m_EditorVersion:"))
                .Substring("m_EditorVersion:".Length).Trim();
            Assert.That(pinned, Is.EqualTo("6000.6.3f1"), "Approved baseline is 6000.6.3f1");
            Assert.That(Application.unityVersion, Is.EqualTo(pinned), "Running editor differs from ProjectVersion.txt");
        }

        [Test]
        public void GraphicsDefault_IsThePcUrpAsset()
        {
            RenderPipelineAsset rp = GraphicsSettings.defaultRenderPipeline;
            Assert.That(rp, Is.InstanceOf<UniversalRenderPipelineAsset>());
            Assert.That(AssetDatabase.GetAssetPath(rp), Is.EqualTo(PcPipelineAsset));
        }

        [Test]
        public void EveryQualityLevel_OverridesWithAUrpAsset()
        {
            string[] names = QualitySettings.names;
            Assert.That(names, Is.EquivalentTo(new[] { "Mobile", "PC" }));
            for (int i = 0; i < names.Length; i++)
                Assert.That(QualitySettings.GetRenderPipelineAssetAt(i), Is.InstanceOf<UniversalRenderPipelineAsset>(), names[i]);
            Assert.That(AssetDatabase.GetAssetPath(QualitySettings.GetRenderPipelineAssetAt(1)), Is.EqualTo(PcPipelineAsset));
        }

        [Test]
        public void GameLayers_MatchTagManager()
        {
            Assert.That(LayerMask.NameToLayer("Drivable"), Is.EqualTo(NightSignal.Art.GameLayers.Drivable));
            Assert.That(LayerMask.NameToLayer("Barrier"), Is.EqualTo(NightSignal.Art.GameLayers.Barrier));
            Assert.That(LayerMask.NameToLayer("Vehicle"), Is.EqualTo(NightSignal.Art.GameLayers.Vehicle));
            Assert.That(LayerMask.NameToLayer("Scenery"), Is.EqualTo(NightSignal.Art.GameLayers.Scenery));
            Assert.That(LayerMask.NameToLayer("Avatar"), Is.EqualTo(NightSignal.Art.GameLayers.Avatar));
        }

        [Test]
        public void ColorSpace_IsLinear()
        {
            Assert.That(PlayerSettings.colorSpace, Is.EqualTo(ColorSpace.Linear));
        }

        [Test]
        public void SmokeScene_ContainsCameraLightAndRunner()
        {
            Assert.That(File.Exists(BuildCommands.SetupSmokeScene), Is.True);
            bool alreadyOpen = SceneManager.GetSceneByPath(BuildCommands.SetupSmokeScene).isLoaded;
            Scene scene = EditorSceneManager.OpenScene(BuildCommands.SetupSmokeScene, OpenSceneMode.Additive);
            try
            {
                GameObject[] roots = scene.GetRootGameObjects();
                Assert.That(roots.SelectMany(r => r.GetComponentsInChildren<Camera>()).Count(), Is.EqualTo(1));
                Assert.That(roots.SelectMany(r => r.GetComponentsInChildren<Light>())
                    .Count(l => l.type == LightType.Directional), Is.EqualTo(1));
                SmokeTestRunner runner = roots.SelectMany(r => r.GetComponentsInChildren<SmokeTestRunner>()).Single();
                Assert.That(runner.SpinTarget, Is.Not.Null, "Runner must reference a visible spin target");
            }
            finally
            {
                // Never close a scene the developer already had open.
                if (!alreadyOpen)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
