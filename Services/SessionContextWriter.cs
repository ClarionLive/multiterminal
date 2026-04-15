using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Mechanical session context writer — queries the REST API for active task state
    /// and writes ACTIVE-CONTEXT.md. Used by terminal/app close interception for
    /// guaranteed fast backup (&lt;1 second) independent of Claude agent state.
    /// </summary>
    public static class SessionContextWriter
    {
        private static readonly HttpClient _http = new()
        {
            BaseAddress = new Uri("http://127.0.0.1:5050"),
            Timeout = TimeSpan.FromSeconds(3)
        };

        private static readonly string MemoryDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects",
            "H--DevLaptop-ClarionPowerShell-MultiTerminal",
            "memory"
        );

        private static readonly string ContextFile = Path.Combine(MemoryDir, "ACTIVE-CONTEXT.md");

        /// <summary>
        /// Writes ACTIVE-CONTEXT.md by querying the REST API for active task state.
        /// Fast, mechanical, no Claude involvement. Returns true on success.
        /// </summary>
        public static async Task<bool> WriteContextAsync(string agentName = null)
        {
            try
            {
                // Fetch in-progress tasks
                var tasksJson = await _http.GetStringAsync("/api/tasks?status=in_progress");
                using var tasksDoc = JsonDocument.Parse(tasksJson);

                JsonElement taskArray;
                if (tasksDoc.RootElement.TryGetProperty("tasks", out var tasksProperty))
                    taskArray = tasksProperty;
                else
                    taskArray = tasksDoc.RootElement;

                if (taskArray.ValueKind != JsonValueKind.Array || taskArray.GetArrayLength() == 0)
                    return false;

                // Find active task (prefer matching agent name)
                JsonElement activeTask = default;
                bool found = false;

                foreach (var task in taskArray.EnumerateArray())
                {
                    // KanbanTask uses SubStatus (serialized as subStatus) — "active" means this is the active task
                    bool isActive = task.TryGetProperty("subStatus", out var ss) && ss.GetString() == "active";
                    string assignee = task.TryGetProperty("assignee", out var a) ? a.GetString() : "";

                    if (isActive &&
                        (string.IsNullOrEmpty(agentName) || string.Equals(assignee, agentName, StringComparison.OrdinalIgnoreCase)))
                    {
                        activeTask = task;
                        found = true;
                        break;
                    }
                }

                if (!found && !string.IsNullOrEmpty(agentName))
                {
                    // Fall back to any task assigned to me (even if not subStatus=active)
                    foreach (var task in taskArray.EnumerateArray())
                    {
                        string assignee = task.TryGetProperty("assignee", out var a2) ? a2.GetString() : "";
                        if (string.Equals(assignee, agentName, StringComparison.OrdinalIgnoreCase))
                        { activeTask = task; found = true; break; }
                    }
                }

                if (!found)
                {
                    // Fall back to any active task regardless of assignee
                    foreach (var task in taskArray.EnumerateArray())
                    {
                        bool isActive = task.TryGetProperty("subStatus", out var ss) && ss.GetString() == "active";
                        if (isActive) { activeTask = task; found = true; break; }
                    }
                }

                if (!found && taskArray.GetArrayLength() > 0)
                {
                    // Final fallback: first in-progress task
                    activeTask = taskArray[0];
                    found = true;
                }

                if (!found) return false;

                string taskId = activeTask.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(taskId)) return false;

                // Fetch full detail
                var detailJson = await _http.GetStringAsync($"/api/tasks/{taskId}");
                using var detailDoc = JsonDocument.Parse(detailJson);
                var detail = detailDoc.RootElement;

                // Build context markdown
                string content = BuildContext(detail, agentName);

                // Write file
                Directory.CreateDirectory(MemoryDir);
                await File.WriteAllTextAsync(ContextFile, content);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionContextWriter] Failed: {ex.Message}");
                return false;
            }
        }

        private static string BuildContext(JsonElement task, string agentName)
        {
            var now = DateTime.UtcNow;
            var lines = new System.Collections.Generic.List<string>();

            string title = task.TryGetProperty("title", out var t) ? t.GetString() : "Unknown Task";
            string taskId = task.TryGetProperty("id", out var id) ? id.GetString() : "?";

            lines.Add("# Active Context");
            lines.Add($"## Current Work ({now:yyyy-MM-dd})");
            lines.Add("");

            // Parse checklist
            string checklistRaw = task.TryGetProperty("checklist_json", out var cj) ? cj.GetString() : null;
            if (checklistRaw == null) checklistRaw = task.TryGetProperty("checklistJson", out var cj2) ? cj2.GetString() : null;

            int done = 0, testing = 0, coding = 0, pending = 0, total = 0;
            if (!string.IsNullOrEmpty(checklistRaw))
            {
                try
                {
                    using var clDoc = JsonDocument.Parse(checklistRaw);
                    foreach (var item in clDoc.RootElement.EnumerateArray())
                    {
                        total++;
                        string status = item.TryGetProperty("status", out var s) ? s.GetString() : "pending";
                        switch (status)
                        {
                            case "done": done++; break;
                            case "testing": testing++; break;
                            case "coding": coding++; break;
                            default: pending++; break;
                        }
                    }
                }
                catch { }
            }

            string phase = "IN PROGRESS";
            if (total > 0 && done == total) phase = "COMPLETE";
            else if (testing > 0 && coding == 0 && pending == 0) phase = "PIPELINE / TESTING";
            else if (coding > 0) phase = "CODING";
            else if (pending == total && total > 0) phase = "PLANNING";

            lines.Add($"### {title} (Ticket {taskId}) \u2014 {phase}");
            lines.Add("");

            if (total > 0)
            {
                lines.Add($"**Checklist:** {done}/{total} done, {testing} testing, {coding} coding, {pending} pending");
                lines.Add("");
            }

            // Continuation notes
            string contNotes = task.TryGetProperty("continuation_notes", out var cn) ? cn.GetString() : null;
            if (contNotes == null) contNotes = task.TryGetProperty("continuationNotes", out var cn2) ? cn2.GetString() : null;
            if (!string.IsNullOrEmpty(contNotes))
            {
                lines.Add("**Continuation Notes:**");
                lines.Add(contNotes);
                lines.Add("");
            }

            // Plan preview
            string plan = task.TryGetProperty("plan", out var p) ? p.GetString() : null;
            if (!string.IsNullOrEmpty(plan))
            {
                string preview = plan.Length > 300 ? plan.Substring(0, 300) + "..." : plan;
                lines.Add("**Plan Preview:**");
                lines.Add(preview);
                lines.Add("");
            }

            lines.Add($"_Auto-generated by SessionContextWriter at {now:O} — trigger: terminal close (mechanical save)_");

            return string.Join("\n", lines);
        }
    }
}
