using System;
using System.Collections.Generic;
using System.Text;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The identity a project launch REQUESTS, before the broker makes it unique (task 6a8d029f).
    /// </summary>
    /// <param name="Name">The requested name.</param>
    /// <param name="RegisterUnique">Register through the atomic <c>MessageBroker.RegisterTerminalUnique</c>,
    /// which hands back the first free <c>-N</c> suffix.</param>
    /// <param name="IsProjectDerived">The name was derived from the project (not a lead, not the Codex
    /// default). Such a launch FAILS CLOSED when registration is refused: it must never fall back to a
    /// placeholder or pool name.</param>
    internal readonly record struct ProjectLaunchIdentityRequest(string Name, bool RegisterUnique, bool IsProjectDerived);

    /// <summary>
    /// The reserved-name census, or why it could not be taken. ALL-OR-NOTHING (6a8d029f Run 2): a
    /// partial set would let a project take exactly the name a missing row would have reserved, so
    /// <see cref="Names"/> is null whenever <see cref="Error"/> is set.
    /// </summary>
    internal readonly record struct ReservedNamesCensus(HashSet<string> Names, string Error)
    {
        internal bool Succeeded => Error == null;
    }

    /// <summary>One registered project, as the reserved-name census needs it.</summary>
    internal readonly record struct ProjectIdentitySource(string Id, string Name, string TeamLead, IReadOnlyCollection<string> AgentNames);

    /// <summary>
    /// Which identity a terminal launched on a project gets — ticket 6a8d029f. Pure and static so the
    /// rule is testable without WinForms; every project launch site in <c>MainForm</c> asks here.
    ///
    /// <para><b>The rule.</b> A project with a team lead launches as that lead, unchanged (a second
    /// launch still shows the IdentityPicker). A Codex launch with a configured default agent name uses
    /// that name. EVERY OTHER project-backed launch gets a unique, non-placeholder identity derived from
    /// the project, whatever its name contains:</para>
    /// <list type="number">
    /// <item>the sanitized project name ("TestB", "My Project" → "My-Project");</item>
    /// <item>if that is empty or the placeholder ("Unassigned", emoji, non-Latin-only, punctuation-only):
    /// <c>Project-&lt;first 6 of the project id&gt;</c>;</item>
    /// <item>if the result is RESERVED — another project's sanitized name, any project's team lead or
    /// roster agent, Oracle, or the Codex default agent — it is qualified with
    /// <c>-&lt;first 4 of the project id&gt;</c>, so it can never land on another project's or a known
    /// agent's name even while that agent is offline;</item>
    /// <item>the broker then makes it unique among connected terminals ("TestB", "TestB-2"…).</item>
    /// </list>
    /// <para>"Unassigned" is only for project-less launches (Open PowerShell), which never reach here.</para>
    ///
    /// <para>Suffix reuse is intended (PM decision overnight by Alice, 2026-10-02; Owner may overrule):
    /// the broker counts connected terminals only, so after "TestB-2" closes the next second pane is
    /// "TestB-2" again and inherits that name's profile, active task and inbox.</para>
    ///
    /// <para>Residual: an agent who is never a team lead or roster member of any project is not in the
    /// reserved set (nothing records which profiles are agents), so a project named like such an agent
    /// can take that name while the agent is offline.</para>
    /// </summary>
    internal static class ProjectLaunchIdentity
    {
        /// <summary>The shared placeholder name. Exempt from uniqueness inside the broker.</summary>
        internal const string Unassigned = "Unassigned";

        /// <summary>
        /// Cap on the sanitized name, leaving room for the id qualifier and a <c>-N</c> suffix well inside
        /// <c>ConPtyTerminal.MaxSessionNameLength</c> (which skips <c>-n</c> rather than truncate).
        /// </summary>
        internal const int MaxBaseLength = 64;

        internal const string FallbackPrefix = "Project";
        internal const int FallbackIdChars = 6;
        internal const int QualifierIdChars = 4;

        /// <summary>
        /// The one identity alphabet: ASCII letters, digits, <c>-</c> and <c>_</c>. Shared with
        /// <c>TerminalDocument.IsSafeStatusLineSegment</c>, so every file named after an identity
        /// (inbox <c>&lt;name&gt;.json</c>, <c>mt-statusline-&lt;name&gt;-&lt;docId&gt;.json</c>) is a plain
        /// segment and the statusline glob fallback accepts it.
        /// </summary>
        internal static bool IsIdentityChar(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';

        /// <summary>
        /// Decides the requested identity for a project launch. See the class doc for the rule.
        /// </summary>
        /// <param name="teamLead">The project's team lead, or null/empty for none.</param>
        /// <param name="kind">The terminal kind being launched.</param>
        /// <param name="codexDefaultAgentName">The per-user Codex default agent setting, if any.</param>
        /// <param name="projectName">The project's display name.</param>
        /// <param name="projectId">The project's id; source of the fallback and qualifier.</param>
        /// <param name="reserved">Names this project must not take (see <see cref="ReservedNames"/>).</param>
        internal static ProjectLaunchIdentityRequest Resolve(
            string teamLead,
            TerminalKind kind,
            string codexDefaultAgentName,
            string projectName,
            string projectId,
            IReadOnlyCollection<string> reserved)
        {
            if (!string.IsNullOrEmpty(teamLead))
                return new ProjectLaunchIdentityRequest(teamLead, RegisterUnique: false, IsProjectDerived: false);

            if (!NeedsProjectDerivedIdentity(teamLead, kind, codexDefaultAgentName))
                return new ProjectLaunchIdentityRequest(codexDefaultAgentName, RegisterUnique: true, IsProjectDerived: false);

            // No census, no project-derived name: a null set must never read as "nothing reserved".
            if (reserved == null)
                throw new ArgumentNullException(nameof(reserved), "A project-derived identity needs the reserved-name census (6a8d029f).");

            string name = FromProjectName(projectName) ?? FallbackBase(projectId);
            if (Contains(reserved, name))
            {
                string qualifier = IdPrefix(projectId, QualifierIdChars);
                if (qualifier.Length > 0) name = $"{name}-{qualifier}";
            }

            return new ProjectLaunchIdentityRequest(name, RegisterUnique: true, IsProjectDerived: true);
        }

        /// <summary>
        /// True when this launch will take a project-derived identity, and therefore needs the
        /// reserved-name census: no team lead, and not a Codex launch with a configured default agent
        /// ("Unassigned" as that setting used to mean "behave like Claude Code", so it falls through).
        /// Team-lead and Codex-default launches never wait on, or fail because of, the census.
        /// </summary>
        internal static bool NeedsProjectDerivedIdentity(string teamLead, TerminalKind kind, string codexDefaultAgentName) =>
            string.IsNullOrEmpty(teamLead)
            && !(kind == TerminalKind.Codex
                 && !string.IsNullOrWhiteSpace(codexDefaultAgentName)
                 && !IsUnassigned(codexDefaultAgentName));

        /// <summary>
        /// Takes the reserved-name census from the two project-database queries, ALL-OR-NOTHING: a missing
        /// database (null delegate), a failing project query, a null result, or ANY failing roster query
        /// is a failure with no names at all, never a partial set. Pure apart from the delegates, so the
        /// decision is testable without WinForms; <c>MainForm</c> fails the launch closed on failure.
        /// </summary>
        /// <param name="thisProjectId">The project being launched (its own name is not reserved).</param>
        /// <param name="listProjects">Every registered project; null when there is no database.</param>
        /// <param name="listAgentNames">A project's roster agent names; null when there is no database.</param>
        /// <param name="extraNames">Fixed names (Oracle, the Codex default agent).</param>
        internal static ReservedNamesCensus TryTakeCensus(
            string thisProjectId,
            Func<IEnumerable<(string Id, string Name, string TeamLead)>> listProjects,
            Func<string, IEnumerable<string>> listAgentNames,
            IEnumerable<string> extraNames)
        {
            if (listProjects == null || listAgentNames == null)
                return new ReservedNamesCensus(null, "the project database is not available");

            var sources = new List<ProjectIdentitySource>();
            try
            {
                var projects = listProjects();
                if (projects == null)
                    return new ReservedNamesCensus(null, "the project list could not be read");

                foreach (var (id, name, teamLead) in projects)
                {
                    var agents = listAgentNames(id);
                    if (agents == null)
                        return new ReservedNamesCensus(null, $"the agent roster of project '{name}' could not be read");
                    sources.Add(new ProjectIdentitySource(id, name, teamLead, new List<string>(agents)));
                }
            }
            catch (Exception ex)
            {
                return new ReservedNamesCensus(null, ex.Message);
            }

            return new ReservedNamesCensus(ReservedNames(thisProjectId, sources, extraNames), null);
        }

        /// <summary>
        /// The reserved-name census for launching <paramref name="thisProjectId"/>: every project's team
        /// lead and roster agents, the sanitized name of every OTHER project, and
        /// <paramref name="extraNames"/> (Oracle, the Codex default agent). Case-insensitive.
        /// </summary>
        internal static HashSet<string> ReservedNames(string thisProjectId, IEnumerable<ProjectIdentitySource> projects, IEnumerable<string> extraNames)
        {
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string n)
            {
                if (!string.IsNullOrWhiteSpace(n)) reserved.Add(n);
            }

            foreach (var p in projects ?? Array.Empty<ProjectIdentitySource>())
            {
                Add(p.TeamLead);
                foreach (var agent in p.AgentNames ?? Array.Empty<string>()) Add(agent);
                if (!string.Equals(p.Id, thisProjectId, StringComparison.OrdinalIgnoreCase))
                    Add(FromProjectName(p.Name) ?? FallbackBase(p.Id));
            }

            foreach (var n in extraNames ?? Array.Empty<string>()) Add(n);
            return reserved;
        }

        /// <summary>
        /// Turns a project name into an identity base, or null when nothing usable is left.
        /// Keeps only <see cref="IsIdentityChar"/> characters; each run of anything else (and each run
        /// of <c>-</c>) becomes one <c>-</c>, trimmed from both ends, capped at <see cref="MaxBaseLength"/>.
        /// Turning spaces into <c>-</c> also means "Agent Smith" cannot produce the temporary-subagent
        /// shape "Agent …". Null for empty and for the placeholder itself.
        /// </summary>
        internal static string FromProjectName(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName)) return null;

            string name = Collapse(projectName);
            if (name.Length > MaxBaseLength) name = name.Substring(0, MaxBaseLength).TrimEnd('-');
            if (name.Length == 0 || IsUnassigned(name)) return null;
            return name;
        }

        /// <summary>
        /// True when <paramref name="agentName"/> is an identity a no-lead launch of
        /// <paramref name="projectName"/> produces from its NAME: the base itself, or the base with the
        /// broker's numeric <c>-N</c> suffix. Lets the tab read "TestB-2" instead of "TestB-2 - TestB".
        /// (An id-qualified or <c>Project-…</c> identity keeps the project in the tab, which is useful
        /// precisely because the name alone no longer says which project it is.)
        /// </summary>
        internal static bool IsProjectIdentity(string agentName, string projectName)
        {
            string projectBase = FromProjectName(projectName);
            if (projectBase == null || string.IsNullOrEmpty(agentName)) return false;
            if (agentName.Equals(projectBase, StringComparison.OrdinalIgnoreCase)) return true;
            if (agentName.Length <= projectBase.Length + 1
                || !agentName.StartsWith(projectBase + "-", StringComparison.OrdinalIgnoreCase))
                return false;

            for (int i = projectBase.Length + 1; i < agentName.Length; i++)
            {
                if (agentName[i] < '0' || agentName[i] > '9') return false;
            }

            return true;
        }

        private static string FallbackBase(string projectId)
        {
            string id = IdPrefix(projectId, FallbackIdChars);
            return id.Length > 0 ? $"{FallbackPrefix}-{id}" : FallbackPrefix;
        }

        private static string IdPrefix(string projectId, int length)
        {
            string id = Collapse(projectId ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal);
            return id.Length > length ? id.Substring(0, length) : id;
        }

        private static string Collapse(string value)
        {
            var sb = new StringBuilder(value.Length);
            bool pendingSeparator = false;
            foreach (char c in value)
            {
                if (!IsIdentityChar(c) || c == '-')
                {
                    pendingSeparator = true;
                    continue;
                }

                if (pendingSeparator && sb.Length > 0) sb.Append('-');
                pendingSeparator = false;
                sb.Append(c);
            }

            return sb.ToString();
        }

        private static bool Contains(IReadOnlyCollection<string> reserved, string name)
        {
            if (reserved == null) return false;
            foreach (var r in reserved)
            {
                if (string.Equals(r, name, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static bool IsUnassigned(string name) =>
            string.Equals(name, Unassigned, StringComparison.OrdinalIgnoreCase);
    }
}
