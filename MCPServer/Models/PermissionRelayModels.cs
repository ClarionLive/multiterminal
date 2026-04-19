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
}
