using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Security regression guard for the launch-nonce log-redaction (task fd3437e6,
    /// codex-security-auditor A01/CWE-200). The terminal launch command line embeds
    /// <c>$env:MULTITERMINAL_LAUNCH_NONCE = '&lt;secret&gt;'</c>, and DebugLogService output is readable
    /// by agents via the <c>debug_logs</c> MCP tool — so the value must be scrubbed before it is logged.
    /// These tests exercise the pure <see cref="ConPtyTerminal.RedactLaunchNonceForLog"/> seam so the
    /// scrub is verified without launching a process.
    /// </summary>
    public class ConPtyTerminalNonceRedactionTests
    {
        [Fact]
        public void Redacts_the_nonce_value_but_keeps_non_secret_assignments()
        {
            const string secret = "3c7f9a12b4e6d8f0a1b2c3d4e5f60718";
            string cmd = "\"pwsh\" -Command \"$env:MULTITERMINAL_DOC_ID = 'doc12345'; " +
                         $"$env:MULTITERMINAL_LAUNCH_NONCE = '{secret}'; " +
                         "$env:MULTITERMINAL_NAME = 'Alice'; \"";

            string redacted = ConPtyTerminal.RedactLaunchNonceForLog(cmd);

            Assert.DoesNotContain(secret, redacted);
            Assert.Contains("$env:MULTITERMINAL_LAUNCH_NONCE = '***REDACTED***'", redacted);
            // Non-secret assignments are preserved (docId/name are not secrets).
            Assert.Contains("$env:MULTITERMINAL_DOC_ID = 'doc12345'", redacted);
            Assert.Contains("$env:MULTITERMINAL_NAME = 'Alice'", redacted);
        }

        [Fact]
        public void Leaves_the_null_clear_form_untouched()
        {
            // The "no nonce supplied" branch emits `= $null;` with no quoted value — nothing to redact.
            const string cmd = "\"pwsh\" -Command \"$env:MULTITERMINAL_LAUNCH_NONCE = $null; \"";
            Assert.Equal(cmd, ConPtyTerminal.RedactLaunchNonceForLog(cmd));
        }

        [Fact]
        public void Handles_null_and_empty_input()
        {
            Assert.Null(ConPtyTerminal.RedactLaunchNonceForLog(null));
            Assert.Equal("", ConPtyTerminal.RedactLaunchNonceForLog(""));
        }
    }
}
