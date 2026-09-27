using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace NightSignal.Editor.Build
{
    /// <summary>
    /// Named build entry points. Usable from the editor, the MCP bridge, or
    /// <c>Unity.exe -batchmode -projectPath . -executeMethod NightSignal.Editor.Build.BuildCommands.&lt;Name&gt;Cli -quit</c>
    /// when no other editor has the project open. Output stays under the ignored Builds/ folder.
    /// </summary>
    public static class BuildCommands
    {
        public const string SetupSmokeScene = "Assets/Tests/Verification/SetupSmoke.unity";
        public const string SetupSmokeOutput = "Builds/SetupSmoke/NightSignalSmoke.exe";

        public static BuildReport BuildSetupSmoke() =>
            Build(new[] { SetupSmokeScene }, SetupSmokeOutput, BuildTarget.StandaloneWindows64,
                StandaloneBuildSubtarget.Player, BuildOptions.Development);

        public static void BuildSetupSmokeCli() => ExitWith(BuildSetupSmoke());

        public static BuildReport Build(string[] scenes, string outputPath, BuildTarget target,
            StandaloneBuildSubtarget subtarget, BuildOptions options)
        {
            foreach (string scene in scenes)
                if (!File.Exists(scene))
                    throw new FileNotFoundException("Build scene missing", scene);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? "Builds");
            var buildOptions = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = target,
                subtarget = (int)subtarget,
                options = options,
            };
            BuildReport report = BuildPipeline.BuildPlayer(buildOptions);
            BuildSummary s = report.summary;
            Debug.Log($"[NightSignal.Build] {s.result} {target}/{subtarget} -> {outputPath} " +
                      $"size={s.totalSize} errors={s.totalErrors} warnings={s.totalWarnings} time={s.totalTime}");
            return report;
        }

        static void ExitWith(BuildReport report)
        {
            if (Application.isBatchMode)
                EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
