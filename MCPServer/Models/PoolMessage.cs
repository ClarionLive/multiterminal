using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Pool message action types for multi-instance coordination.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PoolAction
    {
        /// <summary>Terminal is actively working on files.</summary>
        WORKING_ON,

        /// <summary>Terminal completed a task.</summary>
        COMPLETED,

        /// <summary>Terminal is blocked waiting on another.</summary>
        BLOCKED_BY,

        /// <summary>Terminal learned something shareable.</summary>
        LEARNED
    }

    /// <summary>
    /// Represents a Pool Block message for multi-instance coordination.
    /// Based on claude-cognitive's Pool Coordinator pattern.
    /// </summary>
    public class PoolMessage
    {
        /// <summary>
        /// Unique message identifier.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Terminal/instance identifier (e.g., "Alice", "Bob", "Diana").
        /// </summary>
        [JsonPropertyName("instance")]
        public string Instance { get; set; }

        /// <summary>
        /// The action type.
        /// </summary>
        [JsonPropertyName("action")]
        public PoolAction Action { get; set; }

        /// <summary>
        /// Human-readable topic/description of the work.
        /// </summary>
        [JsonPropertyName("topic")]
        public string Topic { get; set; }

        /// <summary>
        /// Optional detailed summary.
        /// </summary>
        [JsonPropertyName("summary")]
        public string Summary { get; set; }

        /// <summary>
        /// Files affected by this work (soft lock signal).
        /// </summary>
        [JsonPropertyName("affects")]
        public List<string> Affects { get; set; } = new List<string>();

        /// <summary>
        /// Tasks/instances this work blocks (downstream dependencies).
        /// </summary>
        [JsonPropertyName("blocks")]
        public List<string> Blocks { get; set; } = new List<string>();

        /// <summary>
        /// Tasks/instances this work is blocked by (upstream dependencies).
        /// </summary>
        [JsonPropertyName("blockedBy")]
        public List<string> BlockedBy { get; set; } = new List<string>();

        /// <summary>
        /// Tags for interest-based filtering/subscription.
        /// </summary>
        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// Unix timestamp when this message was created.
        /// </summary>
        [JsonPropertyName("ts")]
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>
        /// Serialize to JSON for JSONL storage.
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        /// <summary>
        /// Parse from JSON line.
        /// </summary>
        public static PoolMessage FromJson(string json)
        {
            return JsonSerializer.Deserialize<PoolMessage>(json);
        }
    }

    /// <summary>
    /// Factory methods for creating Pool Messages.
    /// </summary>
    public static class PoolMessageFactory
    {
        /// <summary>
        /// Create a WORKING_ON message - signals active work on files.
        /// </summary>
        public static PoolMessage WorkingOn(string instance, string topic, params string[] affects)
        {
            return new PoolMessage
            {
                Instance = instance,
                Action = PoolAction.WORKING_ON,
                Topic = topic,
                Affects = new List<string>(affects)
            };
        }

        /// <summary>
        /// Create a COMPLETED message - signals task completion.
        /// </summary>
        public static PoolMessage Completed(string instance, string topic, string summary = null, params string[] affects)
        {
            return new PoolMessage
            {
                Instance = instance,
                Action = PoolAction.COMPLETED,
                Topic = topic,
                Summary = summary,
                Affects = new List<string>(affects)
            };
        }

        /// <summary>
        /// Create a BLOCKED_BY message - signals waiting on another instance.
        /// </summary>
        public static PoolMessage BlockedBy(string instance, string topic, string waitingOn, string reason = null)
        {
            return new PoolMessage
            {
                Instance = instance,
                Action = PoolAction.BLOCKED_BY,
                Topic = topic,
                Summary = reason,
                BlockedBy = new List<string> { waitingOn }
            };
        }

        /// <summary>
        /// Create a LEARNED message - shareable discovery with tags.
        /// </summary>
        public static PoolMessage Learned(string instance, string topic, string summary, params string[] tags)
        {
            return new PoolMessage
            {
                Instance = instance,
                Action = PoolAction.LEARNED,
                Topic = topic,
                Summary = summary,
                Tags = new List<string>(tags)
            };
        }
    }
}
