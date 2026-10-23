using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The stored form of the open terminal panes (the <c>SessionTerminals</c> setting), task d71bb221,
    /// pipeline Run 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why JSON.</b> The old format wrote every field raw into <c>dir|size|title;...</c>. A tab
    /// title is an agent name, and an agent chooses its own name, so a name such as
    /// <c>Evil|&lt;key&gt;|Evil|&lt;target&gt;</c> shifted the columns on the next start and forged a team
    /// link, while an honest name containing a separator lost its link. JSON escapes every string, so
    /// any value in any field round-trips exactly and can never reach another field. The value is one
    /// line (the encoder escapes control characters), which the line-based settings file needs.
    /// </para>
    /// <para>
    /// <b>Shape:</b> <c>{"v":2,"terminals":[{"dir":..,"size":..,"title":..,"paneKey":..,"helper":..,
    /// "spawnerPaneKey":..,"spawnerName":..}]}</c>. A reader meeting any other <c>v</c> restores nothing
    /// rather than guessing at a format it does not know.
    /// </para>
    /// <para>
    /// <b>Backward compatibility.</b> A value that is not a JSON object is read as the legacy
    /// <c>dir|size|title</c> lines. Every entry keeps its directory, size and title (everything after
    /// the size, rejoined), and NOTHING else: no pane key and no helper link is ever read from a legacy
    /// line, because no field count can tell a writer's columns from a title that contained separators
    /// (pipeline Run 2). The panes mint fresh keys; helper links start again from the next spawn.
    /// </para>
    /// <para>
    /// <b>Keys.</b> After either reader, <see cref="NormalizePaneKeys"/> makes every restored pane key a
    /// well-formed GUID that no other restored pane holds, regenerating the rest, and clears every link
    /// that points at an ambiguous or malformed key.
    /// </para>
    /// <para>
    /// <b>Downgrade.</b> A build older than this one reads the JSON value as one malformed legacy
    /// entry, restores no panes and starts empty; it does not crash.
    /// </para>
    /// </remarks>
    internal static class SessionTerminalsFormat
    {
        /// <summary>The version this build writes and the only one it reads.</summary>
        internal const int Version = 2;

        /// <summary>A stored string longer than this is treated as absent. Far above any real path or name.</summary>
        internal const int MaxFieldLength = 4096;

        private const string VersionField = "v";
        private const string TerminalsField = "terminals";
        private const string DirField = "dir";
        private const string SizeField = "size";
        private const string TitleField = "title";
        private const string PaneKeyField = "paneKey";
        private const string HelperField = "helper";
        private const string SpawnerPaneKeyField = "spawnerPaneKey";
        private const string SpawnerNameField = "spawnerName";

        /// <summary>Serialises the panes as one line of versioned JSON. Null entries are skipped.</summary>
        internal static string Write(IEnumerable<TerminalSessionInfo> terminals)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteNumber(VersionField, Version);
                w.WriteStartArray(TerminalsField);
                foreach (var t in terminals ?? Enumerable.Empty<TerminalSessionInfo>())
                {
                    if (t == null) continue;
                    w.WriteStartObject();
                    w.WriteString(DirField, t.WorkingDirectory ?? string.Empty);
                    w.WriteNumber(SizeField, t.FontSize);
                    WriteOptional(w, TitleField, t.CustomTitle);
                    WriteOptional(w, PaneKeyField, t.PaneKey);

                    // A link is written only with the helper name it is checked against on restore.
                    if (!string.IsNullOrEmpty(t.HelperName))
                    {
                        w.WriteString(HelperField, t.HelperName);
                        WriteOptional(w, SpawnerPaneKeyField, t.SpawnerPaneKey);
                        WriteOptional(w, SpawnerNameField, t.SpawnerName);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Reads either format. Returns the panes in stored order (possibly empty), or null when the
        /// value is a JSON object of an unknown version or shape. Never throws.
        /// </summary>
        internal static List<TerminalSessionInfo> Read(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return new List<TerminalSessionInfo>();

            if (data.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    return ReadJson(doc.RootElement);
                }
                catch (JsonException)
                {
                    // Not JSON after all: a legacy directory that happens to start with '{'.
                }
            }

            return ReadLegacy(data);
        }

        private static List<TerminalSessionInfo> ReadJson(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(VersionField, out var v)
                || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out int version)
                || version != Version
                || !root.TryGetProperty(TerminalsField, out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<TerminalSessionInfo>();
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!e.TryGetProperty(SizeField, out var sizeEl)
                    || sizeEl.ValueKind != JsonValueKind.Number
                    || !sizeEl.TryGetSingle(out float size)
                    || float.IsNaN(size) || float.IsInfinity(size))
                {
                    continue;
                }

                var info = new TerminalSessionInfo
                {
                    WorkingDirectory = Str(e, DirField) ?? string.Empty,
                    FontSize = size,
                    CustomTitle = NonEmpty(Str(e, TitleField)),
                    PaneKey = NonEmpty(Str(e, PaneKeyField)),
                    HelperName = NonEmpty(Str(e, HelperField)),
                };

                if (info.HelperName != null)
                {
                    info.SpawnerPaneKey = NonEmpty(Str(e, SpawnerPaneKeyField));
                    info.SpawnerName = NonEmpty(Str(e, SpawnerNameField));
                }

                result.Add(info);
            }

            return result;
        }

        /// <summary>The legacy pipe/semicolon lines. See the class remarks for the trust rule.</summary>
        private static List<TerminalSessionInfo> ReadLegacy(string data)
        {
            var parsed = new List<(string[] Parts, float Size)>();
            foreach (string entry in data.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split('|');
                if (parts.Length >= 2 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float size))
                {
                    parsed.Add((parts, size));
                }
            }

            // No pane key or helper link is EVER taken from a legacy line (pipeline Run 2). No field
            // count can tell a writer's own columns from a title that held separators: a pre-ticket
            // three-field writer turns the title "x|<guid>|Evil|<guid>" into exactly the six parts a
            // key-writing build would have produced, and a session holding only that pane looks
            // key-written throughout. The only builds that wrote six-part lines never shipped, so
            // dropping their links costs nothing; every pane keeps its directory, size and title
            // (everything after the size, rejoined, because the separators were part of the name)
            // and mints a fresh key.
            var result = new List<TerminalSessionInfo>();
            foreach (var (parts, size) in parsed)
            {
                var info = new TerminalSessionInfo { WorkingDirectory = parts[0], FontSize = size };
                if (parts.Length >= 3)
                {
                    info.CustomTitle = NonEmpty(string.Join("|", parts, 2, parts.Length - 2));
                }

                result.Add(info);
            }

            return result;
        }

        /// <summary>
        /// Makes every restored pane key a well-formed GUID ("N" form, lower case) that is unique in
        /// this session. A malformed key, or a key two panes share, is replaced by a fresh one on
        /// EVERY pane holding it — which of them was the original is unknowable. A link pointing at
        /// such a key, at a malformed key or at its own pane loses its spawner key; the recorded
        /// spawner name stays, so the rail says "Helpers of &lt;name&gt;" rather than attaching the
        /// helper to a pane. An absent key stays absent (the pane mints its own). A pane with no
        /// helper name keeps no link at all.
        /// </summary>
        internal static void NormalizePaneKeys(List<TerminalSessionInfo> terminals)
        {
            if (terminals == null) return;

            var originals = terminals.Select(t => CanonicalKey(t?.PaneKey)).ToList();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var k in originals.Where(k => k != null))
            {
                counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
            }

            for (int i = 0; i < terminals.Count; i++)
            {
                var t = terminals[i];
                if (t == null) continue;

                string own = originals[i];
                if (!string.IsNullOrEmpty(t.PaneKey))
                {
                    t.PaneKey = own != null && counts[own] == 1 ? own : Guid.NewGuid().ToString("N");
                }

                if (string.IsNullOrWhiteSpace(t.HelperName))
                {
                    t.HelperName = null;
                    t.SpawnerPaneKey = null;
                    t.SpawnerName = null;
                    continue;
                }

                string spawner = CanonicalKey(t.SpawnerPaneKey);
                bool usable = spawner != null
                    && !string.Equals(spawner, own, StringComparison.Ordinal)
                    && (!counts.TryGetValue(spawner, out int holders) || holders == 1);
                t.SpawnerPaneKey = usable ? spawner : null;
            }
        }

        /// <summary>The key in canonical "N" form, or null when it is not a well-formed GUID in that form.</summary>
        internal static string CanonicalKey(string key)
            => !string.IsNullOrEmpty(key) && Guid.TryParseExact(key, "N", out var g) ? g.ToString("N") : null;

        private static void WriteOptional(Utf8JsonWriter w, string name, string value)
        {
            if (!string.IsNullOrEmpty(value)) w.WriteString(name, value);
        }

        private static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
               && p.GetString() is string s && s.Length <= MaxFieldLength
                ? s
                : null;

        private static string NonEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
