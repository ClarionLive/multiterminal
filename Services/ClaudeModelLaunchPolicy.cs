using System.Text.RegularExpressions;

namespace MultiTerminal.Services
{
    /// <summary>What a launch is, for choosing its model (ticket b0d2f090).</summary>
    public enum ClaudeModelRole
    {
        /// <summary>A project's own pane: Launch Project, Just Claude, Launch as, New Project.</summary>
        ProjectPane,

        /// <summary>A helper (spawn_helper, a helper resumed with its PM) or a headless agent.</summary>
        Helper,
    }

    /// <summary>The settings a model decision reads. Injected, so the decision stays pure.</summary>
    public sealed class ClaudeModelSettingsValues
    {
        /// <summary>Settings > General "Project model". Null or blank means the built-in default.</summary>
        public string GlobalProjectModel { get; set; }

        /// <summary>Settings > General "Helper model". Null or blank means the built-in default.</summary>
        public string GlobalHelperModel { get; set; }

        /// <summary>Settings > General "Subagent model". Null or blank means the built-in default.</summary>
        public string GlobalSubagentModel { get; set; }
    }

    /// <summary>The outcome of <see cref="ClaudeModelLaunchPolicy.Decide"/>.</summary>
    public sealed class ClaudeModelDecision
    {
        /// <summary>The name for <c>--model</c>, or null for no flag (Claude Code's own default).</summary>
        public string Model { get; set; }

        /// <summary>The name for CLAUDE_CODE_SUBAGENT_MODEL, or null for none.</summary>
        public string SubagentModel { get; set; }

        /// <summary>Which rule chose <see cref="Model"/>: requested, project, global, or default.</summary>
        public string Source { get; set; }

        /// <summary>
        /// Set when a name could not be used. For a REQUESTED name the launch must be refused; for a
        /// stored name the launch goes ahead on the next level's model and this is logged.
        /// </summary>
        public string Problem { get; set; }

        /// <summary>True when the problem is with the requested name (a spawn_helper call), so the caller refuses.</summary>
        public bool RequestedNameRejected { get; set; }
    }

    /// <summary>
    /// The one decision of which model a Claude launch uses (ticket b0d2f090). Every launch site asks
    /// here; none reads the settings itself.
    /// <para>Order, first match wins:
    /// 1. the model named in the spawn_helper call (a helper only);
    /// 2. a helper: the project's helper model, then the global helper model;
    ///    a project pane: the project's model, then the global project model;
    /// 3. the built-in default for the role (opus for a project pane, sonnet for a helper).</para>
    /// <para>"default" (<see cref="DefaultToken"/>) at any level means Claude Code's own default: no flag.
    /// A helper resumed with its PM is a helper. A helper with no project passes null project values.</para>
    /// <para>A model name is untrusted text bound for a PowerShell command line, so
    /// <see cref="IsValidModelName"/> is the only gate and it runs on every name that can win. A stored
    /// invalid name is skipped with a warning naming it and where it was stored, and the next level decides,
    /// ending at the built-in model: never "no flag", which on a resume would bring back the transcript's
    /// model. The explicit default token still means no flag. An invalid requested name is rejected.</para>
    /// </summary>
    public static class ClaudeModelLaunchPolicy
    {
        /// <summary>The stored value meaning "Claude Code's default": launch with no flag.</summary>
        public const string DefaultToken = "default";

        public const string BuiltInProjectModel = "opus";
        public const string BuiltInHelperModel = "sonnet";
        public const string BuiltInSubagentModel = "sonnet";

        // Letters, digits, dots and hyphens, starting with a letter or digit, and an optional [1m] suffix.
        // Nothing else may reach the command line: no quote, space, semicolon, $ or parenthesis.
        private static readonly Regex NamePattern =
            new Regex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,63}(\[1m\])?\z",RegexOptions.CultureInvariant);

