// sign-comment.mjs — appends the authoring agent's name to text `gh` is about to publish
// (task b42b1883, item 7).
//
// WHY THIS EXISTS. One GitHub App is ONE identity: Alice, Charlie and Ezra all post as
// clarionlive-agent[bot]. That is the right trade (an App per agent would mean N keys, N
// installations and N rate-limit pools for cosmetic gain), but it means the bot marker alone tells a
// reader "an agent wrote this" and never "WHICH agent wrote this". The signature restores the second
// half. It is the per-agent attribution that the shared identity gives up.
//
// ⚠️ WHAT THIS IS NOT. This is a BEST-EFFORT REWRITE OF ARGUMENTS, not an invariant. `gh api` posts
// raw JSON and is deliberately NOT intercepted; editor-driven bodies never pass through argv at all.
// An agent that wants to publish unsigned text can trivially do so. Do not describe this as a
// guarantee anywhere — it is a default that makes the common path attributable, and the honest claim
// is exactly that. There is no server-side chokepoint available to make it stronger: MT hands the
// agent a token and the agent talks to GitHub directly, so argv is the only seam that exists.
//
// WHY THE ALLOWLIST IS EXPLICIT RATHER THAN "ANY COMMAND WITH A --body". `gh api` takes `-F` meaning
// FIELD, not body-file. Rewriting by flag name alone would corrupt `gh api` calls. Matching the
// subcommand first is what makes `-F` unambiguous at the point we act on it.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

/**
 * Subcommands whose text a human will read as authored by whoever ran the command.
 *
 * `gh api` is absent on purpose (see the header). `gh release create --notes` is absent because
 * release notes describe a release, not a participant in a conversation — signing them would be
 * noise, not attribution.
 */
export const AUTHORING_SUBCOMMANDS = [
  ['issue', 'comment'],
  ['pr', 'comment'],
  ['issue', 'create'],
  ['pr', 'create'],
  ['pr', 'review'],
];

/** Flags carrying an inline body, longest-first so `--body-file` never matches as `--body`. */
const BODY_FLAGS = ['--body', '-b'];
const BODY_FILE_FLAGS = ['--body-file', '-F'];

/**
 * The signature itself. An em dash then the name, on its own paragraph.
 *
 * The em dash is not decoration: it has to survive Node -> CreateProcessW -> gh's Go runtime on
 * Windows, so the integration test asserts it arrives intact in the CHILD's argv rather than
 * assuming the encoding holds.
 */
export function signatureFor(agentName) {
  const name = String(agentName ?? '').trim();
  return name ? `— ${name}` : null;
}

/**
 * Append the signature unless it is already the last line.
 *
 * IDEMPOTENCY IS EXACT, NOT FUZZY. Only OUR OWN signature suppresses appending. A body already
 * ending in `— Charlie` still gets `— Alice` added, because that is the truthful record: Charlie
 * wrote the words, Alice is the terminal publishing them. Treating any trailing `— Name` as
 * "already signed" would let one agent publish another's text under the other's name.
 */
export function signBody(body, agentName) {
  const signature = signatureFor(agentName);
  if (!signature) return body;

  const text = String(body ?? '');
  const trimmed = text.replace(/\s+$/, '');
  if (trimmed.endsWith(signature)) return text;

  return `${trimmed}\n\n${signature}\n`;
}

