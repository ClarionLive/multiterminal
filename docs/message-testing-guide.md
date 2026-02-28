# Message Testing & Debugging Guide

**Created:** 2026-02-06
**Purpose:** Debug stuck messages in inter-terminal communication

## The Problem

When multiple Claudes are chatting, messages can get stuck and it's hard to identify which specific message is causing the issue because:
1. Messages don't have visible IDs in the UI
2. Messages send too fast to isolate problems
3. No way to tell teammates "don't respond during testing"

## The Solution

Three new debugging capabilities:

### 1. **Message ID Logging** ✅
Every sent message now logs its ID prominently:
```
[MessageBroker] MESSAGE SENT → [MSG-ID: 12345] from [Alice] to [Bob] | Content: Test message...
```

### 2. **Controlled Test Mode** ✅
New MCP tool: `send_test_messages` sends messages one-at-a-time with pauses:
- Broadcasts "TESTING IN PROGRESS - DO NOT RESPOND"
- Sends messages sequentially with configurable pauses
- Logs each message ID prominently
- Broadcasts "TESTING COMPLETE" when done

### 3. **Message ID in Received Messages** ✅
`get_messages` now returns `MessageId` field:
```json
{
  "MessageId": "12345",
  "From": "Alice",
  "Content": "Test message",
  ...
}
```

## How to Use This System

### Step 1: Start Test Mode

From any terminal, use the new MCP tool:

```javascript
// Load the tool first
mcp__multiterminal__send_test_messages(
  from_terminal_id: "your-terminal-id",
  messages_json: '[{"to":"Alice","content":"Test 1"},{"to":"Bob","content":"Test 2"}]',
  pause_seconds: 3
)
```

**What happens:**
1. All terminals receive: `[SYSTEM: MESSAGE TESTING IN PROGRESS - DO NOT RESPOND]`
2. Messages send one-at-a-time with 3-second pauses
3. Each message ID is logged: `[MSG-ID: 12345]`
4. All terminals receive: `[SYSTEM: MESSAGE TESTING COMPLETE]`

### Step 2: When a Message Gets Stuck

If you see a message stuck in a terminal's input:

1. **Copy the message ID** from the received message (it's now visible!)
2. **Alert the tester:** "Message ID 12345 is stuck in my prompt"

### Step 3: Find the Problem in Debug Logs

Use the debug search tool to find that specific message:

```javascript
mcp__multiterminal__search_debug_messages(
  keyword: "12345",
  since_seconds: 300
)
```

This returns all log entries mentioning that message ID:
- When it was sent
- When it was delivered (or failed)
- Any errors that occurred

### Step 4: Analyze the Flow

Look for these key log entries:

```
✅ Message created:
[MessageBroker] MESSAGE SENT → [MSG-ID: 12345] ...

✅ Push delivery attempted:
[MessageBroker] Calling OnMessageDelivery for message 12345 ...

❓ Delivery success/failure:
[MessageBroker] Message 12345 marked as delivered
[MessageBroker] Message 12345 marked as failed
```

## Testing Workflow Example

**Alice (Tester):**
```javascript
// Start controlled test
send_test_messages(
  from_terminal_id: "alice-123",
  messages_json: '[
    {"to":"Charlie","content":"Test message 1"},
    {"to":"Diana","content":"Test message 2"},
    {"to":"Bob","content":"Test message 3"}
  ]',
  pause_seconds: 3
)
```

**Charlie (Stuck Message):**
```
[Charlie's prompt]: Test message 1 appears and gets stuck
Charlie reports: "MSG-ID 78910 is stuck"
```

**Alice (Debug):**
```javascript
// Search for that specific message
search_debug_messages(keyword: "78910")

// Returns:
// [MessageBroker] MESSAGE SENT → [MSG-ID: 78910] from [Alice] to [Charlie]
// [MessageBroker] Calling OnMessageDelivery for message 78910
// [MessageBroker] Message 78910 marked as delivered  ← SUCCESS!
```

**Conclusion:** Message was delivered successfully by the broker. Problem is likely in Charlie's terminal UI processing, not the messaging infrastructure.

## Benefits

### Before This System:
- ❌ Can't identify which message is stuck
- ❌ Messages fly too fast to isolate problems
- ❌ No way to search logs for specific messages
- ❌ Manual coordination required ("everyone stop sending!")

### After This System:
- ✅ Every message has a visible, searchable ID
- ✅ Controlled one-at-a-time sending
- ✅ Automated "don't respond" coordination
- ✅ Search logs by message ID instantly
- ✅ Pinpoint exact failure point in message flow

## Pro Tips

1. **Use longer pauses for visual inspection** - Set `pause_seconds: 5` to manually watch each message deliver
2. **Keep test messages short** - Easier to identify: "Test 1", "Test 2", "Test 3"
3. **Test one recipient at a time first** - Isolate per-terminal issues before multi-recipient tests
4. **Save the message IDs** - Keep a log of which IDs were sent to which terminals for reporting
5. **Check debug stats first** - Run `get_debug_stats()` to see if there are errors before testing

## Technical Details

### Message ID Format
- SQLite-persisted messages: Integer ID (e.g., "12345")
- In-memory-only messages: GUID without dashes (e.g., "a1b2c3d4...")

### Debug Log Locations
- **Console Output:** Visual Studio Output window (Debug category)
- **DebugLogService:** In-memory ring buffer (searchable via MCP tools)
- **SQLite:** Message queue status in `message_queue.db`

### Key Components Modified

1. **MessageBroker.cs:374** - Added prominent message ID logging
2. **MessagingTools.cs:236** - Added `send_test_messages` tool
3. **MessagingTools.cs:131** - Added `MessageId` to `ReceivedMessage`

## Future Enhancements

Potential improvements for this system:

1. **Message tracing dashboard** - Real-time UI showing message flow
2. **Delivery confirmation** - Explicit ACK from recipient terminals
3. **Message replay** - Resend stuck messages by ID
4. **Timeout detection** - Auto-flag messages that don't deliver within X seconds
5. **Message filtering** - Ignore system messages, broadcasts, etc. during testing

## Related Documentation

- [Chat Panel Documentation](../ChatPanel/README.md)
- [Debug Logging Guide](./debug-logging-guide.md)
- [MCP Tools Reference](./mcp-tools-reference.md)

---

**Remember:** This system is designed for debugging. Use normal `send_message` for regular inter-terminal communication!
