using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Detects and cleans stale Codex companion broker state. The Codex companion's
    /// <c>getCodexAuthStatus</c> calls <c>CodexAppServerClient.connect</c> with
    /// <c>reuseExistingBroker: true</c>, which reads the cached <c>broker.json</c>
    /// endpoint without verifying the named pipe is alive
    /// (<c>lib/app-server.mjs:336-338</c>). If the broker process died (machine
    /// reboot, MT host restart, broker crash) the cached endpoint is stale and every
    /// subsequent auth probe reports <c>loggedIn: false</c> — even though
    /// <c>auth.json</c> is valid.
    ///
    /// We can't patch the third-party plugin (it's reinstalled on update). This
    /// service runs MT-side: it computes the same state-dir path the companion uses,
    /// probes the pipe, and deletes a stale <c>broker.json</c> + the orphan
    /// <c>cxc-*</c> session dir whenever the pipe is unreachable.
    ///
    /// Mirrors the resolver logic in <c>lib/state.mjs:29-44</c>. If Node's logic
    /// changes upstream, the parity check the dev verification step exposes will
    /// surface the drift.
    /// </summary>
    public static class CodexBrokerHealthService
    {
        // Constants mirroring the Codex companion. Sourced from:
        //   lib/state.mjs:9-11    — PLUGIN_DATA_ENV, FALLBACK_STATE_ROOT_DIR
        //   lib/broker-lifecycle.mjs:13 — BROKER_STATE_FILE
        //   lib/broker-lifecycle.mjs:15 — createBrokerSessionDir prefix
        private const string PluginDataEnvVar = "CLAUDE_PLUGIN_DATA";
        private const string FallbackStateRootName = "codex-companion";
        private const string BrokerStateFileName = "broker.json";
        private const string CompanionSessionDirPrefix = "cxc-";
        private const string PipeSchemePrefix = "pipe:";
        private const string WindowsPipePrefix = @"\\.\pipe\";
        private const string BrokerPidFileName = "broker.pid";
        private const string BrokerLogFileName = "broker.log";

        // Pipe probe budget: NamedPipeClientStream.Connect() blocks the calling thread up to
        // this many ms. 200ms is generous for a local pipe — a healthy broker responds in <5ms.
        private const int PipeProbeTimeoutMs = 200;

        // Don't delete broker.json files that were just written; another launch may have
        // spawned a fresh broker whose endpoint hasn't bound to the pipe yet. 5s covers the
        // ensureBrokerSession spawn-and-bind window (broker-lifecycle.mjs:113-170).
        private static readonly TimeSpan FreshFileGuard = TimeSpan.FromSeconds(5);

        // Absolute minimum age floor for orphan-prune even in force mode. The companion
        // creates the session dir BEFORE spawning the broker process that writes broker.pid
        // (broker-lifecycle.mjs:131-149), so a freshly-created dir with no pid file may be
        // a broker mid-startup. 30s is comfortably longer than the spawn window.
        private static readonly TimeSpan OrphanPruneMinAge = TimeSpan.FromSeconds(30);

        /// <summary>Result bag returned by cleanup methods so callers can surface what changed.</summary>
        public sealed class CleanupResult
        {
            public List<string> CleanedFiles { get; } = new List<string>();
            public List<string> CleanedDirs { get; } = new List<string>();
            public List<string> Errors { get; } = new List<string>();

            public bool DidAnything => CleanedFiles.Count > 0 || CleanedDirs.Count > 0;
        }

        /// <summary>
        /// DTO matching the companion's <c>broker.json</c> shape
        /// (<c>broker-lifecycle.mjs:162-168</c>). All fields are nullable; we never
        /// write this file ourselves so deserialization tolerates missing keys.
        /// </summary>
        private sealed class BrokerSessionRecord
        {
            public string Endpoint { get; set; }
            public string PidFile { get; set; }
            public string LogFile { get; set; }
            public string SessionDir { get; set; }
            public int? Pid { get; set; }
        }

        /// <summary>
        /// Walks up from <paramref name="startDir"/> until a directory containing
        /// <c>.git</c> (folder or file — the latter for git worktrees) is found.
        /// Returns that directory if any; otherwise returns <paramref name="startDir"/>.
        ///
        /// Mirrors the companion's <c>resolveWorkspaceRoot</c>
        /// (<c>lib/workspace.mjs:3-9</c>) which delegates to
        /// <c>ensureGitRepository</c>. The Codex companion uses this to compute the
        /// state dir, so MT must use the same logic to preflight the same files.
        /// Callers that already know they have a git root can skip this and call
        /// <see cref="ComputeSlugAndHash"/> directly.
        /// </summary>
        public static string ResolveWorkspaceRoot(string startDir)
        {
            if (string.IsNullOrWhiteSpace(startDir)) return startDir;

            try
            {
                var dir = new DirectoryInfo(startDir);
                while (dir != null)
                {
                    string gitPath = Path.Combine(dir.FullName, ".git");
                    if (Directory.Exists(gitPath) || File.Exists(gitPath))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch
            {
                // Permission errors or invalid paths: fall through to the original.
            }

            return startDir;
        }

        /// <summary>
        /// Returns the candidate state directories for the given workspace root, in
        /// priority order. Both can validly contain a <c>broker.json</c> from
        /// different launches (one written when <c>CLAUDE_PLUGIN_DATA</c> was set,
        /// another when it was not), so callers should probe both.
        /// </summary>
        public static IEnumerable<string> ResolveStateDirCandidates(string workspaceRoot)
        {
            string slugHash = ComputeSlugAndHash(workspaceRoot);
            foreach (string root in ResolveStateRoots())
                yield return Path.Combine(root, slugHash);
        }

        /// <summary>
        /// Computes the <c>slug-hash16</c> directory segment that the companion
        /// appends to its state root. Mirrors <c>lib/state.mjs:38-40</c>. Public so
        /// the dev-time parity check can call it directly.
        ///
        /// Parity caveat: Node's <c>fs.realpathSync.native</c> resolves symlinks AND
        /// case-normalizes to the actual on-disk casing
        /// (<c>GetFinalPathNameByHandleW</c>). .NET's <see cref="Path.GetFullPath"/>
        /// only normalizes separators — it does not case-fold to disk. For typical
        /// workspaces (no symlinks, path matches disk case) the hash is identical to
        /// Node's. For the case-mismatch edge case, the manual "Reset Codex broker"
        /// button in Settings is the user-visible escape hatch.
        /// </summary>
        public static string ComputeSlugAndHash(string workspaceRoot)
        {
            string canonical;
            try { canonical = Path.GetFullPath(workspaceRoot ?? string.Empty); }
            catch { canonical = workspaceRoot ?? string.Empty; }

            string basename = Path.GetFileName(workspaceRoot ?? string.Empty);
            if (string.IsNullOrEmpty(basename)) basename = "workspace";

            // lib/state.mjs:39 — collapse runs of disallowed chars into a single '-',
            // strip leading/trailing '-', fall back to "workspace" if nothing remains.
            string slug = Regex.Replace(basename, "[^a-zA-Z0-9._-]+", "-").Trim('-');
            if (string.IsNullOrEmpty(slug)) slug = "workspace";

            string hash16;
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                hash16 = Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
            }

            return slug + "-" + hash16;
        }

        /// <summary>
        /// Probes a Codex broker endpoint to see if a server is listening. Returns
        /// true only when a connection succeeds within
        /// <see cref="PipeProbeTimeoutMs"/>.
        ///
        /// Endpoint format: <c>pipe:\\.\pipe\&lt;name&gt;</c> on Windows. Any
        /// exception (TimeoutException, IOException, ArgumentException, etc.) means
        /// dead — we don't distinguish, the cleanup action is the same.
        /// </summary>
        public static bool IsPipeAlive(string endpoint, int timeoutMs = PipeProbeTimeoutMs)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return false;
            if (!endpoint.StartsWith(PipeSchemePrefix, StringComparison.Ordinal)) return false;

            string fullPipePath = endpoint.Substring(PipeSchemePrefix.Length);
            if (!fullPipePath.StartsWith(WindowsPipePrefix, StringComparison.Ordinal)) return false;
            string pipeName = fullPipePath.Substring(WindowsPipePrefix.Length);
            if (string.IsNullOrEmpty(pipeName)) return false;

            try
            {
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    client.Connect(timeoutMs);
                    return client.IsConnected;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Probes both candidate state dirs for the workspace, parses any
        /// <c>broker.json</c> found, and deletes <c>broker.json</c> + the referenced
        /// session dir whenever the named pipe is unreachable. Skips files written
        /// within the last ~5 seconds (a freshly-spawned broker may not have bound
        /// its listener yet).
        ///
        /// Accepts a launch CWD (project source path, etc.) and internally walks up
        /// to find the git root via <see cref="ResolveWorkspaceRoot"/> — that's the
        /// path Node uses to compute the state dir, so we have to match.
        ///
        /// Best-effort: per-file errors are captured in the result, never thrown.
        /// Safe to call from launch paths where blocking on errors would be worse
        /// than a redundant probe.
        /// </summary>
        public static CleanupResult EnsureFreshBrokerState(string launchCwd)
        {
            var result = new CleanupResult();
            if (string.IsNullOrWhiteSpace(launchCwd))
                return result;

            string workspaceRoot = ResolveWorkspaceRoot(launchCwd);

            foreach (string stateDir in ResolveStateDirCandidates(workspaceRoot))
                CleanStaleBrokerInDir(stateDir, result, bypassFreshGuard: false);

            if (result.DidAnything)
                Debug.WriteLine($"[CodexBrokerHealthService] Cleaned {result.CleanedFiles.Count} broker.json file(s) + {result.CleanedDirs.Count} session dir(s) for {workspaceRoot}");

            return result;
        }

        /// <summary>
        /// Sweeps EVERY workspace's <c>broker.json</c> in both candidate state roots.
        /// Used by the manual "Reset Codex broker" Settings button as a force-reset:
        /// the user doesn't have to know which workspaces have stale state. Healthy
        /// brokers (live pipe) are left alone — only stale ones are removed. The
        /// 5s FreshFileGuard is BYPASSED on the manual reset path (the user's intent
        /// is "I clicked this because Codex is broken right now" — skipping recently-
        /// written stale state would silently leave the very file the user wanted to
        /// reset). Live brokers still survive because the pipe-alive check returns
        /// before the delete.
        /// </summary>
        public static CleanupResult EnsureFreshBrokerStateForAllWorkspaces()
        {
            var result = new CleanupResult();

            foreach (string stateRoot in ResolveStateRoots())
            {
                if (!Directory.Exists(stateRoot)) continue;

                string[] workspaceDirs;
                try
                {
                    // State subdir naming is "<slug>-<hash16>" — the dash separator gives
                    // a wildcard pattern that filters out unrelated stuff in the root.
                    workspaceDirs = Directory.GetDirectories(stateRoot, "*-*");
                }
                catch (Exception ex)
                {
                    result.Errors.Add("enumerate " + stateRoot + ": " + ex.Message);
                    Debug.WriteLine($"[CodexBrokerHealthService] EnsureFreshBrokerStateForAllWorkspaces enumerate failed: {ex.Message}");
                    continue;
                }

                foreach (string workspaceStateDir in workspaceDirs)
                    CleanStaleBrokerInDir(workspaceStateDir, result, bypassFreshGuard: true);
            }

            if (result.DidAnything)
                Debug.WriteLine($"[CodexBrokerHealthService] Reset cleaned {result.CleanedFiles.Count} broker.json file(s) + {result.CleanedDirs.Count} session dir(s) across all workspaces");

            return result;
        }

        /// <summary>
        /// Returns the candidate state roots (primary first), deduplicated by canonical
        /// path. When <c>CLAUDE_PLUGIN_DATA</c> is set to a path inside <c>%TEMP%</c>,
        /// the primary and fallback roots can resolve to the same physical directory —
        /// dedup avoids double-iteration and double-counted errors in the Reset
        /// MessageBox.
        /// </summary>
        private static IEnumerable<string> ResolveStateRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string pluginData = Environment.GetEnvironmentVariable(PluginDataEnvVar);
            if (!string.IsNullOrEmpty(pluginData))
            {
                string root = TryCanonicalize(Path.Combine(pluginData, "state"));
                if (seen.Add(root)) yield return root;
            }

            string fallback = TryCanonicalize(Path.Combine(Path.GetTempPath().TrimEnd('\\', '/'), FallbackStateRootName));
            if (seen.Add(fallback)) yield return fallback;
        }

        private static string TryCanonicalize(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        /// <summary>
        /// Per-state-dir cleanup: probes the broker.json in <paramref name="stateDir"/>
        /// and deletes it (plus the referenced session dir) if the named pipe is
        /// unreachable. No-op when the file doesn't exist or when the pipe is alive.
        /// Used by both single-workspace and all-workspace entry points.
        ///
        /// <paramref name="bypassFreshGuard"/> = true skips the 5-second
        /// <see cref="FreshFileGuard"/>. Manual reset (Settings button) sets this so a
        /// user clicking Reset right after a failed launch actually clears the just-
        /// written stale file, instead of getting a misleading "clean" message. The
        /// auto-launch path leaves it false to avoid racing a concurrent launch.
        ///
        /// <c>SessionDir</c> from broker.json is validated against
        /// <see cref="IsSafeBrokerSessionDir"/> before recursive deletion — the third-
        /// party companion's broker.json is local-trusted but a corrupted/hostile file
        /// could otherwise drive arbitrary recursive deletion.
        /// </summary>
        private static void CleanStaleBrokerInDir(string stateDir, CleanupResult result, bool bypassFreshGuard)
        {
            string brokerFile = Path.Combine(stateDir, BrokerStateFileName);
            if (!File.Exists(brokerFile)) return;

            try
            {
                var info = new FileInfo(brokerFile);
                bool isFresh = DateTime.UtcNow - info.LastWriteTimeUtc < FreshFileGuard;
                if (isFresh && !bypassFreshGuard)
                {
                    Debug.WriteLine($"[CodexBrokerHealthService] Skipping fresh broker.json at {brokerFile} (age < {FreshFileGuard.TotalSeconds}s)");
                    return;
                }

                BrokerSessionRecord record = null;
                try
                {
                    record = JsonSerializer.Deserialize<BrokerSessionRecord>(File.ReadAllText(brokerFile), JsonOptions);
                }
                catch (JsonException)
                {
                    // Malformed broker.json is itself stale — fall through to delete.
                }

                // Force-reset path safety net: even when bypassFreshGuard=true, if the file
                // is fresh AND the recorded PID is alive, the broker is starting up (the
                // pipe-down reading is transient — broker-lifecycle.mjs writes broker.json
                // AFTER the pipe is bound, but the pipe can drop briefly under contention).
                // Don't tear down a healthy in-flight launch in another window.
                if (isFresh && bypassFreshGuard)
                {
                    int? livenessPid = record?.Pid;
                    if (livenessPid.HasValue && livenessPid.Value > 0 && IsProcessAlive(livenessPid.Value))
                    {
                        Debug.WriteLine($"[CodexBrokerHealthService] Force-reset: skipping fresh broker.json with live PID {livenessPid.Value} at {brokerFile}");
                        return;
                    }
                }

                string endpoint = record?.Endpoint;
                if (!string.IsNullOrEmpty(endpoint) && IsPipeAlive(endpoint))
                    return; // healthy

                TryDeleteFile(brokerFile, result);

                string sessionDir = record?.SessionDir;
                if (!string.IsNullOrEmpty(sessionDir))
                {
                    if (IsSafeBrokerSessionDir(sessionDir))
                    {
                        TryDeleteDirectory(sessionDir, result);
                    }
                    else
                    {
                        result.Errors.Add("refused to delete out-of-bounds SessionDir: " + sessionDir);
                        Debug.WriteLine($"[CodexBrokerHealthService] Refused to delete SessionDir outside %TEMP%\\{CompanionSessionDirPrefix}*: {sessionDir}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(brokerFile + ": " + ex.Message);
                Debug.WriteLine($"[CodexBrokerHealthService] CleanStaleBrokerInDir error on {brokerFile}: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates a directory path before recursive deletion: must canonicalize
        /// successfully, must be a direct child of <c>%TEMP%</c>, and the basename
        /// must start with <see cref="CompanionSessionDirPrefix"/>. The Codex companion
        /// always writes session dirs as <c>%TEMP%\cxc-XXXXXX</c>
        /// (<c>broker-lifecycle.mjs:15-17</c>), so anything outside that shape is not
        /// a real broker session — and is at high risk of being a corrupted /
        /// tampered-with broker.json field. Defense in depth against turning the
        /// preflight into an arbitrary-recursive-delete primitive.
        /// </summary>
        private static bool IsSafeBrokerSessionDir(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string canonical;
            try { canonical = Path.GetFullPath(path); }
            catch { return false; }

            string parent = Path.GetDirectoryName(canonical);
            if (string.IsNullOrEmpty(parent)) return false;

            string tempRoot;
            try { tempRoot = Path.GetFullPath(Path.GetTempPath().TrimEnd('\\', '/')); }
            catch { return false; }

            if (!string.Equals(parent.TrimEnd('\\', '/'), tempRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            string basename = Path.GetFileName(canonical);
            return !string.IsNullOrEmpty(basename)
                && basename.StartsWith(CompanionSessionDirPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sweeps <c>%TEMP%\cxc-*</c> session dirs left behind by crashed brokers.
        /// A dir is pruned when ALL of:
        ///   - dir age >= max(<paramref name="maxAgeHours"/>, <see cref="OrphanPruneMinAge"/>)
        ///     — the absolute floor (~30s) protects a starting broker whose session
        ///     dir was created BEFORE its <c>broker.pid</c> was written
        ///     (<c>broker-lifecycle.mjs:131-149</c>).
        ///   - dir contains <c>broker.pid</c> OR <c>broker.log</c> — proves the dir
        ///     was actually created by the Codex companion. A planted <c>cxc-*</c>
        ///     dir without these markers is left alone, so a local attacker can't
        ///     redirect the prune into chosen-target cleanup.
        ///   - <c>broker.pid</c> is missing OR the PID inside is not a live process.
        ///     Live brokers (PID-alive) are NEVER pruned, even in force mode.
        ///
        /// Pass <c>maxAgeHours = 0</c> for force-prune (used by the manual "Reset
        /// Codex broker" button). The min-age floor + marker-file requirement still
        /// apply.
        /// </summary>
        // KNOWN LIMITATION (security MEDIUM, deferred): the marker check
        // (broker.pid OR broker.log) is forgeable — a local process can plant
        // %TEMP%\cxc-victim\broker.log and a future Reset/force-prune will treat
        // it as a stale companion dir. Threat model: the local attacker already
        // has %TEMP% write access and could just delete files directly, so the
        // escalation value is low. Proper fix would require a non-forgeable
        // ownership manifest of MT-created session dirs; deferred to a follow-up.
        public static CleanupResult PruneOrphanSessionDirs(double maxAgeHours = 24)
        {
            var result = new CleanupResult();
            string tempRoot = TryCanonicalize(Path.GetTempPath().TrimEnd('\\', '/'));
            if (!Directory.Exists(tempRoot)) return result;

            string[] candidates;
            try
            {
                candidates = Directory.GetDirectories(tempRoot, CompanionSessionDirPrefix + "*");
            }
            catch (Exception ex)
            {
                result.Errors.Add("enumerate " + tempRoot + ": " + ex.Message);
                Debug.WriteLine($"[CodexBrokerHealthService] PruneOrphanSessionDirs enumerate failed: {ex.Message}");
                return result;
            }

            // Effective min-age floor: never go below OrphanPruneMinAge even when called with
            // maxAgeHours=0 (force mode). Protects a starting broker whose dir was just created.
            TimeSpan requestedAge = TimeSpan.FromHours(maxAgeHours);
            TimeSpan effectiveMinAge = requestedAge > OrphanPruneMinAge ? requestedAge : OrphanPruneMinAge;
            DateTime ageThreshold = DateTime.UtcNow - effectiveMinAge;

            foreach (string dir in candidates)
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if (info.LastWriteTimeUtc > ageThreshold) continue; // too fresh

                    string pidFile = Path.Combine(dir, BrokerPidFileName);
                    string logFile = Path.Combine(dir, BrokerLogFileName);
                    bool hasMarker = File.Exists(pidFile) || File.Exists(logFile);
                    if (!hasMarker) continue; // no companion marker — not our dir

                    int? pid = TryReadPid(pidFile);
                    if (pid.HasValue && IsProcessAlive(pid.Value))
                        continue; // PID file present and that process is still running — leave alone

                    TryDeleteDirectory(dir, result);
                }
                catch (Exception ex)
                {
                    result.Errors.Add(dir + ": " + ex.Message);
                    Debug.WriteLine($"[CodexBrokerHealthService] PruneOrphanSessionDirs error on {dir}: {ex.Message}");
                }
            }

            if (result.DidAnything)
                Debug.WriteLine($"[CodexBrokerHealthService] Pruned {result.CleanedDirs.Count} orphan {CompanionSessionDirPrefix}* dir(s)");

            return result;
        }

        // ----- private helpers -----

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static int? TryReadPid(string pidFile)
        {
            if (!File.Exists(pidFile)) return null;
            try
            {
                string text = File.ReadAllText(pidFile).Trim();
                return int.TryParse(text, out int pid) ? pid : (int?)null;
            }
            catch { return null; }
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch (ArgumentException)
            {
                // No such process — definitely dead.
                return false;
            }
            catch (InvalidOperationException)
            {
                // Documented HasExited race: process exited between GetProcessById and HasExited.
                // Treat as dead.
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Permission denied (e.g. SYSTEM-owned PID) — treat as "unknown, assume alive"
                // so we don't accidentally prune a dir whose broker is healthy but unreadable.
                return true;
            }
        }

        private static void TryDeleteFile(string path, CleanupResult result)
        {
            try
            {
                File.Delete(path);
                result.CleanedFiles.Add(path);
            }
            catch (Exception ex)
            {
                result.Errors.Add("delete " + path + ": " + ex.Message);
            }
        }

        private static void TryDeleteDirectory(string path, CleanupResult result)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                Directory.Delete(path, recursive: true);
                result.CleanedDirs.Add(path);
            }
            catch (Exception ex)
            {
                result.Errors.Add("delete dir " + path + ": " + ex.Message);
            }
        }
    }
}
