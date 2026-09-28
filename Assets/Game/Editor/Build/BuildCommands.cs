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

        /// <summary>
        /// Setup/editor smoke player: a non-development build, so it opens no socket at all (a development player listens
        /// for the editor/profiler on all interfaces — Addendum 04: the smoke build needs no firewall exception).
        /// </summary>
        public static BuildReport BuildSetupSmoke() =>
            Build(new[] { SetupSmokeScene }, SetupSmokeOutput, BuildTarget.StandaloneWindows64,
                StandaloneBuildSubtarget.Player, BuildOptions.None);

        public static void BuildSetupSmokeCli() => ExitWith(BuildSetupSmoke());

        public const string BootScene = "Assets/Game/Scenes/Boot.unity";
        public const string GameOutput = "Builds/Game/NightSignal.exe";

        /// <summary>Boot scene first, then every authored course scene (Assets/Content/Courses/*/*.unity).</summary>
        public static string[] GameScenes()
        {
            var scenes = new System.Collections.Generic.List<string> { BootScene };
            foreach (string dir in Directory.GetDirectories("Assets/Content/Courses"))
            {
                string id = Path.GetFileName(dir);
                string scene = $"{dir}/{id}.unity".Replace('\\', '/');
                if (File.Exists(scene)) scenes.Add(scene);
            }
            // Private Garage facility (Addendum 02 §10) — not a counted course.
            const string yard = "Assets/Content/Facilities/TestYard/TestYard.unity";
            if (File.Exists(yard)) scenes.Add(yard);
            // The meet (spec §12): Cedar Lantern Terrace, a separate scene/room from race instances.
            if (File.Exists(Courses.CourseSceneAuthoring.MeetScene)) scenes.Add(Courses.CourseSceneAuthoring.MeetScene);
            return scenes.ToArray();
        }

        /// <summary>
        /// One Windows player serves every role (client, dedicated server via -batchmode -nographics -nsServer). Routine
        /// automation uses the default non-development build: a development player also listens for the editor/profiler
        /// on all interfaces (TCP 55000+, measured in V-067), which the local, loopback-only workflow must not open
        /// (Addendum 04). <paramref name="development"/> is for an explicit, attended profiling session only.
        /// </summary>
        public static BuildReport BuildGame(bool development = false)
        {
            // The build must carry exactly the content documents the control plane reads (same ContentHash).
            ContentTools.ContentLibraryAuthoring.Refresh();
            string[] scenes = GameScenes();
            EditorBuildSettings.scenes = System.Array.ConvertAll(scenes, s => new EditorBuildSettingsScene(s, true));
            return Build(scenes, GameOutput, BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player,
                development ? BuildOptions.Development : BuildOptions.None);
        }

        public static void BuildGameCli() => ExitWith(BuildGame());

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
