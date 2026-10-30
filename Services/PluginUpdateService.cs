using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>What a plugin check found (task 4dc459e4).</summary>
    internal enum PluginCheckOutcome
    {
        /// <summary>The stamp's commit is GitHub main's newest commit.</summary>
        UpToDate,

        /// <summary>The stamp names an older commit than GitHub main's newest.</summary>
        UpdateAvailable,

        /// <summary>No usable stamp (a developer clone, or an install before 3.0.1). GitHub's newest is known.</summary>
        UnknownOrigin,

        /// <summary>No plugin folder, or GitHub could not be asked. <see cref="PluginCheckResult.Reason"/> says why.</summary>
        Failed,
    }

    /// <summary>
    /// The marketplace root's <c>.mt-plugin-source.json</c>: which GitHub commit the folder was made from.
    /// Written by the installer (task 4dc459e4 part A) and by <see cref="PluginUpdateService"/>.
    /// </summary>
    internal sealed class PluginSourceStamp
    {
        public string Repo { get; init; }
        public string Ref { get; init; }
        public string Commit { get; init; }
        public DateTimeOffset? StampedAt { get; init; }
        public string StampedBy { get; init; }
    }

    /// <summary>GitHub main's newest commit, every field validated or sanitized.</summary>
    internal sealed class PluginLatestCommit
    {
        public string Sha { get; init; }
        public DateTimeOffset? Date { get; init; }
        public string Message { get; init; }
    }

    internal sealed class PluginCheckResult
    {
        public PluginCheckOutcome Outcome { get; init; }

        /// <summary>The marketplace root the check was made for; null when no plugin folder was found.</summary>
        public string MarketplaceRoot { get; init; }

        public PluginSourceStamp Installed { get; init; }
        public PluginLatestCommit Latest { get; init; }

        /// <summary>Why the check failed (<see cref="PluginCheckOutcome.Failed"/> only).</summary>
        public string Reason { get; init; }

        /// <summary>True when "Update now" may be offered: GitHub's newest is known and differs from (or cannot be compared with) the installed one.</summary>
        public bool CanUpdate =>
            (Outcome == PluginCheckOutcome.UpdateAvailable || Outcome == PluginCheckOutcome.UnknownOrigin)
            && Latest != null && MarketplaceRoot != null;
    }

    internal sealed class PluginUpdateResult
    {
        public bool Success { get; init; }
        public string Message { get; init; }

        public static PluginUpdateResult Ok(string message) => new PluginUpdateResult { Success = true, Message = message };
        public static PluginUpdateResult Fail(string message) => new PluginUpdateResult { Success = false, Message = message };
    }

    /// <summary>
    /// The disk operations of the swap, so a test can inject a fault at any step. Production uses
    /// <see cref="PluginUpdateService.RealSwapFileSystem"/>.
    /// </summary>
    internal interface IPluginSwapFileSystem
    {
        bool DirectoryExists(string path);
        void DeleteDirectory(string path);
        void MoveDirectory(string from, string to);
    }

    /// <summary>
    /// Keeps MultiTerminal's Claude Code plugin current from the marketplace's GitHub main (task 4dc459e4).
    /// Installed machines register the marketplace as a local directory, so <c>claude plugin marketplace
    /// update</c> never fetches anything; this is the only updater they have.
    ///
    /// <para><b>Which folder:</b> the one MT hands to every terminal with <c>--plugin-dir</c>
    /// (<see cref="LaunchCommandBuilder.GetMtPluginPath"/>); the marketplace root is two levels above it.</para>
    ///
    /// <para><b>Trust:</b> the repository, the hosts and the ref are constants. The stamp on disk only says
    /// which commit is installed; nothing in it chooses what is downloaded. The update fetches exactly the
    /// commit the check reported, so what was shown is what is installed.</para>
    ///
    /// <para><b>Swap:</b> the zipball is downloaded under <c>%LOCALAPPDATA%\MultiTerminal\plugin-update</c>
    /// and extracted BESIDE the live root (<c>&lt;root&gt;.incoming</c>), so the swap is two renames on one
    /// volume (the shared location may be on another drive than %LOCALAPPDATA%). The live root becomes
    /// <c>&lt;root&gt;.previous</c> (one kept); any failure after that moves it back.</para>
    ///
    /// Running terminals keep the plugin they loaded; terminals started afterwards use the new one.
    /// </summary>
    internal sealed class PluginUpdateService : IDisposable
    {
        public const string Repo = "ClarionLive/multiterminal-marketplace";
        public const string Ref = "main";
        public const string StampFileName = ".mt-plugin-source.json";
        public const string StampedByUpdate = "mt-update";

        /// <summary>SettingsService key: when the startup check last ran (ISO-8601 UTC).</summary>
        public const string LastCheckSettingKey = "PluginUpdateLastCheckUtc";

        /// <summary>SettingsService key: the GitHub commit an Inbox notice was last posted for, so one commit is announced once.</summary>
        public const string NotifiedShaSettingKey = "PluginUpdateNotifiedSha";

        /// <summary>Off switch for the startup check (<c>0</c>/<c>false</c>/<c>off</c>/<c>no</c>/<c>disabled</c>). The Settings buttons still work.</summary>
        public const string EnvStartupCheck = "MULTITERMINAL_PLUGIN_UPDATE_CHECK";

        public const string InboxType = "plugin_update";
        public const string SenderName = "MultiTerminal";

        public const string UpdatedMessage =
            "Updated. Terminals you start from now on use it; running terminals keep the plugin they started with.";

        internal const string CommitsUrl = "https://api.github.com/repos/" + Repo + "/commits/" + Ref;
        internal const string ZipUrlPrefix = "https://codeload.github.com/" + Repo + "/zip/";

        /// <summary>The zipball is ~1.5 MB (2026-10-30); a download larger than this is refused.</summary>
        internal const long MaxDownloadBytes = 50L * 1024 * 1024;

        /// <summary>Extracted, it is ~3 MB in ~220 entries; an archive over either cap is refused.</summary>
        internal const long MaxExtractedBytes = 200L * 1024 * 1024;
        internal const int MaxEntries = 10000;

        private const int MaxCommitResponseBytes = 1024 * 1024;
        private const int MaxMessageLength = 120;
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

        private static readonly HashSet<string> DisableTokens =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0", "false", "off", "no", "disabled" };
        private static readonly HashSet<string> EnableTokens =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1", "true", "on", "yes", "enabled" };

        // One update at a time across every caller (two Settings dialogs, a dialog and anything later).
        private static readonly SemaphoreSlim UpdateGate = new SemaphoreSlim(1, 1);

        private readonly HttpClient _http;
        private readonly Func<string> _pluginDir;
        private readonly string _downloadFolder;

        /// <param name="handler">Null for the real network; tests pass a stub.</param>
        /// <param name="pluginDir">The plugin folder MT loads; null for <see cref="LaunchCommandBuilder.GetMtPluginPath"/>.</param>
        /// <param name="downloadFolder">Where the zipball is saved; null for <c>%LOCALAPPDATA%\MultiTerminal\plugin-update</c>.</param>
        public PluginUpdateService(HttpMessageHandler handler = null, Func<string> pluginDir = null, string downloadFolder = null)
        {
            // No redirects: both URLs answer directly, and a redirect must not take the download to another host.
            // CA2000: the HttpClient owns the handler it creates (disposeHandler: true).
#pragma warning disable CA2000
            _http = handler == null
                ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
                : new HttpClient(handler, disposeHandler: false);
#pragma warning restore CA2000
            _http.Timeout = Timeout.InfiniteTimeSpan; // each request has its own timeout token
            _pluginDir = pluginDir ?? LaunchCommandBuilder.GetMtPluginPath;
            _downloadFolder = downloadFolder ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiTerminal", "plugin-update");
        }

        public void Dispose() => _http.Dispose();

        // ------------------------------------------------------------------ pure decisions

        /// <summary>Pure: a full 40-hex commit id. Anything else (short, padded, non-hex) is not one.</summary>
        internal static bool IsValidCommit(string sha)
        {
            if (sha == null || sha.Length != 40) return false;
            foreach (char c in sha)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>
        /// Pure: <c>&lt;root&gt;</c> for a plugin folder <c>&lt;root&gt;\plugins\multiterminal</c>, or null
        /// when the folder is not shaped like that (the root is then not a marketplace MT knows how to update).
        /// </summary>
        internal static string MarketplaceRootFromPluginDir(string pluginDir)
        {
            if (string.IsNullOrWhiteSpace(pluginDir)) return null;
            try
            {
                string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginDir));
                if (!string.Equals(Path.GetFileName(dir), "multiterminal", StringComparison.OrdinalIgnoreCase)) return null;
                string plugins = Path.GetDirectoryName(dir);
                if (plugins == null || !string.Equals(Path.GetFileName(plugins), "plugins", StringComparison.OrdinalIgnoreCase)) return null;
                string root = Path.GetDirectoryName(plugins);
                // A drive root cannot be renamed, so it cannot be swapped.
                if (string.IsNullOrEmpty(root) || Path.GetPathRoot(root) == root) return null;
                return root;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        /// <summary>
        /// Pure: the stamp in <paramref name="json"/>, or null when it is not a usable one. The commit must be
        /// 40 hex; repo, ref and stampedBy are kept only as short sanitized text; a bad date is dropped
        /// rather than voiding the stamp. Untrusted: a stamp only ever says what is installed.
        /// </summary>
        internal static PluginSourceStamp ParseStamp(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                string commit = StringProperty(root, "commit");
                if (!IsValidCommit(commit)) return null;

                DateTimeOffset? stampedAt = null;
                if (DateTimeOffset.TryParse(StringProperty(root, "stampedAt"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var at))
                    stampedAt = at;

                return new PluginSourceStamp
                {
                    Repo = GitHubIssueFeed.Sanitize(StringProperty(root, "repo"), 100),
                    Ref = GitHubIssueFeed.Sanitize(StringProperty(root, "ref"), 100),
                    Commit = commit.ToLowerInvariant(),
                    StampedAt = stampedAt,
                    StampedBy = GitHubIssueFeed.Sanitize(StringProperty(root, "stampedBy"), 40),
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Pure: the stamp file's text for <paramref name="commit"/>.</summary>
        internal static string BuildStampJson(string commit, DateTimeOffset stampedAt, string stampedBy)
        {
            return JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["repo"] = Repo,
                ["ref"] = Ref,
                ["commit"] = commit.ToLowerInvariant(),
                ["stampedAt"] = stampedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["stampedBy"] = stampedBy,
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Pure: GitHub's <c>GET /repos/{repo}/commits/{ref}</c> answer, or null when its sha is not a full
        /// commit id. The message keeps its first line only, sanitized (control and format characters removed).
        /// </summary>
        internal static PluginLatestCommit ParseLatestCommit(byte[] body)
        {
            if (body == null) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                string sha = StringProperty(root, "sha");
                if (!IsValidCommit(sha)) return null;

                DateTimeOffset? date = null;
                string message = null;
                if (root.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object)
                {
                    if (commit.TryGetProperty("committer", out var committer) && committer.ValueKind == JsonValueKind.Object
                        && DateTimeOffset.TryParse(StringProperty(committer, "date"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var d))
                        date = d;
                    message = FirstLine(StringProperty(commit, "message"));
                }

                return new PluginLatestCommit
                {
                    Sha = sha.ToLowerInvariant(),
                    Date = date,
                    Message = GitHubIssueFeed.Sanitize(message, MaxMessageLength),
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Pure: the verdict. Failure wins; then a missing stamp, or one naming another repository or ref, is
        /// <see cref="PluginCheckOutcome.UnknownOrigin"/> (its commit cannot be compared with this repo's main);
        /// then equal commits are up to date and anything else is an update.
        /// </summary>
        internal static PluginCheckResult Decide(string marketplaceRoot, PluginSourceStamp installed, PluginLatestCommit latest, string failure)
        {
            if (marketplaceRoot == null)
                return new PluginCheckResult { Outcome = PluginCheckOutcome.Failed, Reason = failure ?? "The MultiTerminal plugin folder was not found." };
            if (failure != null || latest == null || !IsValidCommit(latest.Sha))
                return new PluginCheckResult { Outcome = PluginCheckOutcome.Failed, MarketplaceRoot = marketplaceRoot, Installed = installed, Reason = failure ?? "GitHub's answer had no commit." };

            bool comparable = installed != null
                && string.Equals(installed.Repo, Repo, StringComparison.OrdinalIgnoreCase)
                && string.Equals(installed.Ref, Ref, StringComparison.Ordinal);
            if (!comparable)
                return new PluginCheckResult { Outcome = PluginCheckOutcome.UnknownOrigin, MarketplaceRoot = marketplaceRoot, Installed = installed, Latest = latest };

            var outcome = string.Equals(installed.Commit, latest.Sha, StringComparison.OrdinalIgnoreCase)
                ? PluginCheckOutcome.UpToDate
                : PluginCheckOutcome.UpdateAvailable;
            return new PluginCheckResult { Outcome = outcome, MarketplaceRoot = marketplaceRoot, Installed = installed, Latest = latest };
        }

        /// <summary>
        /// Pure: why the folder must not be replaced, or null. A <c>.git</c> (folder, or a worktree's file) is
        /// probed with <paramref name="probeGit"/> and decided by <see cref="GitCheckoutRefuseReason"/>:
        /// 3.0.0's installer copied the build machine's clone, <c>.git</c> included, and Inno never deletes it,
        /// so a <c>.git</c> on its own is a leftover, not a developer's clone.
        /// </summary>
        internal static string RefuseReason(string marketplaceRoot, Func<string, bool> directoryExists, Func<string, bool> fileExists,
            Func<string, bool> isWritable, Func<string, GitProbe> probeGit)
        {
            if (string.IsNullOrWhiteSpace(marketplaceRoot) || !directoryExists(marketplaceRoot))
                return "The MultiTerminal plugin folder was not found.";
            string git = Path.Combine(marketplaceRoot, ".git");
            if (directoryExists(git) || fileExists(git))
            {
                var probe = probeGit(marketplaceRoot);
                string refuse = GitCheckoutRefuseReason(marketplaceRoot, probe.GitAvailable, probe.Dirty, probe.AheadCount, probe.FetchFailed);
                if (refuse != null) return refuse;
            }
            if (!isWritable(marketplaceRoot))
                return $"MultiTerminal cannot write to {marketplaceRoot} or the folder that holds it, so it cannot update the plugin there.";
            return null;
        }

        /// <summary>What <c>git</c> said about a marketplace root that has a <c>.git</c>.</summary>
        internal readonly record struct GitProbe(bool GitAvailable, bool Dirty, int AheadCount, bool FetchFailed);

        /// <summary>
        /// Pure: refuse only a git checkout with local work, uncommitted changes or commits GitHub main lacks.
        /// No git, a status git could not answer (reported as not dirty), or a failed fetch all read as a
        /// leftover <c>.git</c> and the update goes ahead: the swap moves the whole old root, <c>.git</c>
        /// included, to <c>&lt;root&gt;.previous</c>, so nothing is lost.
        /// </summary>
        internal static string GitCheckoutRefuseReason(string marketplaceRoot, bool gitAvailable, bool dirty, int aheadCount, bool fetchFailed)
        {
            if (!gitAvailable) return null;
            if (dirty) return $"{marketplaceRoot} is a git checkout with uncommitted changes. Update it with git instead.";
            if (fetchFailed) return null;
            if (aheadCount > 0) return $"{marketplaceRoot} is a git checkout with commits that are not on GitHub main. Update it with git instead.";
            return null;
        }

        /// <summary>
        /// Pure: an archive entry's path with the zipball's top folder (<c>multiterminal-marketplace-&lt;sha&gt;/</c>)
        /// removed, using <c>\</c>. Null for the top folder itself. Throws <see cref="InvalidDataException"/>
        /// when the entry is not under <paramref name="topFolder"/> (every entry of a GitHub zipball is).
        /// </summary>
        internal static string StripTopFolder(string entryFullName, string topFolder)
        {
            if (string.IsNullOrEmpty(entryFullName) || string.IsNullOrEmpty(topFolder))
                throw new InvalidDataException("The archive has an entry with no name.");
            string name = entryFullName.Replace('\\', '/');
            string prefix = topFolder + "/";
            if (string.Equals(name, prefix, StringComparison.Ordinal) || string.Equals(name, topFolder, StringComparison.Ordinal))
                return null;
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException($"The archive entry '{GitHubIssueFeed.Sanitize(entryFullName, 200)}' is outside its top folder.");
            string rest = name.Substring(prefix.Length);
            return rest.Length == 0 ? null : rest.Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Pure: the full path an entry extracts to. Throws <see cref="InvalidDataException"/> when it would
        /// land outside <paramref name="target"/> (<c>..</c>, a rooted or drive path): the zip-slip guard.
        /// </summary>
        internal static string SafeEntryPath(string target, string relativePath)
        {
            string targetFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)) + Path.DirectorySeparatorChar;
            string full;
            try
            {
                if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
                    throw new InvalidDataException($"The archive entry '{GitHubIssueFeed.Sanitize(relativePath, 200)}' is an absolute path.");
                full = Path.GetFullPath(Path.Combine(targetFull, relativePath));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new InvalidDataException($"The archive entry '{GitHubIssueFeed.Sanitize(relativePath, 200)}' is not a valid path.", ex);
            }
            if (!full.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The archive entry '{GitHubIssueFeed.Sanitize(relativePath, 200)}' points outside the folder it is extracted to.");
            return full;
        }

        /// <summary>Pure: whether the once-a-day startup check is due, from the stored last-check time.</summary>
        internal static bool IsDailyCheckDue(string storedLastCheck, DateTimeOffset now)
        {
            if (!DateTimeOffset.TryParse(storedLastCheck, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var last))
                return true;
            // A stored time in the future (clock moved back) would otherwise silence the check until it is reached.
            if (last > now + TimeSpan.FromHours(1)) return true;
            return now - last >= CheckInterval;
        }

        /// <summary>
        /// Pure: whether <see cref="EnvStartupCheck"/> allows the startup check. Unset is on; an unrecognized
        /// value is on and comes back in <paramref name="warning"/> so a typo is distinguishable from intent.
        /// </summary>
        internal static bool IsStartupCheckEnabled(string raw, out string warning)
        {
            warning = null;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            string v = raw.Trim();
            if (DisableTokens.Contains(v)) return false;
            if (EnableTokens.Contains(v)) return true;
            warning = $"{EnvStartupCheck}='{GitHubIssueFeed.Sanitize(v, 40)}' is not a recognized on/off value; treating as on.";
            return true;
        }

        /// <summary>
        /// Pure: whether a startup check posts an Inbox notice. Only for a real update (an unknown origin is a
        /// developer clone as often as not, and would be announced every day), and once per GitHub commit.
        /// </summary>
        internal static bool ShouldNotify(PluginCheckResult check, string notifiedSha) =>
            check != null && check.Outcome == PluginCheckOutcome.UpdateAvailable && check.Latest != null
            && !string.Equals(check.Latest.Sha, notifiedSha?.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Pure: the Inbox notice text for an available update.</summary>
        internal static string BuildNotice(PluginCheckResult check) =>
            "A newer MultiTerminal plugin is on GitHub (" + DescribeLatest(check.Latest) + "). Installed: " + DescribeInstalled(check.Installed) + ". Install it from Settings > General > MultiTerminal plugin > Update now.";

        /// <summary>
        /// The quiet startup check: at most once a day, never installs. Posts one Inbox notice per new GitHub
        /// commit through <paramref name="postNotice"/>. A failed check is not recorded, so the next start retries.
        /// </summary>
        public async Task RunStartupCheckAsync(SettingsService settings, Func<string, bool> postNotice, Action<string> log, CancellationToken cancellationToken = default)
        {
            if (!IsStartupCheckEnabled(Environment.GetEnvironmentVariable(EnvStartupCheck), out string warning))
            {
                log?.Invoke($"Startup plugin check disabled by {EnvStartupCheck}.");
                return;
            }
            if (warning != null) log?.Invoke(warning);
            if (!IsDailyCheckDue(settings.Get(LastCheckSettingKey), DateTimeOffset.UtcNow)) return;

            var check = await CheckAsync(cancellationToken).ConfigureAwait(false);
            log?.Invoke("Startup plugin check: " + DescribeCheck(check));
            if (check.Outcome == PluginCheckOutcome.Failed) return;
            settings.Set(LastCheckSettingKey, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            if (ShouldNotify(check, settings.Get(NotifiedShaSettingKey)) && postNotice(BuildNotice(check)))
                settings.Set(NotifiedShaSettingKey, check.Latest.Sha);
        }

        /// <summary>Pure: the Settings line, e.g. <c>4015135 (installed 2026-10-30)</c> or <c>unknown origin</c>.</summary>
        internal static string DescribeInstalled(PluginSourceStamp stamp)
        {
            if (stamp == null) return "unknown origin";
            string text = stamp.Commit.Substring(0, 7);
            if (stamp.StampedAt is DateTimeOffset at)
                text += " (installed " + at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")";
            return text;
        }

        /// <summary>Pure: one line describing a check, for the Settings dialog and the Inbox notice.</summary>
        internal static string DescribeCheck(PluginCheckResult check)
        {
            switch (check.Outcome)
            {
                case PluginCheckOutcome.UpToDate:
                    return "Up to date with GitHub main.";
                case PluginCheckOutcome.UpdateAvailable:
                    return "Update available: " + DescribeLatest(check.Latest) + ".";
                case PluginCheckOutcome.UnknownOrigin:
                    return "Installed version unknown. GitHub main is " + DescribeLatest(check.Latest) + ".";
                default:
                    return "Check failed: " + (check.Reason ?? "unknown reason") + ".";
            }
        }

        internal static string DescribeLatest(PluginLatestCommit latest)
        {
            if (latest == null) return "unknown";
            string text = latest.Sha.Substring(0, 7);
            if (latest.Date is DateTimeOffset d)
                text += " of " + d.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(latest.Message))
                text += ", \"" + latest.Message + "\"";
            return text;
        }

        // ------------------------------------------------------------------ IO

        /// <summary>The marketplace root MT's plugin lives in, or null.</summary>
        public string ResolveMarketplaceRoot()
        {
            try
            {
                return MarketplaceRootFromPluginDir(_pluginDir());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>The stamp in <paramref name="marketplaceRoot"/>, or null when absent, unreadable or invalid.</summary>
        public static PluginSourceStamp ReadStamp(string marketplaceRoot)
        {
            if (string.IsNullOrEmpty(marketplaceRoot)) return null;
            try
            {
                var file = new FileInfo(Path.Combine(marketplaceRoot, StampFileName));
                if (!file.Exists || file.Length > 64 * 1024) return null;
                return ParseStamp(File.ReadAllText(file.FullName));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Asks GitHub for main's newest commit and compares it with the installed stamp. Never throws.</summary>
        public async Task<PluginCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            string root = ResolveMarketplaceRoot();
            if (root == null) return Decide(null, null, null, null);
            var installed = ReadStamp(root);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CheckTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, CommitsUrl);
                request.Headers.UserAgent.ParseAdd("MultiTerminal");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
                    return Decide(root, installed, null, $"GitHub refused the request ({(int)response.StatusCode}); probably rate limited, try again later");
                if (!response.IsSuccessStatusCode)
                    return Decide(root, installed, null, $"GitHub returned {(int)response.StatusCode}");
                if (response.Content.Headers.ContentLength > MaxCommitResponseBytes)
                    return Decide(root, installed, null, "GitHub's answer was too large");

                byte[] body = await ReadCappedAsync(response.Content, MaxCommitResponseBytes, timeout.Token).ConfigureAwait(false);
                if (body == null) return Decide(root, installed, null, "GitHub's answer was too large");
                var latest = ParseLatestCommit(body);
                return Decide(root, installed, latest, latest == null ? "GitHub's answer had no valid commit" : null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Decide(root, installed, null, "GitHub did not answer in time");
            }
            catch (HttpRequestException ex)
            {
                return Decide(root, installed, null, "network error: " + (GitHubIssueFeed.Sanitize(ex.Message, 200) ?? "unknown"));
            }
        }

        /// <summary>
        /// Replaces the plugin with the commit <paramref name="check"/> reported. Never throws; the result says
        /// what happened, including where the old plugin is if a rollback itself failed.
        /// </summary>
        public async Task<PluginUpdateResult> UpdateAsync(PluginCheckResult check, CancellationToken cancellationToken = default)
        {
            if (check == null || !check.CanUpdate)
                return PluginUpdateResult.Fail("There is no update to install. Check for an update first.");
            string sha = check.Latest.Sha;
            if (!IsValidCommit(sha))
                return PluginUpdateResult.Fail("The update has no valid commit id.");

            string root = ResolveMarketplaceRoot();
            if (root == null || !string.Equals(root, check.MarketplaceRoot, StringComparison.OrdinalIgnoreCase))
                return PluginUpdateResult.Fail("The plugin folder changed since the check. Check for an update again.");

            if (!await UpdateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return PluginUpdateResult.Fail("An update is already running.");
            string zipPath = null;
            string incoming = root + ".incoming";
            try
            {
                string refuse = RefuseReason(root, Directory.Exists, File.Exists, IsWritable, ProbeGit);
                if (refuse != null) return PluginUpdateResult.Fail(refuse);

                Directory.CreateDirectory(_downloadFolder);
                zipPath = Path.Combine(_downloadFolder, sha + ".zip");
                string downloadError = await DownloadAsync(ZipUrlPrefix + sha, zipPath, cancellationToken).ConfigureAwait(false);
                if (downloadError != null) return PluginUpdateResult.Fail(downloadError);

                if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
                using (var stream = File.OpenRead(zipPath))
                    ExtractZipball(stream, incoming, MaxExtractedBytes, MaxEntries);

                string invalid = ValidateTree(incoming);
                if (invalid != null) return PluginUpdateResult.Fail("The downloaded plugin is not valid: " + invalid);

                File.WriteAllText(Path.Combine(incoming, StampFileName), BuildStampJson(sha, DateTimeOffset.UtcNow, StampedByUpdate));

                return Swap(root, incoming, RealSwapFileSystem.Instance);
            }
            catch (InvalidDataException ex)
            {
                return PluginUpdateResult.Fail("The downloaded archive was refused: " + ex.Message);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return PluginUpdateResult.Fail("The update failed: " + (GitHubIssueFeed.Sanitize(ex.Message, 300) ?? "disk error"));
            }
            finally
            {
                TryDeleteFile(zipPath);
                TryDeleteDirectory(incoming);
                UpdateGate.Release();
            }
        }

        /// <summary>
        /// Extracts a GitHub zipball into <paramref name="target"/> (which must not exist), dropping its top
        /// folder. Refuses (<see cref="InvalidDataException"/>) an entry outside the target, an entry outside
        /// the top folder, more than <paramref name="maxEntries"/> entries, or more than <paramref name="maxBytes"/>
        /// extracted bytes, counted as written rather than trusted from the header.
        /// </summary>
        internal static void ExtractZipball(Stream zip, string target, long maxBytes, int maxEntries)
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count == 0) throw new InvalidDataException("The archive is empty.");
            if (archive.Entries.Count > maxEntries) throw new InvalidDataException($"The archive has more than {maxEntries} entries.");

            string first = archive.Entries[0].FullName.Replace('\\', '/');
            int slash = first.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0) throw new InvalidDataException("The archive has no top folder.");
            string topFolder = first.Substring(0, slash);

            Directory.CreateDirectory(target);
            long written = 0;
            var buffer = new byte[81920];
            foreach (var entry in archive.Entries)
            {
                string relative = StripTopFolder(entry.FullName, topFolder);
                if (relative == null) continue;
                string path = SafeEntryPath(target, relative);

                bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                if (isDirectory)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using var input = entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    if (written > maxBytes) throw new InvalidDataException($"The archive extracts to more than {maxBytes / (1024 * 1024)} MB.");
                    output.Write(buffer, 0, read);
                }
            }
        }

        /// <summary>Why <paramref name="tree"/> is not a MultiTerminal marketplace, or null when it is.</summary>
        internal static string ValidateTree(string tree)
        {
            string marketplace = Path.Combine(tree, ".claude-plugin", "marketplace.json");
            string plugin = Path.Combine(tree, "plugins", "multiterminal", ".claude-plugin", "plugin.json");
            string error = ValidateManifest(marketplace, "multiterminal-marketplace");
            if (error != null) return error;
            return ValidateManifest(plugin, "multiterminal");
        }

        private static string ValidateManifest(string path, string expectedName)
        {
            string shown = Path.GetFileName(path);
            if (!File.Exists(path)) return $"{shown} is missing";
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return $"{shown} is not a JSON object";
                if (!string.Equals(StringProperty(doc.RootElement, "name"), expectedName, StringComparison.Ordinal))
                    return $"{shown} does not name {expectedName}";
                return null;
            }
            catch (JsonException)
            {
                return $"{shown} is not valid JSON";
            }
        }

        /// <summary>
        /// Swaps <paramref name="incoming"/> in for <paramref name="liveRoot"/>: delete an older
        /// <c>&lt;root&gt;.previous</c>, move the live root there, move the new tree in, check it. Any failure
        /// after the live root moved puts it back. Public steps go through <paramref name="fs"/> so a test can
        /// fail any one of them.
        /// </summary>
        internal static PluginUpdateResult Swap(string liveRoot, string incoming, IPluginSwapFileSystem fs)
        {
            string previous = liveRoot + ".previous";
            try
            {
                if (fs.DirectoryExists(previous)) fs.DeleteDirectory(previous);
                fs.MoveDirectory(liveRoot, previous);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Nothing has moved yet (a failed rename leaves the source where it was).
                return PluginUpdateResult.Fail("The plugin folder could not be moved aside (a file in it may be in use): "
                    + (GitHubIssueFeed.Sanitize(ex.Message, 300) ?? "disk error") + ". Nothing was changed.");
            }

            try
            {
                fs.MoveDirectory(incoming, liveRoot);
                string invalid = fs.DirectoryExists(liveRoot) ? ValidateTree(liveRoot) : "the new folder is not in place";
                if (invalid != null) throw new IOException(invalid);
                return PluginUpdateResult.Ok(UpdatedMessage);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                string why = GitHubIssueFeed.Sanitize(ex.Message, 300) ?? "disk error";
                try
                {
                    if (fs.DirectoryExists(liveRoot)) fs.DeleteDirectory(liveRoot);
                    fs.MoveDirectory(previous, liveRoot);
                    return PluginUpdateResult.Fail("The update failed (" + why + "). The previous plugin was put back.");
                }
                catch (Exception rollback) when (rollback is IOException || rollback is UnauthorizedAccessException)
                {
                    return PluginUpdateResult.Fail("The update failed (" + why + ") and the previous plugin could not be put back ("
                        + (GitHubIssueFeed.Sanitize(rollback.Message, 300) ?? "disk error") + "). It is in " + previous
                        + ". Rename it to " + liveRoot + " to restore it.");
                }
            }
        }

        /// <summary>
        /// The real moves and deletes, each retried for up to ~5 s. Renaming a folder whose files were just
        /// written (the extracted <c>.incoming</c>, or a root renamed a moment ago) is refused with "Access
        /// denied" for a short while, seen in this repo's tests on 2026-10-30; antivirus scanning is the likely
        /// holder. A missing source or an existing destination is not retried: waiting cannot fix either.
        /// </summary>
        internal sealed class RealSwapFileSystem : IPluginSwapFileSystem
        {
            public static readonly RealSwapFileSystem Instance = new RealSwapFileSystem();
            private static readonly int[] RetryDelaysMs = { 50, 100, 200, 400, 800, 1200, 2000 };

            public bool DirectoryExists(string path) => Directory.Exists(path);

            public void DeleteDirectory(string path) => Retry(() => Directory.Delete(path, recursive: true), () => Directory.Exists(path));

            public void MoveDirectory(string from, string to) =>
                Retry(() => Directory.Move(from, to), () => Directory.Exists(from) && !Directory.Exists(to));

            private static void Retry(Action action, Func<bool> worthRetrying)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        action();
                        return;
                    }
                    catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException)
                                               && ex is not DirectoryNotFoundException
                                               && attempt < RetryDelaysMs.Length && worthRetrying())
                    {
                        Thread.Sleep(RetryDelaysMs[attempt]);
                    }
                }
            }
        }

        private async Task<string> DownloadAsync(string url, string path, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("MultiTerminal");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return $"GitHub returned {(int)response.StatusCode} for the download.";
                if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                    return "The download is larger than expected; refused.";

                using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes) return "The download is larger than expected; refused.";
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return "The download did not finish in time.";
            }
            catch (HttpRequestException ex)
            {
                return "Network error: " + (GitHubIssueFeed.Sanitize(ex.Message, 200) ?? "unknown");
            }
        }

        /// <summary>
        /// Whether the root's parent accepts a new folder (the swap renames in it) and the root accepts a new
        /// file. Probes with real writes, removed straight away; ACL reading would miss a read-only volume.
        /// </summary>
        private static bool IsWritable(string marketplaceRoot)
        {
            string probeDir = marketplaceRoot + ".probe-" + Guid.NewGuid().ToString("N");
            string probeFile = Path.Combine(marketplaceRoot, ".mt-write-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(probeDir);
                Directory.Delete(probeDir);
                File.WriteAllText(probeFile, "");
                File.Delete(probeFile);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDeleteDirectory(probeDir);
                TryDeleteFile(probeFile);
                return false;
            }
        }

        /// <summary>
        /// Asks git about a root with a <c>.git</c>: <c>status --porcelain</c>, then a fetch of main from the
        /// constant GitHub URL (never the folder's own <c>origin</c>, which is whatever the build machine had)
        /// and <c>rev-list --count FETCH_HEAD..HEAD</c>. No prompts (<c>GIT_TERMINAL_PROMPT=0</c>, no
        /// credential helper) and no fsmonitor hook from the folder's config.
        /// </summary>
        private static GitProbe ProbeGit(string marketplaceRoot)
        {
            var status = RunGit(marketplaceRoot, TimeSpan.FromSeconds(20), "status", "--porcelain");
            if (status.NotFound) return new GitProbe(false, false, 0, true);
            bool dirty = status.ExitCode == 0 && !string.IsNullOrWhiteSpace(status.Output);

            var fetch = RunGit(marketplaceRoot, TimeSpan.FromSeconds(60), "fetch", "--quiet", "--no-tags",
                "https://github.com/" + Repo + ".git", Ref);
            if (fetch.ExitCode != 0) return new GitProbe(true, dirty, 0, true);

            var ahead = RunGit(marketplaceRoot, TimeSpan.FromSeconds(20), "rev-list", "--count", "FETCH_HEAD..HEAD");
            if (ahead.ExitCode != 0 || !int.TryParse(ahead.Output?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                return new GitProbe(true, dirty, 0, true);
            return new GitProbe(true, dirty, count, false);
        }

        private readonly record struct GitRun(bool NotFound, int ExitCode, string Output);

        private static GitRun RunGit(string workingDirectory, TimeSpan timeout, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (string a in new[] { "-C", workingDirectory, "-c", "credential.helper=", "-c", "core.fsmonitor=false" })
                psi.ArgumentList.Add(a);
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";

            try
            {
                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return new GitRun(true, -1, null);
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                    return new GitRun(false, -1, null);
                }
                _ = stderr.Result;
                return new GitRun(false, process.ExitCode, stdout.Result);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new GitRun(true, -1, null); // git is not on PATH
            }
        }

        private static async Task<byte[]> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
        {
            using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        private static string StringProperty(JsonElement element, string name) =>
            element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string FirstLine(string text)
        {
            if (text == null) return null;
            int end = text.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? text : text.Substring(0, end);
        }

        private static void TryDeleteFile(string path)
        {
            if (path == null) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (path == null) return;
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
