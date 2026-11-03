using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ItchBuildUploader
{
    [Serializable]
    internal class UploadJob
    {
        public string path;
        public string target;
        public string channel;
        public string version;

        public string Destination => $"{target}:{channel}";
    }

    /// <summary>
    /// Runs uploads one at a time in the background. The queue is kept in SessionState, and a job only leaves it
    /// once it has finished, so an upload interrupted by a domain reload simply starts over afterwards.
    /// </summary>
    [InitializeOnLoad]
    internal static class ItchUploadQueue
    {
        const string QueueKey = "ItchBuildUploader.Queue";
        const string LastJobKey = "ItchBuildUploader.LastJob";
        const string LogKey = "ItchBuildUploader.Log";
        const int MaxLogLines = 200;

        [Serializable]
        class JobList
        {
            public List<UploadJob> jobs = new List<UploadJob>();
        }

        class Run : IButlerListener
        {
            public UploadJob job;
            public int progressId;
            public Task task;
            public readonly CancellationTokenSource cancellation = new CancellationTokenSource();

            public void OnLog(string message)
            {
                Post(() => AppendLog(message));
            }

            public void OnProgress(float progress, double etaSeconds, double bytesPerSecond)
            {
                var description = $"{bytesPerSecond / (1024 * 1024):0.0} MB/s, about {Math.Ceiling(etaSeconds / 60):0} min left";
                Post(() =>
                {
                    Status = $"{Label}: {progress:P0} ({description})";
                    if (progressId != 0)
                        Progress.Report(progressId, progress, description);
                    Changed?.Invoke();
                });
            }

            string Label => job != null ? $"Uploading to {job.Destination}" : "Checking connection";
        }

        static readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
        static readonly List<string> log = new List<string>();
        static JobList queue = new JobList();
        static Run running;

        public static event Action Changed;

        public static string Status { get; private set; } = "Idle";

        public static IReadOnlyList<string> Log => log;

        public static bool IsBusy => running != null || queue.jobs.Count > 0;

        public static UploadJob LastJob
        {
            get
            {
                var json = SessionState.GetString(LastJobKey, "");
                return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<UploadJob>(json);
            }
        }

        static ItchUploadQueue()
        {
            JsonUtility.FromJsonOverwrite(SessionState.GetString(QueueKey, "{}"), queue);

            var savedLog = SessionState.GetString(LogKey, "");
            if (savedLog.Length > 0)
                log.AddRange(savedLog.Split('\n'));

            EditorApplication.update += () => Pump(false);
            AssemblyReloadEvents.beforeAssemblyReload += StopForReload;
            EditorApplication.quitting += StopForReload;
        }

        public static void Enqueue(UploadJob job)
        {
            // Rebuilding a platform that is still waiting makes the waiting build obsolete.
            queue.jobs.RemoveAll(j => j != CurrentJob && j.Destination == job.Destination);
            queue.jobs.Add(job);
            SaveQueue();
            AppendLog($"Queued {job.path} for {job.Destination}");
        }

        public static void CheckConnection()
        {
            if (IsBusy)
                return;

            if (!ItchSettings.TryParseTarget(ItchSettings.Load().gameUrl, out var target))
            {
                AppendLog("Enter a valid game page link first.");
                return;
            }

            AppendLog($"Checking access to {target}...");
            var apiKey = ItchSettings.EffectiveApiKey;
            var butlerPath = ItchSettings.ButlerPath;
            var run = new Run();
            Begin(run, () => ButlerClient.Status(target, apiKey, butlerPath, run, run.cancellation.Token));
        }

        public static void Cancel()
        {
            running?.cancellation.Cancel();
        }

        /// <summary>Blocks until everything queued has finished. For batch mode, where the editor loop may never run again.</summary>
        public static void RunToCompletion()
        {
            while (IsBusy)
            {
                Pump(true);
                Thread.Sleep(100);
            }

            Pump(true);
        }

        static UploadJob CurrentJob => running?.job;

        static void Post(Action action)
        {
            mainThreadActions.Enqueue(action);
        }

        static void Pump(bool ignoreEditorState)
        {
            while (mainThreadActions.TryDequeue(out var action))
                action();

            if (running != null)
            {
                if (!running.task.IsCompleted)
                    return;
                Finish();
            }

            if (queue.jobs.Count == 0)
                return;

            // The build files are only final once the build has fully wrapped up.
            if (!ignoreEditorState && (BuildPipeline.isBuildingPlayer || EditorApplication.isCompiling))
                return;

            StartUpload(queue.jobs[0]);
        }

        static void StartUpload(UploadJob job)
        {
            var apiKey = ItchSettings.EffectiveApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                queue.jobs.Remove(job);
                SaveQueue();
                SessionState.SetString(LastJobKey, JsonUtility.ToJson(job));
                Fail($"Upload to {job.Destination} skipped: set your API key in Tools > Itch.io Build Uploader.");
                return;
            }

            SessionState.SetString(LastJobKey, JsonUtility.ToJson(job));
            AppendLog($"Uploading {job.path} to {job.Destination}{(string.IsNullOrEmpty(job.version) ? "" : $" (version {job.version})")}");

            var butlerPath = ItchSettings.ButlerPath;
            var run = new Run { job = job };
            run.progressId = Progress.Start("Uploading to itch.io", job.Destination);
            Progress.RegisterCancelCallback(run.progressId, () =>
            {
                run.cancellation.Cancel();
                return true;
            });
            Begin(run, () => ButlerClient.Push(job, apiKey, butlerPath, run, run.cancellation.Token));
        }

        static void Begin(Run run, Action work)
        {
            running = run;
            Status = run.job != null ? $"Uploading to {run.job.Destination}" : "Checking connection";
            run.task = Task.Run(work);
            Changed?.Invoke();
        }

        static void Finish()
        {
            var run = running;
            running = null;
            var job = run.job;

            if (job != null)
            {
                queue.jobs.Remove(job);
                SaveQueue();
            }

            var exception = run.task.Exception?.GetBaseException();
            var cancelled = run.task.IsCanceled || exception is OperationCanceledException;

            if (run.progressId != 0)
            {
                Progress.Finish(run.progressId, cancelled ? Progress.Status.Canceled : exception != null ? Progress.Status.Failed : Progress.Status.Succeeded);
            }

            if (cancelled)
            {
                AppendLog(job != null ? $"Upload to {job.Destination} cancelled." : "Check cancelled.");
                Status = "Cancelled";
            }
            else if (exception != null)
            {
                Fail(job != null ? $"Upload to {job.Destination} failed: {exception.Message}" : $"Connection check failed: {exception.Message}");
            }
            else if (job != null)
            {
                var message = $"Uploaded to {job.Destination}. itch.io is processing the build and will replace the previous one in this channel shortly.";
                Debug.Log("[Itch Uploader] " + message);
                AppendLog(message);
                Status = "Upload finished";
            }
            else
            {
                AppendLog("Connection OK.");
                Status = "Connection OK";
            }

            Changed?.Invoke();
        }

        static void Fail(string message)
        {
            Debug.LogError("[Itch Uploader] " + message);
            AppendLog(message);
            Status = "Failed";
            Changed?.Invoke();
        }

        static void StopForReload()
        {
            var run = running;
            if (run == null)
                return;

            // The worker dies with the domain, and with it butler's output pipe, so stop cleanly.
            // The job stays queued and restarts after the reload.
            running = null;
            run.cancellation.Cancel();
            try
            {
                run.task.Wait(2000);
            }
            catch (AggregateException)
            {
                // Cancellation surfaces here, which is the expected outcome.
            }

            if (run.progressId != 0)
                Progress.Finish(run.progressId, Progress.Status.Canceled);
            AppendLog(run.job != null
                ? $"Upload to {run.job.Destination} interrupted by a script reload, it will restart."
                : "Connection check interrupted by a script reload, please try again.");
        }

        static void SaveQueue()
        {
            SessionState.SetString(QueueKey, JsonUtility.ToJson(queue));
        }

        static void AppendLog(string message)
        {
            log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (log.Count > MaxLogLines)
                log.RemoveRange(0, log.Count - MaxLogLines);
            SessionState.SetString(LogKey, string.Join("\n", log));
            Changed?.Invoke();
        }

        public static void ClearLog()
        {
            log.Clear();
            SessionState.EraseString(LogKey);
            Changed?.Invoke();
        }
    }
}
