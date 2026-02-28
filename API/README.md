# MultiTerminal REST API Documentation

**Base URL:** `http://localhost:5050`

## Health Check

```bash
GET /
GET /health
```

## Messaging API

### Register Terminal
```bash
POST /api/messaging/register
Content-Type: application/json

{
  "name": "Alice",
  "docId": "abc123"
}

Response: { "terminalId": "xyz789" }
```

### Send Message
```bash
POST /api/messaging/send
Content-Type: application/json

{
  "fromTerminalId": "xyz789",
  "to": "Bob",
  "message": "Hello!"
}
```

### Broadcast Message
```bash
POST /api/messaging/broadcast
Content-Type: application/json

{
  "fromTerminalId": "xyz789",
  "message": "Hello everyone!"
}
```

### Get Messages
```bash
GET /api/messaging/messages/{terminalId}
```

### List Terminals
```bash
GET /api/messaging/terminals
```

## Tasks API

### List Tasks
```bash
GET /api/tasks?status=all
# status: all, todo, in_progress, done, suggestion
```

### Get Task
```bash
GET /api/tasks/{taskId}
```

### Create Task
```bash
POST /api/tasks
Content-Type: application/json

{
  "title": "Fix bug",
  "description": "Details here",
  "createdBy": "Alice",
  "status": "todo",
  "priority": "normal"
}
```

### Update Task Status
```bash
PATCH /api/tasks/{taskId}/status
Content-Type: application/json

{
  "status": "in_progress",
  "updatedBy": "Alice"
}
```

### Delete Task
```bash
DELETE /api/tasks/{taskId}?deletedBy=Alice
```

## Inbox API

### Get Inbox Messages
```bash
GET /api/tasks/inbox/{userId}?unreadOnly=false&limit=50
# unreadOnly: true/false (default: false)
# limit: max messages to return (default: 50)

Response:
{
  "success": true,
  "messages": [
    {
      "id": "abc12345",
      "userId": "john",
      "taskId": "xyz789",
      "taskTitle": "Add login feature",
      "checklistItemIndex": 2,
      "checklistItemName": "Add validation",
      "type": "ready_for_testing",
      "summary": "Diana finished 'Add validation' - ready for testing",
      "createdAt": "2026-02-10T10:30:00Z",
      "createdBy": "Diana",
      "readAt": null,
      "replyText": null,
      "repliedAt": null
    }
  ],
  "unreadCount": 5,
  "totalCount": 1
}
# type: ready_for_testing, escalation, task_complete, helper_request
```

### Mark Message as Read
```bash
POST /api/tasks/inbox/{messageId}/read

Response: { "success": true }
```

### Mark All Messages as Read
```bash
POST /api/tasks/inbox/{userId}/read-all

Response: { "success": true }
```

### Reply to Inbox Message
```bash
POST /api/tasks/inbox/{messageId}/reply
Content-Type: application/json

{
  "replyText": "Fixed the issue, ready for re-testing"
}

Response: { "success": true }
# Also auto-marks message as read if not already read
```

### Get Unread Count
```bash
GET /api/tasks/inbox/{userId}/unread-count

Response: { "count": 5 }
```

## Example Usage from Claude Code

I can now use these endpoints directly with the `Bash` tool:

```bash
# List tasks
curl http://localhost:5050/api/tasks

# Create a task
curl -X POST http://localhost:5050/api/tasks \
  -H "Content-Type: application/json" \
  -d '{"title":"Test","description":"Testing API","createdBy":"Claude","status":"todo"}'

# Send a message
curl -X POST http://localhost:5050/api/messaging/send \
  -H "Content-Type: application/json" \
  -d '{"fromTerminalId":"abc123","to":"Alice","message":"Hello!"}'

# Get inbox (unread only)
curl http://localhost:5050/api/tasks/inbox/john?unreadOnly=true

# Mark message as read
curl -X POST http://localhost:5050/api/tasks/inbox/abc12345/read

# Reply to inbox message
curl -X POST http://localhost:5050/api/tasks/inbox/abc12345/reply \
  -H "Content-Type: application/json" \
  -d '{"replyText":"Fixed, ready for retest"}'

# Get unread count
curl http://localhost:5050/api/tasks/inbox/john/unread-count
```

## Benefits of REST API

✅ **Simple** - No MCP protocol complexity
✅ **Debuggable** - Easy to test with curl
✅ **Reliable** - No session management issues
✅ **Fast** - Direct HTTP requests
✅ **Flexible** - Can be called from any HTTP client

## Migration Notes

- Old MCP tools are removed
- All functionality is preserved via REST endpoints
- No more "Session not found" errors!
- Server starts on same port (5050)