        /// <summary>True when <paramref name="name"/> is safe to put after <c>--model</c>.</summary>
        public static bool IsValidModelName(string name) =>
            !string.IsNullOrEmpty(name) && NamePattern.IsMatch(name);

        /// <summary>True for the "Claude Code's default" value (no flag), ignoring case and surrounding space.</summary>
        public static bool IsDefaultToken(string name) =>
            name != null && string.Equals(name.Trim(), DefaultToken, System.StringComparison.OrdinalIgnoreCase);

        /// <summary>A stored or typed name trimmed; null when blank.</summary>
        public static string Normalize(string name) =>
            string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        /// <param name="role">Project pane or helper.</param>
        /// <param name="requestedModel">The spawn_helper call's model; null for every other launch.</param>
        /// <param name="projectModel">The launching project's own model (a project pane); null when none.</param>
        /// <param name="projectHelperModel">The launching project's helper model; null when none.</param>
        /// <param name="settings">The global settings; null is treated as all built-in defaults.</param>
        public static ClaudeModelDecision Decide(
            ClaudeModelRole role,
            string requestedModel,
            string projectModel,
            string projectHelperModel,
            ClaudeModelSettingsValues settings)
        {
            var result = new ClaudeModelDecision();
            bool helper = role == ClaudeModelRole.Helper;

            result.SubagentModel = ResolveSubagent(settings, result);

            string requested = Normalize(requestedModel);
            if (requested != null && helper)
            {
                if (IsDefaultToken(requested))
                {
                    result.Source = "requested";
                    return result;
                }
                if (!IsValidModelName(requested))
                {
                    result.Problem = $"'{requested}' is not a valid model name (letters, digits, dots and hyphens, with an optional [1m] suffix).";
                    result.RequestedNameRejected = true;
                    result.Source = "requested";
                    return result;
                }
                result.Model = requested;
                result.Source = "requested";
                return result;
            }

            // A STORED name that is not valid is skipped with a warning and the next level decides, ending at
            // the built-in. It is never "no flag": on --resume that would bring back the model stored in the
            // transcript, which is what this ticket exists to prevent. Only the explicit default token means
            // no flag, because the Owner chose that.
            if (TryLevel(result, Normalize(helper ? projectHelperModel : projectModel), "project", helper ? "the project's Helper model" : "the project's Model"))
                return result;

            if (TryLevel(result, Normalize(helper ? settings?.GlobalHelperModel : settings?.GlobalProjectModel), "global", helper ? "Settings > General Helper model" : "Settings > General Project model"))
                return result;

            result.Model = helper ? BuiltInHelperModel : BuiltInProjectModel;
            result.Source = "built-in";
            return result;
        }

        private static string ResolveSubagent(ClaudeModelSettingsValues settings, ClaudeModelDecision result)
        {
            string stored = Normalize(settings?.GlobalSubagentModel);
            if (stored == null) return BuiltInSubagentModel;
            if (IsDefaultToken(stored)) return null;
            if (IsValidModelName(stored)) return stored;
            AddProblem(result, $"Stored model '{stored}' in Settings > General Subagent model is not a valid model name; using the built-in '{BuiltInSubagentModel}'.");
            return BuiltInSubagentModel;
        }

        // True when this level decided (a valid name, or the default token = no flag). False when the level is
        // unset or holds an invalid name, so the caller carries on down the order.
        private static bool TryLevel(ClaudeModelDecision result, string stored, string source, string where)
        {
            if (stored == null) return false;
            if (IsDefaultToken(stored))
            {
                result.Source = source;
                return true;
            }
            if (!IsValidModelName(stored))
            {
                AddProblem(result, $"Stored model '{stored}' in {where} is not a valid model name; skipped.");
                return false;
            }
            result.Model = stored;
            result.Source = source;
            return true;
        }

        private static void AddProblem(ClaudeModelDecision result, string text) =>
            result.Problem = result.Problem == null ? text : result.Problem + " " + text;
    }
}
