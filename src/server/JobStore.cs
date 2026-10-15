using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>Persistent background jobs for long Revit operations (exports, clash).
    /// Progress is coarse (queued/running/complete) because Revit offers no
    /// intermediate callbacks. Cancel is real while queued and best-effort
    /// afterwards: an already-dispatched Revit call cannot be interrupted, so a
    /// late cancel detaches (marks canceled, keeps the stashed result) instead
    /// of pretending the model work stopped.</summary>
    internal static class RevitJobStore
    {
        public const int MaxJobs = 100;
        public const int MaxPreviewChars = 4096;
        public const int MinTimeoutSeconds = 1;
        public const int MaxTimeoutSeconds = 3600;
        public const int DefaultTimeoutSeconds = 600;

        public static readonly string[] JobCommands =
        {
            "export_pdf", "export_dwg", "export_dgn", "export_dwf", "export_ifc",
            "export_nwc", "export_fbx", "export_gbxml", "export_image",
            "export_schedule_csv", "export_elements_data", "batch_export_sheets",
            "clash_detection"
        };

        internal static string RootOverride { get; set; }

        private static readonly object Gate = new object();
        private static readonly ConcurrentDictionary<string, bool> Live = new ConcurrentDictionary<string, bool>();

        private static string StorePath()
        {
            var dir = RootOverride
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RvtVerify", "jobs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "jobs.json");
        }

        private static JArray Load()
        {
            var path = StorePath();
            if (!File.Exists(path))
                return new JArray();
            try
            {
                var parsed = JToken.Parse(File.ReadAllText(path, Encoding.UTF8));
                return parsed as JArray ?? new JArray();
            }
            catch
            {
                return new JArray();
            }
        }

        private static void Prune(JArray jobs)
        {
            while (jobs.Count > MaxJobs)
            {
                JToken oldest = null;
                foreach (var item in jobs.OfType<JObject>())
                {
                    if ((item.Value<string>("status") ?? "") == "running") continue;
                    if (oldest == null || string.Compare(
                        item.Value<string>("created_utc") ?? "",
                        ((JObject)oldest).Value<string>("created_utc") ?? "",
                        StringComparison.Ordinal) < 0)
                        oldest = item;
                }
                if (oldest == null) break;
                jobs.Remove(oldest);
            }
        }

        private static void Save(JArray jobs)
        {
            Prune(jobs);
            var path = StorePath();
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, jobs.ToString(Formatting.None), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private static JObject Find(JArray jobs, string jobId)
        {
            return jobs.OfType<JObject>()
                .FirstOrDefault(j => string.Equals(j.Value<string>("job_id"), jobId, StringComparison.Ordinal));
        }

        public static JObject Submit(string command, JObject parameters, int timeoutSeconds,
            Func<string, object, Task<JObject>> send)
        {
            var id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow.ToString("o");
            lock (Gate)
            {
                var jobs = Load();
                jobs.Add(new JObject
                {
                    ["job_id"] = id,
                    ["command"] = command,
                    ["parameters"] = parameters?.DeepClone(),
                    ["timeout_seconds"] = timeoutSeconds,
                    ["status"] = "queued",
                    ["created_utc"] = now,
                    ["started_utc"] = null,
                    ["finished_utc"] = null,
                    ["elapsed_s"] = null,
                    ["preview"] = null,
                    ["error"] = null,
                    ["note"] = null,
                });
                Save(jobs);
            }
            Live[id] = true;
            var capturedId = id;
            Task.Run(async () =>
            {
                try { await RunJob(capturedId, command, parameters, timeoutSeconds, send); }
                finally { Live.TryRemove(capturedId, out _); }
            });
            return new JObject { ["job_id"] = id, ["status"] = "queued", ["command"] = command };
        }

        private static async Task RunJob(string jobId, string command, JObject parameters,
            int timeoutSeconds, Func<string, object, Task<JObject>> send)
        {
            DateTimeOffset started = DateTimeOffset.UtcNow;
            lock (Gate)
            {
                var jobs = Load();
                var job = Find(jobs, jobId);
                if (job == null) return;
                if (!string.Equals(job.Value<string>("status"), "queued", StringComparison.Ordinal)) return;
                job["started_utc"] = started.ToString("o");
                job["status"] = "running";
                Save(jobs);
            }
            JObject result = null;
            string error = null;
            try
            {
                result = await send(command, parameters);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            var finished = DateTimeOffset.UtcNow;
            lock (Gate)
            {
                var jobs = Load();
                var job = Find(jobs, jobId);
                if (job == null) return;
                var wasCanceled = string.Equals(job.Value<string>("status"), "canceled", StringComparison.Ordinal);
                job["finished_utc"] = finished.ToString("o");
                job["elapsed_s"] = Math.Round((finished - started).TotalSeconds, 1);
                if (error != null)
                {
                    job["status"] = wasCanceled ? "canceled" : "failed";
                    job["error"] = error;
                }
                else
                {
                    if (!wasCanceled) job["status"] = "complete";
                    job["note"] = wasCanceled
                        ? "Canceled while running: detached, Revit work may still have completed; preview below."
                        : null;
                }
                job["preview"] = Preview(result);
                Prune(jobs);
                Save(jobs);
            }
        }

        private static string Preview(JObject result)
        {
            if (result == null) return null;
            var text = result.ToString(Formatting.None);
            if (text.Length > MaxPreviewChars)
                return text.Substring(0, MaxPreviewChars) + "...[truncated " + (text.Length - MaxPreviewChars) + " chars; rerun synchronously for the full payload]";
            return text;
        }

        public static JObject Snapshot(string jobId)
        {
            lock (Gate)
            {
                var job = Find(Load(), jobId);
                if (job == null)
                    return new JObject { ["job_id"] = jobId, ["status"] = "unknown", ["error"] = "No such job." };
                return (JObject)job.DeepClone();
            }
        }

        public static JObject List(int limit)
        {
            if (limit < 1 || limit > MaxJobs)
                return new JObject { ["error"] = "limit must be 1-" + MaxJobs + "." };
            lock (Gate)
            {
                var jobs = Load().OfType<JObject>().Reverse().Take(limit).ToArray();
                var summaries = new JArray();
                foreach (var job in jobs)
                {
                    summaries.Add(new JObject
                    {
                        ["job_id"] = job.Value<string>("job_id"),
                        ["command"] = job.Value<string>("command"),
                        ["status"] = job.Value<string>("status"),
                        ["created_utc"] = job.Value<string>("created_utc"),
                        ["finished_utc"] = job.Value<string>("finished_utc"),
                        ["elapsed_s"] = job["elapsed_s"]?.DeepClone(),
                    });
                }
                return new JObject { ["jobs"] = summaries, ["count"] = summaries.Count };
            }
        }

        public static JObject Cancel(string jobId)
        {
            lock (Gate)
            {
                var jobs = Load();
                var job = Find(jobs, jobId);
                if (job == null)
                    return new JObject { ["job_id"] = jobId, ["canceled"] = false, ["error"] = "No such job." };
                var status = job.Value<string>("status") ?? "";
                if (status == "complete" || status == "failed")
                    return new JObject { ["job_id"] = jobId, ["status"] = status, ["canceled"] = false, ["note"] = "Job already finished." };
                if (status == "canceled")
                    return new JObject { ["job_id"] = jobId, ["status"] = status, ["canceled"] = false, ["note"] = "Job already canceled." };
                job["status"] = "canceled";
                job["finished_utc"] = DateTimeOffset.UtcNow.ToString("o");
                job["note"] = status == "queued"
                    ? "Canceled before dispatch; nothing ran."
                    : "Canceled while running: detached, Revit work may still complete; check status for the stashed preview.";
                Save(jobs);
                return new JObject { ["job_id"] = jobId, ["status"] = "canceled", ["canceled"] = true, ["note"] = job.Value<string>("note") };
            }
        }
    }
}
