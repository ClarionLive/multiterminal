# Paste Mode Fix - Test & Validation Plan

**Fix Summary:** MaxChunkSize changed from 4096 to 500 bytes to prevent PowerShell's bracketed paste mode from triggering.

**Implementation:** `Controls\TerminalControl.cs` - Lines 270, 293, 356

**Target:** Prevent PowerShell from entering paste mode (which requires user confirmation) when injecting messages.

---

## Test Scenarios

### 1. Small Messages (< 500 bytes)
**Objective:** Verify single-message mode works without chunking

| Test Case | Message Size | Expected Behavior |
|-----------|-------------|-------------------|
| 1.1 | 50 bytes | Single injection, no chunking |
| 1.2 | 250 bytes | Single injection, no chunking |
| 1.3 | 499 bytes | Single injection, no chunking |

**Test Data:**
- **1.1:** `"Hello Diana, testing small message!"`
- **1.2:** `"This is a medium-sized test message that contains enough text to approach the boundary but still stays well under 500 bytes. We want to verify that the message arrives correctly without any chunking behavior being triggered by the system."`
- **1.3:** `"A" * 499` (string of 499 'A' characters)

**Validation:**
- ✅ Message appears in terminal without paste mode prompt
- ✅ No chunk markers ([n/total]) present
- ✅ Log shows "using single mode"
- ✅ Full content received correctly

---

### 2. Boundary Testing (~500 bytes)
**Objective:** Verify behavior at the threshold

| Test Case | Message Size | Expected Behavior |
|-----------|-------------|-------------------|
| 2.1 | 500 bytes | Single or chunked (boundary) |
| 2.2 | 501 bytes | Chunked into 2 parts |
| 2.3 | 600 bytes | Chunked into 2 parts |

**Test Data:**
- **2.1:** `"B" * 500` (exactly 500 bytes)
- **2.2:** `"C" * 501` (exactly 501 bytes)
- **2.3:** String with 600 characters including spaces and words

**Validation:**
- ✅ 500 bytes: Check if single or chunked (implementation decides)
- ✅ 501+ bytes: Must chunk with [1/2], [2/2] markers
- ✅ No PowerShell paste mode triggered
- ✅ Full content received correctly

---

### 3. Large Messages (> 500 bytes)
**Objective:** Verify chunking works correctly for large messages

| Test Case | Message Size | Expected Chunks |
|-----------|-------------|-----------------|
| 3.1 | 1000 bytes | 2-3 chunks |
| 3.2 | 2000 bytes | 4-5 chunks |
| 3.3 | 4096 bytes | 8-9 chunks |

**Test Data:**
- **3.1:** Paragraph of text (~1000 bytes)
- **3.2:** Two paragraphs (~2000 bytes)
- **3.3:** Full article or code block (~4096 bytes, old threshold)

**Validation:**
- ✅ Log shows "using chunked mode"
- ✅ Chunk markers present: [1/n], [2/n], ..., [n/n]
- ✅ Word boundary splitting (no mid-word breaks)
- ✅ No PowerShell paste mode triggered for any chunk
- ✅ All chunks arrive in order
- ✅ Full content reconstructable from chunks

---

### 4. Edge Cases
**Objective:** Test unusual scenarios

| Test Case | Scenario | Expected Behavior |
|-----------|----------|-------------------|
| 4.1 | Unicode (emoji, multi-byte) | Correct byte counting |
| 4.2 | Special chars (\n, \t, quotes) | Proper escaping |
| 4.3 | Very long word (no boundaries) | Force split at byte limit |
| 4.4 | Empty message | Early return, no injection |
| 4.5 | Null message | Early return, no injection |

**Test Data:**
- **4.1:** `"Hello 👋 Diana 🎉 Testing Unicode! 你好 🚀"` (repeated to reach >500 bytes)
- **4.2:** `"Line1\nLine2\tTabbed\"Quoted\""` (repeated to reach >500 bytes)
- **4.3:** `"A" * 1000` (single 1000-character word)
- **4.4:** `""`
- **4.5:** `null`

**Validation:**
- ✅ 4.1: Emoji/Unicode preserved, correct byte counting (UTF-8)
- ✅ 4.2: Special characters handled correctly
- ✅ 4.3: Forced split occurs, no crash
- ✅ 4.4-4.5: Early return, no injection attempted

---

## Validation Criteria

### Primary Success Criteria:
1. **No PowerShell Paste Mode** - Most critical! Messages should not trigger PowerShell's bracketed paste mode prompt
2. **Complete Message Delivery** - All content arrives correctly, no data loss
3. **Correct Chunking** - Messages > 500 bytes split with proper markers
4. **Performance** - No significant delays or performance degradation

### Secondary Success Criteria:
1. **Word Boundary Splitting** - Chunks break at word boundaries when possible
2. **Logging** - Proper trace logs for debugging
3. **Error Handling** - Graceful handling of edge cases

---

## Test Execution Plan

### Step 1: Verify Build
```powershell
# Ensure application is rebuilt with MaxChunkSize=500
cd H:\DevLaptop\ClarionPowerShell\MultiTerminal
dotnet build --configuration Debug
```

