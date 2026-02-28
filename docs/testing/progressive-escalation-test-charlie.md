# Progressive Escalation Testing - Charlie's Test Suite

**Test Owner:** Charlie
**Related Task:** ff3c68cb
**Implementation:** WebViewTerminalRenderer.cs lines 686-805 (SendEnterWithRetryAsync with progressive escalation)
**Test Focus:** 4-part progressive escalation validation

---

## Overview

This document provides step-by-step instructions for manually testing the progressive escalation mechanism implemented in WebViewTerminalRenderer.cs. This mechanism fixes stuck messages by trying increasingly aggressive methods to submit Enter keypresses.

**Progressive Escalation Strategy:**
- **Attempts 1-2**: Method 1 (JS without focus) - handles 87% case, no focus stealing
- **Attempts 3-5**: Method 2 (focus + JS) - fallback for focus-dependent scenarios
- **Attempts 6-8**: Method 3 (SendInput API) - ultimate fallback for stubborn cases

**Success Criteria:**
- 100% message delivery across all test scenarios
- Zero stuck messages (especially on Alice's problematic terminal)
- Focus stealing only during Method 2/3 fallback (NOT proactive)
- Progressive escalation correctly triggers M1→M2→M3

---

## Prerequisites

1. **MultiTerminal Restart** - New binary must be running (bin\Release\net8.0-windows\MultiTerminal.exe)
2. **Open Debug Panel** - Click Debug button (🐛) in toolbar to monitor escalation logs
3. **Enable System-Wide Capture** - In Debug Panel, click "System-Wide: OFF" to enable OutputDebugString capture
4. **All Team Members Registered** - Alice, Charlie, Diana, Bob all connected
5. **Coordination with Alice** - Test 3 is coordinated with Alice's task 3d640493

---

## Test 1: Happy Path (87% Case)

### Goal
Verify that Method 1 (JS without focus) succeeds on first or second attempt for normal message scenarios. This should be the most common case with NO focus stealing.

### Setup
1. Ensure all terminals are idle at prompt
2. Open Debug Panel and clear existing logs
3. Ensure System-Wide capture is ON

### Execution Steps
1. Send 5-10 normal messages between different terminals:
   - Charlie → Bob: "Test message 1"
   - Charlie → Diana: "Test message 2"
   - Charlie → Alice: "Test message 3"
   - etc.
2. Watch Debug Panel for each message

### Expected Debug Panel Logs
```
[WebViewTerminalRenderer] [HH:mm:ss.fff] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Method 1 (JS without focus)
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ✅ Method 1 (JS without focus) succeeded! Output detected after XXXms.
```

### Success Criteria
- ✅ 100% of messages succeed with Method 1 on attempt 1-2
- ✅ NO focus stealing observed (terminals don't flash or steal focus)
- ✅ All messages delivered within ~500-1000ms
- ❌ NO Method 2 or Method 3 attempts should occur

### Results
*[To be filled during testing]*

---

## Test 2: Forced Escalation

### Goal
Verify that progressive escalation (M1→M2→M3) works when Method 1 fails. Simulate scenarios that require fallback methods.

### Setup
1. Identify scenarios that might cause Method 1 to fail:
   - Terminal is busy (processing tools, thinking)
   - Terminal lost focus
   - WebView2 is in transitional state
2. Open Debug Panel and clear logs

### Execution Steps
1. While target terminal is BUSY (e.g., Alice executing a long tool operation):
   - Send message from Charlie to Alice
   - Watch Debug Panel for escalation attempts
2. Repeat with different busy states if possible

### Expected Debug Panel Logs
```
[WebViewTerminalRenderer] Attempt 1/8: Method 1 (JS without focus)
[WebViewTerminalRenderer] Attempt 1: No output change detected with Method 1 (JS without focus)
[WebViewTerminalRenderer] Attempt 2/8: Method 1 (JS without focus)
[WebViewTerminalRenderer] Attempt 2: No output change detected with Method 1 (JS without focus)
[WebViewTerminalRenderer] Attempt 3/8: Method 2 (focus + JS)
[WebViewTerminalRenderer] ✅ Method 2 (focus + JS) succeeded! Output detected after XXXms.
```

### Success Criteria
- ✅ Method 1 attempts first (attempts 1-2)
- ✅ Method 2 triggers on attempts 3-5 when Method 1 fails
- ✅ Focus stealing occurs ONLY during Method 2 (visible flash/activation)
- ✅ Message eventually delivers (100% reliability)
- ✅ If Method 2 fails, Method 3 should trigger on attempts 6-8

### Results
*[To be filled during testing]*

---

## Test 3: Alice's Terminal (13% Stuck Case) - COORDINATED WITH ALICE

### Goal
Verify that progressive escalation breaks the stuck message state on Alice's terminal. Alice's terminal exhibits the 13% failure case where messages historically got stuck.

### Setup
1. Alice is ready and observing from receiver side (task 3d640493)
2. Alice's terminal is idle at prompt
3. Both Charlie and Alice have Debug Panels open

### Execution Steps
1. **Rapid-Fire Test**: Charlie sends 20+ messages to Alice in quick succession
   - Use Chat panel or MCP send_message tool
   - Send as fast as possible without waiting for responses
   - Messages can be simple: "Test 1", "Test 2", ... "Test 20"

2. **Observations**:
   - Charlie observes: Debug Panel for each message attempt
   - Alice observes: Messages appearing in her terminal, any stuck states

### Expected Behavior

**Charlie's Side (Sender):**
- All 20+ messages should show progressive escalation working
- Some may succeed with Method 1 (attempts 1-2)
- Some may escalate to Method 2 (attempts 3-5) due to rapid-fire timing
- ZERO messages should fail all 8 attempts

**Alice's Side (Receiver):**
- ALL messages should appear in terminal
- ZERO messages should get stuck in buffer
- May observe terminal gaining focus during Method 2 attempts
- Progressive escalation should break any stuck states

### Success Criteria
- ✅ 100% delivery rate (all 20+ messages delivered)
- ✅ ZERO stuck messages on Alice's terminal
- ✅ Progressive escalation successfully breaks stuck states
- ✅ Alice confirms zero manual Enter keypresses needed
- ✅ Both sender and receiver perspectives align

### Results
*[To be filled during testing - coordination with Alice's observations]*

---

## Test 4: No Proactive Focus Stealing

### Goal
Verify that focus stealing ONLY occurs during Method 2/3 fallback, NOT proactively on every message. This is critical for user experience.

### Setup
1. All terminals idle at prompt
2. User has another window active (e.g., browser, notepad)
3. Debug Panel open but not focused

### Execution Steps
1. Activate a different window (NOT MultiTerminal)
2. Send messages between terminals while other window is active
3. Observe whether MultiTerminal steals focus

### Expected Behavior
- ✅ Method 1 succeeds WITHOUT stealing focus (87% case)
- ✅ MultiTerminal should NOT steal focus from active window during Method 1
- ✅ Focus stealing should ONLY occur if Method 2 is triggered (attempts 3+)
- ✅ User can continue working in other windows during normal messaging

### Success Criteria
- ✅ NO focus stealing during Method 1 (attempts 1-2)
- ✅ Focus stealing ONLY during Method 2/3 fallback (attempts 3+)
- ✅ User experience is minimally invasive
- ❌ MultiTerminal does NOT proactively steal focus on every message

### Results
*[To be filled during testing]*

---

## Overall Test Summary

### Test Execution Checklist
- [ ] Test 1: Happy Path (Method 1 succeeds)
- [ ] Test 2: Forced Escalation (M1→M2→M3)
- [ ] Test 3: Rapid-fire to Alice (coordinated with Alice)
- [ ] Test 4: No Proactive Focus Stealing

### Success Metrics
- **Target**: 100% message delivery across all tests
- **Target**: Zero stuck messages (especially on Alice's terminal)
- **Target**: Focus stealing only during fallback (not proactive)
- **Target**: Progressive escalation correctly implemented

### Post-Test Actions
1. Document all results in this file
2. Report findings to Bob and team
3. Update task ff3c68cb status to completed if all tests pass
4. Flag any issues or unexpected behavior for follow-up

---

## Notes

*[To be filled during testing]*
