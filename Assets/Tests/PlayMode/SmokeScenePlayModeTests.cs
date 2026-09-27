using System.Collections;
using System.Linq;
using NightSignal.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NightSignal.Tests
{
    public sealed class SmokeScenePlayModeTests
    {
        const string SmokeScenePath = "Assets/Tests/Verification/SetupSmoke.unity";

        [UnityTest]
        public IEnumerator SmokeScene_RunsUnderUrpAndAnimates()
        {
#if UNITY_EDITOR
            AsyncOperation load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                SmokeScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return load;
#else
            Assert.Ignore("Scene loading by path is an editor-only test.");
            yield break;
#endif
            Assert.That(GraphicsSettings.currentRenderPipeline, Is.InstanceOf<UniversalRenderPipelineAsset>());

            SmokeTestRunner runner = Object.FindObjectsByType<SmokeTestRunner>().Single();
            Assert.That(Camera.main, Is.Not.Null, "Smoke scene needs a MainCamera-tagged camera");

            Quaternion before = runner.SpinTarget.rotation;
            float start = Time.time;
            while (Time.time - start < 0.5f)
                yield return null;

            float turned = Quaternion.Angle(before, runner.SpinTarget.rotation);
            Assert.That(turned, Is.GreaterThan(5f), "Update loop did not animate the spin target");
        }
    }
}
