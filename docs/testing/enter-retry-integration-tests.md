# Enter Keypress Retry Mechanism - Integration Test Plan

**Task:** e798d0a9 - Fix Enter keypress ignored on busy terminals during message injection
**Implementation:** WebViewTerminalRenderer.cs - SendEnterWithRetryAsync()
**Test Task:** 197c2418
**Testers:** Diana (backend/stress), Alice (UI/busy terminals)

---

## Overview

This test plan validates the Enter keypress retry mechanism that ensures inter-terminal messages are reliably submitted even when the receiving terminal is busy (processing tools, thinking, etc.).

**What We're Testing:**
- Enter keypress retry with exponential backoff (500ms → 1s → 2s → 4s → 8s)
- Output change detection (monitors terminal output to confirm Enter was consumed)
- Reliable message delivery on both idle and busy terminals
- Graceful timeout behavior after max retries (~15 seconds)
- Cancellation token support

**Success Criteria:**
- 100% message delivery on idle terminals (baseline)
- 100% message delivery on busy terminals (core fix)
- No manual Enter keypresses required
- Graceful failure with clear logs on timeout
- Clean cancellation without errors

---

## Prerequisites

### 1. Build MultiTerminal
```powershell
# Navigate to MultiTerminal project
cd H:\DevLaptop\ClarionPowerShell\MultiTerminal

# Build the solution
dotnet build MultiTerminal.sln --configuration Debug
```

**Expected:** Build succeeds with 0 warnings, 0 errors

### 2. Launch MultiTerminal
```powershell
# Run MultiTerminal
.\bin\Debug\net8.0-windows\MultiTerminal.exe
```

### 3. Open Debug Panel
- Click the Debug toolbar button (🐛 icon)
- Position Debug Panel where you can watch it during tests
- Verify it's capturing messages (should see system startup logs)

### 4. Launch Test Terminals
Launch at least 2-3 terminals with different identities:
- Right-click terminal grid → Launch as "Alice"
- Right-click terminal grid → Launch as "Bob"
- Right-click terminal grid → Launch as "Diana"

Wait for all terminals to initialize and show Claude prompts.

---

## Test Scenario Template

Each test follows this structure:

### Test N: [Test Name]

