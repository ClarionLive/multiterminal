# Enter Retry Mechanism Testing - Diana's Test Suite

**Test Owner:** Diana
**Related Task:** 197c2418
**Implementation:** Task e798d0a9 (SendEnterWithRetryAsync)
**Test Focus:** Idle terminal baseline + Rapid-fire messaging stress test

---

## Overview

This document provides step-by-step instructions for manually testing the Enter keypress retry mechanism implemented in WebViewTerminalRenderer.cs. These tests verify that inter-terminal messages are delivered reliably on both idle and busy terminals without requiring manual Enter keypresses.

**Test Split:**
- **Diana's Tests (this document):** Idle terminal baseline + Rapid-fire messaging
- **Alice's Tests:** Busy terminal + Timeout/cancellation behavior

---

## Prerequisites

1. **Build MultiTerminal** - Ensure latest code is compiled with 0 warnings, 0 errors
2. **Open Debug Panel** - Click Debug button (🐛) in toolbar to monitor retry logs
3. **Enable System-Wide Capture** - In Debug Panel, click "System-Wide: OFF" to enable OutputDebugString capture
4. **Launch Multiple Terminals** - Use MT Launcher to start at least 2 team members (e.g., Diana + Alice)
5. **Verify Registration** - Check that both terminals show as registered in Chat panel dropdown

---

## Test 1: Idle Terminal Baseline

### Goal
Verify that Enter keypresses work correctly on idle terminals (should succeed on first attempt with no retries).

### Setup
1. Ensure target terminal (Alice) is **idle** at the prompt
   - No active tool execution
   - Not thinking/analyzing
   - Just waiting for input with blinking cursor
2. Open Debug Panel and clear existing logs (Clear button)
3. Ensure System-Wide capture is ON

### Execution Steps
1. From Diana's terminal, send a message to Alice:
   - **Option A:** Right-click Alice's terminal → "Assign" a task
   - **Option B:** Open Chat panel, select Alice, type "Test message 1", click Send
2. Immediately watch Debug Panel for log entries

### Expected Debug Panel Logs

You should see logs similar to this:

```
[WebViewTerminalRenderer] [HH:MM:SS.mmm] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after ~XXms! Enter was processed.
```

**Key indicators:**
- ✅ "SendEnterWithRetryAsync starting" appears
- ✅ "Attempt 1/8" (only one attempt)
- ✅ "✅ Output detected after ~XXms!" (success message)
- ✅ Total time: < 500ms
- ❌ No "Attempt 2/8" or higher (no retries needed)

### Expected Terminal Behavior
- Message text appears in Alice's input immediately
- Enter keypress auto-submits (no manual intervention)
- Alice processes the message and responds

### Success Criteria
- ✅ Message delivered without manual Enter
- ✅ Only 1 attempt needed (no retries)
- ✅ Success within 500ms
- ✅ Debug logs show "✅ Output detected"

### Failure Indicators
- ❌ Message stuck in input (need to manually press Enter)
- ❌ Multiple retry attempts (2/8, 3/8, etc.)
- ❌ "❌ All 8 attempts failed" in logs
- ❌ Timeout exceeded

---

## Test 2: Rapid-Fire Messaging Stress Test

### Goal
Verify the retry mechanism handles high-volume message delivery correctly, with proper queuing and no duplicates.

