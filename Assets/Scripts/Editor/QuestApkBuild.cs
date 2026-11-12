using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CityHall.EditorTools
{
    /// <summary>Builds a fresh Quest APK from the project's enabled build scenes.</summary>
    public static class QuestApkBuild
    {
        [MenuItem("City Hall/Rebuild Town Scene and Quest APK")]
        public static void RebuildTownSceneAndQuestApk()
        {
            TownSceneBuilder.BuildScene();
            Build();
        }

        [MenuItem("City Hall/Build Quest APK")]
        public static void Build()
        {
            var scenePaths = new List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled)
                    scenePaths.Add(scene.path);
            }

            if (scenePaths.Count == 0)
                throw new InvalidOperationException("No enabled scenes are configured in Build Settings.");

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string outputDirectory = Path.Combine(projectRoot, "Builds");
            Directory.CreateDirectory(outputDirectory);
            string outputPath = Path.Combine(outputDirectory, "VRDemocracy-Quest-2026-10-14.apk");

            var options = new BuildPlayerOptions
            {
                scenes = scenePaths.ToArray(),
                locationPathName = outputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Quest APK build failed: " + report.summary.result);

            Debug.Log("Quest APK built: " + outputPath + " (" + report.summary.totalSize + " bytes)");
        }
    }
}
