using System;
using System.Text;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The identity a project launch REQUESTS, before the broker makes it unique (task 6a8d029f).
    /// <see cref="Unique"/> says whether the caller must register it through the atomic
    /// <c>MessageBroker.RegisterTerminalUnique</c>, which hands back the first free <c>-N</c> suffix.
    /// </summary>
    internal readonly record struct ProjectLaunchIdentityRequest(string Name, bool Unique);

    /// <summary>
    /// Which identity a terminal launched on a project gets (task 6a8d029f). Pure and static so the
    /// rule is testable without WinForms; every project launch site in <c>MainForm</c> asks here.
    ///
    /// <para>Owner decision 2026-10-02: a project with NO team lead launches under its PROJECT NAME,
    /// made unique by the broker ("TestB", then "TestB-2", "TestB-3"…) with no prompt. It used to be the
    /// shared "Unassigned" sentinel, so two panes of one project shared an active task, a worktree and an
    /// inbox file, and could not be messaged apart. Team-lead projects are unchanged (the lead's name, and
    /// a second launch still shows the IdentityPicker). "Open PowerShell" has no project and stays
    /// "Unassigned" — it never reaches this class.</para>
    /// </summary>
    internal static class ProjectLaunchIdentity
    {
        /// <summary>The shared placeholder name. Exempt from uniqueness inside the broker.</summary>
        internal const string Unassigned = "Unassigned";

        /// <summary>
        /// Cap on the base name, leaving room for a <c>-N</c> suffix well inside
        /// <c>ConPtyTerminal.MaxSessionNameLength</c> (which skips <c>-n</c> rather than truncate).
        /// </summary>
        internal const int MaxBaseLength = 64;

        /// <summary>
        /// Decides the requested identity for a project launch.
        /// Order: team lead → configured Codex default agent (Codex only) → project name → "Unassigned".
        /// </summary>
        /// <param name="teamLead">The project's team lead, or null/empty for none.</param>
        /// <param name="kind">The terminal kind being launched.</param>
        /// <param name="codexDefaultAgentName">The per-user Codex default agent setting, if any.</param>
        /// <param name="projectName">The project's display name.</param>
        internal static ProjectLaunchIdentityRequest Resolve(string teamLead, TerminalKind kind, string codexDefaultAgentName, string projectName)
        {
            // Team lead: the lead's own name, registered plainly. A held name is the IdentityPicker's job
            // at the call site, never a silent suffix.
            if (!string.IsNullOrEmpty(teamLead))
                return new ProjectLaunchIdentityRequest(teamLead, Unique: false);

            // An explicit Codex default is the user's chosen name for headless Codex launches and keeps
            // precedence over the project name. "Unassigned" as that setting used to mean "behave like
            // Claude Code", so it now falls through to the project name with everything else.
            if (kind == TerminalKind.Codex
                && !string.IsNullOrWhiteSpace(codexDefaultAgentName)
                && !IsUnassigned(codexDefaultAgentName))
            {
                return new ProjectLaunchIdentityRequest(codexDefaultAgentName, Unique: true);
            }

            string fromProject = FromProjectName(projectName);
            return fromProject != null
                ? new ProjectLaunchIdentityRequest(fromProject, Unique: true)
                : new ProjectLaunchIdentityRequest(Unassigned, Unique: false);
        }

        /// <summary>
        /// Turns a project name into an identity base, or null when nothing usable is left.
        ///
        /// <para>An identity is a file-name segment (<c>&lt;name&gt;.json</c> inbox fallback,
        /// <c>mt-statusline-&lt;name&gt;-&lt;docId&gt;.json</c>), an env var value and a <c>-n</c> session
        /// name. PowerShell quoting is already escaped at <c>ConPtyTerminal.StartProcess</c>, but a path
        /// separator or <c>:*?"&lt;&gt;|</c> would break the files. So the base keeps only the characters
        /// <c>TerminalDocument.IsSafeStatusLineSegment</c> accepts (ASCII letters, digits, <c>-</c>,
        /// <c>_</c>); each run of anything else becomes one <c>-</c>, and <c>-</c> is trimmed from both
        /// ends. "My Project" → "My-Project", "TestB" → "TestB". Turning spaces into <c>-</c> also means a
        /// project called "Agent Smith" cannot produce the temporary-subagent shape "Agent …".</para>
        ///
        /// <para>A project literally named "Unassigned" maps to null: that name is the shared sentinel
        /// the broker refuses to make unique, so claiming it would reproduce the sharing this replaces.
        /// The caller falls back to the sentinel, which is today's behaviour for that one name.</para>
        /// </summary>
        internal static string FromProjectName(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName)) return null;

            var sb = new StringBuilder(projectName.Length);
            bool pendingSeparator = false;
            foreach (char c in projectName)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok || c == '-')
                {
                    pendingSeparator = true;
                    continue;
                }

                if (pendingSeparator && sb.Length > 0) sb.Append('-');
                pendingSeparator = false;
                sb.Append(c);
            }

            string name = sb.ToString();
            if (name.Length > MaxBaseLength) name = name.Substring(0, MaxBaseLength).TrimEnd('-');
            if (name.Length == 0 || IsUnassigned(name)) return null;
            return name;
        }

        /// <summary>
        /// True when <paramref name="agentName"/> is the identity a no-lead launch of
        /// <paramref name="projectName"/> produces: the base itself, or the base with the broker's numeric
        /// <c>-N</c> suffix. Lets the tab read "TestB-2" instead of "TestB-2 - TestB" (GH #26 follow-on).
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

        private static bool IsUnassigned(string name) =>
            string.Equals(name, Unassigned, StringComparison.OrdinalIgnoreCase);
    }
}
