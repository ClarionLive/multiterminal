# Verification discipline — when a green test is not evidence

Produced by the 2026-09-15 program (`7806024f` → `c28e6177`, `f420feeb`, `837e16a3`), which found
**nine separate cases of a description outrunning the code beneath it** — three during a pipeline run,
four during an Owner testing pass, and, most usefully, **three inside the tests written to prevent
exactly that**. Credited to the program rather than to any agent; every rule below was earned by
someone discovering it in their own work, usually while green.

## The core rule

> **The unexecuted parts of a test — its name, its doc comment, its failure message, its lists — are
> prose. And prose in a test file is the least audited prose in a codebase, because the green tick is
> read as covering it.**

In all three in-test cases the executable part was **sound every time**. What was wrong was the
sentence above it:

- a doc comment claimed the fact "exists to fail if anyone swaps the comparer for an ignore-case one".
  The swap was run. It stayed green — the ids were digits, and digits have no case.
- a census asserted a method **count**, which passed, while naming a method that does not exist
  (`OnSpawnRequested` for what is actually `OnStartScreenNewProject`). A census whose names nobody
  verifies is the defect it exists to catch, relocated into a test.
- a class doc promised that one specific line "will be rewritten" by the fix. It was not. All five
  facts stayed green, passing for a reason the fix did not supply.

**Running the suite cannot catch any of these.** That is the whole point.

## Falsification is not a formality — it decides what the prose may claim

Write the test, then **break the code and watch it go red**. Not as ceremony: the result of that run
is what the doc comment is *allowed to say*. If you claim a fact catches X, make X happen. Twice on
this program an author discovered their own test was weaker than its description only by doing this.

### A test can pass by luck

A cross-talk fact passed against the pre-fix take-any implementation because `ConcurrentDictionary`
happened to enumerate the right bucket for those two keys. "It went red when I broke the code" is
evidence **only if you also know it could not have gone green by accident**. The fix was not to re-run
until convinced — it was to force the broken implementation to be wrong in at least one direction
*whichever way the buckets fall*.

### A test that hangs is worse than one that fails

Awaiting the task you expect, under a broken registry, **hangs the suite**. Checking `IsCompleted`
**fails** it. A hang produces no verdict, gets blamed on CI flakiness, and ends in quarantine. Most
people write the `await`.

### The obvious fix to a verification failure is often another verification failure

Three times a natural repair would have hidden the problem rather than closed it. The sharpest: a
review found tests passing `.Name` into a `docId` parameter (both `string`, so it compiled silently).
Swapping in `.DocId` makes them pass — **but they would have passed before the fix too**, so the file
still could not distinguish the new key from the old. The real repair needed a *discriminator*: two
live rows with **byte-identical names and different panes**, where a name-based predicate must answer
true and only a docId answers false.

## Source censuses: strip comments, and know both failure directions

A census that scans raw file text is satisfiable by **prose**, and the defeating edit is usually the
most natural future edit — the comment a person writes when removing the thing:

```
// Canonicalisation moved upstream, so request.AgentName.Trim() is no longer done here.
```

Demonstrated, not theorised: deleting the real code and leaving that comment kept the census green.

**Both directions exist and they are not equally bad:**

| Shape | Failure | Cost |
|---|---|---|
| `Contains("X")` unstripped | satisfied by a comment mentioning `X` | **hides** a defect |
| `DoesNotContain("X")` unstripped | tripped by a *refusal comment* naming `X` | **manufactures** a failure |

The second is worse, and it is actively hostile in this codebase: writing "do NOT use X here, because…"
above the code is **house style**. Such a census punishes the correct documenting instinct, and the
natural response to a red test is to change the *code*. Prefer a structural pin to a textual one; if
the guarantee can be expressed as behaviour rather than text, the whole class disappears.

Also: **scope the scan to the method, not the file** (a file-wide scan is satisfied by an unrelated
site elsewhere), and **assert the extraction succeeded** (`body.Length > 2000`) — otherwise a rename
makes every other fact in the file vacuous against an empty string.

## Pinning a known gap requires a self-destruct line

A test asserting that a defect *is present* goes red the day someone fixes it — and in a repo whose
doctrine is "red means regression, fix the code not the test", that teaches the next person to delete
the improvement.

Pinning a gap is allowed **only** with an explicit line:

> `🔴 WHEN THIS GOES RED, DELETE THIS TEST AND RETURN TO <ticket> — THE GAP HAS BEEN CLOSED, THIS IS
> NOT A REGRESSION.`

Without it, a gap is indistinguishable from an invariant. The default is to keep gap-descriptions out
of the suite entirely and put them in ticket evidence; pin one only when the pin forces a conscious
decision later, which a TODO cannot do because a TODO cannot fail.

## Mint the id before you cite it

