using System.Text;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// Whether keyboard data from xterm.js submitted a prompt: Enter on an input line that had text
    /// typed or pasted into it since the previous submission (task 3d51315b). Pure, so the rules can
    /// be tested without a terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not raised by arrow keys and Enter alone, which is how a menu such as AskUserQuestion is
    /// answered, or by a bare Enter on an empty line.
    /// </para>
    /// <para>
    /// Not raised by a CR inside a bracketed paste (<c>ESC[200~ … ESC[201~</c>), because xterm turns
    /// every line break of a pasted text into CR and Claude Code keeps those as newlines in its input
    /// box (pipeline run 1, debugger: a multi-line paste read as "You replied" before anything was
    /// sent). Not raised by the CR of a backslash-Enter either, Claude Code's typed newline.
    /// </para>
    /// <para>
    /// MT's own typing through <c>TerminalControl.TypeAndSubmitAsync</c> arrives the same way and
    /// does raise it, which is right: that is a prompt too.
    /// </para>
    /// </remarks>
    internal sealed class PromptSubmitDetector
    {
        private bool _lineHasText;
        private bool _afterBackslash;
        private bool _inPaste;
        private bool _afterEscape;
        private bool _inCsi;
        private readonly StringBuilder _csiParams = new StringBuilder();

        /// <summary>Feeds one chunk of keyboard data; returns how many prompts it submitted.</summary>
        public int Feed(byte[] data)
        {
            if (data == null) return 0;

            int submitted = 0;
            foreach (byte b in data)
            {
                if (_inCsi)
                {
                    if (b >= 0x40 && b <= 0x7E)
                    {
                        _inCsi = false;
                        if (b == (byte)'~')
                        {
                            string p = _csiParams.ToString();
                            if (p == "200") _inPaste = true;
                            else if (p == "201") _inPaste = false;
                        }
                    }
                    else if (_csiParams.Length < 16)
                    {
                        _csiParams.Append((char)b);
                    }

                    continue;
                }

                if (_afterEscape)
                {
                    _afterEscape = false;
                    if (b == 0x5B)
                    {
                        _inCsi = true;
                        _csiParams.Clear();
                    }

                    continue;
                }

                switch (b)
                {
                    case 0x1B:
                        _afterEscape = true;
                        break;

                    case 0x0D:
                        if (_inPaste || _afterBackslash)
                        {
                            // A newline inside the prompt, not its submission.
                            _afterBackslash = false;
                            break;
                        }

                        if (_lineHasText) submitted++;
                        _lineHasText = false;
                        break;

                    case 0x03: // Ctrl+C clears the input line.
                        _lineHasText = false;
                        _afterBackslash = false;
                        break;

                    default:
                        if (b >= 0x20 && b != 0x7F)
                        {
                            _lineHasText = true;
                            _afterBackslash = b == (byte)'\\';
                        }

                        break;
                }
            }

            return submitted;
        }
    }
}
