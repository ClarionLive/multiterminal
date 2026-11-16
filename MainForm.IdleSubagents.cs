using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MultiTerminal.Docking;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal
{
    /// <summary>
    /// Ticket b3590495: each pane's header shows how many subagents its Claude session still has open, and a
    /// session with subagents idle 30+ minutes is reminded once, at a pause, to stop the ones it is done with.
    /// MT cannot stop another process's in-process teammates; only the session that spawned them can.
    /// </summary>
    public partial class MainForm
    {
        private System.Windows.Forms.Timer _idleSubagentTimer;

        // UI thread only. One key per (session, subagent name, spawn time): each subagent is mentioned once.
        private readonly HashSet<string> _subagentsNudged = new HashSet<string>(StringComparer.Ordinal);

        // DocIds with a reminder on its way, so a sweep cannot send a second one before the first lands.
        private readonly HashSet<string> _subagentNudgeInFlight = new HashSet<string>(StringComparer.Ordinal);

        // 1 while a sweep's file reads are running, so a slow disk cannot stack sweeps.
        private int _idleSubagentSweepRunning;

        /// <summary>Starts the once-a-minute sweep. Called once at startup, on the UI thread.</summary>
        private void StartIdleSubagentSweep()
        {
            try
            {
                _idleSubagentTimer = new System.Windows.Forms.Timer { Interval = (int)IdleSubagents.SweepInterval.TotalMilliseconds };
                _idleSubagentTimer.Tick += (s, e) => SweepIdleSubagents();
                _idleSubagentTimer.Start();
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("IdleSubagents", $"Could not start: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads every live Claude pane's open subagents off the UI thread, then updates the headers and sends
        /// any reminder on it. UI thread.
        /// </summary>
        private void SweepIdleSubagents()
        {
            if (_dockPanel == null || System.Threading.Interlocked.Exchange(ref _idleSubagentSweepRunning, 1) == 1)
            {
                return;
            }

            try
            {
                var panes = _dockPanel.Contents.OfType<TerminalDocument>()
                    .Where(doc => doc != null && !doc.IsDisposed)
                    .Select(doc => new
                    {
                        Doc = doc,
                        SessionId = doc.HasActiveLaunch ? doc.CurrentClaudeSessionId : null,
                        ProfileName = doc.LaunchClaudeProfileName,
                    })
                    .ToList();

                _ = Task.Run(() =>
                {
                    var results = new List<(TerminalDocument Doc, string SessionId, IReadOnlyList<OpenSubagent> Open)>();
                    try
                    {
                        foreach (var pane in panes)
                        {
                            IReadOnlyList<OpenSubagent> open = Array.Empty<OpenSubagent>();
                            if (!string.IsNullOrEmpty(pane.SessionId))
                            {
                                string root = TeamWatcherService.GetConfigRootForProfileName(pane.ProfileName);
                                open = OpenSubagentReader.Read(root, pane.SessionId);
                            }

                            results.Add((pane.Doc, pane.SessionId, open));
                        }
                    }
                    catch (Exception ex)
                    {
                        _debugLogService?.Info("IdleSubagents", $"Sweep failed: {ex.Message}");
                    }

                    PostGitHubUi(() =>
                    {
                        System.Threading.Interlocked.Exchange(ref _idleSubagentSweepRunning, 0);
                        ApplyIdleSubagentSweep(results);
                    });
                });
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Exchange(ref _idleSubagentSweepRunning, 0);
                _debugLogService?.Info("IdleSubagents", $"Sweep failed: {ex.Message}");
            }
        }

        /// <summary>Updates each header and reminds paused sessions. UI thread.</summary>
        private void ApplyIdleSubagentSweep(List<(TerminalDocument Doc, string SessionId, IReadOnlyList<OpenSubagent> Open)> results)
        {
            DateTime now = DateTime.UtcNow;
            foreach (var (doc, sessionId, open) in results)
            {
                if (doc == null || doc.IsDisposed)
                {
                    continue;
                }

                // A pane whose session moved on (/clear, a relaunch) since the read shows nothing until the next sweep.
                bool sameSession = string.Equals(doc.HasActiveLaunch ? doc.CurrentClaudeSessionId : null, sessionId, StringComparison.Ordinal);
                doc.SetSubagentsInHeader(sameSession ? IdleSubagents.ToWire(open, now) : "null");
                if (sameSession && !string.IsNullOrEmpty(sessionId))
                {
                    TryRemindAboutIdleSubagents(doc, sessionId, open, now);
                }
            }
        }

        /// <summary>
        /// Sends one reminder naming the session's subagents idle 30+ minutes that were not mentioned before, if
        /// the agent is at a pause. Checked again just before sending, as GitHub notices are. UI thread.
        /// </summary>
        private void TryRemindAboutIdleSubagents(TerminalDocument doc, string sessionId, IReadOnlyList<OpenSubagent> open, DateTime nowUtc)
        {
            var broker = _mcpServer?.Broker;
            string name = doc.OriginalAgentName;
            string docId = doc.DocId;
            if (broker == null || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(docId) || _subagentNudgeInFlight.Contains(docId))
            {
                return;
            }

            var due = IdleSubagents.DueForNudge(open, sessionId, _subagentsNudged, nowUtc);
            if (due.Count == 0)
            {
                return;
            }

            DateTime? launchStarted = doc.ActiveLaunchStartedUtc;
            long launchGeneration = doc.LaunchGeneration;
            var snapshot = broker.AgentAttention?.Snapshot() ?? (IReadOnlyList<AgentAttentionEntry>)Array.Empty<AgentAttentionEntry>();
            if (!GitHubAgentNotices.IsPaneAtPause(snapshot, sessionId, name, launchStarted, nowUtc))
            {
                return;
            }

            var terminal = broker.GetTerminal(name);
            int? port = terminal?.ChannelPort;
            if (!string.Equals(terminal?.DocId, docId, StringComparison.OrdinalIgnoreCase) || !HasSessionRoute(name, port))
            {
                return;
            }

            string message = IdleSubagents.BuildMessage(due, nowUtc);
            var keys = due.Select(s => IdleSubagents.NudgeKey(sessionId, s)).ToList();
            _subagentNudgeInFlight.Add(docId);
            _ = Task.Run(async () =>
            {
                bool ok = false;
                try
                {
                    // Again, immediately before the write: the same pane, launch and session, still at a pause.
                    bool samePane = string.Equals(broker.GetTerminal(name)?.DocId, docId, StringComparison.OrdinalIgnoreCase)
                                    && doc.LaunchGeneration == launchGeneration
                                    && string.Equals(doc.CurrentClaudeSessionId, sessionId, StringComparison.Ordinal);
                    bool stillPaused = GitHubAgentNotices.IsPaneAtPause(broker.AgentAttention?.Snapshot(), sessionId, name, launchStarted, DateTime.UtcNow);
                    if (samePane && stillPaused)
                    {
                        ok = await DeliverToSessionAsync(name, port, IdleSubagents.SenderName, message).ConfigureAwait(false);
                        _debugLogService?.Info("IdleSubagents", ok
                            ? $"Reminded {name} about {due.Count} idle subagent(s): {string.Join(", ", due.Select(s => s.Name))}"
                            : $"Could not reach {name} about {due.Count} idle subagent(s); will try again");
                    }
                }
                catch (Exception ex)
                {
                    _debugLogService?.Warning("IdleSubagents", $"Reminder to {name} failed: {ex.Message}");
                }
                finally
                {
                    PostGitHubUi(() =>
                    {
                        _subagentNudgeInFlight.Remove(docId);
                        if (ok)
                        {
                            foreach (string key in keys) _subagentsNudged.Add(key);
                        }
                    });
                }
            });
        }
    }
}
