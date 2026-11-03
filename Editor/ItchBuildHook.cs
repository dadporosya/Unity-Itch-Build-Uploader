using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ItchBuildUploader
{
    /// <summary>Turns each finished player build into an upload job.</summary>
    internal class ItchBuildHook : IPostprocessBuildWithReport
    {
        public int callbackOrder => int.MaxValue;

        public void OnPostprocessBuild(BuildReport report)
        {
            var settings = ItchSettings.Load();
            if (!settings.autoUpload)
                return;

            var summary = report.summary;
            var platform = settings.ForTarget(summary.platform);
            if (platform == null)
            {
                Debug.Log($"[Itch Uploader] {summary.platform} builds are not supported, skipping upload.");
                return;
            }

            if (!platform.enabled)
                return;

            // Don't use summary.totalErrors here: it counts every error logged during the build,
            // including harmless ones from other tools, so it can be above zero for a good build.
            if (summary.result == BuildResult.Failed || summary.result == BuildResult.Cancelled)
            {
                Debug.LogWarning($"[Itch Uploader] The build {summary.result.ToString().ToLowerInvariant()}, skipping upload.");
                return;
            }

            if ((summary.options & BuildOptions.Development) != 0 && !settings.uploadDevelopmentBuilds)
            {
                Debug.Log("[Itch Uploader] Skipping Development Build. Enable uploading these in Tools > Itch.io Build Uploader.");
                return;
            }

            if (!TryCreateJob(settings, platform, summary.platform, summary.outputPath, out var job, out var error))
            {
                Debug.LogError($"[Itch Uploader] Upload skipped: {error}");
                return;
            }

            if (summary.platform == BuildTarget.WebGL)
                WarnAboutWebCompression();

            ItchUploadQueue.Enqueue(job);

            // A batch mode editor usually quits right after the build, so nothing would pick the job up later.
            if (Application.isBatchMode)
                ItchUploadQueue.RunToCompletion();
        }

        internal static bool TryCreateJob(ItchSettings settings, PlatformSetting platform, BuildTarget buildTarget, string outputPath, out UploadJob job, out string error)
        {
            job = null;

            if (!ItchSettings.TryParseTarget(settings.gameUrl, out var target))
            {
                error = "set a valid game page link in Tools > Itch.io Build Uploader.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(platform.channel))
            {
                error = $"no channel name is set for {buildTarget}.";
                return false;
            }

            if (!TryResolveUploadPath(buildTarget, outputPath, out var path, out error))
                return false;

            job = new UploadJob
            {
                path = path,
                target = target,
                channel = platform.channel.Trim().ToLowerInvariant(),
                version = PlayerSettings.bundleVersion
            };
            return true;
        }

        static bool TryResolveUploadPath(BuildTarget buildTarget, string outputPath, out string path, out string error)
        {
            path = null;
            error = null;
            var output = Path.GetFullPath(outputPath);

            switch (buildTarget)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneLinux64:
                    // The output is the executable; the game is the whole folder around it.
                    path = Path.GetDirectoryName(output);
                    break;

                case BuildTarget.Android:
                    if (!File.Exists(output))
                    {
                        error = "exported Android projects cannot be played from itch.io, build an .apk instead.";
                        return false;
                    }
                    path = output;
                    break;

                default:
                    // WebGL builds to a folder and macOS to an .app, which butler wraps correctly by itself.
                    path = output;
                    break;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                error = $"the build output was not found at {path}.";
                return false;
            }

            if (IsUnsafeToPublish(path))
            {
                error = $"\"{path}\" holds more than the build, and everything in it would be published. Build into a dedicated folder such as Builds/Windows.";
                return false;
            }

            return true;
        }

        /// <summary>Uploads are public and unattended, so never push a folder that also contains the project or personal files.</summary>
        static bool IsUnsafeToPublish(string path)
        {
            if (File.Exists(path))
                return false;

            var folder = Normalize(path);
            var project = Normalize(Directory.GetCurrentDirectory());
            if (project == folder || project.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;

            if (Path.GetPathRoot(path) == path)
                return true;

            foreach (var special in new[]
            {
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments
            })
            {
                var personal = Environment.GetFolderPath(special);
                if (!string.IsNullOrEmpty(personal) && string.Equals(Normalize(personal), folder, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return string.Equals(Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")), folder, StringComparison.OrdinalIgnoreCase);
        }

        static string Normalize(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        static void WarnAboutWebCompression()
        {
            if (PlayerSettings.WebGL.compressionFormat != WebGLCompressionFormat.Disabled && !PlayerSettings.WebGL.decompressionFallback)
            {
                Debug.LogWarning("[Itch Uploader] This WebGL build is compressed without Decompression Fallback, so it may fail to load on itch.io. " +
                    "Turn on Player Settings > Publishing Settings > Decompression Fallback, or set Compression Format to Disabled.");
            }
        }
    }
}
