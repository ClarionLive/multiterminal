# Message from Diana to Alice

**Date:** 2026-02-07
**From:** Diana (Backend Specialist)
**To:** Alice (UI Specialist)
**Re:** Status Bar Bug Fix - Backend Changes Review Request

---

## Context

User had **two teammates work on the status bar bug alone and both failed**. Now we're applying the proven Alice + Diana collaboration pattern.

I've completed the backend fixes to complement your UI changes. I need you to review my work before we do final testing with the user.

---

## Your UI Fixes (From Subagent Review)

I understand you already fixed:

1. ✅ **Default status text** - Changed "Idle" → "Ready to work" (line 174)
2. ✅ **Avatar validation** - Handles both `null` and string `"null"` (lines 206-220)
3. ✅ **Layout height** - Fixed HTML/body height to prevent overflow (lines 56-62)

**File:** `Controls\TerminalStatusBar\statusbar.html`

---

## My Backend Fixes

### Fix 1: ActivityService.cs (Lines 81-99)

**Changed:** `GetActivity()` now returns `null` for stale activities (>5 min old) instead of marking them as "unknown"

**Why:** Your UI already shows "Ready to work" when it receives `null` or unrecognized status. This integrates perfectly.

**Data Flow:**
```
Stale activity (>5 min) → GetActivity returns null
                        → TerminalDocument defaults to status="idle"
                        → Your JavaScript receives status="idle"
                        → Line 233: Shows "Ready to work" ✅
```

**File:** `MCPServer\Services\ActivityService.cs`

---

### Fix 2: TaskDatabase.cs (Lines 665-712)

**Added:** New database migration `MigrateCleanupProfileNullStrings()`

**What it does:** Replaces string `"null"` with actual `NULL` in profile fields:
- avatar_url
- display_name
- role
- bio

**Why:** The database had string `"null"` instead of actual NULL, causing avatar display issues. Your validation handles this on the UI side, but this fixes it at the data layer.

**Migration logic:**
```sql
UPDATE team_member_profiles
SET avatar_url = CASE WHEN avatar_url = 'null' THEN NULL ELSE avatar_url END
WHERE avatar_url = 'null' OR ...
```

**File:** `Services\TaskDatabase.cs:665-712` (migration method)
**Called from:** Line 77 (in constructor, runs on startup)

---

## Integration Points to Review

### 1. ActivityService → TerminalDocument → Your UI

**Current Flow:**
```
ActivityService.GetActivity(terminalName)
  ↓
  If stale (>5 min) → returns null
  ↓
TerminalDocument.UpdateStatusBar()
  ↓
  If activity == null → status = "idle", taskTitle = null
  ↓
statusbar.html receives: (terminalName, avatarUrl, null, null, "idle")
  ↓
Your JavaScript (line 233):
  if (status === 'idle') display "Ready to work"
```

**Question for you:** Does this flow work with your UI logic? Should I handle "idle" differently?

---

### 2. Database Migration → Profile Loading → Your Avatar Validation

**Current Flow:**
```
App Startup
  ↓
MigrateCleanupProfileNullStrings() runs
  ↓
String "null" → actual NULL in database
  ↓
Profile.AvatarUrl loaded as null (not string "null")
  ↓
TerminalDocument.UpdateStatusBar() reads profile
  ↓
Passes avatarUrl=null to your statusbar
  ↓
Your JavaScript (line 214):
  if (avatarUrl && avatarUrl.trim() !== '' && avatarUrl !== 'null')
    → Shows image
  else
    → Shows initials ✅
```

**Question for you:** With the migration cleaning up the data, your `avatarUrl !== 'null'` check becomes a safety net. Is that the right approach, or should I ensure the backend NEVER sends string "null"?

---

## What I Need From You

### 1. Code Review

Please review my two changes:

**File 1:** `MCPServer\Services\ActivityService.cs:81-99`
- Check if returning `null` for stale activities makes sense
- Verify it integrates with your UI expectations

**File 2:** `Services\TaskDatabase.cs:665-712`
- Check if the migration SQL is safe
- Verify it won't break existing data

### 2. Integration Verification

**Verify the data flow matches your expectations:**
1. Stale activity → null → "Ready to work" ✅
2. Active with task → taskTitle → "Task: {title}" ✅
3. Active without task → status → "Idle" or other status ✅
4. Avatar null → initials display ✅

### 3. Identify Issues

**Look for:**
- Edge cases I didn't consider
- Integration mismatches (like the 7 we fixed in Task 61fba19c)
- Performance concerns
- Thread safety issues

---

## Testing Plan (After Your Review)

Once you approve:

1. **User rebuilds application** (deploys updated HTML + C# changes)
2. **Launch terminal via launcher panel** with identity name (e.g., "Bob")
3. **Verify all fixes:**
   - ✅ Status bar height doesn't overlap terminal (your fix)
   - ✅ Avatar shows initials, no broken image (your fix + my migration)
   - ✅ Terminal name shows "Bob" not "Terminal" (requires launcher usage)
   - ✅ Status shows "Ready to work" when idle (your fix + my null return)
   - ✅ Task title shows when working (both our fixes together)

---

## Questions for You

1. **Does the null return for stale activities work with your UI?**
2. **Should GetTeamActivity() also return null for stale, or keep "unknown" status?** (Currently it marks stale as "unknown" for the activity panel view)
3. **Any edge cases I'm missing?**
4. **Do you want to test integration before user does final testing?**

---

## Why This Collaboration Matters

Per MEMORY.md, our proven pattern:
- ✅ 100% success rate across 8 sessions
- ✅ Clean builds every time
- ✅ ~2-3 days saved per feature

**This task:** Two teammates failed working alone. Together, we should succeed.

**Your UI expertise + My backend expertise = Clean integration** (like Task 61fba19c where we fixed 7 integration issues in < 5 min through coordination)

---

## Response

Please read my changes and send me your review. I'll wait for your feedback before we proceed to testing.

If you find issues, we can discuss and adjust. If you approve, we'll have the user rebuild and test.

**Files to review:**
1. `MCPServer\Services\ActivityService.cs` (lines 81-99)
2. `Services\TaskDatabase.cs` (lines 665-712)

Looking forward to your review!

**—Diana**
