using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ItchBuildUploader
{
    /// <summary>Receives butler's output. Called on the worker thread.</summary>
    internal interface IButlerListener
    {
        void OnLog(string message);
        void OnProgress(float progress, double etaSeconds, double bytesPerSecond);
    }

    internal class ButlerException : Exception
    {
        public ButlerException(string message) : base(message) { }
    }

    /// <summary>
    /// Wraps butler, itch.io's official upload tool. All members run synchronously and off the main thread,
    /// so no Unity API that requires the main thread may be used here.
    /// </summary>
    internal static class ButlerClient
    {
        public const string ApiKeyVariable = "BUTLER_API_KEY";
        const string DownloadRoot = "https://broth.itch.zone/butler";

        // Unity writes these next to the player and tells you not to ship them.
        static readonly string[] IgnoredFiles =
        {
            "*_ButDontShipItWithYourGame",
            "*_DoNotShip"
        };

#pragma warning disable 649 // Assigned by JsonUtility
        [Serializable]
        class Message
        {
            public string type;
            public string message;
            public string level;
            public float progress;
            public double eta;
            public double bps;
            public StatusValue value;
        }

        [Serializable]
        class StatusValue
        {
            public Channel[] channels;
        }

        [Serializable]
        class Channel
        {
            public string name;
            public Build head;
            public Build pending;
        }

        [Serializable]
        class Build
        {
            public long id;
            public string state;
            public string userVersion;
        }
#pragma warning restore 649

        public static void Push(UploadJob job, string apiKey, string butlerPath, IButlerListener listener, CancellationToken cancellation)
        {
            var exe = ResolveExecutable(butlerPath, listener, cancellation);

            var args = new List<string> { "--json", "push" };
            foreach (var pattern in IgnoredFiles)
                args.Add("--ignore=" + pattern);
            if (!string.IsNullOrWhiteSpace(job.version))
                args.Add("--userversion=" + job.version.Trim());
            args.Add(job.path);
            args.Add(job.target + ":" + job.channel);

            Run(exe, args, apiKey, listener, cancellation);
        }

        /// <summary>Checks the key and page access, and lists what is already on the page.</summary>
        public static void Status(string target, string apiKey, string butlerPath, IButlerListener listener, CancellationToken cancellation)
        {
            var exe = ResolveExecutable(butlerPath, listener, cancellation);
            Run(exe, new List<string> { "--json", "status", target }, apiKey, listener, cancellation);
        }

        static string ManagedFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ItchBuildUploader", "butler");

        static string ExecutableName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "butler.exe" : "butler";

        static string DownloadChannel
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return "windows-amd64";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "darwin-arm64" : "darwin-amd64";
                return "linux-amd64";
            }
        }

        static string ResolveExecutable(string butlerPath, IButlerListener listener, CancellationToken cancellation)
        {
            if (!string.IsNullOrEmpty(butlerPath))
            {
                if (!File.Exists(butlerPath))
                    throw new ButlerException($"butler was not found at {butlerPath}. Fix or clear the path in the uploader window.");
                return butlerPath;
            }

            var exe = Path.Combine(ManagedFolder, ExecutableName);
            if (File.Exists(exe))
                return exe;

            listener.OnLog("Downloading butler (itch.io's upload tool). This only happens once...");
            Install($"{DownloadRoot}/{DownloadChannel}/LATEST/archive/default", ManagedFolder, cancellation);
            return exe;
        }

        static void Install(string url, string folder, CancellationToken cancellation)
        {
            // Unpack beside the final folder and move it into place, so a failed download never leaves a broken install.
            var staging = folder + ".download";
            var archive = staging + ".zip";
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                using (var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        throw new ButlerException($"Could not download butler from {url} (HTTP {(int)response.StatusCode}).");

                    Directory.CreateDirectory(Path.GetDirectoryName(archive));
                    using (var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var file = File.Create(archive))
                        source.CopyToAsync(file, 81920, cancellation).GetAwaiter().GetResult();
                }

                if (Directory.Exists(staging))
                    Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);

                using (var zip = new ZipArchive(File.OpenRead(archive), ZipArchiveMode.Read))
                {
                    // The archive is flat: butler plus two 7-zip libraries.
                    foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0))
                    {
                        using (var input = entry.Open())
                        using (var output = File.Create(Path.Combine(staging, entry.Name)))
                            input.CopyTo(output);
                    }
                }

                var exe = Path.Combine(staging, ExecutableName);
                if (!File.Exists(exe))
                    throw new ButlerException($"The butler download from {url} did not contain {ExecutableName}.");

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start("chmod", $"+x {Quote(exe)}")?.WaitForExit();

                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
                Directory.Move(staging, folder);
            }
            catch (HttpRequestException e)
            {
                throw new ButlerException($"Could not download butler: {e.Message}");
            }
            finally
            {
                if (File.Exists(archive))
                    File.Delete(archive);
                if (Directory.Exists(staging))
                    Directory.Delete(staging, true);
            }
        }

        static void Run(string exe, IEnumerable<string> args, string apiKey, IButlerListener listener, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();

            var startInfo = new ProcessStartInfo(exe)
            {
                Arguments = string.Join(" ", args.Select(Quote)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Passed through the environment so the key never shows up in a process list or log.
            if (!string.IsNullOrEmpty(apiKey))
                startInfo.EnvironmentVariables[ApiKeyVariable] = apiKey;

            using (var process = new Process { StartInfo = startInfo })
            {
                string lastError = null;
                process.ErrorDataReceived += (_, e) =>
                {
                    // In JSON mode fatal errors also arrive on stdout, where they were already reported.
                    if (!string.IsNullOrWhiteSpace(e.Data) && !e.Data.StartsWith("bailing out:"))
                        lastError = e.Data;
                };

                try
                {
                    process.Start();
                }
                catch (Exception e)
                {
                    throw new ButlerException($"Could not start butler at {exe}: {e.Message}");
                }

                process.BeginErrorReadLine();

                // Without a login butler would wait for an answer; closing stdin makes it fail instead.
                process.StandardInput.Close();

                using (cancellation.Register(() => Kill(process)))
                {
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        var error = HandleLine(line, listener);
                        if (error != null)
                            lastError = error;
                    }

                    process.WaitForExit();
                }

                cancellation.ThrowIfCancellationRequested();

                if (process.ExitCode != 0)
                    throw new ButlerException(lastError ?? $"butler exited with code {process.ExitCode}.");
            }
        }

        /// <summary>Forwards one line of output and returns its text if it was an error.</summary>
        static string HandleLine(string line, IButlerListener listener)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            Message message = null;
            if (line.StartsWith("{"))
            {
                try
                {
                    message = JsonUtility.FromJson<Message>(line);
                }
                catch (ArgumentException)
                {
                    // Not one of butler's messages, shown as plain text below.
                }
            }

            if (message == null)
            {
                listener.OnLog(line);
                return null;
            }

            switch (message.type)
            {
                case "log":
                    if (message.level != "debug" && !string.IsNullOrWhiteSpace(message.message))
                        listener.OnLog(message.message);
                    return null;

                case "progress":
                    listener.OnProgress(message.progress, message.eta, message.bps);
                    return null;

                case "error":
                    return message.message;

                case "result":
                    if (message.value?.channels != null)
                        LogChannels(message.value.channels, listener);
                    return null;

                default:
                    return null;
            }
        }

        static void LogChannels(Channel[] channels, IButlerListener listener)
        {
            if (channels.Length == 0)
            {
                listener.OnLog("The page has no butler builds yet.");
                return;
            }

            listener.OnLog("Builds on the page (a new build replaces the one in the same channel):");
            foreach (var channel in channels)
                listener.OnLog($"  {channel.name}: {Describe(channel)}");
        }

        static string Describe(Channel channel)
        {
            var head = channel.head;
            if (head == null || head.id == 0)
                return "no finished build";

            var version = string.IsNullOrEmpty(head.userVersion) ? $"build {head.id}" : $"version {head.userVersion}";
            var pending = channel.pending;
            return pending != null && pending.id != 0 ? $"{version}, build {pending.id} processing" : $"{version} ({head.state})";
        }

        static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Already exiting.
            }
        }

        /// <summary>Quotes one argument using the rules of the Windows C runtime, which Mono also follows.</summary>
        static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return argument;

            var quoted = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                quoted.Append(c);
            }

            return quoted.Append('\\', backslashes * 2).Append('"').ToString();
        }
    }
}
