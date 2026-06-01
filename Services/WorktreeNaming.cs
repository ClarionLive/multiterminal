using System;
using System.Security.Cryptography;
using System.Text;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Single source of truth for per-agent worktree branch + directory names
    /// (task bab81a92 / design ff1dc68f). Centralizing construction here keeps
    /// the naming scheme — and its one load-bearing invariant — in one place
    /// across <see cref="WorktreeManager"/>, the merge service, the janitor, and
    /// the listing/HUD code.
    ///
    /// <para><b>Topology (assignee-canonical):</b> the task's assignee holds the
    /// canonical worktree on branch <c>task/&lt;idShort&gt;</c> at
    /// <c>.claude/worktrees/&lt;idShort&gt;</c> — byte-identical to the pre-isolation
    /// layout. Each helper gets their own worktree on branch
    /// <c>task/&lt;idShort&gt;--&lt;slug&gt;</c> at
    /// <c>.claude/worktrees/&lt;idShort&gt;--&lt;slug&gt;</c>.</para>
    ///
    /// <para><b>Why the <c>--</c> separator and NOT <c>task/&lt;id&gt;/&lt;agent&gt;</c>:</b>
    /// git stores refs as files, so the ref <c>task/&lt;id&gt;</c> (a file) and
    /// <c>task/&lt;id&gt;/&lt;agent&gt;</c> (which would require <c>task/&lt;id&gt;/</c>
    /// to be a directory) cannot coexist — a directory/file conflict. The double-dash
    /// keeps the canonical ref intact and collision-free. <c>idShort</c> is hex and
    /// the slug never contains <c>--</c> (repeats are collapsed), so the full name
    /// contains exactly one <c>--</c> and parses unambiguously.</para>
    /// </summary>
    public static class WorktreeNaming
    {
        /// <summary>Separator between the short task id and the agent slug.</summary>
        public const string Separator = "--";

        /// <summary>Backfill sentinel for legacy rows whose owning agent is unknown.</summary>
        public const string LegacyAgent = "__legacy__";

        /// <summary>
        /// The first 8 characters of the task id (or the whole id if shorter) —
        /// the stable short key that drives every worktree path and branch name.
        /// </summary>
        public static string ShortId(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return taskId;
            return taskId.Length >= 8 ? taskId.Substring(0, 8) : taskId;
        }

        /// <summary>
        /// Deterministic, filesystem- and git-ref-safe slug for an agent name.
        /// Lowercased; any char outside <c>[a-z0-9-_]</c> becomes <c>-</c>; runs of
        /// <c>-</c> are collapsed; leading/trailing <c>-</c> and <c>.</c> are trimmed.
        ///
        /// <para><b>Collision safety:</b> when sanitization changes the name (so two
        /// distinct names could collapse to the same slug), yields an empty result, or
        /// exceeds the length cap, a stable 64-bit hash of the ORIGINAL name is
        /// appended — making a deliberate collision between two distinct agent names
        /// cryptographically negligible (the agent name is attacker-influenced, so the
        /// suffix entropy must be large). The slug is also length-capped to keep
        /// worktree paths shallow on Windows. The result is pure and stable across
        /// processes (SHA-256 based), so it is safe to freeze on the worktree row at
        /// creation time. (Callers should still treat <c>(task_id, agent_name)</c> as
        /// the authoritative identity; the slug is a derived path/ref label.)</para>
        /// </summary>
        public static string Slug(string agentName)
        {
            if (string.IsNullOrWhiteSpace(agentName))
            {
                return "agent-" + ShortHash(agentName ?? string.Empty);
            }

            // CA1308: lowercase is intentional — git refs and (case-insensitive)
            // worktree dir names are canonicalized to lowercase here, not for
            // round-trippable display. ToUpperInvariant would defeat the purpose.
#pragma warning disable CA1308
            string lower = agentName.ToLowerInvariant();
#pragma warning restore CA1308

            var sb = new StringBuilder(lower.Length);
            char prev = '\0';
            foreach (char c in lower)
            {
                char mapped = ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_') ? c : '-';
                // Collapse runs of '-' as we build.
                if (mapped == '-' && prev == '-') continue;
                sb.Append(mapped);
                prev = mapped;
            }

            string trimmed = sb.ToString().Trim('-', '.');

            // Cap the readable base so worktree dir + branch names stay short.
            const int maxBase = 32;

            // Lossless = sanitization preserved the name exactly. If anything was
            // lost (empty or changed), append a stable 64-bit hash of the original so
            // distinct names cannot collide on the same slug.
            bool lossless = string.Equals(trimmed, lower, StringComparison.Ordinal);
            if (string.IsNullOrEmpty(trimmed) || !lossless)
            {
                string baseSlug = string.IsNullOrEmpty(trimmed) ? "agent" : trimmed;
                if (baseSlug.Length > maxBase) baseSlug = baseSlug.Substring(0, maxBase).Trim('-', '.');
                return baseSlug + "-" + ShortHash(agentName);
            }

            // Lossless but over the cap: truncating would lose information (and could
            // alias two long names sharing a prefix), so fall through to the hashed form.
            if (trimmed.Length > maxBase)
            {
                return trimmed.Substring(0, maxBase).Trim('-', '.') + "-" + ShortHash(agentName);
            }

            return trimmed;
        }

        /// <summary>Canonical (assignee) branch: <c>task/&lt;idShort&gt;</c>.</summary>
        public static string CanonicalBranch(string taskId) => $"task/{ShortId(taskId)}";

        /// <summary>Helper branch: <c>task/&lt;idShort&gt;--&lt;slug&gt;</c>.</summary>
        public static string HelperBranch(string taskId, string agentName) =>
            $"task/{ShortId(taskId)}{Separator}{Slug(agentName)}";

        /// <summary>
        /// Branch name for an agent on a task, choosing canonical vs helper form.
        /// </summary>
        public static string BranchFor(string taskId, string agentName, bool isCanonical) =>
            isCanonical ? CanonicalBranch(taskId) : HelperBranch(taskId, agentName);

        /// <summary>Canonical (assignee) worktree dir name: <c>&lt;idShort&gt;</c>.</summary>
        public static string CanonicalDirName(string taskId) => ShortId(taskId);

        /// <summary>Helper worktree dir name: <c>&lt;idShort&gt;--&lt;slug&gt;</c>.</summary>
        public static string HelperDirName(string taskId, string agentName) =>
            $"{ShortId(taskId)}{Separator}{Slug(agentName)}";

        /// <summary>
        /// Worktree dir name for an agent on a task, choosing canonical vs helper form.
        /// Combine with the <c>.claude/worktrees</c> parent to get the full path.
        /// </summary>
        public static string DirNameFor(string taskId, string agentName, bool isCanonical) =>
            isCanonical ? CanonicalDirName(taskId) : HelperDirName(taskId, agentName);

        /// <summary>
        /// First 2 bytes of SHA-256(<paramref name="s"/>) as 4 lowercase hex chars.
        /// Stable across processes (unlike <c>string.GetHashCode</c>), so slugs are
        /// reproducible and safe to persist.
        /// </summary>
        private static string ShortHash(string s)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(s));
            // 8 bytes = 64 bits. A short 16-bit suffix was brute-forceable when the
            // agent name is attacker-influenced (it becomes a git ref + dir name); at
            // 64 bits a deliberate collision is cryptographically negligible.
            return $"{hash[0]:x2}{hash[1]:x2}{hash[2]:x2}{hash[3]:x2}{hash[4]:x2}{hash[5]:x2}{hash[6]:x2}{hash[7]:x2}";
        }
    }
}
