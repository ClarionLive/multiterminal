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
        void WriteAllText(string path, string text);
        void DeleteFile(string path);
    }

    /// <summary>What the startup/check reconcile does about a marketplace root and its sibling folders.</summary>
    internal enum PluginFolderRepair
    {
        /// <summary>Nothing to do: the root is in place with no leftover, or nothing was ever installed here.</summary>
        None,
        /// <summary>The root is in place; a <c>.incoming</c> beside it never became live and is deleted.</summary>
        DeleteStaleIncoming,
        /// <summary>The root is missing and a valid <c>.incoming</c> is waiting: the crash came after rename 1. Move it in.</summary>
        FinishSwap,
        /// <summary>The root is missing, no valid <c>.incoming</c>, a valid <c>.previous*</c>: move that back.</summary>
        RestorePrevious,
        /// <summary>The root is missing and nothing beside it is valid. Nothing is deleted; logged loudly.</summary>
        NothingValid,
    }

    /// <summary>A <c>&lt;root&gt;.previous</c> or <c>&lt;root&gt;.previous-&lt;yyyyMMddHHmmss&gt;</c> folder and whether it passes the manifest checks.</summary>
    internal readonly record struct PluginPreviousFolder(string Path, bool Valid);

    /// <summary>The three folders of one marketplace root as the reconcile found them.</summary>
    internal sealed class PluginFolderSnapshot
    {
        public bool RootExists { get; init; }
        public bool IncomingExists { get; init; }
        public bool IncomingValid { get; init; }

        /// <summary>Most preferred first: the one the swap journal names, then plain <c>.previous</c>, then newest timestamp.</summary>
        public IReadOnlyList<PluginPreviousFolder> Previous { get; init; } = Array.Empty<PluginPreviousFolder>();
        public bool JournalExists { get; init; }
    }

    internal readonly record struct PluginFolderDecision(PluginFolderRepair Action, string RestoreFrom, bool DeleteIncomingAfter, bool DeleteJournal);

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
    /// <c>&lt;root&gt;.previous</c> (one kept), or <c>&lt;root&gt;.previous-&lt;yyyyMMddHHmmss&gt;</c> when it held a
    /// <c>.git</c> (never deleted by MT); any failure after that moves it back.</para>
    ///
    /// <para><b>Crash between the renames:</b> <see cref="ReconcileRoots"/> runs at MT startup, before any
    /// terminal can launch, and at the start of every check and update. <see cref="DecideRepair"/> finishes
    /// the swap, restores the previous folder or deletes a stale <c>.incoming</c>; it never deletes anything
    /// while the root is missing.</para>
    ///
    /// Running terminals keep the plugin they loaded; terminals started afterwards use the new one.
    /// </summary>
    internal sealed class PluginUpdateService : IDisposable
    {
        public const string Repo = "ClarionLive/multiterminal-marketplace";
        public const string Ref = "main";
        public const string StampFileName = ".mt-plugin-source.json";
        public const string StampedByUpdate = "mt-update";

        internal const string IncomingSuffix = ".incoming";
        internal const string PreviousSuffix = ".previous";

        /// <summary>
        /// Beside the root during a swap: the leaf name of the folder the live root was moved to, so a
        /// reconcile after a crash restores THAT one rather than an older <c>.previous*</c>.
        /// </summary>
        internal const string JournalSuffix = ".swap-previous";
        private const string PreviousStampFormat = "yyyyMMddHHmmss";

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
        private readonly Func<IEnumerable<string>> _pluginDirCandidates;
        private readonly string _downloadFolder;

        /// <summary>Where reconcile messages go (the debug log in MT); null drops them.</summary>
        public Action<string> Log { get; set; }

        /// <param name="handler">Null for the real network; tests pass a stub.</param>
        /// <param name="pluginDir">The plugin folder MT loads; null for <see cref="LaunchCommandBuilder.GetMtPluginPath"/>.
        /// A test's folder is also the only one reconciled; otherwise every <see cref="LaunchCommandBuilder.ExpectedMtPluginPaths"/> is.</param>
        /// <param name="downloadFolder">Where the zipball is saved; null for <c>%LOCALAPPDATA%\MultiTerminal\plugin-update</c>.</param>
        public PluginUpdateService(HttpMessageHandler handler = null, Func<string> pluginDir = null, string downloadFolder = null)
        {
            _pluginDirCandidates = pluginDir != null ? () => new[] { pluginDir() } : () => LaunchCommandBuilder.ExpectedMtPluginPaths;
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
        /// probed with <paramref name="probeGit"/> and decided by <see cref="GitCheckoutRefuseReason"/>.
        /// </summary>
        internal static string RefuseReason(string marketplaceRoot, Func<string, bool> directoryExists, Func<string, bool> fileExists,
            Func<string, bool> isWritable, Func<string, GitProbe> probeGit)
        {
            if (string.IsNullOrWhiteSpace(marketplaceRoot) || !directoryExists(marketplaceRoot))
                return "The MultiTerminal plugin folder was not found.";
            if (HasGit(marketplaceRoot, directoryExists, fileExists))
            {
                string refuse = GitCheckoutRefuseReason(marketplaceRoot, probeGit(marketplaceRoot));
                if (refuse != null) return refuse;
            }
            if (!isWritable(marketplaceRoot))
                return $"MultiTerminal cannot write to {marketplaceRoot} or the folder that holds it, so it cannot update the plugin there.";
            return null;
        }

        /// <summary>Pure: whether <paramref name="folder"/> has a <c>.git</c> folder, or a worktree's <c>.git</c> file.</summary>
        internal static bool HasGit(string folder, Func<string, bool> directoryExists, Func<string, bool> fileExists)
        {
            string git = Path.Combine(folder, ".git");
            return directoryExists(git) || fileExists(git);
        }

        /// <summary>One git command's outcome. <see cref="TimedOut"/> runs report exit code -1 and no output.</summary>
        internal readonly record struct GitCommandResult(int ExitCode, string Output, bool TimedOut = false)
        {
            public bool Succeeded => !TimedOut && ExitCode == 0;
        }

        /// <summary>
        /// What git said about a root that has a <c>.git</c>, one result per command in the order they run.
        /// A command after the first failure is not run and stays null.
        /// </summary>
        internal sealed record GitProbe(bool GitAvailable, GitCommandResult? Status = null, GitCommandResult? Stash = null,
            GitCommandResult? Fetch = null, GitCommandResult? Ahead = null);

        /// <summary>
        /// Pure: fails CLOSED. When git is on PATH, a root with a <c>.git</c> is replaced only if
        /// <c>status --porcelain --ignored</c> succeeds and is empty (our stamp file excluded), <c>stash list</c>
        /// succeeds and is empty, the fetch of GitHub main succeeds, and <c>rev-list --count FETCH_HEAD..HEAD</c>
        /// succeeds and is 0. Any failed, timed-out or unrun command refuses with "could not check the git
        /// checkout". When git is NOT on PATH the <c>.git</c> is a 3.0.0 leftover (its installer copied the build
        /// machine's clone and Inno never deletes it; a developer always has git) and the update goes ahead,
        /// moving the old root to <c>&lt;root&gt;.previous-&lt;time&gt;</c>, which MT never deletes.
        /// </summary>
        internal static string GitCheckoutRefuseReason(string marketplaceRoot, GitProbe probe)
        {
            if (probe == null) return CouldNotCheck(marketplaceRoot, "git was not asked");
            if (!probe.GitAvailable) return null;

            // In command order, so the reason names the first thing that is wrong.
            string failed = Failed("git status", probe.Status);
            if (failed != null) return CouldNotCheck(marketplaceRoot, failed);
            if (!string.IsNullOrWhiteSpace(probe.Status.Value.Output))
                return $"{marketplaceRoot} is a git checkout with uncommitted changes or ignored files. Update it with git instead.";

            failed = Failed("git stash list", probe.Stash);
            if (failed != null) return CouldNotCheck(marketplaceRoot, failed);
            if (!string.IsNullOrWhiteSpace(probe.Stash.Value.Output))
                return $"{marketplaceRoot} is a git checkout with stashed changes. Update it with git instead.";

            failed = Failed("the fetch of GitHub main", probe.Fetch) ?? Failed("git rev-list", probe.Ahead);
            if (failed != null) return CouldNotCheck(marketplaceRoot, failed);
            if (!int.TryParse(probe.Ahead.Value.Output?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int ahead))
                return CouldNotCheck(marketplaceRoot, "git rev-list did not print a count");
            if (ahead > 0)
                return $"{marketplaceRoot} is a git checkout with commits that are not on GitHub main. Update it with git instead.";
            return null;
        }

        private static string Failed(string what, GitCommandResult? result)
        {
            if (result == null) return what + " was not run";
            if (result.Value.TimedOut) return what + " timed out";
            if (result.Value.ExitCode != 0) return $"{what} failed (exit {result.Value.ExitCode})";
            return null;
        }

        private static string CouldNotCheck(string marketplaceRoot, string why) =>
            $"MultiTerminal could not check the git checkout in {marketplaceRoot}: {why}. Nothing was changed; update it with git, or try again.";

        /// <summary>
        /// Pure: where the swap moves the live root, and whether an existing folder there is deleted first.
        /// A root with a <c>.git</c> goes to <c>&lt;root&gt;.previous-&lt;yyyyMMddHHmmss&gt;</c> and is never
        /// deleted by MT. Otherwise to <c>&lt;root&gt;.previous</c>, replacing the one there, unless that one
        /// itself holds a <c>.git</c>: it is then left alone and the timestamped name is used.
        /// </summary>
        internal static (string Target, bool DeleteExisting) PreviousTarget(string liveRoot, bool rootHasGit,
            bool plainPreviousExists, bool plainPreviousHasGit, DateTimeOffset now)
        {
            string plain = liveRoot + PreviousSuffix;
            if (rootHasGit || (plainPreviousExists && plainPreviousHasGit))
                return (plain + "-" + now.UtcDateTime.ToString(PreviousStampFormat, CultureInfo.InvariantCulture), false);
            return (plain, plainPreviousExists);
        }

        /// <summary>
        /// Pure: what to do about one root after a possible crash mid-swap.
        /// <list type="bullet">
        /// <item>Root present: delete a <c>.incoming</c> (it never became live) and the journal.</item>
        /// <item>Root missing, valid <c>.incoming</c>: the crash came after rename 1, the new tree (stamp inside)
        /// is complete. Finish the swap.</item>
        /// <item>Root missing, no valid <c>.incoming</c>, a valid <c>.previous*</c>: restore the most preferred
        /// one; an invalid <c>.incoming</c> is deleted only once the root is back.</item>
        /// <item>Root missing and nothing valid: delete nothing. <see cref="PluginFolderRepair.NothingValid"/> when
        /// something is there to explain; plain <see cref="PluginFolderRepair.None"/> when this location was simply
        /// never installed (the other candidate location is in use).</item>
        /// </list>
        /// </summary>
        internal static PluginFolderDecision DecideRepair(PluginFolderSnapshot s)
        {
            if (s.RootExists)
                return new PluginFolderDecision(s.IncomingExists ? PluginFolderRepair.DeleteStaleIncoming : PluginFolderRepair.None,
                    null, false, s.JournalExists);
            if (s.IncomingExists && s.IncomingValid)
                return new PluginFolderDecision(PluginFolderRepair.FinishSwap, null, false, s.JournalExists);
            foreach (var previous in s.Previous)
            {
                if (previous.Valid)
                    return new PluginFolderDecision(PluginFolderRepair.RestorePrevious, previous.Path, s.IncomingExists, s.JournalExists);
            }
            bool anything = s.IncomingExists || s.Previous.Count > 0 || s.JournalExists;
            return new PluginFolderDecision(anything ? PluginFolderRepair.NothingValid : PluginFolderRepair.None, null, false, false);
        }

        /// <summary>
        /// Pure: the <c>.previous*</c> folder names beside <paramref name="liveRoot"/> in preference order: the one
        /// the swap journal names, then plain <c>.previous</c>, then timestamped ones newest first. A journal
        /// naming anything that is not one of these is ignored (it is a file on disk, not trusted).
        /// </summary>
        internal static IReadOnlyList<string> OrderPreviousFolders(string liveRoot, IEnumerable<string> siblingDirs, string journalLeaf)
        {
            string leaf = Path.GetFileName(liveRoot);
            string plainLeaf = leaf + PreviousSuffix;
            var plain = new List<string>();
            var stamped = new List<(string Stamp, string Path)>();
            foreach (string dir in siblingDirs)
            {
                string name = Path.GetFileName(dir);
                if (string.Equals(name, plainLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    plain.Add(dir);
                }
                else if (name.Length == plainLeaf.Length + 1 + PreviousStampFormat.Length
                         && name.StartsWith(plainLeaf + "-", StringComparison.OrdinalIgnoreCase)
                         && name.AsSpan(plainLeaf.Length + 1).IndexOfAnyExceptInRange('0', '9') < 0)
                {
                    stamped.Add((name.Substring(plainLeaf.Length + 1), dir));
                }
            }
            stamped.Sort((a, b) => string.CompareOrdinal(b.Stamp, a.Stamp));

            var ordered = new List<string>(plain);
            foreach (var s in stamped) ordered.Add(s.Path);
            if (!string.IsNullOrWhiteSpace(journalLeaf))
            {
                int named = ordered.FindIndex(p => string.Equals(Path.GetFileName(p), journalLeaf.Trim(), StringComparison.OrdinalIgnoreCase));
                if (named > 0)
                {
                    string first = ordered[named];
                    ordered.RemoveAt(named);
                    ordered.Insert(0, first);
                }
            }
            return ordered;
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
            // An update in progress owns .incoming; its own reconcile ran when it started.
            if (UpdateGate.Wait(0))
            {
                try { ReconcileRoots(SafeCandidates(), RealSwapFileSystem.Instance, Log); }
                finally { UpdateGate.Release(); }
            }

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

            if (!await UpdateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return PluginUpdateResult.Fail("An update is already running.");
            string zipPath = null;
            string root = null;
            string incoming = null;
            try
            {
                ReconcileRoots(SafeCandidates(), RealSwapFileSystem.Instance, Log);

                root = ResolveMarketplaceRoot();
                if (root == null || !string.Equals(root, check.MarketplaceRoot, StringComparison.OrdinalIgnoreCase))
                    return PluginUpdateResult.Fail("The plugin folder changed since the check. Check for an update again.");
                incoming = root + IncomingSuffix;

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

                string plainPrevious = root + PreviousSuffix;
                bool plainExists = Directory.Exists(plainPrevious);
                var (previous, deleteExisting) = PreviousTarget(root, HasGit(root, Directory.Exists, File.Exists),
                    plainExists, plainExists && HasGit(plainPrevious, Directory.Exists, File.Exists), DateTimeOffset.UtcNow);
                return Swap(root, incoming, RealSwapFileSystem.Instance, previous, deleteExisting);
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
                // Never while the root is missing: a failed rollback can leave the only valid tree in .incoming,
                // and the next reconcile finishes the swap from it.
                if (root != null && Directory.Exists(root)) TryDeleteDirectory(incoming);
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
        /// Swaps <paramref name="incoming"/> in for <paramref name="liveRoot"/>: delete the old
        /// <paramref name="previous"/> when <paramref name="deleteExistingPrevious"/>, write the journal, move the
        /// live root to <paramref name="previous"/>, move the new tree in, check it. Any failure after the live
        /// root moved puts it back, by RENAMING the new tree back to <c>.incoming</c> rather than deleting it, so
        /// every state a crash can leave is one <see cref="DecideRepair"/> handles. Steps go through
        /// <paramref name="fs"/> so a test can fail any one of them.
        /// </summary>
        internal static PluginUpdateResult Swap(string liveRoot, string incoming, IPluginSwapFileSystem fs, string previous, bool deleteExistingPrevious)
        {
            string journal = liveRoot + JournalSuffix;
            try
            {
                if (deleteExistingPrevious && fs.DirectoryExists(previous)) fs.DeleteDirectory(previous);
                fs.WriteAllText(journal, Path.GetFileName(previous));
                fs.MoveDirectory(liveRoot, previous);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Nothing has moved yet (a failed rename leaves the source where it was).
                TryFs(() => fs.DeleteFile(journal));
                return PluginUpdateResult.Fail("The plugin folder could not be moved aside (a file in it may be in use): "
                    + (GitHubIssueFeed.Sanitize(ex.Message, 300) ?? "disk error") + ". Nothing was changed.");
            }

            try
            {
                fs.MoveDirectory(incoming, liveRoot);
                string invalid = fs.DirectoryExists(liveRoot) ? ValidateTree(liveRoot) : "the new folder is not in place";
                if (invalid != null) throw new IOException(invalid);
                TryFs(() => fs.DeleteFile(journal));
                return PluginUpdateResult.Ok(UpdatedMessage);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                string why = GitHubIssueFeed.Sanitize(ex.Message, 300) ?? "disk error";
                try
                {
                    if (fs.DirectoryExists(liveRoot)) fs.MoveDirectory(liveRoot, incoming);
                    fs.MoveDirectory(previous, liveRoot);
                    TryFs(() => fs.DeleteFile(journal));
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
        /// Reconciles the marketplace root of every plugin folder MT might hand to a terminal. MT calls it once
        /// at startup, before any terminal can launch: while the shared root is missing,
        /// <see cref="LaunchCommandBuilder.GetMtPluginPath"/> would silently fall through to the default
        /// location. Never throws; each root's outcome goes to <paramref name="log"/>.
        /// </summary>
        internal static void ReconcileRoots(IEnumerable<string> pluginDirs, IPluginSwapFileSystem fs, Action<string> log)
        {
            if (pluginDirs == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pluginDir in pluginDirs)
            {
                string root = MarketplaceRootFromPluginDir(pluginDir);
                if (root != null && seen.Add(root)) ReconcileRoot(root, fs, log);
            }
        }

        /// <summary>Reconciles one root (see <see cref="DecideRepair"/>) and returns what was decided. Never throws.</summary>
        internal static PluginFolderDecision ReconcileRoot(string root, IPluginSwapFileSystem fs, Action<string> log)
        {
            string incoming = root + IncomingSuffix;
            string journal = root + JournalSuffix;
            PluginFolderDecision decision;
            try
            {
                decision = DecideRepair(Snapshot(root));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                log?.Invoke($"Could not inspect {root} for an interrupted update: {ex.Message}");
                return new PluginFolderDecision(PluginFolderRepair.None, null, false, false);
            }

            try
            {
                switch (decision.Action)
                {
                    case PluginFolderRepair.DeleteStaleIncoming:
                        fs.DeleteDirectory(incoming);
                        log?.Invoke($"Deleted {incoming}, left by an update that never went live.");
                        break;
                    case PluginFolderRepair.FinishSwap:
                        fs.MoveDirectory(incoming, root);
                        log?.Invoke($"An update was interrupted after the old plugin moved aside; finished it by moving {incoming} to {root}.");
                        break;
                    case PluginFolderRepair.RestorePrevious:
                        fs.MoveDirectory(decision.RestoreFrom, root);
                        log?.Invoke($"An update was interrupted with no usable new plugin; restored {decision.RestoreFrom} to {root}.");
                        if (decision.DeleteIncomingAfter) fs.DeleteDirectory(incoming);
                        break;
                    case PluginFolderRepair.NothingValid:
                        log?.Invoke($"PROBLEM: the MultiTerminal plugin folder {root} is missing and nothing beside it ({incoming}, {root}{PreviousSuffix}*) "
                            + "is a valid plugin. Nothing was deleted. Reinstall MultiTerminal or restore the folder by hand.");
                        break;
                }
                if (decision.DeleteJournal) fs.DeleteFile(journal);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke($"PROBLEM: repairing {root} after an interrupted update failed ({decision.Action}): {ex.Message}");
            }
            return decision;
        }

        private static PluginFolderSnapshot Snapshot(string root)
        {
            string incoming = root + IncomingSuffix;
            string journal = root + JournalSuffix;
            string parent = Path.GetDirectoryName(root);
            string[] siblings = parent != null && Directory.Exists(parent)
                ? Directory.GetDirectories(parent, Path.GetFileName(root) + PreviousSuffix + "*")
                : Array.Empty<string>();

            string journalLeaf = null;
            bool journalExists = File.Exists(journal);
            if (journalExists && new FileInfo(journal).Length <= 1024)
                journalLeaf = File.ReadAllText(journal);

            var previous = new List<PluginPreviousFolder>();
            foreach (string dir in OrderPreviousFolders(root, siblings, journalLeaf))
                previous.Add(new PluginPreviousFolder(dir, ValidateTree(dir) == null));

            bool incomingExists = Directory.Exists(incoming);
            return new PluginFolderSnapshot
            {
                RootExists = Directory.Exists(root),
                IncomingExists = incomingExists,
                IncomingValid = incomingExists && ValidateTree(incoming) == null,
                Previous = previous,
                JournalExists = journalExists,
            };
        }

        private IEnumerable<string> SafeCandidates()
        {
            try
            {
                return _pluginDirCandidates();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                Log?.Invoke("Could not list the plugin folders to check for an interrupted update: " + ex.Message);
                return Array.Empty<string>();
            }
        }

        private static void TryFs(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
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

            public void WriteAllText(string path, string text) => File.WriteAllText(path, text);

            public void DeleteFile(string path)
            {
                if (File.Exists(path)) File.Delete(path);
            }

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
        /// Asks git about a root with a <c>.git</c>, stopping at the first command that already decides
        /// (<see cref="GitCheckoutRefuseReason"/> reads what ran): <c>status --porcelain --ignored</c> (our stamp
        /// file excluded: the installer and each update write it, so it is not local work), <c>stash list</c>,
        /// a fetch of main from the constant GitHub URL (never the folder's own <c>origin</c>, which is whatever
        /// the build machine had) and <c>rev-list --count FETCH_HEAD..HEAD</c>. No prompts
        /// (<c>GIT_TERMINAL_PROMPT=0</c>, no credential helper) and no fsmonitor hook from the folder's config.
        /// </summary>
        private static GitProbe ProbeGit(string marketplaceRoot)
        {
            var status = RunGit(marketplaceRoot, TimeSpan.FromSeconds(20), "status", "--porcelain", "--ignored",
                "--", ".", ":(exclude)" + StampFileName);
            if (status.NotFound) return new GitProbe(false);
            var probe = new GitProbe(true, Status: status.Result);
            if (!status.Result.Succeeded || !string.IsNullOrWhiteSpace(status.Result.Output)) return probe;

            var stash = RunGit(marketplaceRoot, TimeSpan.FromSeconds(20), "stash", "list");
            probe = probe with { Stash = stash.Result };
            if (!stash.Result.Succeeded || !string.IsNullOrWhiteSpace(stash.Result.Output)) return probe;

            var fetch = RunGit(marketplaceRoot, TimeSpan.FromSeconds(60), "fetch", "--quiet", "--no-tags",
                "https://github.com/" + Repo + ".git", Ref);
            probe = probe with { Fetch = fetch.Result };
            if (!fetch.Result.Succeeded) return probe;

            var ahead = RunGit(marketplaceRoot, TimeSpan.FromSeconds(20), "rev-list", "--count", "FETCH_HEAD..HEAD");
            return probe with { Ahead = ahead.Result };
        }

        private readonly record struct GitRun(bool NotFound, GitCommandResult Result);

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
                if (process == null) return new GitRun(true, new GitCommandResult(-1, null));
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                    return new GitRun(false, new GitCommandResult(-1, null, TimedOut: true));
                }
                _ = stderr.Result;
                return new GitRun(false, new GitCommandResult(process.ExitCode, stdout.Result));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new GitRun(true, new GitCommandResult(-1, null)); // git is not on PATH
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
