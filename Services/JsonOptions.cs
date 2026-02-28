using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Shared JsonSerializerOptions instances for consistent Unicode-safe serialization across the app.
    /// Uses UnsafeRelaxedJsonEscaping to preserve international characters (e.g. Dutch, emoji)
    /// instead of escaping them as \uXXXX sequences.
    /// </summary>
    public static class JsonOptions
    {
        /// <summary>
        /// Unicode-safe options: preserves non-ASCII characters as-is.
        /// Use for all WebView PostMessage serialization.
        /// </summary>
        public static readonly JsonSerializerOptions Unicode = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// Unicode-safe options with camelCase property naming.
        /// Use where camelCase is required (e.g. checklist JSON stored in DB).
        /// </summary>
        public static readonly JsonSerializerOptions UnicodeCamelCase = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Unicode-safe options for JSONL file writes (no indentation, skip nulls).
        /// </summary>
        public static readonly JsonSerializerOptions UnicodeCompact = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Unicode-safe options with indentation (for human-readable file output).
        /// </summary>
        public static readonly JsonSerializerOptions UnicodeIndented = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };
    }
}