### Step 2: Launch Application
```powershell
# Start MultiTerminal with logging enabled
.\bin\Debug\net8.0-windows\MultiTerminal.exe
```

### Step 3: Enable Trace Logging
- Check if TerminalControl logging is enabled
- Monitor output for "InjectInputAsync", "chunked mode", "single mode" messages

### Step 4: Execute Test Scenarios
For each test case:
1. Send message via inter-terminal chat or MCP tool
2. Observe PowerShell terminal behavior
3. Check for paste mode prompt (should NOT appear)
4. Verify message content received
5. Review logs for expected behavior

### Step 5: Document Results
- Record pass/fail for each test case
- Note any unexpected behaviors
- Capture screenshots if issues occur
- Document performance observations

---

## Test Environment

**Platform:** Windows
**Terminal:** PowerShell
**Application:** MultiTerminal
**Implementation File:** `Controls\TerminalControl.cs`
**Key Constant:** `MaxChunkSize = 500` (line 270)

---

## Expected Log Output

### For Small Messages (< 500 bytes):
```
InjectInputAsync called, text length=250
InjectInputAsync: using single mode
InjectSingleInputAsync called
Writing text to ConPTY pipe (no newline)...
Text written, now triggering Enter via xterm.js...
Calling SendEnterViaXtermAsync...
SendEnterViaXtermAsync completed with result: True
InjectSingleInputAsync completed successfully
```

### For Large Messages (> 500 bytes):
```
InjectInputAsync called, text length=1200
InjectInputAsync: using chunked mode (1200 bytes)
Chunking message: 1200 bytes into 3 chunks
InjectSingleInputAsync called
[Chunk 1 injection logs]
InjectSingleInputAsync completed successfully
[Delay]
InjectSingleInputAsync called
[Chunk 2 injection logs]
InjectSingleInputAsync completed successfully
[Delay]
InjectSingleInputAsync called
[Chunk 3 injection logs]
InjectSingleInputAsync completed successfully
```

---

## Success Metrics

| Metric | Target |
|--------|--------|
| Test Cases Passed | 100% |
| PowerShell Paste Mode Triggered | 0 times |
| Message Delivery Success Rate | 100% |
| Data Loss/Corruption | 0 occurrences |
| Performance Degradation | < 10% vs baseline |

---

## Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| Chunks too small (500 bytes) | More overhead, slower delivery | Monitor performance, adjust if needed |
| Word boundary split fails | Mid-word breaks | Fallback to byte-based split implemented |
| Unicode byte counting wrong | Data corruption | UTF-8 encoding used correctly |
| PowerShell still triggers paste mode | Fix ineffective | Reduce MaxChunkSize further if needed |

---

## Rollback Plan

If tests fail:
1. Identify root cause (threshold too high? implementation bug?)
2. Adjust MaxChunkSize lower (e.g., 400 bytes) if needed
3. Review SplitIntoChunks logic for byte counting accuracy
4. Check for PowerShell version-specific behaviors

---

## Test Data Generation

For automated testing, use these PowerShell snippets:

```powershell
# Generate message of specific size
function New-TestMessage {
    param([int]$Size)
    "Test " * ($Size / 5)  # Approximate size with words
}

# Generate message exactly N bytes
function New-ExactMessage {
    param([int]$Bytes)
    $text = ""
    while ([System.Text.Encoding]::UTF8.GetByteCount($text) -lt $Bytes) {
        $text += "X"
    }
    $text.Substring(0, $text.Length - ([System.Text.Encoding]::UTF8.GetByteCount($text) - $Bytes))
}

# Test examples
New-TestMessage -Size 250    # Small message
New-TestMessage -Size 500    # Boundary
New-TestMessage -Size 1000   # Large message
New-ExactMessage -Bytes 499  # Exactly 499 bytes
New-ExactMessage -Bytes 501  # Exactly 501 bytes
```

---

## Notes

- PowerShell's bracketed paste mode typically triggers at 500-600 characters
- Using 500 bytes as threshold provides safety margin
- Chunk markers ([n/total]) add ~9 bytes overhead per chunk
- SplitIntoChunks reserves 10 bytes for markers (effectiveMaxBytes = 490)
- Word boundary splitting improves readability but may not always be possible

---

## Test Execution Checklist

- [ ] Build application with MaxChunkSize=500
- [ ] Launch MultiTerminal application
- [ ] Enable trace logging
- [ ] Execute Test Scenario 1 (Small Messages)
- [ ] Execute Test Scenario 2 (Boundary Testing)
- [ ] Execute Test Scenario 3 (Large Messages)
- [ ] Execute Test Scenario 4 (Edge Cases)
- [ ] Verify no PowerShell paste mode triggered
- [ ] Verify all messages delivered correctly
- [ ] Review logs for expected behavior
- [ ] Document results
- [ ] Mark task complete or identify issues

---

**Test Plan Created:** 2026-02-06
**Tested By:** Diana
**Status:** Ready for execution
