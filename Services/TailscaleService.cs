using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Typed result of a Tailscale status probe (task 642c14e3, item 3). Every failure mode —
    /// not-installed, logged-out, service-down, non-zero exit, empty/malformed JSON — is reported
    /// here, never as a thrown exception, so the REST endpoint and the Settings-tab "Detect" button
    /// can render a clear readout without try/catch.
    /// </summary>
    public sealed class TailscaleStatus
    {
        /// <summary>True when tailscale.exe was located (PATH or the default install dir).</summary>
        public bool Installed { get; set; }

        /// <summary>True only when the backend reported "Running" (logged in + connected).</summary>
        public bool Running { get; set; }

        /// <summary>Raw BackendState from the daemon ("Running", "Stopped", "NeedsLogin", …) when known.</summary>
        public string BackendState { get; set; }

        /// <summary>This node's MagicDNS name (Self.DNSName), trailing dot trimmed, e.g. "desktop.tailXXXX.ts.net".</summary>
        public string Hostname { get; set; }

        /// <summary>Human-readable diagnostic for the non-running cases (null when Running).</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// Locates and shells out to tailscale.exe to read connectivity status. Stateless; safe to call
    /// from REST handlers. Never throws — all paths return a populated <see cref="TailscaleStatus"/>.
    /// </summary>
    public static class TailscaleService
    {
        private const string DefaultInstallPath = @"C:\Program Files\Tailscale\tailscale.exe";
        private const int ProcessTimeoutMs = 8000;

        /// <summary>
        /// Probes Tailscale: locate the exe, run <c>tailscale status --json</c>, parse Self.DNSName +
        /// BackendState. Returns a typed status for every outcome. Synchronous work is wrapped on a
        /// thread-pool thread so callers can await without blocking the request thread.
        /// </summary>
        public static Task<TailscaleStatus> GetStatusAsync() => Task.Run(GetStatus);

        /// <summary>
        /// Synchronous core. See <see cref="GetStatusAsync"/>. Never throws.
        /// </summary>
        public static TailscaleStatus GetStatus()
        {
            string exe = LocateExe();
            if (exe == null)
            {
                return new TailscaleStatus
                {
                    Installed = false,
                    Running = false,
                    Error = "Tailscale is not installed (tailscale.exe not found on PATH or in C:\\Program Files\\Tailscale).",
                };
            }

            string stdout;
            string stderr;
            int exitCode;
            try
            {
                if (!RunStatus(exe, out stdout, out stderr, out exitCode))
                {
                    return new TailscaleStatus
                    {
                        Installed = true,
                        Running = false,
                        Error = "Tailscale status command timed out (the Tailscale service may be down).",
                    };
                }
            }
            catch (Exception ex)
            {
                // Process couldn't be launched (e.g. service/driver missing) — installed but unusable.
                return new TailscaleStatus
                {
                    Installed = true,
                    Running = false,
                    Error = "Could not run tailscale.exe: " + ex.Message,
                };
            }

            // tailscale exits non-zero when stopped/logged-out; it still often prints JSON. Try to
            // parse first, but fall back to the exit/stderr diagnostic when there's no usable JSON.
            if (string.IsNullOrWhiteSpace(stdout))
            {
                string detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : $"exit code {exitCode}";
                return new TailscaleStatus
                {
                    Installed = true,
                    Running = false,
                    Error = "Tailscale returned no status output (" + detail + "). The service may be stopped or you may be logged out.",
                };
            }

            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;

                string backendState = null;
                if (root.TryGetProperty("BackendState", out var bs) && bs.ValueKind == JsonValueKind.String)
                    backendState = bs.GetString();

                string hostname = null;
                if (root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object &&
                    self.TryGetProperty("DNSName", out var dns) && dns.ValueKind == JsonValueKind.String)
                {
                    hostname = dns.GetString()?.TrimEnd('.');
                    if (string.IsNullOrWhiteSpace(hostname)) hostname = null;
                }

                bool running = string.Equals(backendState, "Running", StringComparison.Ordinal);
                return new TailscaleStatus
                {
                    Installed = true,
                    Running = running,
                    BackendState = backendState,
                    Hostname = hostname,
                    Error = running
                        ? null
                        : "Tailscale is not running (BackendState=" + (backendState ?? "unknown") + "). Run 'tailscale up' and sign in.",
                };
            }
            catch (JsonException)
            {
                return new TailscaleStatus
                {
                    Installed = true,
                    Running = false,
                    Error = "Tailscale returned malformed status JSON.",
                };
            }
        }

        /// <summary>
        /// Finds tailscale.exe: the default install dir first, then each PATH entry. Returns null
        /// when not found.
        /// </summary>
        private static string LocateExe()
        {
            try
            {
                if (File.Exists(DefaultInstallPath))
                    return DefaultInstallPath;
            }
            catch { /* ignore probe errors, fall through to PATH */ }

            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar))
                return null;

            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "tailscale.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { /* malformed PATH entry — skip */ }
            }
            return null;
        }

        /// <summary>
        /// Runs <c>tailscale status --json</c> capturing stdout/stderr. Returns false on timeout
        /// (process killed); true when the process exited within the budget.
        /// </summary>
        private static bool RunStatus(string exe, out string stdout, out string stderr, out int exitCode)
        {
            stdout = string.Empty;
            stderr = string.Empty;
            exitCode = -1;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "status --json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = psi };
            var outBuilder = new StringBuilder();
            var errBuilder = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) outBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) errBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(ProcessTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            // Ensure async readers have flushed.
            process.WaitForExit();
            stdout = outBuilder.ToString();
            stderr = errBuilder.ToString();
            exitCode = process.ExitCode;
            return true;
        }
    }
}
