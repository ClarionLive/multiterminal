using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// Watches a terminal's output for Claude Code's startup: the dev-channel warning dialog and the
    /// banner. Owns the accumulated buffer and the one-shot flags that used to live inline in
    /// <see cref="TerminalControl"/>, so the matching rules can be tested without a UI (task 00cdd389).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the banner is matched on normalized text.</b> Claude Code's renderer writes spaces as
    /// cursor-forward escapes. The raw dump of the dev-channel dialog logged on 2026-09-22 reads
    /// <c>WARNING:\x1b[1CLoading\x1b[1Cdevelopment</c>, so a banner drawn the same way arrives as
    /// <c>Claude\x1b[1CCode</c> and a raw <c>Contains("Claude Code")</c> never sees it. That is how the
    /// first terminal after launch sat on its banner with no "initializing..." typed.
    /// </para>
    /// <para>
    /// <b>Why detection disarms, and why it re-arms only once.</b> A missed banner used to leave the
    /// detector armed for the life of the pane, so the first time an agent printed the words
    /// "Claude Code" MT typed "initializing..." + Enter into a live conversation. The banner only
    /// matters before the session has started. Once a prompt is submitted, or
    /// <see cref="BannerWindow"/> has passed, detection stops for good. Nothing a person types
    /// afterwards can restart it: a prompt that begins with "claude" looks exactly like a shell
    /// command that launches it, and treating it as one reopened the live-session trigger (pipeline
    /// run 1). What arms detection is how the pane was LAUNCHED: a Claude launch command arms it at
    /// start, and a plain shell arms it once, when the Owner types the launch line.
    /// </para>
    /// <para>
    /// <b>What reaches the debug log.</b> That log is readable by agents, and pane output can hold
    /// secrets. So the dumps are redacted (<see cref="RedactForLog"/>), and the no-banner dump is
    /// limited to short excerpts around the word "claude", which is all the open question needs.
    /// </para>
    /// <para>
    /// Not thread-safe, and it does not need to be: TerminalControl calls <see cref="Append"/> after
    /// marshalling ConPTY output onto the UI thread, and <see cref="NotifyLineSubmitted"/> from the
    /// renderer's input path, which WebView2 raises on the UI thread.
    /// </para>
    /// </remarks>
    internal sealed class ClaudeStartupDetector
    {
        /// <summary>Buffer length at which the oldest output is dropped, AFTER that chunk has been scanned.</summary>
        internal const int MaxBufferChars = 8000;

        /// <summary>Length the buffer is cut back to once it exceeds <see cref="MaxBufferChars"/>.</summary>
        internal const int TrimToChars = 4000;

        /// <summary>
        /// Shortest submitted line that disarms detection. One character is excluded because the
        /// dev-channel dialog is answered with a bare "1", which is not the session starting.
        /// </summary>
        internal const int MinSubmittedLineLength = 2;

        /// <summary>Characters kept on each side of a "claude" occurrence in the no-banner dump.</summary>
        internal const int ExcerptRadius = 80;

        /// <summary>Most excerpts the no-banner dump will include.</summary>
        internal const int MaxExcerpts = 3;

        /// <summary>How long after arming the banner may still be recognised.</summary>
        internal static readonly TimeSpan BannerWindow = TimeSpan.FromSeconds(120);

        // Matches ANSI/VT escape sequences (CSI, OSC, and single-char escapes) so detection
        // works against the visible text, not the styled byte stream.
        private static readonly Regex AnsiEscapeRegex =
            new Regex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))", RegexOptions.Compiled);

        // "Claude Code v2.1.280" normalizes to "claude code v2 1 280"; the escape-spaced form
        // normalizes to "claudecodev2 1 280", because stripping \x1b[1C leaves no space behind.
        // The version anchor keeps agent prose such as "Claude Code detected in terminal" (the
        // text that fired the 2026-09-22 11:40:51 false trigger) from matching.
        private static readonly Regex BannerRegex =
            new Regex(@"claude ?code ?v ?\d", RegexOptions.Compiled);

        // API keys, bearer tokens, hex credentials: long unbroken runs. '-' is deliberately NOT in the
        // class, so hyphenated flags such as --dangerously-load-development-channels survive, and the
        // dev-channel dump still shows the wording it exists to recover.
        private static readonly Regex TokenLikeRegex =
            new Regex(@"[A-Za-z0-9_+/=]{20,}", RegexOptions.Compiled);

        // Structured secrets split by '-' or '.': UUIDs, JWTs, sk-ant-api03-... keys. Candidates only;
        // RedactText keeps a run that has no digit, which is what spares the flag wording above.
        private static readonly Regex StructuredTokenRegex =
            new Regex(@"[A-Za-z0-9_+/=.\-]{16,}", RegexOptions.Compiled);

        // The password in https://user:password@host.
        private static readonly Regex UrlUserInfoRegex =
            new Regex(@"(?<=://)[^/\s@]+@", RegexOptions.Compiled);

        // Arguments after the executable that run a command and exit instead of opening a session. A
        // plain shell gets ONE arming, so `claude --version` must not spend it before the real launch.
        private static readonly string[] NonSessionFlags = { "-v", "--version", "-h", "--help", "-p", "--print" };

        private static readonly string[] NonSessionSubcommands =
            { "update", "mcp", "doctor", "config", "install", "plugin", "setup-token", "migrate-installer" };

        private const string NpmPackage = "@anthropic-ai/claude-code";

        private readonly Func<DateTime> _utcNow;
        private readonly StringBuilder _buffer = new StringBuilder();
        private DateTime _startedUtc;
        private bool _devChannelDumped;

        /// <param name="launchesClaude">
        /// True when the pane's launch command starts Claude Code. False for a plain shell, which stays
        /// unarmed until a launch line is typed.
        /// </param>
        /// <param name="utcNow">Clock; tests pass a fake one.</param>
        internal ClaudeStartupDetector(bool launchesClaude = false, Func<DateTime> utcNow = null)
        {
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            Reset(launchesClaude);
        }

        public bool BannerDetected { get; private set; }

        public bool DevChannelWarningHandled { get; private set; }

        /// <summary>
        /// Not scanning output. True both after detection stopped and for a plain shell that has not
        /// been armed yet (<see cref="AwaitingShellLaunch"/> tells the two apart).
        /// </summary>
        public bool Disarmed { get; private set; }

        /// <summary>A plain-shell pane that has not yet seen a Claude launch line typed.</summary>
        public bool AwaitingShellLaunch { get; private set; }

        public bool IsWatching => !Disarmed && (!BannerDetected || !DevChannelWarningHandled);

        /// <summary>Starts over for a new process in the same pane.</summary>
        public void Reset(bool launchesClaude)
        {
            Arm();
            AwaitingShellLaunch = !launchesClaude;
            Disarmed = !launchesClaude;
        }

        private void Arm()
        {
            _buffer.Clear();
            BannerDetected = false;
            DevChannelWarningHandled = false;
            Disarmed = false;
            _devChannelDumped = false;
            _startedUtc = _utcNow();
        }

        /// <summary>
        /// Feeds one chunk of terminal output. Returns what, if anything, this chunk completed.
        /// </summary>
        public StartupScan Append(string text)
        {
            var scan = new StartupScan();
            if (!IsWatching) return scan;

            if (_utcNow() - _startedUtc > BannerWindow)
            {
                scan.DisarmLog = Disarm("banner window of " + BannerWindow.TotalSeconds + "s elapsed");
                return scan;
            }

            _buffer.Append(text);
            string buffer = _buffer.ToString();
            string normalized = NormalizeForMatch(buffer);

            // Ground-truth dump the first time "development" appears, so the exact warning wording
            // can be recovered if the anchors below miss on a future Claude Code version.
            if (!_devChannelDumped && normalized.Contains("development"))
            {
                _devChannelDumped = true;
                scan.DevChannelDumpRaw = RedactForLog(buffer);
            }

            // The dev-channel check runs BEFORE the banner check so the latter's buffer clear
            // can't wipe the warning text within one call.
            if (!DevChannelWarningHandled &&
                (normalized.Contains("for local development") ||
                 normalized.Contains("loading development channels") ||
                 normalized.Contains("development channels") ||
                 normalized.Contains("enter to confirm")))
            {
                DevChannelWarningHandled = true;
                scan.DevChannelWarning = true;
            }

            if (!BannerDetected)
            {
                string anchor = MatchBanner(buffer, normalized);
                if (anchor != null)
                {
                    BannerDetected = true;
                    scan.BannerAnchor = anchor;
                    _buffer.Clear();
                    return scan;
                }
            }

            // Trim only after scanning: trimming first let one large chunk push the banner out
            // of the buffer before anything looked at it.
            if (_buffer.Length > MaxBufferChars)
            {
                _buffer.Remove(0, _buffer.Length - TrimToChars);
            }

            return scan;
        }

        /// <summary>
        /// Called when a line is submitted at the prompt. Returns a log line, or null.
        /// </summary>
        /// <remarks>
        /// In a plain-shell pane that has not launched Claude yet, lines are shell commands: they are
        /// ignored, except a Claude launch line, which arms detection with a fresh window. That can
        /// happen once per process. Once armed, a submitted prompt means the session has started, with
        /// or without our help, so detection stops permanently.
        /// A launch line recalled with the up arrow is not seen: ESC clears the input line buffer, so
        /// that launch gets no auto-start. This limitation predates this class.
        /// </remarks>
        public string NotifyLineSubmitted(string line)
        {
            if (AwaitingShellLaunch)
            {
                if (!IsClaudeLaunchLine(line)) return null;
                AwaitingShellLaunch = false;
                Arm();
                return "Claude Code launched from the shell prompt; startup detection armed";
            }

            if (!IsWatching) return null;
            if (line == null || line.Trim().Length < MinSubmittedLineLength) return null;
            return Disarm("prompt submitted");
        }

        /// <summary>
        /// True when the line opens a Claude Code session. That means either the executable is the first
        /// token (<c>claude</c>, <c>claude.exe</c> or <c>claude.cmd</c>, bare or at the end of a path,
        /// optionally after PowerShell's <c>&amp;</c> or inside quotes), or the npm package is run through a
        /// runner (<c>npx</c>, <c>bunx</c>, <c>pnpx</c>, <c>pnpm dlx</c>, <c>npm exec</c>). In both cases
        /// the arguments must not be one of the forms that print and exit (<c>--version</c>,
        /// <c>-p</c>, <c>mcp …</c>, and so on). Merely mentioning the package, as <c>npm i -g</c> or
        /// <c>echo</c> would, is not a launch.
        /// </summary>
        internal static bool IsClaudeLaunchLine(string line)
        {
            List<string> tokens = TokenizeCommand(line);
            if (tokens.Count == 0) return false;

            int argsFrom;
            if (IsClaudeExecutable(tokens[0]))
            {
                argsFrom = 1;
            }
            else
            {
                int runnerEnd;
                if (Is(tokens[0], "npx") || Is(tokens[0], "bunx") || Is(tokens[0], "pnpx")) runnerEnd = 1;
                else if (tokens.Count > 1 && Is(tokens[0], "pnpm") && Is(tokens[1], "dlx")) runnerEnd = 2;
                else if (tokens.Count > 1 && Is(tokens[0], "npm") && (Is(tokens[1], "exec") || Is(tokens[1], "x"))) runnerEnd = 2;
                else return false;

                // The package must be the FIRST positional after the runner's own options: that is what
                // the runner executes. In `npx cowsay @anthropic-ai/claude-code` it is only an argument.
                int pkg = tokens.FindIndex(runnerEnd, t => !t.StartsWith('-'));
                if (pkg < 0) return false;
                string candidate = tokens[pkg];
                if (!Is(candidate, NpmPackage) && !candidate.StartsWith(NpmPackage + "@", StringComparison.OrdinalIgnoreCase))
                    return false;
                argsFrom = pkg + 1;
            }

            bool firstPositional = true;
            for (int i = argsFrom; i < tokens.Count; i++)
            {
                string t = tokens[i];
                if (Array.Exists(NonSessionFlags, f => Is(t, f))) return false;
                if (t.StartsWith('-')) continue;
                if (firstPositional && Array.Exists(NonSessionSubcommands, s => Is(t, s))) return false;
                firstPositional = false;
            }

            return true;
        }

        private static bool Is(string token, string value) => token.Equals(value, StringComparison.OrdinalIgnoreCase);

        private static bool IsClaudeExecutable(string token)
        {
            int slash = Math.Max(token.LastIndexOf('\\'), token.LastIndexOf('/'));
            string name = token.Substring(slash + 1);
            return Is(name, "claude") || Is(name, "claude.exe") || Is(name, "claude.cmd");
        }

        /// <summary>
        /// Splits one shell command into tokens: whitespace separates, quotes group, and <c>;</c> or
        /// <c>|</c> ends the command. A leading PowerShell <c>&amp;</c> call operator is dropped.
        /// </summary>
        private static List<string> TokenizeCommand(string line)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(line)) return tokens;

            string s = line.TrimStart();
            if (s.StartsWith('&')) s = s.Substring(1);

            var current = new StringBuilder();
            char quote = '\0';
            foreach (char c in s)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else current.Append(c);
                    continue;
                }

                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == ';' || c == '|') break;
                if (char.IsWhiteSpace(c))
                {
                    if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }

        private string Disarm(string reason)
        {
            Disarmed = true;
            string message = null;
            if (!BannerDetected)
            {
                // The one thing still unexplained (task 00cdd389) is why some panes' banners were
                // missed. The bytes around "claude" answer it without dumping the whole pane.
                message = "Claude Code banner was never detected; startup detection disarmed (" + reason +
                          "). Excerpts (escaped, redacted): " + ExcerptForLog(_buffer.ToString());
            }

            _buffer.Clear();
            return message;
        }

        /// <summary>
        /// Up to <see cref="MaxExcerpts"/> redacted windows of ±<see cref="ExcerptRadius"/> characters
        /// around each "claude" (any case) in <paramref name="raw"/>. Says so when there is none, which
        /// is itself an answer: the banner never reached the buffer.
        /// </summary>
        internal static string ExcerptForLog(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "(buffer empty)";

            // Redact the WHOLE buffer before cutting: an excerpt that starts mid-token would otherwise
            // leave a tail shorter than the redaction threshold, visible in the log.
            string redacted = Redact(raw);
            var sb = new StringBuilder();
            int found = 0;
            int from = 0;
            while (found < MaxExcerpts && from < redacted.Length)
            {
                int i = redacted.IndexOf("claude", from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;

                int start = Math.Max(0, i - ExcerptRadius);
                int end = Math.Min(redacted.Length, i + "claude".Length + ExcerptRadius);
                if (found > 0) sb.Append(" … ");
                sb.Append(EscapeForLog(redacted.Substring(start, end - start)));
                found++;
                from = end;
            }

            return found == 0
                ? "(no \"claude\" text in the last " + raw.Length + " chars)"
                : sb.ToString();
        }

        /// <summary>Replaces token-like runs with their length, then escapes control bytes for one log line.</summary>
        internal static string RedactForLog(string s) => EscapeForLog(Redact(s));

        /// <summary>
        /// Redacts the visible text between escape sequences and copies the escapes through untouched.
        /// Secrets live in the text, never in escape parameters. Redacting across the boundary glued a
        /// cursor move's "3H" onto "--dangerously-load-development-channels", and that digit made the
        /// flag look like a structured token.
        /// Known gap: a secret rendered with an escape between every few characters splits into segments
        /// that are each under the thresholds. That is judged unrealistic here, because detection only
        /// runs from a Claude launch until the first prompt, so the output is Claude Code's own startup.
        /// </summary>
        private static string Redact(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var sb = new StringBuilder(s.Length);
            int last = 0;
            foreach (Match esc in AnsiEscapeRegex.Matches(s))
            {
                sb.Append(RedactText(s.Substring(last, esc.Index - last)));
                sb.Append(esc.Value);
                last = esc.Index + esc.Length;
            }
            sb.Append(RedactText(s.Substring(last)));
            return sb.ToString();
        }

        private static string RedactText(string s)
        {
            if (s.Length == 0) return s;
            s = UrlUserInfoRegex.Replace(s, "[redacted]@");
            s = TokenLikeRegex.Replace(s, m => "[redacted " + m.Length + "]");
            return StructuredTokenRegex.Replace(s, m => HasDigit(m.Value) ? "[redacted " + m.Length + "]" : m.Value);
        }

        private static bool HasDigit(string s)
        {
            foreach (char c in s)
            {
                if (char.IsDigit(c)) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the name of the rule that recognised the banner, or null. The name is logged so
        /// the trace shows which rendering the banner actually arrived in.
        /// </summary>
        internal static string MatchBanner(string raw, string normalized)
        {
            var m = BannerRegex.Match(normalized ?? string.Empty);
            if (m.Success)
            {
                return m.Value.StartsWith("claude code", StringComparison.Ordinal)
                    ? "banner (spaced)"
                    : "banner (escape-spaced)";
            }

            // Older banner layout: a rounded box containing "Tips". Matched raw because the box
            // characters are folded away by normalization.
            if (raw != null && raw.Contains("╭─") && raw.Contains("Tips"))
            {
                return "box + Tips";
            }

            return null;
        }

        /// <summary>
        /// Normalizes raw terminal output for robust phrase matching: strips ANSI escapes,
        /// folds every non-alphanumeric character (box-drawing borders, punctuation, newlines
        /// from TUI wrapping) to a single space, lowercases, and collapses runs of spaces.
        /// </summary>
        internal static string NormalizeForMatch(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            string noAnsi = AnsiEscapeRegex.Replace(s, string.Empty);
            var sb = new StringBuilder(noAnsi.Length);
            foreach (char c in noAnsi)
            {
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }
            return Regex.Replace(sb.ToString(), " +", " ");
        }

        /// <summary>Escapes control bytes so a buffer can be written to a single trace line.</summary>
        internal static string EscapeForLog(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\x1B", "\\x1b").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }

    /// <summary>What one chunk of output completed. Every member is empty when nothing happened.</summary>
    internal sealed class StartupScan
    {
        public bool DevChannelWarning { get; set; }

        /// <summary>Non-null when this chunk completed the banner; names the rule that matched.</summary>
        public string BannerAnchor { get; set; }

        /// <summary>Non-null the first time "development" appears: the buffer, escaped and redacted.</summary>
        public string DevChannelDumpRaw { get; set; }

        /// <summary>Non-null when detection disarmed without ever seeing the banner.</summary>
        public string DisarmLog { get; set; }
    }
}
