using System.Text.Json;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Typed view over the Cloudflare Worker's response JSON for one of the 5 permission
    /// relay request types. Each accessor returns null if the field is absent or the wrong
    /// JSON kind, so callers can ignore parse errors without try/catch noise.
    ///
    /// Shape by request_type:
    ///   tool_permission → AsDecision()                (legacy; row.status carries it too)
    ///   elicitation     → AsText()
    ///   choice          → AsValue()
    ///   plan_approval   → AsDecision() + AsComment()
    ///   notification    → (no response — never polled)
    /// </summary>
    public readonly struct PermissionResponse
    {
        public JsonElement Raw { get; }

        public PermissionResponse(JsonElement raw) { Raw = raw; }

        public string AsDecision() => ReadString("decision");
        public string AsText() => ReadString("text");
        public string AsValue() => ReadString("value");
        public string AsComment() => ReadString("comment");

        private string ReadString(string prop)
        {
            if (Raw.ValueKind != JsonValueKind.Object) return null;
            if (!Raw.TryGetProperty(prop, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
    }

    /// <summary>
    /// Result of a plan_approval round-trip via the permission relay. Decision is the
    /// wire value ("approved" or "revise" per Worker contract); Comment is the optional
    /// revise comment (null for a straight approval).
    /// </summary>
    public sealed record PlanApprovalResult(string Decision, string Comment);

    /// <summary>
    /// Result of an ask_owner round-trip via the permission relay.
    ///
    /// Source values (see constants below) tell the caller which branch fired:
    ///   "owner"   — owner answered on the phone; Answer is the chosen value
    ///   "default" — no answer within timeout, caller-provided default was used; Answer is the default
    ///   "timeout" — no answer within timeout and no default was provided; Answer is null
    ///   "local"   — remote mode is OFF; relay was skipped entirely; Answer is null (caller should ask locally)
    /// </summary>
    public sealed record AskOwnerResult(string Answer, string Source)
    {
        public const string SourceOwner = "owner";
        public const string SourceDefault = "default";
        public const string SourceTimeout = "timeout";
        public const string SourceLocal = "local";
    }
}