**Goal:** [What we're validating]

**Setup:**
1. [Preparation steps]
2. [Terminal state requirements]

**Execution:**
1. [Actions to perform]
2. [Specific steps]

**Validation:**
1. Watch Debug Panel for retry logs
2. Verify expected log patterns
3. Check message delivery

**Expected Logs:**
```
[Expected log output examples]
```

**Success Criteria:**
- [ ] [Criterion 1]
- [ ] [Criterion 2]
- [ ] [Overall success condition]

---

## Test Scenarios

### Test 1: Idle Terminal Baseline (Diana)

**Goal:** Verify Enter keypress succeeds immediately on idle terminals (no retries needed). This establishes the baseline behavior - when everything is working optimally, the message should be delivered instantly with zero retries.

**Setup:**
1. **Ensure target terminal (Bob) is IDLE:**
   - No active tool execution (Grep, Read, Write, etc.)
   - Not thinking or analyzing
   - Just waiting at the prompt with blinking cursor
   - You should see the standard Claude prompt ready for input
2. **Prepare Debug Panel:**
   - Open Debug Panel if not already open (🐛 icon in toolbar)
   - Click "Clear" button to remove old logs
   - Ensure "System-Wide: ON" is enabled (toggle button should be blue)
3. **Prepare message sender:**
   - Option A: Use Chat Panel (select Bob as recipient)
   - Option B: Use another terminal (Diana or Alice) to send directly

**Execution:**
1. **Send test message to Bob:**
   - **Method 1 (Chat Panel):** Type "Test message 1 - Idle baseline" and click Send
   - **Method 2 (Task Assignment):** Right-click a Kanban task → Assign to Bob
2. **Immediately watch BOTH:**
   - Bob's terminal window (message should appear in input)
   - Debug Panel (should show log entries in real-time)
3. **Observe the timing:**
   - Note the timestamp when you click Send
   - Note when the message appears in Bob's input
   - Note when Bob processes the message (prompt appears)

**Validation:**

**Watch Bob's terminal:**
1. Message text should appear in input box immediately (< 100ms)
2. Message should auto-submit without any manual Enter press
3. Bob should start processing the message (you'll see tool calls or response)
4. Total time from Send to Bob responding: < 1 second

**Watch Debug Panel for this pattern:**
```
[MessageBroker] Calling OnMessageDelivery for message 123...
[MainForm] Injecting message to Bob: "Test message 1 - Idle baseline"
[WebViewTerminalRenderer] [HH:MM:SS.mmm] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after ~50-200ms! Enter was processed.
```

**Key indicators of SUCCESS:**
- ✅ Only "Attempt 1/8" appears (no Attempt 2, 3, etc.)
- ✅ "✅ Output detected after ~XXms" appears quickly (< 300ms)
- ✅ No retry warning messages
- ✅ Bob immediately processes the message

**Key indicators of FAILURE:**
- ❌ Message stuck in Bob's input (doesn't auto-submit)
- ❌ "Attempt 2/8" or higher appears (retries happening on idle terminal)
- ❌ "❌ All 8 attempts failed" appears
- ❌ User has to manually press Enter in Bob's terminal

**Expected Logs:**
```
[WebViewTerminalRenderer] [12:34:56.789] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after 127ms! Enter was processed.
```

**Success Criteria:**
- [ ] Message appears in Bob's input buffer immediately
- [ ] Message auto-submits with NO manual Enter required
- [ ] Enter succeeds on first attempt (Attempt 1/8 only)
- [ ] Success logged within 300ms
- [ ] No retry attempts logged (no "Attempt 2/8")
- [ ] Bob processes the message normally
- [ ] Total delivery time: < 500ms

---

### Test 2: Rapid-Fire Messaging Stress Test (Diana)

**Goal:** Verify the retry mechanism handles high-volume message delivery correctly under load. Tests message queuing, sequential processing, retry coordination, and duplicate prevention. This stress test ensures the system remains reliable when multiple messages arrive in quick succession.

**Setup:**
1. **Target terminal (Bob) can be idle OR busy** - doesn't matter for this test
2. **Prepare Debug Panel:**
   - Clear existing logs (Click "Clear" button)
   - Ensure System-Wide capture is ON
3. **Prepare to send multiple messages quickly:**
   - Have Chat Panel open with Bob selected as recipient
   - OR have 10 small Kanban tasks ready to assign to Bob

**Execution:**

**Method A: Chat Panel Rapid-Fire (Recommended)**
1. Open Chat Panel
2. Select Bob as recipient
3. Send messages as fast as you can type and click Send:
   - Type "Message 1" → Click Send
   - Type "Message 2" → Click Send
   - Type "Message 3" → Click Send
   - Continue up to "Message 10"
4. Goal: Send all 10 messages in < 10 seconds (< 1 second between sends)
5. Watch Debug Panel scroll with logs

**Method B: Task Assignment Rapid-Fire (Alternate)**
1. Create 10 test tasks on Kanban board beforehand
2. Rapidly assign all 10 to Bob:
   - Click task 1 → Assign dropdown → Bob
   - Click task 2 → Assign dropdown → Bob
   - Continue for all 10 tasks
3. Assign as fast as possible (< 1 second per assignment)

**Validation:**

**Watch Bob's terminal:**
1. All 10 messages should appear sequentially
2. Each message should auto-submit (no manual Enter needed)
3. Messages should arrive in order sent (Message 1, 2, 3, ... 10)
4. Bob should process each message one at a time
5. No duplicate messages should appear

**Watch Debug Panel for these patterns:**

**Pattern for EACH message (repeated 10 times):**
```
[MessageBroker] Calling OnMessageDelivery for message 123...
[MainForm] Injecting message to Bob: "Message 1"
[WebViewTerminalRenderer] [HH:MM:SS.mmm] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after ~XXms! Enter was processed.

[MessageBroker] Calling OnMessageDelivery for message 124...
[MainForm] Injecting message to Bob: "Message 2"
[WebViewTerminalRenderer] [HH:MM:SS.mmm] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after ~XXms! Enter was processed.

... (continues for all 10 messages)
```

**Key indicators of SUCCESS:**
- ✅ Each message gets its own "SendEnterWithRetryAsync starting" entry
- ✅ Messages processed sequentially (not overlapping)
- ✅ All messages show "✅ Output detected"
- ✅ NO duplicate "SendEnterWithRetryAsync starting" for same message ID
- ✅ Retry attempts may vary per message (some 1/8, some 2/8, etc.) - this is NORMAL
- ✅ No "❌ All 8 attempts failed" messages
- ✅ Message IDs increment: 123, 124, 125, ... (no gaps, no duplicates)

**Key indicators of FAILURE:**
- ❌ One or more messages stuck in Bob's input (need manual Enter)
- ❌ Duplicate messages appearing in Bob's terminal (same message twice)
- ❌ Messages out of order (Message 5 arrives before Message 3)
- ❌ "❌ All 8 attempts failed" for any message
- ❌ Same message ID appears multiple times in logs (duplicate delivery)
- ❌ Crashes, exceptions, or hung terminals

**Count verification:**
- Sent: 10 messages
- Bob's terminal: Should process exactly 10 messages (count them!)
- Debug Panel: Should show 10 "SendEnterWithRetryAsync starting" entries
- NO message should appear twice

**Expected Logs (Full Sequence):**
```
[MessageBroker] Calling OnMessageDelivery for message 201...
[MainForm] Injecting message to Bob: "Message 1"
[WebViewTerminalRenderer] [12:45:01.234] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] ✅ Output detected after 134ms! Enter was processed.

[MessageBroker] Calling OnMessageDelivery for message 202...
[MainForm] Injecting message to Bob: "Message 2"
[WebViewTerminalRenderer] [12:45:02.156] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] ✅ Output detected after 97ms! Enter was processed.

... (8 more messages)

[MessageBroker] Calling OnMessageDelivery for message 210...
[MainForm] Injecting message to Bob: "Message 10"
[WebViewTerminalRenderer] [12:45:09.891] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] ✅ Output detected after 203ms! Enter was processed.
```

**Success Criteria:**
- [ ] All 10 messages sent successfully
- [ ] All 10 messages delivered to Bob's terminal
- [ ] All 10 messages auto-submit (no manual Enter for ANY message)
- [ ] Messages arrive in correct order (1, 2, 3, ... 10)
- [ ] NO duplicate messages in Bob's terminal
- [ ] NO duplicate log entries for same message ID
- [ ] Sequential processing (messages don't overlap in logs)
- [ ] Each message gets independent retry handling
- [ ] Debug Panel shows success ("✅ Output detected") for all 10
- [ ] No errors, exceptions, or crashes

**Performance Observations:**
- **Retry patterns:** Some messages may need 1 attempt, some may need 2-3 attempts (depending on Bob's state when each arrives) - this is EXPECTED and CORRECT
- **Total time:** All 10 messages should be delivered within 15-30 seconds
- **No UI freezing:** MultiTerminal UI should remain responsive throughout
- **No queue overflow:** All messages should be processed, none dropped

---

### Test 3: Busy Terminal During Tool Execution (Alice)

**Goal:** Verify Enter retry mechanism activates and succeeds when the target terminal is busy executing tools (Grep, Read, Write, etc.). This is the core scenario the retry mechanism was designed to fix - messages sent while Claude is processing tools should queue up and auto-submit when the terminal becomes ready.

**Setup:**
1. **Prepare Bob terminal** - will become the busy target
2. **Prepare Alice terminal or Chat Panel** - will send the message
3. **Prepare Debug Panel:**
   - Clear existing logs (Click "Clear")
   - Ensure System-Wide capture is ON
4. **Position windows** for simultaneous observation:
   - Bob's terminal (watch for tool execution → idle transition)
   - Debug Panel (watch retry logs in real-time)
   - Chat Panel or Alice terminal (send message from here)

**Execution:**

**Step 1: Create busy condition in Bob terminal**

Choose ONE of these busy scenarios:

**Option A - Codebase Search (Recommended, ~8-12 seconds):**
```
User: Search the entire codebase for "MessageBroker" and analyze all usages in detail
```
This triggers multiple Grep calls across all C# files. Watch Bob's terminal show tool execution progress.

**Option B - Multi-File Analysis (~10-15 seconds):**
```
User: Read all C# files in the Services folder and explain what each service does in detail
```
This triggers multiple Read tool calls. You'll see Bob reading ServiceFile1.cs, ServiceFile2.cs, etc.

**Option C - Complex Search + Analysis (~12-20 seconds):**
```
User: Find all async Task methods in the entire project, read them, and categorize them by purpose
```
This combines Grep + Read operations - longest duration.

**Option D - PowerShell Command (~10 seconds):**
```
User: Run this PowerShell command: Get-ChildItem -Path H:\DevLaptop\ClarionPowerShell\MultiTerminal -Recurse -Filter *.cs | Select-String -Pattern "async Task"
```
Direct PowerShell execution (no Claude tools).

**Step 2: Send message while Bob is busy**

**CRITICAL TIMING:** Send the message WHILE Bob is actively processing (watch for tool execution in terminal)

**Visual cues that Bob is BUSY:**
- Terminal shows "Using Grep tool..." or "Using Read tool..."
- Claude is outputting tool results
- No blinking cursor at prompt
- Terminal appears "locked" (can't type)

**When you see Bob is busy, immediately:**

**Method 1 - Chat Panel (Recommended):**
1. Open Chat Panel
2. Select Bob as recipient
3. Type test message: "Hey Bob, are you there?"
4. Click Send

**Method 2 - Alice Terminal:**
1. In Alice, send message to Bob using inter-terminal messaging
2. Watch both terminals

**Step 3: Observe the retry behavior**

**Watch Bob's terminal:**
- Message text should appear in input buffer WHILE Bob is still busy
- Message will NOT submit immediately (Enter is ignored while busy)
- After Bob finishes and returns to prompt, message should auto-submit

**Watch Debug Panel in real-time:**
- Initial Send entry logged
- Attempt 1/8 logged
- "Waiting 500ms..." logged
- "No output detected, retrying..." (because Bob is still busy)
- Attempt 2/8, Attempt 3/8, etc. (retry cycle)
- Eventually: "✅ Output detected after XXms!" (when Bob becomes idle)

**Validation:**

**Watch Bob's terminal for this sequence:**
1. Bob is executing tools (busy state)
2. Message text appears in input buffer (injected successfully)
3. Message does NOT submit yet (expected - terminal is busy)
4. Bob finishes tool execution, returns to prompt
5. Message auto-submits immediately (no manual Enter!)
6. Bob processes the message normally

**Watch Debug Panel for this pattern:**
```
[MessageBroker] Calling OnMessageDelivery for message 345...
[MainForm] Injecting message to Bob: "Hey Bob, are you there?"
[TerminalControl] InjectInputAsync called, text length=24
[TerminalControl] Writing text to terminal...
[TerminalControl] Text written, waiting 200ms...
[TerminalControl] Calling SendEnterViaXtermAsync...
[WebViewTerminalRenderer] [14:23:45.123] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 500ms, retrying...
[WebViewTerminalRenderer] Attempt 2/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 2: Waiting 1000ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 1000ms, retrying...
[WebViewTerminalRenderer] Attempt 3/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 3: Waiting 2000ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after 1750ms! Enter was processed.
[TerminalControl] SendEnterViaXtermAsync completed in 3250ms
```

**Key indicators of SUCCESS:**
- ✅ Multiple retry attempts logged (Attempt 2/8, 3/8, etc.)
- ✅ Each retry shows increasing wait time (500ms → 1000ms → 2000ms) - EXPONENTIAL BACKOFF
- ✅ Eventually "✅ Output detected after XXms!" appears
- ✅ Message auto-submits in Bob's terminal (no manual Enter)
- ✅ Total time from first attempt to success: 2-10 seconds (depends on tool execution length)
- ✅ Bob processes message normally after submission

**Key indicators of FAILURE:**
- ❌ "❌ All 8 attempts failed" in Debug Panel
- ❌ Message stuck in Bob's input (never auto-submits)
- ❌ User has to manually press Enter in Bob's terminal
- ❌ Message disappears from input (lost)
- ❌ Crash or exception during retry

**Expected Logs:**
```
[WebViewTerminalRenderer] [14:23:45.123] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 500ms, retrying...
[WebViewTerminalRenderer] Attempt 2/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 2: Waiting 1000ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 1000ms, retrying...
[WebViewTerminalRenderer] Attempt 3/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 3: Waiting 2000ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after 1750ms! Enter was processed.
```

**Success Criteria:**
- [ ] Message injected while Bob is actively executing tools
- [ ] Message text appears in Bob's input buffer
- [ ] Initial Enter attempt ignored (expected - Bob is busy)
- [ ] Retry mechanism activates automatically (Attempt 2/8, 3/8, etc.)
- [ ] Exponential backoff observed in logs (500ms, 1s, 2s, 4s)
- [ ] Enter succeeds when Bob finishes and returns to prompt
- [ ] Message auto-submits WITHOUT manual Enter press
- [ ] Bob processes the message normally
- [ ] Total time to success: 2-10 seconds (varies by tool duration)
- [ ] No errors, exceptions, or crashes

**Performance Notes:**
- **Typical retry count:** 2-5 attempts (depends on tool execution length)
- **Timing variance:** If Bob finishes quickly, may succeed on Attempt 1 or 2
- **Timing variance:** If Bob's tools take 8+ seconds, may need Attempt 4-6
- **This is EXPECTED and CORRECT behavior**

---

### Test 4: Busy Terminal During Thinking/Analysis (Alice)

**Goal:** Verify retry mechanism works when terminal is in "thinking" state - when Claude is analyzing, planning, or processing complex requests WITHOUT executing tools. This tests a different busy condition: pure thinking time before any output begins.

**Setup:**
1. **Prepare Bob terminal** - will be in thinking state
2. **Prepare Alice terminal or Chat Panel** - message sender
3. **Clear Debug Panel logs**
4. **Position windows:**
   - Bob's terminal (watch for thinking indicator)
   - Debug Panel (retry logs)
   - Chat Panel or Alice terminal (sender)

**Execution:**

**Step 1: Trigger long thinking in Bob terminal**

Choose ONE of these thinking-heavy questions:

**Option A - Architecture Explanation (Recommended, ~5-10 seconds thinking):**
```
User: Explain the complete architecture of the MultiTerminal message delivery system in extreme detail, including all services, models, message flow, retry logic, and database interactions
```
This requires deep analysis before Claude can respond - pure thinking time.

**Option B - Code Analysis (~8-15 seconds thinking):**
```
User: Analyze all C# files in the Services folder and create a comprehensive UML class diagram showing all relationships, dependencies, and design patterns used
```
Claude must analyze many files mentally before outputting - long thinking phase.

**Option C - Complex Comparison (~7-12 seconds thinking):**
```
User: Compare the MessageBroker pattern used in MultiTerminal with the standard Observer pattern, explain all differences, and suggest architectural improvements
```
Requires deep reasoning before responding.

**Step 2: Send message during thinking phase**

**CRITICAL TIMING:** Send message AFTER submitting the question but BEFORE Bob starts outputting response

**Visual cues that Bob is THINKING (not executing tools):**
- Bob's prompt shows "thinking" or processing indicator
- Terminal cursor may be blinking but no output yet
- No tool execution messages (no "Using Grep", "Using Read")
- Silent period between question submission and response start

**Perfect timing window:**
1. Submit thinking question in Bob
2. **Wait 1-2 seconds** for thinking to begin
3. **Before Bob outputs first word of response**, send message:
   ```
   Alice → Bob: "Quick question about MessageBroker"
   ```

**Method - Chat Panel:**
1. Open Chat Panel
2. Select Bob
3. Type: "Quick question about MessageBroker"
4. Click Send DURING thinking phase

**Step 3: Observe retry during thinking**

**Watch Bob's terminal:**
- Thinking continues (no interruption)
- Message appears in input buffer
- Once thinking completes and Bob starts outputting, message auto-submits

**Watch Debug Panel:**
- Retry attempts logged during thinking
- Success logged when output begins

**Validation:**

**Watch Bob's terminal for this sequence:**
1. Bob is thinking (silent, processing)
2. Message text appears in input buffer (injected)
3. Message does NOT submit yet (thinking still active)
4. Bob finishes thinking, starts outputting response
5. Message auto-submits immediately
6. Bob switches context to process the new message

**Watch Debug Panel for this pattern:**
```
[MessageBroker] Calling OnMessageDelivery for message 456...
[MainForm] Injecting message to Bob: "Quick question about MessageBroker"
[TerminalControl] InjectInputAsync called, text length=35
[TerminalControl] Calling SendEnterViaXtermAsync...
[WebViewTerminalRenderer] [14:30:15.789] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 500ms, retrying...
[WebViewTerminalRenderer] Attempt 2/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 2: Waiting 1000ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after 850ms! Enter was processed.
[TerminalControl] SendEnterViaXtermAsync completed in 1350ms
```

**Key indicators of SUCCESS:**
- ✅ Message injected during thinking phase (before output)
- ✅ Multiple retry attempts logged (2-4 typical)
- ✅ Retries show increasing wait times (500ms → 1000ms → 2000ms)
- ✅ "✅ Output detected" appears when Bob starts outputting
- ✅ Message auto-submits WITHOUT manual Enter
- ✅ Total time: 1-8 seconds (depends on thinking duration)
- ✅ Bob processes the message after auto-submit

**Key indicators of FAILURE:**
- ❌ Message never auto-submits (stuck in input)
- ❌ "❌ All 8 attempts failed" in Debug Panel
- ❌ User must manually press Enter
- ❌ Message interrupts or corrupts Bob's thinking
- ❌ Crash or exception

**Expected Logs:**
```
[WebViewTerminalRenderer] [14:30:15.789] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 500ms, retrying...
[WebViewTerminalRenderer] Attempt 2/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 2: Waiting 1000ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after 850ms! Enter was processed.
```

**Success Criteria:**
- [ ] Message injected during thinking phase (before output begins)
- [ ] Message appears in Bob's input buffer
- [ ] Retry mechanism activates automatically
- [ ] Enter succeeds when Bob starts outputting (thinking completes)
- [ ] Message auto-submits WITHOUT manual Enter
- [ ] Bob processes the message normally
- [ ] No manual intervention required
- [ ] Typical time to success: 1-8 seconds
- [ ] No errors or exceptions

**Performance Notes:**
- **Thinking duration varies:** Complex questions = longer thinking = more retries
- **Typical retry count:** 2-4 attempts for thinking phase
- **Faster than tool execution:** Thinking usually completes quicker than tool operations
- **Success timing:** Usually succeeds within 2-5 seconds

---

### Test 5: Timeout Behavior (Alice)

**Goal:** Verify graceful timeout and failure handling when terminal remains unresponsive for the entire retry period. Tests that the retry mechanism eventually gives up after max attempts (~15 seconds) and logs a clear failure message WITHOUT crashing or hanging.

**Setup:**
1. **Prepare Bob terminal** - will be unresponsive for 20+ seconds
2. **Prepare Chat Panel** - message sender
3. **Clear Debug Panel logs**
4. **Have stopwatch or timer ready** - to verify ~15 second timeout
5. **Position windows:**
   - Bob's terminal (watch for sustained busy state)
   - Debug Panel (watch ALL 8 retry attempts)
   - Stopwatch (verify timing)

**Execution:**

**Step 1: Create long unresponsive condition in Bob**

Choose ONE method to make Bob unresponsive for 20+ seconds:

**Option A - PowerShell Sleep (Recommended, exactly 30 seconds):**
```powershell
Start-Sleep -Seconds 30
```
Bob's terminal will be completely unresponsive for 30 seconds - perfect for timeout testing.

**Option B - Long-Running PowerShell Command (~20-40 seconds):**
```powershell
Get-ChildItem -Path C:\ -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Length -gt 1000000 } | Select-Object FullName, Length
```
Recursively searches entire C: drive for files > 1MB - very slow.

**Option C - Ask Claude for Massive Analysis (~20-60 seconds):**
```
User: Analyze every single C# file in the H:\DevLaptop\ClarionPowerShell directory recursively, read all of them, and create a complete architectural document with UML diagrams for every class
```
This will trigger hundreds of Read operations - very long execution.

**Option D - Infinite Loop (USE WITH CAUTION - requires Ctrl+C to stop):**
```powershell
while ($true) { Start-Sleep -Milliseconds 100 }
```
⚠️ **WARNING:** This loops forever - you'll need to press Ctrl+C in Bob to stop it after the test

**Step 2: Send message immediately after starting unresponsive operation**

**TIMING:** Start Bob's long operation, then immediately send message (within 1-2 seconds)

1. In Bob, execute chosen unresponsive operation (e.g., `Start-Sleep -Seconds 30`)
2. **Immediately** open Chat Panel
3. Select Bob as recipient
4. Type: "Test message - should timeout"
5. Click Send
6. **Start your stopwatch/timer** when you click Send

**Step 3: Watch the complete retry cycle**

**This test requires patience - you'll watch all 8 attempts fail over ~15 seconds**

**Watch Debug Panel for complete retry sequence:**
- Attempt 1/8 (wait 500ms) → Fail
- Attempt 2/8 (wait 1000ms) → Fail
- Attempt 3/8 (wait 2000ms) → Fail
- Attempt 4/8 (wait 4000ms) → Fail
- Attempt 5/8 (wait 8000ms) → Fail
- Attempt 6/8 (wait 8000ms) → Fail
- Attempt 7/8 (wait 8000ms) → Fail
- Attempt 8/8 (wait 8000ms) → Fail
- **Final result:** "❌ All 8 attempts failed"

**Watch Bob's terminal:**
- Should remain busy/unresponsive entire time
- Message text appears in input but never submits
- After timeout, message remains in input (orphaned)

**Step 4: Verify graceful failure**

After ~15 seconds:
1. **Stop your timer** - verify ~15-16 seconds elapsed
2. Check Debug Panel for failure message
3. Verify no crash, no freeze, no exception
4. MultiTerminal UI should still be responsive

**Validation:**

**Watch Debug Panel for COMPLETE retry sequence:**
```
[MessageBroker] Calling OnMessageDelivery for message 567...
[MainForm] Injecting message to Bob: "Test message - should timeout"
[TerminalControl] InjectInputAsync called, text length=27
[TerminalControl] Calling SendEnterViaXtermAsync...
[WebViewTerminalRenderer] [14:45:00.000] SendEnterWithRetryAsync starting (maxRetries=8)

[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 500ms, retrying...

[WebViewTerminalRenderer] Attempt 2/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 2: Waiting 1000ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 1000ms, retrying...

[WebViewTerminalRenderer] Attempt 3/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 3: Waiting 2000ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 2000ms, retrying...

[WebViewTerminalRenderer] Attempt 4/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 4: Waiting 4000ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 4000ms, retrying...

[WebViewTerminalRenderer] Attempt 5/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 5: Waiting 8000ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 8000ms, retrying...

[WebViewTerminalRenderer] Attempt 6/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 6: Waiting 8000ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 8000ms, retrying...

[WebViewTerminalRenderer] Attempt 7/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 7: Waiting 8000ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 8000ms, retrying...

[WebViewTerminalRenderer] Attempt 8/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 8: Waiting 8000ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 8000ms

[WebViewTerminalRenderer] ❌ All 8 attempts failed - Enter was not processed after 15.5s
[TerminalControl] SendEnterViaXtermAsync completed in 15500ms (FAILED)
```

**Exponential Backoff Verification:**
Count the wait times in Debug Panel:
- Attempt 1: 500ms
- Attempt 2: 1000ms (2x)
- Attempt 3: 2000ms (2x)
- Attempt 4: 4000ms (2x)
- Attempt 5-8: 8000ms each (capped at max)

**Total time calculation:**
500 + 1000 + 2000 + 4000 + 8000 + 8000 + 8000 + 8000 = **39,500ms** = ~39.5 seconds of wait time

BUT retries happen sequentially, so actual elapsed time ≈ **15-16 seconds** (includes overhead)

**Key indicators of SUCCESS:**
- ✅ All 8 retry attempts logged (Attempt 1/8 through Attempt 8/8)
- ✅ Exponential backoff visible in logs (500ms → 1s → 2s → 4s → 8s)
- ✅ "❌ All 8 attempts failed" message logged clearly
- ✅ Total elapsed time: 15-17 seconds (verify with stopwatch)
- ✅ MultiTerminal UI remains responsive (no freeze)
- ✅ No crash, no exception, no error dialog
- ✅ Message remains in Bob's input (orphaned but visible)
- ✅ Other terminals still function normally

**Key indicators of FAILURE:**
- ❌ Fewer than 8 attempts logged (gave up early)
- ❌ No clear failure message in Debug Panel
- ❌ MultiTerminal crashes or freezes
- ❌ Exception dialog appears
- ❌ UI becomes unresponsive
- ❌ Timeout takes < 10 seconds or > 20 seconds (wrong timing)

**Expected Logs:**
```
[WebViewTerminalRenderer] [14:45:00.000] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 500ms, retrying...
[WebViewTerminalRenderer] Attempt 2/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 2: Waiting 1000ms for output change...
... (continues through Attempt 8/8) ...
[WebViewTerminalRenderer] ❌ All 8 attempts failed - Enter was not processed after 15.5s
```

**Success Criteria:**
- [ ] All 8 retry attempts executed and logged
- [ ] Exponential backoff observed (500ms, 1s, 2s, 4s, 8s, 8s, 8s, 8s)
- [ ] Total elapsed time: 15-17 seconds (measure with stopwatch)
- [ ] Clear failure message: "❌ All 8 attempts failed"
- [ ] No exceptions thrown
- [ ] No crashes or freezes
- [ ] MultiTerminal UI remains responsive throughout
- [ ] Other terminals continue working normally
- [ ] Message orphaned in Bob's input (visible but not submitted)
- [ ] Graceful degradation - system continues functioning

**Performance Notes:**
- **This is EXPECTED FAILURE behavior** - not a bug!
- **Purpose:** Demonstrates retry mechanism doesn't hang forever
- **Real-world scenario:** Terminal crashed, Claude hung, network issue
- **Correct behavior:** Give up after reasonable time (15s), log failure, continue functioning

**Cleanup After Test:**
- If Bob is still running `Start-Sleep`, wait for it to complete (~30s total)
- Or press Ctrl+C in Bob to interrupt
- Bob should return to normal prompt after interruption

---

### Test 6: Cancellation Token Support (Alice)

**Goal:** Verify clean cancellation and graceful abort when retry operation is interrupted mid-cycle. Tests that the cancellation token is properly checked and handled, preventing hung tasks, resource leaks, or crashes when operations are cancelled.

**⚠️ NOTE:** This test requires clarification on exact cancellation triggers in MultiTerminal GUI. Test steps below cover likely scenarios - adjust based on actual UI implementation.

**Setup:**
1. **Prepare Bob terminal** - will be unresponsive (trigger retry cycle)
2. **Prepare Chat Panel** - message sender
3. **Clear Debug Panel logs**
4. **Position windows:**
   - Bob's terminal (will be busy)
   - Debug Panel (watch retry cycle and cancellation)
   - Chat Panel (sender)

**Execution:**

**Step 1: Create busy condition and start retry cycle**

1. In Bob terminal, start long unresponsive operation:
   ```powershell
   Start-Sleep -Seconds 30
   ```

2. Immediately send message from Chat Panel to Bob:
   ```
   Message: "Test cancellation - this should be interrupted"
   ```

3. **Verify retry cycle starts** - watch Debug Panel for:
   ```
   [WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
   [WebViewTerminalRenderer] Attempt 1: Waiting 500ms...
   [WebViewTerminalRenderer] ⚠️ No output detected, retrying...
   [WebViewTerminalRenderer] Attempt 2/8: Sending Enter...
   ```

**Step 2: Trigger cancellation mid-retry**

**TIMING:** Wait until you see "Attempt 2/8" or "Attempt 3/8" in Debug Panel, THEN trigger cancellation

**Cancellation Trigger Options (Choose ONE - verify which works):**

**Option A - Close Receiving Terminal (Likely trigger):**
1. While retry cycle is running (Attempt 2 or 3)
2. Right-click Bob's terminal tab → Close Terminal
3. Or click X button on Bob's terminal

**Expected behavior:**
- Retry cycle should abort immediately
- Cancellation token triggered by terminal disposal
- Debug Panel logs cancellation

**Option B - Close MultiTerminal Application:**
1. While retry cycle is running
2. Close entire MultiTerminal application (File → Exit or X button)

**Expected behavior:**
- All pending operations cancelled
- Clean shutdown
- No hung processes

**Option C - Stop/Interrupt Sender:**
1. While retry cycle is running
2. If there's a "Cancel" button in Chat Panel → Click it
3. Or close Chat Panel

**Expected behavior:**
- Pending send operation cancelled
- Retry cycle aborts

**Option D - System-Level Cancellation:**
1. While retry cycle is running
2. Task Manager → End MultiTerminal process

**Expected behavior:**
- Forced termination (not graceful, but tests cleanup)

**⚠️ CLARIFICATION NEEDED:**
- Which cancellation method is the intended test scenario?
- Is there a UI "Cancel" button for pending messages?
- Does closing terminal trigger cancellation token?
- What other cancellation mechanisms exist?

**Step 3: Observe cancellation behavior**

**Watch Debug Panel:**
- Retry cycle should stop immediately (no more attempts)
- Cancellation message should appear
- No exception stack traces

**Watch system resources:**
- MultiTerminal should remain stable (if not closed)
- No hung processes in Task Manager
- Graceful cleanup

**Validation:**

**Watch Debug Panel for cancellation pattern:**

**Scenario 1 - Terminal Closed:**
```
[MessageBroker] Calling OnMessageDelivery for message 678...
[MainForm] Injecting message to Bob: "Test cancellation..."
[TerminalControl] InjectInputAsync called
[WebViewTerminalRenderer] [15:00:00.000] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 500ms, retrying...
[WebViewTerminalRenderer] Attempt 2/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 2: Waiting 1000ms for output change...

[TerminalControl] Terminal disposed - cancelling pending operations
[WebViewTerminalRenderer] 🛑 Operation cancelled during retry
[WebViewTerminalRenderer] SendEnterWithRetryAsync aborted cleanly

[MainForm] Terminal Bob closed - cleaning up resources
```

**Scenario 2 - Application Shutdown:**
```
[WebViewTerminalRenderer] Attempt 2/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 2: Waiting 1000ms for output change...

[MainForm] Application shutdown requested - cancelling all pending operations
[WebViewTerminalRenderer] 🛑 Operation cancelled during retry
[MessageBroker] Shutting down - X pending messages cancelled
[MainForm] Clean shutdown complete
```

**Key indicators of SUCCESS:**
- ✅ Retry cycle stops immediately when cancellation triggered
- ✅ "🛑 Operation cancelled" or similar message logged
- ✅ No additional retry attempts after cancellation
- ✅ No exception stack traces in Debug Panel
- ✅ No error dialogs appear
- ✅ If MultiTerminal still running, UI remains responsive
- ✅ If closed, clean shutdown with exit code 0
- ✅ Task Manager shows no hung MultiTerminal.exe processes
- ✅ No resource leaks (check Task Manager memory)

**Key indicators of FAILURE:**
- ❌ Retry cycle continues after cancellation (ignoring token)
- ❌ Exception thrown: "OperationCancelledException" or other errors
- ❌ MultiTerminal crashes or freezes
- ❌ Hung processes remain in Task Manager
- ❌ Error dialog appears
- ❌ Unable to restart MultiTerminal (resources locked)

**Expected Logs (Clean Cancellation):**
```
[WebViewTerminalRenderer] [15:00:00.000] SendEnterWithRetryAsync starting (maxRetries=8)
[WebViewTerminalRenderer] Attempt 1/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 1: Waiting 500ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 500ms, retrying...
[WebViewTerminalRenderer] Attempt 2/8: Sending Enter...
[WebViewTerminalRenderer] Attempt 2: Waiting 1000ms for output change...

[WebViewTerminalRenderer] 🛑 Cancelled while waiting for output
[TerminalControl] SendEnterViaXtermAsync cancelled after 1500ms
```

**Success Criteria:**
- [ ] Retry cycle aborts immediately on cancellation
- [ ] Clear cancellation message logged ("🛑" or "cancelled")
- [ ] No exceptions thrown (or if thrown, handled gracefully)
- [ ] No error dialogs displayed
- [ ] No retry attempts after cancellation
- [ ] MultiTerminal remains stable (if not closed)
- [ ] Clean shutdown if application closed
- [ ] No hung processes in Task Manager
- [ ] No resource leaks (memory, handles)
- [ ] Can restart MultiTerminal successfully after test

**Cleanup After Test:**

**If terminal closed:**
- Launch new Bob terminal to continue testing

**If application closed:**
- Verify clean exit (no processes in Task Manager)
- Restart MultiTerminal
- Verify all terminals launch normally

**Check system resources:**
- Open Task Manager
- Verify no orphaned MultiTerminal.exe processes
- Memory usage should return to baseline

**Additional Cancellation Scenarios to Test (if time permits):**

**Scenario A - Rapid Cancel (Attempt 1):**
- Send message to busy terminal
- Cancel immediately (within 100ms)
- Verify cancellation during Attempt 1

**Scenario B - Late Cancel (Attempt 7-8):**
- Let retry cycle run almost to completion
- Cancel during Attempt 7 or 8
- Verify cancellation works even near timeout

**Scenario C - Multiple Simultaneous Cancellations:**
- Send messages to 3 busy terminals (Bob, Diana, Charlie)
- All enter retry cycles
- Close all 3 terminals simultaneously
- Verify all 3 operations cancel cleanly

**Performance Notes:**
- **Cancellation should be immediate** (< 100ms response)
- **No cleanup delay** - resources released instantly
- **Graceful abort** - no corruption of terminal state
- **Reusable** - can send more messages after cancellation

**⚠️ IMPORTANT NOTES FOR TESTER:**

This test validates a critical safety mechanism. Proper cancellation handling prevents:
1. **Resource leaks** - hung tasks consuming memory
2. **UI freezes** - blocking threads waiting forever
3. **Process corruption** - orphaned operations
4. **User frustration** - inability to interrupt stuck operations

If cancellation FAILS (retry continues ignoring token), this is a CRITICAL BUG requiring immediate fix.

---

## Debug Panel Log Guide

### Success Pattern (Idle Terminal)
```
[WebViewTerminalRenderer] SendEnterViaXtermAsync: Sending Enter (attempt 1/8)
[WebViewTerminalRenderer] ✅ Output detected after 50-200ms! Enter was processed.
```
**Interpretation:** Enter succeeded immediately, no retries needed

### Retry Pattern (Busy Terminal)
```
[WebViewTerminalRenderer] SendEnterViaXtermAsync: Sending Enter (attempt 1/8)
[WebViewTerminalRenderer] ⏱️ Waiting 500ms for output change...
[WebViewTerminalRenderer] ⚠️ No output detected after 500ms, retrying...
[WebViewTerminalRenderer] SendEnterViaXtermAsync: Sending Enter (attempt 2/8)
[WebViewTerminalRenderer] ⏱️ Waiting 1000ms for output change...
[WebViewTerminalRenderer] ✅ Output detected after 850ms! Enter was processed.
```
**Interpretation:** First attempt failed (terminal busy), retry succeeded after 850ms

### Failure Pattern (Timeout)
```
[WebViewTerminalRenderer] SendEnterViaXtermAsync: Sending Enter (attempt 8/8)
[WebViewTerminalRenderer] ⏱️ Waiting 8000ms for output change...
[WebViewTerminalRenderer] ❌ All 8 attempts failed - Enter was not processed after 15.5s
```
**Interpretation:** All retries exhausted, graceful failure

### Cancellation Pattern
```
[WebViewTerminalRenderer] 🛑 Operation cancelled during retry
```
**Interpretation:** Clean cancellation occurred

---

## Results Reporting

After completing all test scenarios, report results in this format:

### Test Results Summary

**Test 1: Idle Terminal Baseline**
- Status: ✅ PASS / ❌ FAIL
- Details: [Brief description of results]
- Logs: [Relevant Debug Panel excerpts or screenshots]

**Test 2: Rapid-Fire Messaging**
- Status: ✅ PASS / ❌ FAIL
- Details: [Brief description]
- Logs: [Excerpts or screenshots]

**Test 3: Busy Terminal (Tool Execution)**
- Status: ✅ PASS / ❌ FAIL
- Details: [Brief description]
- Logs: [Excerpts or screenshots]

**Test 4: Busy Terminal (Thinking)**
- Status: ✅ PASS / ❌ FAIL
- Details: [Brief description]
- Logs: [Excerpts or screenshots]

**Test 5: Timeout Behavior**
- Status: ✅ PASS / ❌ FAIL
- Details: [Brief description]
- Logs: [Excerpts or screenshots]

**Test 6: Cancellation Token**
- Status: ✅ PASS / ❌ FAIL
- Details: [Brief description]
- Logs: [Excerpts or screenshots]

### Overall Assessment

**Success Rate:** X/6 tests passed

**Issues Discovered:**
1. [Issue 1 description]
2. [Issue 2 description]

**Recommendations:**
- [Fix suggestions]
- [Follow-up work needed]

---

## Notes for Testers

**Diana's Tests (1-2):**
- Focus on baseline behavior and stress testing
- Watch for duplicate message handling
- Monitor sequential processing

**Alice's Tests (3-6):**
- Focus on busy terminal scenarios
- Create realistic busy conditions (tools, thinking)
- Test edge cases (timeout, cancellation)
- Watch for UX issues

**Both:**
- Keep Debug Panel visible during all tests
- Note exact timing of retry attempts
- Report any unexpected behavior immediately
- Screenshots of Debug Panel logs are very helpful

---

## Implementation Reference

**File:** `Terminal\WebViewTerminalRenderer.cs`

**Key Method:** `SendEnterWithRetryAsync()`

**Algorithm:**
1. Capture current output timestamp
2. Send Enter via JavaScript
3. Poll for output changes with exponential backoff
4. Return success when output detected
5. Return failure after max retries (8 attempts, ~15s)

**Configuration:**
- Max retries: 8
- Initial delay: 500ms
- Backoff multiplier: 2x
- Total timeout: ~15.5 seconds

**Cancellation:** Accepts CancellationToken, checks before each retry
