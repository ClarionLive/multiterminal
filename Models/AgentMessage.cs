using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Types of messages received from a headless Claude Code process via stream-json output.
    /// </summary>
    public enum AgentMessageType
    {
        /// <summary>System-level messages (initialization, configuration).</summary>
        System,
        /// <summary>Assistant text responses.</summary>
        Assistant,
        /// <summary>User messages (echoed back).</summary>
        User,
        /// <summary>Tool invocation with name and input.</summary>
        ToolUse,
        /// <summary>Tool execution result.</summary>
        ToolResult,
        /// <summary>Extended thinking content (thinking_delta).</summary>
        Thinking,
        /// <summary>Streaming text delta (text_delta).</summary>
        StreamDelta,
        /// <summary>Error messages from the process.</summary>
        Error,
        /// <summary>Final result message indicating conversation turn is complete.</summary>
        Result
    }

    /// <summary>
    /// Represents a single parsed message from a headless Claude Code process's NDJSON output stream.
    /// Claude Code's --output-format stream-json produces the Anthropic Messages API streaming format:
    ///   - type:"message" objects with content arrays (text, thinking, tool_use blocks)
    ///   - type:"content_block_delta" with streaming deltas (text_delta, thinking_delta)
    ///   - type:"result" for turn completion with cost/token info
    ///   - role:"user" objects for echoed user messages and tool results
    ///   - Noise types to filter: signature_delta, input_json_delta, rate_limit_event, etc.
    /// </summary>
    public class AgentMessage
    {
        public DateTime Timestamp { get; set; }
        public AgentMessageType Type { get; set; }
        public string Content { get; set; }
        public string ToolName { get; set; }
        public string SessionId { get; set; }
        public string RawJson { get; set; }

        /// <summary>
        /// Parse a single NDJSON line from Claude Code's stream-json output.
        /// Returns a list of AgentMessages (may be empty for filtered noise, or multiple for message objects
        /// containing several content blocks).
        /// </summary>
        public static List<AgentMessage> ParseLine(string jsonLine)
        {
            var results = new List<AgentMessage>();

            if (string.IsNullOrWhiteSpace(jsonLine))
                return results;

            try
            {
                using var doc = JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;

                // Extract session_id if present at top level or nested
                string sessionId = ExtractSessionId(root);

                // Check for "type" field
                if (root.TryGetProperty("type", out var typeProp))
                {
                    string typeStr = typeProp.GetString();

                    switch (typeStr)
                    {
                        // === Full message objects (from --include-partial-messages) ===
                        case "message":
                            ParseMessageObject(root, sessionId, jsonLine, results);
                            break;

                        // === Streaming content deltas ===
                        case "content_block_delta":
                            ParseContentBlockDelta(root, sessionId, jsonLine, results);
                            break;

                        // === Turn completion ===
                        case "result":
                            // Result objects have cost/token info at top level, not nested
                            // Serialize the whole object so JS can extract cost fields
                            results.Add(new AgentMessage
                            {
                                Timestamp = DateTime.UtcNow,
                                Type = AgentMessageType.Result,
                                Content = jsonLine,
                                SessionId = sessionId,
                                RawJson = jsonLine
                            });
                            break;

                        // === Error ===
                        case "error":
                            results.Add(new AgentMessage
                            {
                                Timestamp = DateTime.UtcNow,
                                Type = AgentMessageType.Error,
                                Content = ExtractContent(root, "error") ?? ExtractContent(root, "message"),
                                SessionId = sessionId,
                                RawJson = jsonLine
                            });
                            break;

                        // === System/init messages ===
                        case "system":
                            results.Add(new AgentMessage
                            {
                                Timestamp = DateTime.UtcNow,
                                Type = AgentMessageType.System,
                                Content = ExtractContent(root, "message"),
                                SessionId = sessionId,
                                RawJson = jsonLine
                            });
                            break;

                        // === Native team transcript format ===
                        // Claude Code transcripts use type:"assistant"/"user" with a nested
                        // "message" object containing role + content block array.
                        // Delegate to ParseMessageObject when the nested format is detected.
                        case "assistant":
                            if (root.TryGetProperty("message", out var asstMsgObj)
                                && asstMsgObj.ValueKind == JsonValueKind.Object
                                && asstMsgObj.TryGetProperty("content", out var asstContentArr)
                                && asstContentArr.ValueKind == JsonValueKind.Array)
                            {
                                ParseMessageObject(asstMsgObj, sessionId, jsonLine, results);
                            }
                            else
                            {
                                results.Add(new AgentMessage
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Type = AgentMessageType.Assistant,
                                    Content = ExtractContent(root, "message"),
                                    SessionId = sessionId,
                                    RawJson = jsonLine
                                });
                            }
                            break;

                        case "user":
                            if (root.TryGetProperty("message", out var userMsgObj)
                                && userMsgObj.ValueKind == JsonValueKind.Object
                                && userMsgObj.TryGetProperty("content", out var userContentArr)
                                && userContentArr.ValueKind == JsonValueKind.Array)
                            {
                                ParseMessageObject(userMsgObj, sessionId, jsonLine, results);
                            }
                            else if (root.TryGetProperty("message", out var userMsgObj2)
                                && userMsgObj2.ValueKind == JsonValueKind.Object
                                && userMsgObj2.TryGetProperty("content", out var userContentStr)
                                && userContentStr.ValueKind == JsonValueKind.String)
                            {
                                results.Add(new AgentMessage
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Type = AgentMessageType.User,
                                    Content = userContentStr.GetString(),
                                    SessionId = sessionId,
                                    RawJson = jsonLine
                                });
                            }
                            else
                            {
                                results.Add(new AgentMessage
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Type = AgentMessageType.User,
                                    Content = ExtractContent(root, "message"),
                                    SessionId = sessionId,
                                    RawJson = jsonLine
                                });
                            }
                            break;

                        case "tool_use":
                            results.Add(ParseToolUse(root, sessionId, jsonLine));
                            break;

                        case "tool_result":
                            results.Add(ParseToolResult(root, sessionId, jsonLine));
                            break;

                        case "stream_event":
                            var seMsg = ParseStreamEvent(root, sessionId, jsonLine);
                            if (seMsg != null) results.Add(seMsg);
                            break;

                        // === Message lifecycle events (filter out - not useful for display) ===
                        case "message_start":
                        case "message_stop":
                        case "message_delta":
                        case "content_block_start":
                        case "content_block_stop":
                        case "ping":
                            // Lifecycle events - extract session_id but don't display
                            break;

                        // === Noise types (filter out completely) ===
                        case "progress":
                        case "signature_delta":
                        case "input_json_delta":
                        case "rate_limit_event":
                            break;

                        default:
                            // Unknown type - skip rather than dump raw JSON
                            break;
                    }
                }
                else
                {
                    // No "type" field - check for role-based messages (user echoes, tool results)
                    ParseRoleBasedMessage(root, sessionId, jsonLine, results);
                }

                // Propagate session_id to all results
                if (!string.IsNullOrEmpty(sessionId))
                {
                    foreach (var msg in results)
                    {
                        if (string.IsNullOrEmpty(msg.SessionId))
                            msg.SessionId = sessionId;
                    }
                }
            }
            catch (JsonException)
            {
                // Not valid JSON - treat as plain text error
                results.Add(new AgentMessage
                {
                    Timestamp = DateTime.UtcNow,
                    Type = AgentMessageType.Error,
                    Content = jsonLine,
                    RawJson = jsonLine
                });
            }

            return results;
        }

        /// <summary>
        /// Parse a type:"message" object containing a content array.
        /// Each content block becomes a separate AgentMessage.
        /// </summary>
        private static void ParseMessageObject(JsonElement root, string sessionId, string rawJson, List<AgentMessage> results)
        {
            if (!root.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array)
                return;

            // Check role to determine correct message type for text blocks
            string role = root.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : "assistant";
            var textMsgType = role == "user" ? AgentMessageType.User : AgentMessageType.Assistant;

            foreach (var block in contentArr.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var blockTypeProp))
                    continue;

                string blockType = blockTypeProp.GetString();

                switch (blockType)
                {
                    case "text":
                        string text = block.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                        if (!string.IsNullOrEmpty(text))
                        {
                            results.Add(new AgentMessage
                            {
                                Timestamp = DateTime.UtcNow,
                                Type = textMsgType,
                                Content = text,
                                SessionId = sessionId,
                                RawJson = rawJson
                            });
                        }
                        break;

                    case "thinking":
                        string thinking = block.TryGetProperty("thinking", out var thinkProp) ? thinkProp.GetString() : null;
                        if (!string.IsNullOrEmpty(thinking))
                        {
                            results.Add(new AgentMessage
                            {
                                Timestamp = DateTime.UtcNow,
                                Type = AgentMessageType.Thinking,
                                Content = thinking,
                                SessionId = sessionId,
                                RawJson = rawJson
                            });
                        }
                        break;

                    case "tool_use":
                        string toolName = block.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "Unknown";
                        string toolInput = block.TryGetProperty("input", out var inputProp) ? inputProp.ToString() : "";
                        results.Add(new AgentMessage
                        {
                            Timestamp = DateTime.UtcNow,
                            Type = AgentMessageType.ToolUse,
                            ToolName = toolName,
                            Content = toolInput,
                            SessionId = sessionId,
                            RawJson = rawJson
                        });
                        break;

                    case "tool_result":
                        string toolResultContent = null;
                        if (block.TryGetProperty("content", out var trContent))
                        {
                            toolResultContent = ExtractToolResultText(trContent);
                        }
                        string trToolName = block.TryGetProperty("name", out var trNameProp) ? trNameProp.GetString() : null;
                        results.Add(new AgentMessage
                        {
                            Timestamp = DateTime.UtcNow,
                            Type = AgentMessageType.ToolResult,
                            ToolName = trToolName,
                            Content = toolResultContent ?? "",
                            SessionId = sessionId,
                            RawJson = rawJson
                        });
                        break;

                    // Skip signature blocks, redacted thinking, etc.
                    default:
                        break;
                }
            }
        }

        /// <summary>
        /// Parse content_block_delta streaming events into the appropriate message type.
        /// </summary>
        private static void ParseContentBlockDelta(JsonElement root, string sessionId, string rawJson, List<AgentMessage> results)
        {
            if (!root.TryGetProperty("delta", out var deltaProp))
                return;

            string deltaType = deltaProp.TryGetProperty("type", out var dtProp) ? dtProp.GetString() : null;

            switch (deltaType)
            {
                case "text_delta":
                    string text = deltaProp.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                    if (!string.IsNullOrEmpty(text))
                    {
                        results.Add(new AgentMessage
                        {
                            Timestamp = DateTime.UtcNow,
                            Type = AgentMessageType.StreamDelta,
                            Content = text,
                            SessionId = sessionId,
                            RawJson = rawJson
                        });
                    }
                    break;

                case "thinking_delta":
                    string thinking = deltaProp.TryGetProperty("thinking", out var thinkProp) ? thinkProp.GetString() : null;
                    if (!string.IsNullOrEmpty(thinking))
                    {
                        results.Add(new AgentMessage
                        {
                            Timestamp = DateTime.UtcNow,
                            Type = AgentMessageType.Thinking,
                            Content = thinking,
                            SessionId = sessionId,
                            RawJson = rawJson
                        });
                    }
                    break;

                // input_json_delta (tool input streaming) - filter out, not useful for display
                case "input_json_delta":
                case "signature_delta":
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Parse messages without a "type" field but with a "role" field (user echoes, tool results).
        /// </summary>
        private static void ParseRoleBasedMessage(JsonElement root, string sessionId, string rawJson, List<AgentMessage> results)
        {
            if (!root.TryGetProperty("role", out var roleProp))
                return; // No role either - skip completely

            string role = roleProp.GetString();

            if (role == "user")
            {
                if (!root.TryGetProperty("content", out var contentProp))
                    return;

                if (contentProp.ValueKind == JsonValueKind.String)
                {
                    string contentStr = contentProp.GetString();

                    // Check if the string is actually a serialized content block array
                    if (contentStr != null && contentStr.TrimStart().StartsWith("["))
                    {
                        try
                        {
                            using var innerDoc = JsonDocument.Parse(contentStr);
                            if (innerDoc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                var tempResults = new List<AgentMessage>();
                                ParseMessageContentArray(innerDoc.RootElement, sessionId, rawJson, tempResults);
                                if (tempResults.Count > 0)
                                {
                                    // Reclassify text blocks as User type (they came from a user-role message)
                                    foreach (var tr in tempResults)
                                    {
                                        if (tr.Type == AgentMessageType.Assistant)
                                            tr.Type = AgentMessageType.User;
                                    }
                                    results.AddRange(tempResults);
                                    return;
                                }
                            }
                        }
                        catch (JsonException) { /* Not valid JSON array - treat as text */ }
                    }

                    // Simple user text message
                    results.Add(new AgentMessage
                    {
                        Timestamp = DateTime.UtcNow,
                        Type = AgentMessageType.User,
                        Content = contentStr,
                        SessionId = sessionId,
                        RawJson = rawJson
                    });
                }
                else if (contentProp.ValueKind == JsonValueKind.Array)
                {
                    // Content array - may contain text blocks and/or tool results
                    foreach (var item in contentProp.EnumerateArray())
                    {
                        if (!item.TryGetProperty("type", out var itemType))
                            continue;

                        string blockType = itemType.GetString();

                        if (blockType == "tool_result")
                        {
                            string toolResultContent = null;
                            if (item.TryGetProperty("content", out var trContent))
                            {
                                toolResultContent = ExtractToolResultText(trContent);
                            }
                            results.Add(new AgentMessage
                            {
                                Timestamp = DateTime.UtcNow,
                                Type = AgentMessageType.ToolResult,
                                Content = toolResultContent ?? "",
                                SessionId = sessionId,
                                RawJson = rawJson
                            });
                        }
                        else if (blockType == "text")
                        {
                            string text = item.TryGetProperty("text", out var textVal) ? textVal.GetString() : null;
                            if (!string.IsNullOrEmpty(text))
                            {
                                results.Add(new AgentMessage
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Type = AgentMessageType.User,
                                    Content = text,
                                    SessionId = sessionId,
                                    RawJson = rawJson
                                });
                            }
                        }
                    }
                }
            }
            else if (role == "assistant")
            {
                // Assistant messages echoed back (e.g., on resume)
                if (!root.TryGetProperty("content", out var contentProp))
                    return;

                if (contentProp.ValueKind == JsonValueKind.String)
                {
                    string contentStr = contentProp.GetString();

                    // Check if the string is actually a serialized content block array
                    // (e.g., "[{\"type\":\"tool_use\",...}]") — parse it as blocks if so
                    if (contentStr != null && contentStr.TrimStart().StartsWith("["))
                    {
                        try
                        {
                            using var innerDoc = JsonDocument.Parse(contentStr);
                            if (innerDoc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                var tempResults = new List<AgentMessage>();
                                ParseMessageContentArray(innerDoc.RootElement, sessionId, rawJson, tempResults);
                                if (tempResults.Count > 0)
                                {
                                    results.AddRange(tempResults);
                                    return; // Successfully parsed as content blocks
                                }
                            }
                        }
                        catch (JsonException) { /* Not valid JSON array - treat as text */ }
                    }

                    results.Add(new AgentMessage
                    {
                        Timestamp = DateTime.UtcNow,
                        Type = AgentMessageType.Assistant,
                        Content = contentStr,
                        SessionId = sessionId,
                        RawJson = rawJson
                    });
                }
                else if (contentProp.ValueKind == JsonValueKind.Array)
                {
                    // Parse content blocks same as ParseMessageObject
                    foreach (var block in contentProp.EnumerateArray())
                    {
                        if (!block.TryGetProperty("type", out var blockTypeProp))
                            continue;

                        string blockType = blockTypeProp.GetString();
                        switch (blockType)
                        {
                            case "text":
                                string text = block.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                                if (!string.IsNullOrEmpty(text))
                                {
                                    results.Add(new AgentMessage
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Type = AgentMessageType.Assistant,
                                        Content = text,
                                        SessionId = sessionId,
                                        RawJson = rawJson
                                    });
                                }
                                break;

                            case "tool_use":
                                string toolName = block.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "Unknown";
                                string toolInput = block.TryGetProperty("input", out var inputProp) ? inputProp.ToString() : "";
                                results.Add(new AgentMessage
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Type = AgentMessageType.ToolUse,
                                    ToolName = toolName,
                                    Content = toolInput,
                                    SessionId = sessionId,
                                    RawJson = rawJson
                                });
                                break;

                            case "tool_result":
                                string trContent2 = null;
                                if (block.TryGetProperty("content", out var trContent2Prop))
                                {
                                    trContent2 = ExtractToolResultText(trContent2Prop);
                                }
                                string trToolName2 = block.TryGetProperty("name", out var trNameProp2) ? trNameProp2.GetString() : null;
                                results.Add(new AgentMessage
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Type = AgentMessageType.ToolResult,
                                    ToolName = trToolName2,
                                    Content = trContent2 ?? "",
                                    SessionId = sessionId,
                                    RawJson = rawJson
                                });
                                break;

                            case "thinking":
                                string thinking2 = block.TryGetProperty("thinking", out var thinkProp2) ? thinkProp2.GetString() : null;
                                if (!string.IsNullOrEmpty(thinking2))
                                {
                                    results.Add(new AgentMessage
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Type = AgentMessageType.Thinking,
                                        Content = thinking2,
                                        SessionId = sessionId,
                                        RawJson = rawJson
                                    });
                                }
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Parse a top-level tool_use object (legacy format).
        /// </summary>
        private static AgentMessage ParseToolUse(JsonElement root, string sessionId, string rawJson)
        {
            var msg = new AgentMessage
            {
                Timestamp = DateTime.UtcNow,
                Type = AgentMessageType.ToolUse,
                SessionId = sessionId,
                RawJson = rawJson
            };

            if (root.TryGetProperty("tool", out var toolProp))
                msg.ToolName = toolProp.GetString();
            else if (root.TryGetProperty("name", out var nameProp))
                msg.ToolName = nameProp.GetString();

            if (root.TryGetProperty("input", out var inputProp))
                msg.Content = inputProp.ToString();

            return msg;
        }

        /// <summary>
        /// Parse a JSON content block array into AgentMessages.
        /// Handles text, tool_use, tool_result, and thinking blocks.
        /// Used by ParseMessageObject and as fallback for stringified content arrays.
        /// </summary>
        private static void ParseMessageContentArray(JsonElement contentArr, string sessionId, string rawJson, List<AgentMessage> results)
        {
            foreach (var block in contentArr.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var blockTypeProp))
                    continue;

                string blockType = blockTypeProp.GetString();

                switch (blockType)
                {
                    case "text":
                        string text = block.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                        if (!string.IsNullOrEmpty(text))
                        {
                            results.Add(new AgentMessage
                            {
                                Timestamp = DateTime.UtcNow,
                                Type = AgentMessageType.Assistant,
                                Content = text,
                                SessionId = sessionId,
                                RawJson = rawJson
                            });
                        }
                        break;

                    case "thinking":
                        string thinking = block.TryGetProperty("thinking", out var thinkProp) ? thinkProp.GetString() : null;
                        if (!string.IsNullOrEmpty(thinking))
                        {
                            results.Add(new AgentMessage
                            {
                                Timestamp = DateTime.UtcNow,
                                Type = AgentMessageType.Thinking,
                                Content = thinking,
                                SessionId = sessionId,
                                RawJson = rawJson
                            });
                        }
                        break;

                    case "tool_use":
                        string toolName = block.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "Unknown";
                        string toolInput = block.TryGetProperty("input", out var inputProp) ? inputProp.ToString() : "";
                        results.Add(new AgentMessage
                        {
                            Timestamp = DateTime.UtcNow,
                            Type = AgentMessageType.ToolUse,
                            ToolName = toolName,
                            Content = toolInput,
                            SessionId = sessionId,
                            RawJson = rawJson
                        });
                        break;

                    case "tool_result":
                        string toolResultContent = null;
                        if (block.TryGetProperty("content", out var trContent))
                        {
                            toolResultContent = ExtractToolResultText(trContent);
                        }
                        string trToolName = block.TryGetProperty("name", out var trNameProp) ? trNameProp.GetString() : null;
                        results.Add(new AgentMessage
                        {
                            Timestamp = DateTime.UtcNow,
                            Type = AgentMessageType.ToolResult,
                            ToolName = trToolName,
                            Content = toolResultContent ?? "",
                            SessionId = sessionId,
                            RawJson = rawJson
                        });
                        break;

                    default:
                        break;
                }
            }
        }

        /// <summary>
        /// Parse a top-level tool_result object (legacy format).
        /// </summary>
        private static AgentMessage ParseToolResult(JsonElement root, string sessionId, string rawJson)
        {
            var msg = new AgentMessage
            {
                Timestamp = DateTime.UtcNow,
                Type = AgentMessageType.ToolResult,
                SessionId = sessionId,
                RawJson = rawJson
            };

            if (root.TryGetProperty("tool", out var toolProp))
                msg.ToolName = toolProp.GetString();
            else if (root.TryGetProperty("name", out var nameProp))
                msg.ToolName = nameProp.GetString();

            msg.Content = ExtractContent(root, "output") ?? ExtractContent(root, "content");

            return msg;
        }

        /// <summary>
        /// Parse stream_event messages (legacy wrapper format).
        /// </summary>
        private static AgentMessage ParseStreamEvent(JsonElement root, string sessionId, string rawJson)
        {
            if (!root.TryGetProperty("event", out var eventProp))
                return null;

            if (!eventProp.TryGetProperty("delta", out var deltaProp))
                return null;

            string deltaType = deltaProp.TryGetProperty("type", out var dtProp) ? dtProp.GetString() : null;

            if (deltaType == "thinking_delta")
            {
                return new AgentMessage
                {
                    Timestamp = DateTime.UtcNow,
                    Type = AgentMessageType.Thinking,
                    Content = deltaProp.TryGetProperty("thinking", out var tp) ? tp.GetString() : "",
                    SessionId = sessionId,
                    RawJson = rawJson
                };
            }
            else if (deltaType == "text_delta")
            {
                return new AgentMessage
                {
                    Timestamp = DateTime.UtcNow,
                    Type = AgentMessageType.StreamDelta,
                    Content = deltaProp.TryGetProperty("text", out var tp) ? tp.GetString() : "",
                    SessionId = sessionId,
                    RawJson = rawJson
                };
            }

            return null;
        }

        /// <summary>
        /// Extract text from tool result content, which may be a string, array of text objects, or other JSON.
        /// </summary>
        private static string ExtractToolResultText(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString();

            if (content.ValueKind == JsonValueKind.Array)
            {
                var textParts = new List<string>();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        textParts.Add(item.GetString());
                    }
                    else if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("type", out var itemType)
                        && itemType.GetString() == "text"
                        && item.TryGetProperty("text", out var textVal))
                    {
                        textParts.Add(textVal.GetString());
                    }
                }
                if (textParts.Count > 0)
                    return string.Join("\n", textParts);
            }

            return content.ToString();
        }

        /// <summary>
        /// Extract session_id from various locations in the JSON.
        /// </summary>
        private static string ExtractSessionId(JsonElement root)
        {
            // Top-level session_id
            if (root.TryGetProperty("session_id", out var sidProp))
                return sidProp.GetString();

            // Nested in result object
            if (root.TryGetProperty("result", out var resultObj)
                && resultObj.ValueKind == JsonValueKind.Object
                && resultObj.TryGetProperty("session_id", out var resSidProp))
                return resSidProp.GetString();

            // From uuid field (rate_limit_event format)
            if (root.TryGetProperty("uuid", out var uuidProp))
                return uuidProp.GetString();

            return null;
        }

        /// <summary>
        /// Extract a string content field from a JSON element.
        /// Handles nested content arrays like [{"type":"text","text":"actual content"}],
        /// nested message objects like {"role":"user","content":"text"}, and plain strings.
        /// </summary>
        private static string ExtractContent(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop))
                return null;

            if (prop.ValueKind == JsonValueKind.String)
                return prop.GetString();

            // Unwrap content arrays: [{"type":"text","text":"..."}] → concatenated text
            if (prop.ValueKind == JsonValueKind.Array)
            {
                var textParts = new List<string>();
                foreach (var item in prop.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        textParts.Add(item.GetString());
                    }
                    else if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("type", out var itemType)
                        && itemType.GetString() == "text"
                        && item.TryGetProperty("text", out var textVal))
                    {
                        textParts.Add(textVal.GetString());
                    }
                }
                if (textParts.Count > 0)
                    return string.Join("\n", textParts);
            }

            // Unwrap nested message objects: {"role":"...","content":"text"} → extract content
            if (prop.ValueKind == JsonValueKind.Object)
            {
                if (prop.TryGetProperty("content", out var nestedContent))
                {
                    if (nestedContent.ValueKind == JsonValueKind.String)
                        return nestedContent.GetString();
                    if (nestedContent.ValueKind == JsonValueKind.Array)
                        return ExtractToolResultText(nestedContent);
                }
                // Try "text" field as fallback
                if (prop.TryGetProperty("text", out var nestedText) && nestedText.ValueKind == JsonValueKind.String)
                    return nestedText.GetString();
            }

            return prop.ToString();
        }

        /// <summary>
        /// Backward-compatible single-message parse. Returns the first message from ParseLine,
        /// or null if no displayable messages were found.
        /// </summary>
        public static AgentMessage Parse(string jsonLine)
        {
            var messages = ParseLine(jsonLine);
            return messages.Count > 0 ? messages[0] : null;
        }
    }
}
