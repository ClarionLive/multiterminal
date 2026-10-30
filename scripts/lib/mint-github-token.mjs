// mint-github-token.mjs — the one place that asks MultiTerminal for a GitHub App installation token
// (task b42b1883, shared by item 4's git credential helper and item 5's gh shim).
//
// WHY SHARED. Both callers need the identical rules: the nonce travels in a header, the body carries
// at most an installation id, every failure is indistinguishable from every other, and nothing is
// retried. Two copies of that would drift, and the failure mode of drift here is a security one — a
// second copy that logs the token, or that treats an empty nonce as worth sending, reintroduces
// exactly what the gate exists to prevent.
//
// THE CONTRACT IS "A TOKEN OR NOTHING". Returning null for 401, 503, a timeout and a dead MT alike is
// deliberate: no caller can act differently on those, and the distinction is only useful to someone
// probing. Callers degrade to their own no-credential path, which for both of them means "behave
// exactly as if MultiTerminal were not involved".
//
// ONE FAILURE IS NOT A FAULT (GH #68): "no App configured" is the normal, permanent state of every
// install that never registered one, and it arrives as the same 503 as a real failure. It is told
// apart only by the server's error text, so it is reported calmly (onNotConfigured, or one neutral
// line) instead of as a refusal. The return value is still null either way.

const DEFAULT_TIMEOUT_MS = 5000;

/**
 * How GitHubAppTokenService's "no App configured" message begins (Services/GitHubAppTokenService.cs,
 * GetInstallationTokenAsync and ListInstallationsAsync). The controller answers it with a 503 like
 * every other InvalidOperationException, so this prefix is the only thing that marks it.
 * scripts/test/mint-github-token.test.mjs fails if the C# text stops starting with it.
 */
export const NOT_CONFIGURED_PREFIX = 'No GitHub App is configured';

/** Shown when no App is configured and the caller passed no onNotConfigured. */
export const NOT_CONFIGURED_NOTICE = 'No MultiTerminal bot account is set up, so your own GitHub login will be used.';

/** Cap on how much of MT's error text reaches stderr. */
const MAX_ERROR_TEXT_CHARS = 300;

/**
 * MT's `error` field from a failed mint, flattened to one printable line and capped, or '' when the
 * body has none. The server documents these messages as never carrying key material; this only keeps
 * a malformed or hostile body from writing control characters or a wall of text into the terminal.
 */
export function readableMintError(bodyText) {
  let error = '';
  try {
    const parsed = JSON.parse(bodyText);
    if (typeof parsed?.error === 'string') error = parsed.error;
  } catch {
    return '';
  }
  // eslint-disable-next-line no-control-regex
  const flat = error.replace(/[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2066-\u2069]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  return flat.length > MAX_ERROR_TEXT_CHARS ? `${flat.slice(0, MAX_ERROR_TEXT_CHARS)}…` : flat;
}

/**
 * Ask MT to mint a short-lived installation token.
 *
 * @param {object} options
 * @param {string} options.nonce          This terminal's MULTITERMINAL_LAUNCH_NONCE. Required; an
 *                                        empty value is refused WITHOUT a request (see below).
 * @param {string|null} [options.installationId]  Omitted means MT's configured default.
 * @param {string} [options.apiUrl]       MT's base URL.
 * @param {number} [options.timeoutMs]    Cap on how long a caller will wait.
 * @param {(msg: string) => void} [options.warn]  Diagnostics sink. Must never receive the token.
 * @param {() => void} [options.onNotConfigured]  Called instead of warning when MT has no App
 *                                        configured, so the caller can word the consequence itself.
 * @returns {Promise<string|null>} The token, or null for every failure.
 */
export async function mintInstallationToken({
  nonce,
  installationId = null,
  apiUrl = 'http://localhost:5050',
  timeoutMs = DEFAULT_TIMEOUT_MS,
  warn = () => {},
  onNotConfigured = null,
} = {}) {
  // An absent nonce means this terminal cannot mint — an adopted terminal was never launched by MT
  // and holds none. Sending the request anyway would put a pointless 401 in MT's log for every
  // operation, and would invite "just let the endpoint accept an empty nonce" as a later fix.
  if (!nonce) {
    warn('No launch nonce in this environment, so no token can be minted for this terminal.');
    return null;
  }

  let response;
  try {
    response = await fetch(`${apiUrl}/api/github/token`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        // A header, not the body: it keeps the secret out of anything that logs request bodies.
        'X-MultiTerminal-Launch-Nonce': nonce,
      },
      body: JSON.stringify(installationId ? { installationId } : {}),
      signal: AbortSignal.timeout(timeoutMs),
    });
  } catch {
    warn('MultiTerminal did not answer the mint request.');
    return null;
  }

  if (!response.ok) {
    // 401 = not an MT-launched terminal, which is normal and permanent for an adopted one.
    //
    // 503 is TWO different things (GH #68), because GitHubAppController.MintToken maps every
    // InvalidOperationException to it:
    //   - "No GitHub App is configured": the normal state of any install that never registered one.
    //     Not a fault, so it gets one neutral line (or the caller's own wording), never "refused".
    //   - anything else (no installation MT can resolve, GitHub refused): a real fault, reported with
    //     MT's own explanation so the reason is visible without calling the endpoint by hand.
    //
    // Still nothing is retried: this module's contract is "a token or nothing", and a caller cannot
    // act differently on the reasons. Naming the CONSEQUENCE is each caller's job, because only the
    // caller knows what it was about to do — see the shim and the credential helper.
    let detail = '';
    try {
      detail = readableMintError(await response.text());
    } catch {
      // An unreadable body only costs the explanation, not the outcome.
    }

    if (response.status === 503 && detail.startsWith(NOT_CONFIGURED_PREFIX)) {
      if (typeof onNotConfigured === 'function') onNotConfigured();
      else warn(NOT_CONFIGURED_NOTICE);
      return null;
    }

    warn(`MultiTerminal refused to mint a token (HTTP ${response.status})${detail ? `: ${detail}` : '.'}`);
    return null;
  }

  try {
    const body = await response.json();
    const token = body?.token;
    return typeof token === 'string' && token.length > 0 ? token : null;
  } catch {
    warn('The mint response could not be read as JSON.');
    return null;
  }
}

export const MINT_DEFAULT_TIMEOUT_MS = DEFAULT_TIMEOUT_MS;
