using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Service for indexing Claude Code sessions for semantic search.
    /// Spawns the mcp-session-history Node.js CLI script to generate vector embeddings.
    /// All operations are fire-and-forget, running in the background without blocking the UI.
    /// </summary>
    public class SessionIndexingService : IDisposable
    {
        private readonly string _indexCliPath;
        private readonly string _nodePath;
        private bool _isDisposed;
        private CancellationTokenSource _cts;
        private readonly object _lock = new object();
        private bool _isIndexing;

        /// <summary>
        /// Fired when indexing progress changes.
        /// </summary>
        public event EventHandler<IndexingProgressEventArgs> ProgressChanged;

        /// <summary>
        /// Fired when indexing completes.
        /// </summary>
        public event EventHandler<IndexingCompletedEventArgs> IndexingCompleted;

        /// <summary>
        /// Fired when an indexing error occurs.
        /// </summary>
        public event EventHandler<IndexingErrorEventArgs> IndexingError;

        /// <summary>
        /// Gets whether indexing is currently in progress.
        /// </summary>
        public bool IsIndexing
        {
            get { lock (_lock) { return _isIndexing; } }
            private set { lock (_lock) { _isIndexing = value; } }
        }

        /// <summary>
        /// Creates a new SessionIndexingService.
        /// </summary>
        public SessionIndexingService()
        {
            System.Diagnostics.Trace.WriteLine("[SessionIndexingService] Constructor started");

            // Path to the mcp-session-history index-cli.js
            System.Diagnostics.Trace.WriteLine("[SessionIndexingService] Finding index-cli.js...");
            _indexCliPath = FindIndexCliPath();
            System.Diagnostics.Trace.WriteLine($"[SessionIndexingService] index-cli.js path: {_indexCliPath ?? "NOT FOUND"}");

            // Try to find Node.js
            System.Diagnostics.Trace.WriteLine("[SessionIndexingService] Finding Node.js...");
            _nodePath = FindNodePath();
            System.Diagnostics.Trace.WriteLine($"[SessionIndexingService] Node.js path: {_nodePath ?? "NOT FOUND"}");

            _cts = new CancellationTokenSource();
            System.Diagnostics.Trace.WriteLine("[SessionIndexingService] Constructor completed");
        }

        /// <summary>
        /// Creates a new SessionIndexingService with a custom CLI script path.
        /// </summary>
        /// <param name="indexCliPath">Path to the index-cli.js file.</param>
        public SessionIndexingService(string indexCliPath)
        {
            _indexCliPath = indexCliPath;
            _nodePath = FindNodePath();
            _cts = new CancellationTokenSource();
        }

        #region Public Methods

        /// <summary>
        /// Index all sessions for all projects (called on startup).
        /// Runs in the background without blocking.
        /// </summary>
        /// <returns>Task that can be awaited for completion (optional).</returns>
        public Task IndexAllSessionsAsync()
        {
            return Task.Run(() => IndexAsync(null, IndexingType.AllSessions));
        }

        /// <summary>
        /// Index sessions for a specific project directory (called on terminal launch).
        /// Runs in the background without blocking.
        /// </summary>
        /// <param name="projectPath">The project path to index sessions for.</param>
        /// <returns>Task that can be awaited for completion (optional).</returns>
        public Task IndexProjectSessionsAsync(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                throw new ArgumentException("Project path is required", nameof(projectPath));

            return Task.Run(() => IndexAsync(projectPath, IndexingType.ProjectSessions));
        }

        /// <summary>
        /// Index inter-terminal chat messages.
        /// Runs in the background without blocking.
        /// </summary>
        /// <returns>Task that can be awaited for completion (optional).</returns>
        public Task IndexChatMessagesAsync()
        {
            return Task.Run(() => IndexAsync(null, IndexingType.ChatMessages));
        }

        /// <summary>
        /// Gets vector indexing statistics.
        /// </summary>
        /// <returns>Task with the stats result, or null on error.</returns>
        public async Task<IndexingStats> GetStatsAsync()
        {
            if (string.IsNullOrEmpty(_nodePath) || !File.Exists(_indexCliPath))
            {
                return null;
            }

            try
            {
                var result = await RunIndexCliAsync("stats", null, CancellationToken.None);
                if (result.Success)
                {
                    return new IndexingStats
                    {
                        IndexedMessages = result.Indexed,
                        TotalMessages = result.TotalUnindexed + result.Indexed,
                        UnindexedMessages = result.TotalUnindexed,
                        PercentComplete = result.TotalUnindexed + result.Indexed > 0
                            ? (int)(((float)result.Indexed / (result.TotalUnindexed + result.Indexed)) * 100)
                            : 0
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionIndexingService] GetStats error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Cancels any ongoing indexing operation.
        /// </summary>
        public void CancelIndexing()
        {
            lock (_lock)
            {
                if (_isIndexing && _cts != null && !_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                    Debug.WriteLine("[SessionIndexingService] Indexing cancelled by user");
                }
            }
        }

        #endregion

        #region Private Methods

        private enum IndexingType
        {
            AllSessions,
            ProjectSessions,
            ChatMessages
        }

        private async Task IndexAsync(string projectPath, IndexingType indexingType)
        {
            // Don't start if already indexing
            if (IsIndexing)
            {
                Debug.WriteLine("[SessionIndexingService] Indexing already in progress, skipping");
                return;
            }

            // Check prerequisites
            if (string.IsNullOrEmpty(_nodePath))
            {
                OnIndexingError("Node.js not found. Please install Node.js and ensure it's in the PATH.");
                return;
            }

            if (!File.Exists(_indexCliPath))
            {
                OnIndexingError($"Index CLI script not found at: {_indexCliPath}");
                return;
            }

            IsIndexing = true;

            // Reset cancellation token
            lock (_lock)
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
            }

            var token = _cts.Token;

            try
            {
                string typeName = indexingType == IndexingType.ChatMessages ? "chat messages" : "sessions";
                string command = indexingType == IndexingType.ChatMessages ? "chat" : "sessions";

                Debug.WriteLine($"[SessionIndexingService] Starting {typeName} indexing...");
                OnProgressChanged(0, 0, $"Starting {typeName} indexing...");

                // Run the indexing loop - CLI processes batches per call
                bool complete = false;
                int totalIndexed = 0;
                int iterations = 0;
                const int maxIterations = 200; // Safety limit

                while (!complete && iterations < maxIterations && !token.IsCancellationRequested)
                {
                    iterations++;

                    var result = await RunIndexCliAsync(command, projectPath, token);

                    if (!result.Success)
                    {
                        OnIndexingError(result.Error ?? "Unknown error during indexing");
                        return;
                    }

                    totalIndexed += result.Indexed;
                    complete = result.Complete;

                    if (result.TotalUnindexed > 0)
                    {
                        int processed = result.TotalUnindexed - result.RemainingUnindexed;
                        int progress = (int)(((float)processed / result.TotalUnindexed) * 100);
                        OnProgressChanged(progress, totalIndexed,
                            $"Indexed {processed} of {result.TotalUnindexed} messages...");
                    }
                    else if (result.Indexed > 0)
                    {
                        OnProgressChanged(50, totalIndexed, $"Indexed {totalIndexed} messages...");
                    }

                    // Brief pause between batches to avoid overwhelming the system
                    if (!complete)
                    {
                        await Task.Delay(200, token);
                    }
                }

                if (token.IsCancellationRequested)
                {
                    Debug.WriteLine($"[SessionIndexingService] {typeName} indexing cancelled");
                    OnIndexingCompleted(totalIndexed, true);
                }
                else
                {
                    Debug.WriteLine($"[SessionIndexingService] {typeName} indexing complete. Total indexed: {totalIndexed}");
                    OnProgressChanged(100, totalIndexed, "Indexing complete");
                    OnIndexingCompleted(totalIndexed, false);
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[SessionIndexingService] Indexing cancelled");
                OnIndexingCompleted(0, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionIndexingService] Indexing error: {ex.Message}");
                OnIndexingError(ex.Message);
            }
            finally
            {
                IsIndexing = false;
            }
        }

        private class IndexResult
        {
            public bool Success { get; set; }
            public int Indexed { get; set; }
            public int RemainingUnindexed { get; set; }
            public int TotalUnindexed { get; set; }
            public bool Complete { get; set; }
            public string Error { get; set; }
        }

        private async Task<IndexResult> RunIndexCliAsync(string command, string projectPath, CancellationToken token)
        {
            var argsBuilder = new StringBuilder();
            argsBuilder.Append($"\"{_indexCliPath}\" {command}");

            if (!string.IsNullOrEmpty(projectPath))
            {
                argsBuilder.Append($" --project-path \"{projectPath}\"");
            }

            argsBuilder.Append(" --max-messages 25");

            var psi = new ProcessStartInfo
            {
                FileName = _nodePath,
                Arguments = argsBuilder.ToString(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_indexCliPath)
            };

            // Set environment variables
            psi.EnvironmentVariables["NODE_ENV"] = "production";

            using (var process = new Process { StartInfo = psi })
            {
                var stdoutBuilder = new StringBuilder();
                var stderrBuilder = new StringBuilder();

                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                        stdoutBuilder.AppendLine(e.Data);
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                        stderrBuilder.AppendLine(e.Data);
                };

                try
                {
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Wait for process to complete with timeout
                    var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2), token);
                    var processTask = Task.Run(() => process.WaitForExit(), token);

                    var completedTask = await Task.WhenAny(processTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        try { process.Kill(); } catch { }
                        return new IndexResult { Success = false, Error = "Indexing process timed out" };
                    }

                    // Give a moment for output buffers to flush
                    await Task.Delay(50, token);

                    string stdout = stdoutBuilder.ToString().Trim();
                    string stderr = stderrBuilder.ToString().Trim();

                    if (process.ExitCode != 0)
                    {
                        string errorMsg = !string.IsNullOrEmpty(stderr) ? stderr : stdout;
                        return new IndexResult { Success = false, Error = $"Process exited with code {process.ExitCode}: {errorMsg}" };
                    }

                    // Parse the JSON output
                    return ParseIndexResult(stdout);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    throw;
                }
            }
        }

        private IndexResult ParseIndexResult(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new IndexResult { Success = false, Error = "Empty response from index CLI" };
            }

            try
            {
                var result = new IndexResult();

                // Parse success
                result.Success = json.Contains("\"success\":true") || json.Contains("\"success\": true");

                if (!result.Success)
                {
                    // Extract error message
                    result.Error = ExtractJsonString(json, "error") ?? "Unknown error";
                    return result;
                }

                // Parse numeric values
                result.Indexed = ExtractJsonInt(json, "indexed");
                result.RemainingUnindexed = ExtractJsonInt(json, "remainingUnindexed");
                result.TotalUnindexed = ExtractJsonInt(json, "totalUnindexed");
                result.Complete = json.Contains("\"complete\":true") || json.Contains("\"complete\": true");

                // For stats command, parse different fields
                if (json.Contains("\"indexedMessages\""))
                {
                    result.Indexed = ExtractJsonInt(json, "indexedMessages");
                    result.TotalUnindexed = ExtractJsonInt(json, "unindexedMessages");
                }

                return result;
            }
            catch (Exception ex)
            {
                return new IndexResult { Success = false, Error = $"Failed to parse response: {ex.Message}" };
            }
        }

        private string ExtractJsonString(string json, string key)
        {
            string searchKey = $"\"{key}\":";
            int keyPos = json.IndexOf(searchKey);
            if (keyPos == -1)
            {
                searchKey = $"\"{key}\": ";
                keyPos = json.IndexOf(searchKey);
            }

            if (keyPos == -1)
                return null;

            int valueStart = keyPos + searchKey.Length;

            // Skip whitespace
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                valueStart++;

            if (valueStart >= json.Length || json[valueStart] != '"')
                return null;

            valueStart++; // Skip opening quote

            var sb = new StringBuilder();
            while (valueStart < json.Length && json[valueStart] != '"')
            {
                if (json[valueStart] == '\\' && valueStart + 1 < json.Length)
                {
                    valueStart++;
                    switch (json[valueStart])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append(json[valueStart]); break;
                    }
                }
                else
                {
                    sb.Append(json[valueStart]);
                }
                valueStart++;
            }

            return sb.ToString();
        }

        private int ExtractJsonInt(string json, string key)
        {
            string searchKey = $"\"{key}\":";
            int keyPos = json.IndexOf(searchKey);
            if (keyPos == -1)
            {
                searchKey = $"\"{key}\": ";
                keyPos = json.IndexOf(searchKey);
            }

            if (keyPos == -1)
                return 0;

            int valueStart = keyPos + searchKey.Length;

            // Skip whitespace
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                valueStart++;

            // Parse the number
            var sb = new StringBuilder();
            while (valueStart < json.Length && (char.IsDigit(json[valueStart]) || json[valueStart] == '-'))
            {
                sb.Append(json[valueStart]);
                valueStart++;
            }

            if (sb.Length > 0 && int.TryParse(sb.ToString(), out int result))
                return result;

            return 0;
        }

        private string FindIndexCliPath()
        {
            // Try multiple locations to find the CLI script

            // 1. Check relative to the executable
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string[] searchPaths = new[]
            {
                Path.Combine(basePath, "mcp-session-history", "index-cli.js"),
                Path.Combine(basePath, "..", "mcp-session-history", "index-cli.js"),
                Path.Combine(basePath, "..", "..", "mcp-session-history", "index-cli.js"),
                Path.Combine(basePath, "..", "..", "..", "mcp-session-history", "index-cli.js"),
            };

            foreach (var path in searchPaths)
            {
                string fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    Debug.WriteLine($"[SessionIndexingService] Found index-cli.js at: {fullPath}");
                    return fullPath;
                }
            }

            // 2. Search upward from the executable looking for the MultiTerminal folder structure
            string searchDir = basePath;
            for (int i = 0; i < 5; i++)
            {
                string testPath = Path.Combine(searchDir, "mcp-session-history", "index-cli.js");
                if (File.Exists(testPath))
                {
                    Debug.WriteLine($"[SessionIndexingService] Found index-cli.js at: {testPath}");
                    return testPath;
                }

                string parent = Path.GetDirectoryName(searchDir);
                if (parent == null || parent == searchDir)
                    break;
                searchDir = parent;
            }

            Debug.WriteLine("[SessionIndexingService] index-cli.js not found");
            return null;
        }

        private string FindNodePath()
        {
            // Try to find node in PATH first using 'where' command
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "node",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process != null)
                    {
                        Debug.WriteLine("[SessionIndexingService] Running 'where node' command...");

                        // Read output first to avoid deadlock
                        string output = process.StandardOutput.ReadLine();
                        Debug.WriteLine($"[SessionIndexingService] 'where node' output: {output ?? "(null)"}");

                        // Wait for process to exit with timeout
                        if (!process.WaitForExit(3000))
                        {
                            Debug.WriteLine("[SessionIndexingService] 'where node' timed out, killing process");
                            try { process.Kill(); } catch { }
                        }

                        if (!string.IsNullOrEmpty(output) && File.Exists(output))
                        {
                            Debug.WriteLine($"[SessionIndexingService] Found Node.js at: {output}");
                            return output;
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors from 'where' command
            }

            // Check common Windows locations
            string[] possiblePaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm", "current", "node.exe"),
                @"C:\Program Files\nodejs\node.exe",
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    Debug.WriteLine($"[SessionIndexingService] Found Node.js at: {path}");
                    return path;
                }
            }

            // Return "node" and hope it's in PATH
            Debug.WriteLine("[SessionIndexingService] Node.js not found in common locations, trying PATH");
            return "node";
        }

        #endregion

        #region Event Helpers

        private void OnProgressChanged(int percentComplete, int messagesIndexed, string status)
        {
            ProgressChanged?.Invoke(this, new IndexingProgressEventArgs
            {
                PercentComplete = percentComplete,
                MessagesIndexed = messagesIndexed,
                Status = status
            });
        }

        private void OnIndexingCompleted(int totalIndexed, bool wasCancelled)
        {
            IndexingCompleted?.Invoke(this, new IndexingCompletedEventArgs
            {
                TotalIndexed = totalIndexed,
                WasCancelled = wasCancelled
            });
        }

        private void OnIndexingError(string errorMessage)
        {
            Debug.WriteLine($"[SessionIndexingService] Error: {errorMessage}");
            IndexingError?.Invoke(this, new IndexingErrorEventArgs
            {
                ErrorMessage = errorMessage
            });
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                CancelIndexing();
                _cts?.Dispose();
                _cts = null;
            }
            _isDisposed = true;
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Statistics about the vector index.
    /// </summary>
    public class IndexingStats
    {
        /// <summary>
        /// Number of messages with vector embeddings.
        /// </summary>
        public int IndexedMessages { get; set; }

        /// <summary>
        /// Total number of messages in the database.
        /// </summary>
        public int TotalMessages { get; set; }

        /// <summary>
        /// Number of messages without vector embeddings.
        /// </summary>
        public int UnindexedMessages { get; set; }

        /// <summary>
        /// Percentage of messages that have been indexed (0-100).
        /// </summary>
        public int PercentComplete { get; set; }
    }

    /// <summary>
    /// Event args for indexing progress updates.
    /// </summary>
    public class IndexingProgressEventArgs : EventArgs
    {
        /// <summary>
        /// Percentage of indexing complete (0-100).
        /// </summary>
        public int PercentComplete { get; set; }

        /// <summary>
        /// Total number of messages indexed so far.
        /// </summary>
        public int MessagesIndexed { get; set; }

        /// <summary>
        /// Human-readable status message.
        /// </summary>
        public string Status { get; set; }
    }

    /// <summary>
    /// Event args for indexing completion.
    /// </summary>
    public class IndexingCompletedEventArgs : EventArgs
    {
        /// <summary>
        /// Total number of messages indexed.
        /// </summary>
        public int TotalIndexed { get; set; }

        /// <summary>
        /// Whether the indexing was cancelled.
        /// </summary>
        public bool WasCancelled { get; set; }
    }

    /// <summary>
    /// Event args for indexing errors.
    /// </summary>
    public class IndexingErrorEventArgs : EventArgs
    {
        /// <summary>
        /// The error message.
        /// </summary>
        public string ErrorMessage { get; set; }
    }

    #endregion
}