/**
 * Which subcommand this argv invokes, or null.
 *
 * Scans left to right for the first pair of COMMAND PATH words naming an authoring subcommand,
 * tolerating gh's inherited flags between them.
 *
 * Two variants of the same bug lived here, and both read as obviously right at the time:
 *
 *   1. "Skip flags, take the first two remaining tokens." This rested on the claim that a flag VALUE
 *      can never appear before the subcommand. gh is cobra-based and accepts its repo flag ahead of
 *      the subcommand path, so `gh --repo owner/name issue comment 31 --body x` yields the words
 *      ['owner/name', 'issue'] and matches nothing.
 *   2. "Require the two words to be ADJACENT." That fixed (1) and left the mirror image: gh equally
 *      accepts an inherited flag INSIDE the command path, so `gh issue --repo owner/name comment`
 *      has no adjacent pair and also matches nothing.
 *
 * Both shipped UNSIGNED with exit 0 — the bot still authored the comment, so identity held while
 * attribution silently did not, which is the half this file exists to provide. Both were measured
 * live rather than theorised.
 *
 * ⚠️ THE REASONING ERROR IS WORTH MORE THAN EITHER FIX, because it is repeatable. Variant (2)'s
 * justification argued only about FALSE POSITIVES: "a false positive needs two separately passed
 * adjacent tokens spelling a pair in the table, and a single --body value is one token however many
 * words it contains." That was true, and remains true. It simply never addressed FALSE NEGATIVES, and
 * a matcher whose job is deciding whether to sign is wrong in either direction. Twelve green facts sat
 * on top of it. So, in both directions:
 *
 *   FALSE POSITIVES: still need two separately passed path words spelling a table pair. A --body value
 *   is one token regardless of its contents, and nextPathWord() skips only three documented inherited
 *   flags — never arbitrary flags — so no flag's value can be promoted into a command word.
 *   FALSE NEGATIVES: the path may now be interrupted by those inherited flags in any spelling gh
 *   accepts (`--repo o/r`, `-R o/r`, `--repo=o/r`, `-Ro/r`, `--help`). On any OTHER flag the scan
 *   stops rather than guessing, which can only lose a signature, never mangle a command.
 *
 * A canonical invocation still matches at index 0, so the common case is untouched. The attached short
 * form `-bvalue` remains unparsed below for the original reason: guessing at another tool's grammar
 * mangles commands, while declining to guess merely fails to sign one.
 */
/**
 * gh's INHERITED flags — the only ones that may legally sit BETWEEN the two words of a command path.
 *
 * ⚠️ This is a deliberate, bounded exception to this file's rule against modelling gh's flag table,
 * and the distinction is what makes it safe. A full flag table would mean guessing at another tool's
 * grammar, where being wrong MANGLES a command. This list is three documented persistent flags, used
 * only to decide whether to KEEP LOOKING for a subcommand — being wrong here can only cause a missed
 * signature, never a corrupted argv. Cost of error is the whole reason the original rule exists.
 */
const INHERITED_FLAGS_WITH_VALUE = ['--repo', '-R'];
const INHERITED_FLAGS_BOOLEAN = ['--help', '-h'];

/**
 * The index of the next COMMAND PATH word at or after `from`, or -1 if the path cannot continue.
 *
 * Returns -1 on any flag that is not a known inherited one: if we cannot tell whether the following
 * bare word is a flag's value or a command word, the safe answer is to stop. Failing to sign is
 * recoverable and visible; signing the wrong token is not.
 */
function nextPathWord(argv, from) {
  for (let k = from; k < argv.length; k++) {
    const t = String(argv[k]);
    if (!t.startsWith('-')) return k;
    if (INHERITED_FLAGS_WITH_VALUE.includes(t)) { k++; continue; }   // `--repo o/r`, `-R o/r`
    if (t.startsWith('--repo=')) continue;                            // `--repo=o/r`
    if (/^-R.+/.test(t)) continue;                                    // attached short form `-Ro/r`
    if (INHERITED_FLAGS_BOOLEAN.includes(t)) continue;                // `--help`, `-h`
    return -1;
  }
  return -1;
}

export function findAuthoringSubcommand(argv) {
  for (let i = 0; i < argv.length; i++) {
    const first = String(argv[i]);
    if (first.startsWith('-')) continue;

    // ⚠️ NOT argv[i + 1]. Requiring literal adjacency was the second half of the same bug: gh accepts
    // an inherited flag BETWEEN the parent and the subcommand, so `gh issue --repo o/r comment` has no
    // adjacent `issue`,`comment` pair and shipped UNSIGNED — bot identity intact, attribution gone,
    // exit 0. Confirmed live against the deployed module, and gh itself resolves that form: `gh issue
    // --repo X comment --help` prints the issue-comment help. Found by pipeline Run 2 (27002183).
    const j = nextPathWord(argv, i + 1);
    if (j < 0) continue;
    const second = String(argv[j]);

    const hit = AUTHORING_SUBCOMMANDS.find((s) => s[0] === first && s[1] === second);
    if (hit) return [...hit];
  }
  return null;
}

/**
 * Locate a flag and where its value lives.
 *
 * Two spellings are handled: `--flag value` (value in the next slot) and `--flag=value` (value
 * inside the same token). The attached short form `-bvalue` is deliberately NOT parsed — telling it
 * apart from a cluster of short boolean flags needs gh's own flag table, and guessing wrong would
 * mangle a command rather than merely fail to sign it.
 */
