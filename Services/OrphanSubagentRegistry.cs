using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The tailers of non-team ("orphan") subagent panels, keyed by AGENT, not by file (GH #89).
    ///
    /// One subagent can write more than one transcript file. Claude Code keeps a session's files
    /// under the project folder of the session's current directory, so when the parent session
    /// changes worktree while a subagent runs, the subagent goes on writing a new
    /// <c>agent-&lt;id&gt;.jsonl</c> under the new folder (same session id, same agent id, a new
    /// path that repeats some of the old lines). Keyed by path, that second file became a second
    /// panel, and the SubagentStop hook, which names one path, stopped only one of them: the other
    /// sat on "Running" for good.
    ///
    /// So a file is attributed to an agent by <see cref="AgentKey"/> (session id + agent file
    /// name). A file of an agent that already has a tailer is handed to that tailer; a file of an
    /// agent that has already finished is ignored. Paths are still indexed, because the hook and
    /// the root teardown arrive with paths.
    /// </summary>
    internal sealed class OrphanSubagentRegistry
    {
        /// <summary>How long a finished agent is remembered, so a late copy of its file is ignored.</summary>
        internal static readonly TimeSpan FinishedMemory = TimeSpan.FromHours(24);

        private readonly object _lock = new object();
        private readonly Dictionary<string, TranscriptTailer> _byPath = new Dictionary<string, TranscriptTailer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TranscriptTailer> _byAgent = new Dictionary<string, TranscriptTailer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _finished = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The outcome of <see cref="Claim"/>.</summary>
        internal enum ClaimResult
        {
            /// <summary>No tailer for this agent: the caller's new tailer was registered.</summary>
            New,

            /// <summary>The agent already has a tailer (returned): the path was added to it.</summary>
            Existing,

            /// <summary>The agent already finished: nothing was registered.</summary>
            Finished,
        }

        /// <summary>
        /// Which agent a transcript belongs to: <c>&lt;session id&gt;/&lt;agent file name&gt;</c>, read from
        /// <c>...\&lt;session id&gt;\subagents\agent-&lt;id&gt;.jsonl</c>. A path without that shape keys
        /// on itself, so it can never be merged with another agent's file.
        /// </summary>
        internal static string AgentKey(string transcriptPath)
        {
            string fullPath = Normalize(transcriptPath);
            try
            {
                string subagentsDir = Path.GetDirectoryName(fullPath);
                string sessionDir = subagentsDir == null ? null : Path.GetDirectoryName(subagentsDir);
                string fileName = Path.GetFileNameWithoutExtension(fullPath);
                if (sessionDir != null
                    && string.Equals(Path.GetFileName(subagentsDir), "subagents", StringComparison.OrdinalIgnoreCase)
                    && fileName.StartsWith("agent-", StringComparison.OrdinalIgnoreCase))
                {
                    string sessionId = Path.GetFileName(sessionDir);
                    if (!string.IsNullOrEmpty(sessionId))
                        return sessionId + "/" + fileName;
                }
            }
            catch (ArgumentException)
            {
                // Falls through to the path itself.
            }

            return fullPath;
        }

        /// <summary>
        /// Registers <paramref name="candidate"/> for <paramref name="transcriptPath"/> unless its agent
        /// already has a tailer or has finished. Decided and recorded under one lock, so two watchers
        /// reporting the same agent at once produce one panel.
        /// </summary>
        internal ClaimResult Claim(string transcriptPath, TranscriptTailer candidate, DateTime nowUtc, out TranscriptTailer existing)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            return ClaimCore(transcriptPath, candidate, nowUtc, out existing);
        }

        /// <summary>
        /// <see cref="Claim"/> without a tailer to register: joins the path to the agent's tailer or
        /// reports the agent finished, and otherwise answers <see cref="ClaimResult.New"/> having
        /// recorded nothing. Asked before a new file is inspected, because a continuation file
        /// starts mid-conversation and must not be judged as a fresh agent.
        /// </summary>
        internal ClaimResult Classify(string transcriptPath, DateTime nowUtc, out TranscriptTailer existing) =>
            ClaimCore(transcriptPath, null, nowUtc, out existing);

        private ClaimResult ClaimCore(string transcriptPath, TranscriptTailer candidate, DateTime nowUtc, out TranscriptTailer existing)
        {
            string path = Normalize(transcriptPath);
            string agent = AgentKey(path);
            lock (_lock)
            {
                PruneFinished(nowUtc);

                if (_byAgent.TryGetValue(agent, out existing))
                {
                    _byPath[path] = existing;
                    return ClaimResult.Existing;
                }

                if (_finished.ContainsKey(agent))
                    return ClaimResult.Finished;

                if (candidate != null)
                {
                    _byAgent[agent] = candidate;
                    _byPath[path] = candidate;
                }

                return ClaimResult.New;
            }
        }

        /// <summary>
        /// The tailer for a path the SubagentStop hook named: by that exact path, else by its agent,
        /// because the hook may name the other copy of the agent's transcript.
        /// </summary>
        internal TranscriptTailer Find(string transcriptPath)
        {
            string path = Normalize(transcriptPath);
            lock (_lock)
            {
                if (_byPath.TryGetValue(path, out var tailer))
                    return tailer;
                return _byAgent.TryGetValue(AgentKey(path), out tailer) ? tailer : null;
            }
        }

        /// <summary>
        /// Forgets a tailer under every path and agent key it holds. When <paramref name="finished"/>,
        /// its agents are remembered so a later copy of their files opens no panel.
        /// </summary>
        internal void Remove(TranscriptTailer tailer, bool finished, DateTime nowUtc)
        {
            if (tailer == null) return;
            lock (_lock)
            {
                foreach (var key in _byPath.Where(kv => ReferenceEquals(kv.Value, tailer)).Select(kv => kv.Key).ToList())
                    _byPath.Remove(key);

                foreach (var key in _byAgent.Where(kv => ReferenceEquals(kv.Value, tailer)).Select(kv => kv.Key).ToList())
                {
                    _byAgent.Remove(key);
                    if (finished)
                        _finished[key] = nowUtc;
                }
            }
        }

        /// <summary>Takes out the tailer registered for this exact path, if any (a team reclaiming the file).</summary>
        internal TranscriptTailer TakeByPath(string transcriptPath)
        {
            string path = Normalize(transcriptPath);
            lock (_lock)
            {
                if (!_byPath.TryGetValue(path, out var tailer))
                    return null;
                RemoveUnlocked(tailer);
                return tailer;
            }
        }

        /// <summary>
        /// Takes out every tailer holding a path that matches (a root or a team being torn down, or the
        /// watcher stopping). The predicate gets the normalized path and the tailer registered for it.
        /// </summary>
        internal List<TranscriptTailer> TakeWhere(Func<string, TranscriptTailer, bool> matches)
        {
            lock (_lock)
            {
                var tailers = _byPath.Where(kv => matches(kv.Key, kv.Value)).Select(kv => kv.Value).Distinct().ToList();
                foreach (var tailer in tailers)
                    RemoveUnlocked(tailer);
                return tailers;
            }
        }

        private void RemoveUnlocked(TranscriptTailer tailer)
        {
            foreach (var key in _byPath.Where(kv => ReferenceEquals(kv.Value, tailer)).Select(kv => kv.Key).ToList())
                _byPath.Remove(key);
            foreach (var key in _byAgent.Where(kv => ReferenceEquals(kv.Value, tailer)).Select(kv => kv.Key).ToList())
                _byAgent.Remove(key);
        }

        private void PruneFinished(DateTime nowUtc)
        {
            foreach (var key in _finished.Where(kv => nowUtc - kv.Value > FinishedMemory).Select(kv => kv.Key).ToList())
                _finished.Remove(key);
        }

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (ArgumentException) { return path; }
            catch (NotSupportedException) { return path; }
            catch (PathTooLongException) { return path; }
        }
    }
}
