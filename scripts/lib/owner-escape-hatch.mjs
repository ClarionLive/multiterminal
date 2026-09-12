// owner-escape-hatch.mjs — the ONE deliberate way to act as the Owner instead of the bot
// (task b42b1883, item 8, agent-side half).
//
// WHY THIS FILE EXISTS, AND WHAT WAS ALREADY WRONG. Owner decision 2026-09-08 was "bot by default,
// Owner as an EXPLICIT escape hatch", and the plan records that the hatch must be explicit and loud,
// "never a silent fallback reachable by accident". Before this file there was no hatch at all, and
// gh-multiterminal.mjs's header ALREADY CLAIMED there was one: its rule (2) — "an existing GH_TOKEN
// is never overwritten" — is described there as "the Owner escape hatch the plan insists must be
// explicit". It is not one, for a reason that matters:
//
//   To use rule (2) you must ALREADY HOLD the Owner's personal access token and put it in your
//   environment. That is precisely the credential-handling this entire ticket exists to abolish. An
//   escape hatch whose entry fee is "first obtain and export a long-lived PAT" is not an escape
//   hatch; it is the old problem with a new name.
//
// The real hatch needs no credential at all: `gh` is already signed in as the Owner in its own
// keyring, so acting as the Owner just means NOT injecting a bot token. That is a decision, not a
// secret, which is why it belongs in a named environment variable.
//
// ── WHY UNRECOGNIZED VALUES DISABLE, WHEN isSigningEnabled() DOES THE OPPOSITE ────────────────────
// sign-comment.mjs's isSigningEnabled treats an unrecognized MULTITERMINAL_GH_SIGN as ON, and says
// so. That is right for signing: the failure mode of a typo is "you get a signature you did not ask
// for", which costs nothing.
//
// Here the polarity is reversed and so is the safe default. The failure mode of a typo would be
// "your words are published under a real person's name". `MULTITERMINAL_GH_AS_OWNER=flase` must
// therefore leave you as the bot. This is a DELIBERATE divergence from the neighbouring convention,
// not an oversight — an escape hatch that opens on a misspelling is reachable by accident, which is
// the one property the plan forbids by name. Unrecognized values warn loudly AND stay closed.
//
// ── WHAT THIS DOES NOT AND CANNOT CLOSE ───────────────────────────────────────────────────────────
// A caller that invokes the real `gh` directly — by absolute path, or with the shim absent from PATH
// — bypasses every line of this file and acts as the Owner in total silence. No wrapper can intercept
// that; it is not a wrapper's failure, it is the limit of wrapping. The hatch makes the DELIBERATE
// route explicit and loud; it does not make the accidental route impossible. Item 9's audit is the
// complement (it proves no bot credential is lying around), and `gh auth status` remains the ground
// truth for who `gh` would act as.

/** The one variable. Named for gh because that is the motivating path; the git helper honours it too. */
export const OWNER_HATCH_ENV = 'MULTITERMINAL_GH_AS_OWNER';

const AFFIRMATIVE = ['1', 'true', 'on', 'yes', 'enabled'];
const NEGATIVE = ['0', 'false', 'off', 'no', 'disabled'];

/**
 * True only when the hatch was opened ON PURPOSE.
 *
 * Absent, empty, and every negative spelling mean "stay as the bot". An unrecognized value ALSO means
 * "stay as the bot", and warns — see the polarity note in the header.
 *
 * @param {Record<string,string|undefined>} env
 * @param {(m: string) => void} [warn]
 * @returns {boolean}
 */
export function isOwnerHatchRequested(env = {}, warn = () => {}) {
  const raw = env[OWNER_HATCH_ENV];
  if (raw === undefined || raw === '') return false;

  const v = String(raw).trim().toLowerCase();
  if (AFFIRMATIVE.includes(v)) return true;
  if (NEGATIVE.includes(v)) return false;

  warn(`${OWNER_HATCH_ENV}='${raw}' is not recognized, so it is being IGNORED and you are still the `
    + `bot. Set it to one of ${AFFIRMATIVE.join('/')} if you really meant to act as the Owner.`);
  return false;
}

/**
 * The account `gh` would act as, read OFFLINE from its own config.
 *
 * Deliberately a file read rather than `gh auth status`: this runs on the path to every hatched
 * command, and spawning a process (or worse, making a network call) to decorate a warning would be
 * a new failure mode on a path whose entire job is to not break things. A null result degrades the
 * warning from naming the account to describing it, which is a smaller loss than a hang.
 *
 * @param {object} [io]
 * @param {Record<string,string|undefined>} [io.env]
 * @param {(p: string) => string} [io.readFileText]
 * @returns {string|null}
 */
export function readGhActiveAccount(io = {}) {
  const { env = {}, readFileText } = io;
  if (typeof readFileText !== 'function') return null;

  for (const candidate of ghHostsCandidates(env)) {
    let text;
    try {
      text = readFileText(candidate);
    } catch {
      continue;
    }
    const account = parseActiveAccount(text);
    if (account) return account;
  }
  return null;
}

/** Config locations gh actually uses, most specific first. */
export function ghHostsCandidates(env = {}) {
  const out = [];
  if (env.GH_CONFIG_DIR) out.push(`${env.GH_CONFIG_DIR}/hosts.yml`);
  if (env.APPDATA) out.push(`${env.APPDATA}/GitHub CLI/hosts.yml`);
  if (env.XDG_CONFIG_HOME) out.push(`${env.XDG_CONFIG_HOME}/gh/hosts.yml`);
  if (env.HOME) out.push(`${env.HOME}/.config/gh/hosts.yml`);
  if (env.USERPROFILE) out.push(`${env.USERPROFILE}/.config/gh/hosts.yml`);
  return out;
}

/**
 * Pull the active account out of gh's hosts.yml without a YAML dependency.
 *
 * The file nests a `users:` MAP whose keys are every account ever authenticated, and a scalar
 * `user:` naming the active one. Only the scalar has a value on its line, which is what separates
 * them — matching `users:` members here would report whichever account happened to sort first, and
 * a warning that names the WRONG person is worse than one that names nobody.
 *
 * @param {string} text
 * @returns {string|null}
 */
export function parseActiveAccount(text) {
  if (typeof text !== 'string') return null;
  for (const line of text.split(/\r?\n/)) {
    const m = /^\s*user:\s+(\S+)\s*$/.exec(line);
    if (m) return m[1];
  }
  return null;
}

/**
 * The stderr lines shown when the hatch is open. Shared so the gh shim and the git credential helper
 * cannot drift into describing the same decision differently.
 *
 * Says the CONSEQUENCE, not just the cause — the defect Owner ruling 2026-09-12 (item 11) called out
 * in the other warning on this path: naming a missing credential tells a reader nothing about what
 * the command is about to DO, or under whose name.
 *
 * @param {string|null} account   Account gh would act as, when known.
 * @param {string} [what]         What is about to happen, in the caller's own terms.
 * @returns {string[]}
 */
export function ownerHatchNotice(account, what = 'Anything this command publishes') {
  const who = account ? `the account '${account}'` : 'whichever account gh is signed in to';
  return [
    `${OWNER_HATCH_ENV} is set: acting as the OWNER, not as the bot.`,
    `${what} will be attributed to ${who} — a real person, not clarionlive-agent[bot].`,
    `Unset ${OWNER_HATCH_ENV} to go back to the bot identity.`,
  ];
}
