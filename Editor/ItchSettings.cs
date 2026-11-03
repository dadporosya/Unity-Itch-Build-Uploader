using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ItchBuildUploader
{
    [Serializable]
    internal class PlatformSetting
    {
        public bool enabled = true;
        public string channel = "";

        public PlatformSetting() { }

        public PlatformSetting(string channel)
        {
            this.channel = channel;
        }
    }

    /// <summary>
    /// Project settings live in ProjectSettings/ so they can be shared through version control.
    /// The API key is a secret, so it is kept per user in EditorPrefs and never written to the project.
    /// </summary>
    [Serializable]
    internal class ItchSettings
    {
        const string FilePath = "ProjectSettings/ItchBuildUploader.json";
        const string ApiKeyPref = "ItchBuildUploader.ApiKey";
        const string ButlerPathPref = "ItchBuildUploader.ButlerPath";

        public string gameUrl = "";
        public bool autoUpload = true;
        public bool uploadDevelopmentBuilds;

        // A channel is one upload slot on the page: pushing to it again replaces that platform's build.
        // itch.io tags the platform from the channel name (win, linux, mac, android); HTML is tagged on the page.
        public PlatformSetting windows = new PlatformSetting("windows");
        public PlatformSetting windows32 = new PlatformSetting("windows-32");
        public PlatformSetting linux = new PlatformSetting("linux");
        public PlatformSetting mac = new PlatformSetting("mac");
        public PlatformSetting web = new PlatformSetting("html5");
        public PlatformSetting android = new PlatformSetting("android");

        public static string ApiKey
        {
            get => EditorPrefs.GetString(ApiKeyPref, "");
            set => EditorPrefs.SetString(ApiKeyPref, value?.Trim() ?? "");
        }

        /// <summary>Optional path to an existing butler executable. Empty means use the managed download.</summary>
        public static string ButlerPath
        {
            get => EditorPrefs.GetString(ButlerPathPref, "");
            set => EditorPrefs.SetString(ButlerPathPref, value?.Trim() ?? "");
        }

        /// <summary>The key to hand to butler, falling back to the variable butler itself reads (useful on CI).</summary>
        public static string EffectiveApiKey
        {
            get
            {
                var key = ApiKey;
                return string.IsNullOrEmpty(key) ? Environment.GetEnvironmentVariable(ButlerClient.ApiKeyVariable) ?? "" : key;
            }
        }

        public static ItchSettings Load()
        {
            var settings = new ItchSettings();
            if (File.Exists(FilePath))
                JsonUtility.FromJsonOverwrite(File.ReadAllText(FilePath), settings);
            return settings;
        }

        public void Save()
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
        }

        public PlatformSetting ForTarget(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows64: return windows;
                case BuildTarget.StandaloneWindows: return windows32;
                case BuildTarget.StandaloneLinux64: return linux;
                case BuildTarget.StandaloneOSX: return mac;
                case BuildTarget.WebGL: return web;
                case BuildTarget.Android: return android;
                default: return null;
            }
        }

        /// <summary>Turns a game page link into the "user/game" form butler pushes to.</summary>
        public static bool TryParseTarget(string text, out string target)
        {
            target = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var value = text.Trim();
            var match = Regex.Match(value, @"^(?:https?://)?([a-z0-9_-]+)\.itch\.io/([a-z0-9_-]+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(value, @"^([a-z0-9_-]+)/([a-z0-9_-]+)$", RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            target = (match.Groups[1].Value + "/" + match.Groups[2].Value).ToLowerInvariant();
            return true;
        }
    }
}