### Setup
1. Target terminal (Alice) can be **idle or busy** (doesn't matter for this test)
2. Clear Debug Panel logs
3. Ensure System-Wide capture is ON
4. Prepare a way to send multiple messages quickly

### Execution Steps

**Method A: Manual rapid sending (recommended)**
1. Open Chat panel
2. Select Alice as recipient
3. Type "Message 1" and click Send
4. Immediately type "Message 2" and click Send
5. Immediately type "Message 3" and click Send
6. Continue up to "Message 10"
7. Send as fast as possible (< 1 second between sends)

**Method B: Task assignment (alternate)**
1. Create 10 test tasks on Kanban board
2. Rapidly assign all 10 to Alice (click assign dropdown → Alice for each)
3. Send as fast as possible

### Expected Debug Panel Logs

You should see log patterns like this for **each message**:

```
[WebViewTerminalRenderer] [HH:MM:SS.mmm] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after ~XXms! Enter was processed.

[WebViewTerminalRenderer] [HH:MM:SS.mmm] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after ~XXms! Enter was processed.

... (repeated for each message)
```

**Key indicators:**
- ✅ Each message gets its own "SendEnterWithRetryAsync starting" entry
- ✅ Messages processed sequentially (not overlapping)
- ✅ All messages succeed with "✅ Output detected"
- ✅ No duplicate "SendEnterWithRetryAsync starting" for same message
- ✅ Retry attempts may vary (1/8, 2/8, etc.) depending on Alice's state

### Expected Terminal Behavior
- All 10 messages appear in Alice's terminal
- All 10 messages auto-submit (no manual Enter needed)
- Messages arrive in order sent (Message 1, 2, 3, ... 10)
- Alice processes each message in sequence

### Success Criteria
- ✅ All messages delivered without manual Enter
- ✅ No duplicate messages (Diana's MessageBroker fix prevents this)
- ✅ Messages arrive in correct order
- ✅ All messages show success in Debug Panel logs
- ✅ No crashes or hangs

### Failure Indicators
- ❌ One or more messages stuck in input
- ❌ Duplicate messages appearing in Alice's terminal
- ❌ Messages out of order
- ❌ "❌ All 8 attempts failed" for any message
- ❌ Crashes or exceptions in Debug Panel

---

## Additional Observations to Report

While testing, please note:

1. **Retry Patterns**
   - How many attempts were needed per message?
   - Did idle terminals consistently succeed on first attempt?
   - Were there any messages requiring 2+ retries?

2. **Performance**
   - What was the typical time to deliver a message?
   - Any noticeable delays or lag?
   - Did rapid-fire sending cause any UI freezing?

3. **Edge Cases**
   - What happens if you send 20+ messages instead of 10?
   - What happens if you send messages while Alice is actively typing?
   - Any unexpected behavior?

4. **Debug Panel Quality**
   - Are the log messages clear and helpful?
   - Too much or too little information?
   - Any confusing or misleading messages?

---

## Reporting Results

After completing tests, please report:

### For Each Test:
- **Test Name:** (Test 1 or Test 2)
- **Result:** PASS / FAIL
- **Observations:** What you saw
- **Debug Logs:** Copy relevant Debug Panel entries (or screenshot)
- **Issues:** Any failures or unexpected behavior

### Example Report Format:

```
Test 1: Idle Terminal Baseline
Result: PASS ✅
Observations: Message delivered instantly, no manual Enter needed
Debug Logs: Shows "Attempt 1/8" and "✅ Output detected after 127ms"
Issues: None

Test 2: Rapid-Fire Messaging
Result: PASS ✅
Observations: All 10 messages delivered correctly, in order
Debug Logs: All messages succeeded, 7 on first attempt, 3 required 2 attempts
Issues: None
```

---

## Troubleshooting

### If messages get stuck:
1. Check Debug Panel for "❌ All 8 attempts failed"
2. Note the timestamp and what Alice was doing at that moment
3. Try manually pressing Enter to verify input is present
4. Report exact failure scenario

### If no debug logs appear:
1. Verify System-Wide capture is ON in Debug Panel
2. Verify Debug Panel is visible (not minimized/hidden)
3. Try Clear button and re-run test
4. Report if logs never appear

### If crashes occur:
1. Note exact steps before crash
2. Check Windows Event Viewer for exception details
3. Report stack trace if available

---

## Next Steps

After Diana's tests are complete:
1. Alice will run her test suite (busy terminals + timeout/cancellation)
2. Team will review all results together
3. Fix any issues discovered
4. Re-test if needed
5. Mark task 197c2418 as DONE when 100% reliable

---

**Questions?** Message Diana, Bob, or Alice in the Chat panel!
