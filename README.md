# Itch.io Build Uploader

Uploads every Unity build to your itch.io game page automatically. If the page already has a build of the same type (Windows, HTML, ...), the new one replaces it.

Uploads run in the background through [butler](https://itch.io/docs/butler/), itch.io's official upload tool. The plugin downloads butler by itself the first time it is needed.

## Install

1. In Unity, open **Window > Package Manager**.
2. Click **+ > Install package from disk...**
3. Select `package.json` in this folder.

The package is referenced from this folder, so keep the folder where it is. To install it into one project for good, copy this folder into that project's `Packages/` folder instead.

## Set up

1. Create the game page on itch.io if it doesn't exist yet. The plugin can't create pages.
2. Open **Tools > Itch.io Build Uploader**.
3. Click **Create API Key on itch.io**, generate a key and paste it into **API Key**.
4. Paste your page link into **Page Link**, for example `https://your-name.itch.io/your-game`.
5. Click **Test Connection**. The log lists the builds already on the page.

## Use

Build as usual with **File > Build Profiles** (or **Build Settings**), or from a build script. When the build finishes, the upload starts in the background, with progress in the status bar at the bottom right of Unity. You can keep working meanwhile. Progress and errors are also shown in the uploader window.

**Build each platform into its own empty folder**, such as `Builds/Windows` and `Builds/Web`. Everything in that folder gets published. The plugin refuses to upload the project folder, your Desktop, Documents, Downloads or home folder.

## How replacing works

itch.io organises builds into channels, one per slot on the page. Pushing to a channel replaces what's in it:

| Build | Default channel |
| --- | --- |
| Windows 64-bit | `windows` |
| Windows 32-bit | `windows-32` |
| Linux | `linux` |
| macOS | `mac` |
| WebGL | `html5` |
| Android | `android` |

You can rename channels or turn platforms off in the window. Files uploaded before through the itch.io website aren't channels, so butler can't replace them. Delete those once on the page's Edit screen, otherwise they stay next to the new build.

After the upload finishes itch.io still has to process the build, which takes from a few seconds to several minutes. The old build stays available until then.

## Web (WebGL) builds

These need two manual steps, once, because itch.io doesn't offer them through butler:

1. After the first upload, open the page's **Edit game** screen and set **Kind of project** to **HTML**.
2. Tick **This file will be played in the browser** on the uploaded file, then save.

Set **Player Settings > Publishing Settings > Decompression Fallback** on, or Compression Format to Disabled. A compressed build without the fallback may fail to load, and the plugin logs a warning about it.

## Notes

- The API key is saved in Unity's EditorPrefs on your computer, unencrypted, and never in the project. Other settings are in `ProjectSettings/ItchBuildUploader.json`, which is safe to commit.
- Without a key in the window, the `BUTLER_API_KEY` environment variable is used. In batch mode the build waits for the upload to finish.
- Development Builds are skipped unless you enable them. Failed or cancelled builds are never uploaded.
- The upload's version number on itch.io is the project's **Version** from Player Settings.
- A script recompile during an upload restarts that upload.
- Page links are read as `user/game`. If your account name contains `_`, your link shows `-` instead, so type `your_name/your-game` into **Page Link** by hand.
- Butler is stored in `%LOCALAPPDATA%\ItchBuildUploader` on Windows. If itch.io ever rejects the old version, delete that folder and it downloads again.

## Troubleshooting

| Message | Fix |
| --- | --- |
| `invalid api key` or similar | Paste the key again, or create a new one. |
| Page not found or no access | Check **Page Link**. The key's account must own the page or be an admin of it. |
| Nothing happens after a build | Check that **Upload After Each Build** and the platform are on. The Console says why a build was skipped. |
| Upload skipped, folder holds more than the build | Build into a dedicated folder. |
