using UnityEditor;
using UnityEngine;

namespace ItchBuildUploader
{
    internal class ItchUploaderWindow : EditorWindow
    {
        const string ApiKeysUrl = "https://itch.io/user/settings/api-keys";

        ItchSettings settings;
        string apiKey;
        string butlerPath;
        bool showAdvanced;
        Vector2 scroll;
        Vector2 logScroll;

        [MenuItem("Tools/Itch.io Build Uploader")]
        static void Open()
        {
            GetWindow<ItchUploaderWindow>("Itch.io Uploader").minSize = new Vector2(380, 480);
        }

        void OnEnable()
        {
            settings = ItchSettings.Load();
            apiKey = ItchSettings.ApiKey;
            butlerPath = ItchSettings.ButlerPath;
            ItchUploadQueue.Changed += Repaint;
        }

        void OnDisable()
        {
            ItchUploadQueue.Changed -= Repaint;
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawAccount();
            DrawGamePage();
            DrawUploadOptions();
            DrawPlatforms();
            DrawAdvanced();
            EditorGUILayout.EndScrollView();

            DrawActions();
            DrawLog();
        }

        void DrawAccount()
        {
            EditorGUILayout.LabelField("itch.io Account", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            apiKey = EditorGUILayout.PasswordField(new GUIContent("API Key", "Stored only on this computer, never in the project."), apiKey);
            if (EditorGUI.EndChangeCheck())
                ItchSettings.ApiKey = apiKey;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Create API Key on itch.io", GUILayout.Width(180)))
                    Application.OpenURL(ApiKeysUrl);
            }

            EditorGUILayout.Space();
        }

        void DrawGamePage()
        {
            EditorGUILayout.LabelField("Game Page", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            settings.gameUrl = EditorGUILayout.TextField(new GUIContent("Page Link", "For example https://your-name.itch.io/your-game"), settings.gameUrl);
            if (EditorGUI.EndChangeCheck())
                settings.Save();

            if (string.IsNullOrWhiteSpace(settings.gameUrl))
                EditorGUILayout.HelpBox("Paste the link of your game page, like https://your-name.itch.io/your-game. The page must already exist on itch.io.", MessageType.Info);
            else if (ItchSettings.TryParseTarget(settings.gameUrl, out var target))
                EditorGUILayout.LabelField("Uploads to", target, EditorStyles.miniLabel);
            else
                EditorGUILayout.HelpBox("This doesn't look like a game page. Use a link like https://your-name.itch.io/your-game.", MessageType.Error);

            EditorGUILayout.Space();
        }

        void DrawUploadOptions()
        {
            EditorGUILayout.LabelField("Uploading", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            settings.autoUpload = EditorGUILayout.Toggle(new GUIContent("Upload After Each Build"), settings.autoUpload);
            settings.uploadDevelopmentBuilds = EditorGUILayout.Toggle(new GUIContent("Include Development Builds"), settings.uploadDevelopmentBuilds);
            if (EditorGUI.EndChangeCheck())
                settings.Save();

            EditorGUILayout.Space();
        }

        void DrawPlatforms()
        {
            EditorGUILayout.LabelField(new GUIContent("Platforms", "Each channel is one slot on the page. A new build replaces the previous build in its channel."), EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            DrawPlatform("Windows (64-bit)", settings.windows);
            DrawPlatform("Windows (32-bit)", settings.windows32);
            DrawPlatform("Linux", settings.linux);
            DrawPlatform("macOS", settings.mac);
            DrawPlatform("Web (WebGL)", settings.web);
            DrawPlatform("Android", settings.android);
            if (EditorGUI.EndChangeCheck())
                settings.Save();

            EditorGUILayout.Space();
        }

        static void DrawPlatform(string label, PlatformSetting platform)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                platform.enabled = EditorGUILayout.ToggleLeft(label, platform.enabled, GUILayout.Width(150));
                using (new EditorGUI.DisabledScope(!platform.enabled))
                    platform.channel = EditorGUILayout.TextField(platform.channel);
            }
        }

        void DrawAdvanced()
        {
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced", true);
            if (!showAdvanced)
                return;

            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                butlerPath = EditorGUILayout.TextField(new GUIContent("Butler Path", "Leave empty to download butler automatically."), butlerPath);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    var picked = EditorUtility.OpenFilePanel("Select butler", "", "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        butlerPath = picked;
                        GUI.changed = true;
                    }
                }
            }
            if (EditorGUI.EndChangeCheck())
                ItchSettings.ButlerPath = butlerPath;
        }

        void DrawActions()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Status", ItchUploadQueue.Status);

            using (new EditorGUILayout.HorizontalScope())
            {
                var busy = ItchUploadQueue.IsBusy;
                using (new EditorGUI.DisabledScope(busy))
                {
                    if (GUILayout.Button("Test Connection"))
                        ItchUploadQueue.CheckConnection();

                    var lastJob = ItchUploadQueue.LastJob;
                    using (new EditorGUI.DisabledScope(lastJob == null))
                    {
                        if (GUILayout.Button(new GUIContent("Upload Last Build Again", lastJob?.path)))
                            ItchUploadQueue.Enqueue(lastJob);
                    }
                }

                using (new EditorGUI.DisabledScope(!busy))
                {
                    if (GUILayout.Button("Cancel", GUILayout.Width(70)))
                        ItchUploadQueue.Cancel();
                }
            }
        }

        void DrawLog()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
                if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(50)))
                    ItchUploadQueue.ClearLog();
            }

            logScroll = EditorGUILayout.BeginScrollView(logScroll, EditorStyles.helpBox, GUILayout.Height(150));
            var text = string.Join("\n", ItchUploadQueue.Log);
            var height = EditorStyles.label.CalcHeight(new GUIContent(text), position.width - 40);
            EditorGUILayout.SelectableLabel(text, EditorStyles.wordWrappedLabel, GUILayout.MinHeight(height));
            EditorGUILayout.EndScrollView();
        }
    }
}
