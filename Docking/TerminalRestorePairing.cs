#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using MultiTerminal.Models;
using MultiTerminal.Services;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// The persist string a <see cref="TerminalDocument"/> saves in layout.xml (GH #74). It carries
    /// the pane's <see cref="TerminalDocument.PaneKey"/>, so a restored layout entry can be paired with
    /// its saved session by key instead of by position.
    ///
    /// <para>Before #74 the string was the bare type name and every entry looked the same, so
    /// <c>GetContentFromPersistString</c> handed out the saved sessions in order. layout.xml lists
    /// every content (floated ones included), while the session list held only docked terminals, so one
    /// floated terminal shifted every later pane onto the wrong saved session. A layout written before
    /// #74 still has bare names; <see cref="TryParse"/> reads them as "no key", and they are paired by
    /// order as before.</para>
    /// </summary>
    public static class TerminalPersistString
    {
        /// <summary>The bare type name, the whole persist string before GH #74.</summary>
        public static readonly string TypeName = typeof(TerminalDocument).FullName!;

        private const char KeySeparator = ':';

        /// <summary>The persist string for a pane with key <paramref name="paneKey"/>; the bare type
        /// name when there is no key.</summary>
        public static string For(string? paneKey)
            => string.IsNullOrEmpty(paneKey) ? TypeName : TypeName + KeySeparator + paneKey;

        /// <summary>
        /// True when <paramref name="persistString"/> names a terminal pane, with or without a key.
        /// <paramref name="paneKey"/> is the canonical key, or null for a bare name or a key that is
        /// not a well-formed GUID.
        /// </summary>
        public static bool TryParse(string? persistString, out string? paneKey)
        {
            paneKey = null;
            if (persistString == null) return false;
            if (string.Equals(persistString, TypeName, StringComparison.Ordinal)) return true;

            if (persistString.Length > TypeName.Length
                && persistString.StartsWith(TypeName, StringComparison.Ordinal)
                && persistString[TypeName.Length] == KeySeparator)
            {
                paneKey = SessionTerminalsFormat.CanonicalKey(persistString.Substring(TypeName.Length + 1));
                return true;
            }

            return false;
        }

        /// <summary>
        /// The pane keys of the terminal entries in a saved layout, in layout order; keyless entries
        /// are left out. Read before <c>LoadFromXml</c> so <see cref="TerminalRestorePairing"/> knows
        /// which sessions a later entry is keyed to. Throws what <see cref="XmlReader"/>
        /// throws for a layout it cannot read.
        /// </summary>
        public static List<string> ReadLayoutPaneKeys(Stream layout)
        {
            var keys = new List<string>();
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
            using var reader = XmlReader.Create(layout, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && TryParse(reader.GetAttribute("PersistString"), out string? key)
                    && key != null)
                {
                    keys.Add(key);
                }
            }

            return keys;
        }
    }

    /// <summary>
    /// Pairs the terminal entries of a restored layout with the saved sessions (GH #74). Pure: built
    /// from the session list and the keys the layout names, then asked once per terminal entry in
    /// layout order, which is the order DockPanelSuite calls the deserializer in.
    ///
    /// <para>An entry with a key takes the session saved under that key. An entry without one (a
    /// layout from before #74), or whose key no session has, takes the next session in order that no
    /// keyed entry of this layout claims. So a pre-#74 layout pairs exactly as it always did, and a
    /// key that misses cannot take the session a later entry is keyed to. Each session is handed out
    /// at most once; when none is left the entry gets null, and the caller skips it rather than
    /// opening a blank pane.</para>
    /// </summary>
    public sealed class TerminalRestorePairing
    {
        private readonly IReadOnlyList<TerminalSessionInfo> _sessions;
        private readonly bool[] _taken;
        private readonly HashSet<string> _claimedByLayout = new HashSet<string>(StringComparer.Ordinal);

        /// <param name="sessions">The saved sessions, in saved order. Null is treated as empty.</param>
        /// <param name="layoutPaneKeys">The pane key of every terminal entry in the layout being
        /// restored; null and malformed keys are ignored.</param>
        public TerminalRestorePairing(IReadOnlyList<TerminalSessionInfo>? sessions, IEnumerable<string?>? layoutPaneKeys)
        {
            _sessions = sessions ?? Array.Empty<TerminalSessionInfo>();
            _taken = new bool[_sessions.Count];

            if (layoutPaneKeys == null) return;
            foreach (string? key in layoutPaneKeys)
            {
                string? canonical = SessionTerminalsFormat.CanonicalKey(key);
                if (canonical != null) _claimedByLayout.Add(canonical);
            }
        }

        /// <summary>
        /// The session for the next terminal entry, whose key is <paramref name="paneKey"/> (null when
        /// the entry has none), or null when no session is left for it.
        /// </summary>
        public TerminalSessionInfo? Take(string? paneKey)
        {
            string? key = SessionTerminalsFormat.CanonicalKey(paneKey);
            if (key != null)
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (!_taken[i] && string.Equals(KeyOf(i), key, StringComparison.Ordinal))
                    {
                        _taken[i] = true;
                        return _sessions[i];
                    }
                }
            }

            for (int i = 0; i < _sessions.Count; i++)
            {
                if (_taken[i]) continue;
                string? own = KeyOf(i);
                if (own != null && _claimedByLayout.Contains(own)) continue;

                _taken[i] = true;
                return _sessions[i];
            }

            return null;
        }

        /// <summary>
        /// The sessions no layout entry took, in saved order, now marked taken. These exist when the
        /// session list is newer than layout.xml: the list is also saved during a run (a font change,
        /// for one), layout.xml only on a clean exit, so a crash can leave a terminal in the list that
        /// the layout never heard of. Without this the restore dropped it, and the next save lost it
        /// for good (GH #74, pipeline adversary).
        /// </summary>
        public List<TerminalSessionInfo> TakeRemaining()
        {
            var remaining = new List<TerminalSessionInfo>();
            for (int i = 0; i < _sessions.Count; i++)
            {
                if (_taken[i] || _sessions[i] == null) continue;
                _taken[i] = true;
                remaining.Add(_sessions[i]);
            }

            return remaining;
        }

        private string? KeyOf(int index) => SessionTerminalsFormat.CanonicalKey(_sessions[index]?.PaneKey);
    }
}
