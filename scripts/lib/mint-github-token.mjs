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

const DEFAULT_TIMEOUT_MS = 5000;

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
 * @returns {Promise<string|null>} The token, or null for every failure.
 */
export async function mintInstallationToken({
  nonce,
  installationId = null,
  apiUrl = 'http://localhost:5050',
  timeoutMs = DEFAULT_TIMEOUT_MS,
  warn = () => {},
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
    // 503 USED TO BE THE EXPECTED STATE and is no longer. This comment previously read "no App
    // configured yet, which is the CORRECT state until the Owner completes item 3, so this path must
    // stay boring rather than alarming" — written while the App did not exist. The App is registered
    // now, so a 503 here means something is actually wrong (no installation MT can resolve, or GitHub
    // refused), and the response body carries MT's own explanation of which.
    //
    // Still only the status is reported, and still nothing is retried: this module's contract is "a
    // token or nothing", and a caller cannot act differently on the reasons. Naming the CONSEQUENCE
    // is each caller's job, because only the caller knows what it was about to do — see the shim and
    // the credential helper.
    warn(`MultiTerminal refused to mint a token (HTTP ${response.status}).`);
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