A ticket id was written into three code comments **before the ticket existed** — guessed as
`12eb7de7`, where the real id turned out to be `12eb1e7c`. Caught before commit, by chance.

This is worth its own entry because of how it fails, not what it is:

- **Review cannot catch it.** A reviewer seeing a plausible ticket id has no reason to look it up.
  Every other citation defect on this list is visible by comparing a sentence against code; this one
  is only visible by leaving the file.
- **It degrades into a dangling pointer** that outlives everyone who could have spotted it. A
  reference to a ticket that does not exist reads exactly like a reference to one that does.
- The related failure with the same surface but a different cause: a citation that points at
  something **real but irrelevant**. One comment justified a `Trim` by citing a method that trims a
  `raw_type` token, not a name — the cited precedent did not do what it was cited for, so the trim
  was defending a path nothing travels.

**Rule: mint the id first, then write the citation.** Never the other way round. And when citing an
existing symbol as precedent, open it.

## A permissive comparison in front of an exact one is a bug generator

If a predicate decides *whether* to act and a lookup then decides *what to act on*, they must use the
**same comparison**. A predicate more permissive than its lookup can approve work the lookup then
fails to find.

The generalisation that produced this: one trim, in one predicate, against four other sites that
agreed on untrimmed ordinal-ignore-case. The bug was **not a missing trim somewhere** — it was one
**extra** trim. The instinct to "just normalise everywhere" would have merged two identities inside
the comparison that routes messages.

Ask of every hop: is this exact **by decision**, or exact **by default**? Exact-by-default is a latent
defect — it changes nothing observable until an unrelated change (an int id becoming a Guid, a Guid
becoming a name) activates it, at which point the wrong comparison is already in place and invisible.

## Pre-authorising an agent to act on a message inherits three traps

Any design where a trusted channel grants authority to a later, less-trusted message:

1. **The correlation id must be unguessable.** A counter grants nothing — and the obvious
   implementation *is* a counter.
2. **Single-use and expiry are enforced by the model, not by code.** A sentence is not a capability.
3. **The real property reduces to who can reach the transport.** If it is safe, it is safe because the
   transport is local and mediated — not because the scoping is strong.

Describe such a design as a **bounded convention enforced by the recipient's compliance**, never as a
security mechanism. Weaker and true beats stronger and wrong.

## Stored transcripts cannot answer questions about what the harness adds at request time

The CLI **strips** injected framing when persisting a transcript. An investigation using stored
transcripts as its oracle therefore concludes the framing is *never* applied — confidently, silently,
and wrongly.

This was caught only by controlling against a transcript of a message **known** to have arrived framed,
which recorded it as unframed. It generalises to anything the harness adds at request time. When a
result looks instantly confirmatory, that is the condition under which to test whether your instrument
can lie.

**Corollary:** evidence must be an **out-of-band artifact**, never a reply. A reply *is* noting. And
distinguish four outcomes rather than two — did it, noted it, *asked and blocked*, lost — because
"asked and blocked" is indistinguishable from "noted" at the sender and looks like a hung process in
production.

## Sufficient is not the same as necessary — ask what makes X the right thing to check

A reviewer was asked to check whether a lock was **sufficient**. It was, and they said so. Neither
author nor reviewer asked the prior question: **is there any concurrency here at all?** There was
not — every caller was UI-thread confined, so the race being fixed was real in the code and
unreachable by any caller.

Nothing was wasted (the fix stayed: confinement was an accident of every current caller, not a
property of the class, and *unreachable is not correct*). But both passes answered the question in
front of them without asking whether it was the right one.

**A reviewer handed "check X" should ask what makes X the thing worth checking.** The brief is
written by someone who already has a model of the problem, and that model is the least reviewed
artifact in the process.

## Review: the author and the reviewer find disjoint sets

Measured on this program. An author self-reported four defects in his own work immediately before
review; an independent reviewer, deliberately not shown that list, found two more.

**Overlap: zero.** Six defects, no intersection, neither pass redundant, neither alone sufficient.

Both predictions about which defects would be "obvious to fresh eyes" were wrong — including a
reviewer who re-derived the correct fact *more completely than the author's prose had stated it*,
without noticing the prose was narrower than the truth.

Practical consequences:

- **Do not hand a reviewer the author's list.** A reviewer given a list checks the list. Withholding it
  keeps the pass independent *and* makes the overlap question answerable.
- **Ask which defects were missed**, not only which were caught. The misses are the ones no amount of
  review will find, and therefore the ones needing a structural answer — a type, an analyser, a test —
  rather than another pair of eyes.
- A self-report made when it costs the author something (items minutes from an irreversible gate) is
  worth more than the same report made safely.

## Finally: a suite that is green about a path nothing has executed

`1085/1085` on source-and-unit reasoning is a weaker claim than the number sounds, on work whose entire
premise was *"no test can see this path."* State it. It is the sentence that matters most at a gate
that cannot be reopened.