export function findFlag(argv, names) {
  for (let i = 0; i < argv.length; i++) {
    const tok = String(argv[i]);
    for (const name of names) {
      if (tok === name) {
        return i + 1 < argv.length
          ? { name, flagIndex: i, valueIndex: i + 1, value: String(argv[i + 1]), attached: false }
          : { name, flagIndex: i, valueIndex: -1, value: null, attached: false };
      }
      if (name.startsWith('--') && tok.startsWith(`${name}=`)) {
        return {
          name,
          flagIndex: i,
          valueIndex: i,
          value: tok.slice(name.length + 1),
          attached: true,
        };
      }
    }
  }
  return null;
}

/** True unless the environment switches signing off. Unrecognized values mean ON, and say so. */
export function isSigningEnabled(env = {}, warn = () => {}) {
  const raw = env.MULTITERMINAL_GH_SIGN;
  if (raw === undefined || raw === '') return true;

  const v = String(raw).trim().toLowerCase();
  if (['0', 'false', 'off', 'no', 'disabled'].includes(v)) return false;
  if (['1', 'true', 'on', 'yes', 'enabled'].includes(v)) return true;

  warn(`MULTITERMINAL_GH_SIGN='${raw}' is not recognized; signing stays ON.`);
  return true;
}

/**
 * Rewrite argv so the published text carries the agent's signature.
 *
 * Returns the (possibly unchanged) argument list plus any temporary directory the caller must clean
 * up. Nothing here mutates the input array, and a body file on disk is NEVER edited in place — the
 * agent's own file is the agent's, so a signed copy goes to a temp file and argv points at the copy.
 *
 * @param {string[]} argv            Arguments destined for the real gh.
 * @param {string} agentName         MULTITERMINAL_NAME, or empty when unknown.
 * @param {object} [io]
 * @param {(p: string) => string} [io.readFileText]
 * @param {(dir: string, name: string, text: string) => string} [io.writeTempText]
 * @param {() => string} [io.makeTempDir]
 * @param {(m: string) => void} [io.warn]
 * @returns {{args: string[], tempDir: string|null, signed: boolean, reason: string}}
 */
export function transformArgs(argv, agentName, io = {}) {
  const {
    readFileText = (p) => fs.readFileSync(p, 'utf8'),
    writeTempText = (dir, name, text) => {
      const dest = path.join(dir, name);
      fs.writeFileSync(dest, text, 'utf8');
      return dest;
    },
    makeTempDir = () => fs.mkdtempSync(path.join(os.tmpdir(), 'mt-gh-sign-')),
    warn = () => {},
  } = io;

  const args = [...argv];
  const unchanged = (reason) => ({ args, tempDir: null, signed: false, reason });

  if (!signatureFor(agentName)) return unchanged('no-agent-name');

  const subcommand = findAuthoringSubcommand(argv);
  if (!subcommand) return unchanged('not-an-authoring-subcommand');

  const inline = findFlag(argv, BODY_FLAGS);
  if (inline && inline.valueIndex >= 0) {
    const signed = signBody(inline.value, agentName);
    args[inline.valueIndex] = inline.attached ? `${inline.name}=${signed}` : signed;
    return { args, tempDir: null, signed: true, reason: 'inline-body' };
  }

  const fromFile = findFlag(argv, BODY_FILE_FLAGS);
  if (fromFile && fromFile.valueIndex >= 0) {
    // `-` means stdin. The shim runs gh with inherited stdio precisely so interactive and piped
    // input keep working; consuming that stream here to sign it would take the input away from the
    // command that needs it. Unsigned and loud beats signed and broken.
    if (fromFile.value === '-') {
      warn(`${subcommand.join(' ')}: body is read from stdin, so it will be published UNSIGNED.`);
      return unchanged('stdin-body-file');
    }

    let original;
    try {
      original = readFileText(fromFile.value);
    } catch (err) {
      // gh is about to report this far better than we can, and pre-empting it with our own error
      // would turn a missing-file diagnostic into a shim diagnostic. Pass the argv through untouched.
      warn(`Could not read ${fromFile.value} to sign it (${err?.code ?? 'error'}); leaving it to gh.`);
      return unchanged('body-file-unreadable');
    }

    const dir = makeTempDir();
    const dest = writeTempText(dir, 'signed-body.md', signBody(original, agentName));
    args[fromFile.valueIndex] = fromFile.attached ? `${fromFile.name}=${dest}` : dest;
    return { args, tempDir: dir, signed: true, reason: 'body-file' };
  }

  // No body on the command line at all: gh will open an editor or prompt. Nothing to rewrite.
  warn(`${subcommand.join(' ')}: no --body/--body-file, so the text will be published UNSIGNED.`);
  return unchanged('no-body-flag');
}
