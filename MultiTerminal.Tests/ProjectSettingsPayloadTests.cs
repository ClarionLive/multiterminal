using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiTerminal.Models;
using MultiTerminal.ProjectSettings;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The project settings form's JSON contract (ticket e20e2ce0): ProjectSettingsPayload in both
    /// directions, and the page/C# string contract no compiler checks.
    ///
    /// <para>Not covered: the WebView itself, the dropdown contents (live services) and whether the page's
    /// JS reads every field it is sent. The census facts read the source with comments stripped, so a
    /// comment naming a field or message type cannot satisfy or trip them.</para>
    /// </summary>
    public sealed class ProjectSettingsPayloadTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string ReadStripped(params string[] parts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), $"Could not locate '{path}'.");
            string src = File.ReadAllText(path);
            src = Regex.Replace(src, @"<!--.*?-->", " ", RegexOptions.Singleline);
            src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        private static string Page() => ReadStripped("ProjectSettings", "project-settings.html");

        private static Project SampleProject() => new Project
        {
            Id = "p-1", Name = "Sample", Path = @"C:\src\sample", GitAutoCommit = true, QuietStart = null,
            ClaudeProfile = "Work", Status = "parked",
        };

        [Fact]
        public void Load_carries_exactly_the_editable_fields_with_booleans_as_booleans()
        {
            var json = ProjectSettingsPayload.BuildLoad(SampleProject(), null, new ProjectSettingsOptions(), isDark: true);
            using var doc = JsonDocument.Parse(json);
            var fields = doc.RootElement.GetProperty("fields");

            var names = fields.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(ProjectSettingsService.EditableFields.OrderBy(n => n, StringComparer.Ordinal), names);
            Assert.Equal(JsonValueKind.True, fields.GetProperty("gitAutoCommit").ValueKind);
            Assert.Equal(JsonValueKind.False, fields.GetProperty("quietStart").ValueKind); // null reads as off
            Assert.Equal("parked", fields.GetProperty("status").GetString());
            Assert.Equal("dark", doc.RootElement.GetProperty("theme").GetString());
        }

        /// <summary>
        /// Repository watch (ticket df58b35d): the checkbox loads as a boolean (null reads as off), the interval
        /// select loads 15 for a NULL column, and the select's options are exactly the allowed intervals, each a
        /// value the save accepts.
        /// Falsified: making ProjectSettingsService.ReadField return the raw nullable interval turned this red at
        /// the "15" assertion (and An_untouched_form_save_writes_nothing, where the NULL column then counted as
        /// changed against the form's 15).
        /// </summary>
        [Fact]
        public void Load_carries_repository_watch_fields_and_the_interval_choices()
        {
            var json = ProjectSettingsPayload.BuildLoad(SampleProject(), null, new ProjectSettingsOptions(), isDark: true);
            using var doc = JsonDocument.Parse(json);
            var fields = doc.RootElement.GetProperty("fields");

            Assert.Equal(JsonValueKind.False, fields.GetProperty("repoWatchEnabled").ValueKind);
            Assert.Equal("15", fields.GetProperty("repoWatchIntervalMin").GetString());
            Assert.Equal("", fields.GetProperty("repoWatchRepos").GetString());

            var choices = doc.RootElement.GetProperty("options").GetProperty("repoWatchIntervals").EnumerateArray()
                .Select(o => (Value: o.GetProperty("value").GetString(), Label: o.GetProperty("label").GetString())).ToList();
            Assert.Equal(new[] { "0", "5", "15", "30", "60" }, choices.Select(c => c.Value));
            Assert.Equal("At startup and launch only", choices[0].Label);
            Assert.Equal("1 hour", choices[4].Label);
            Assert.All(choices, c => Assert.True(RepoWatchSettings.TryParseInterval(c.Value, out int? m, out _) && m.HasValue));

            var stored = SampleProject();
            stored.RepoWatchEnabled = true;
            stored.RepoWatchIntervalMin = 0;
            stored.RepoWatchRepos = "o/r";
            using var doc2 = JsonDocument.Parse(ProjectSettingsPayload.BuildLoad(stored, null, new ProjectSettingsOptions(), isDark: false));
            var fields2 = doc2.RootElement.GetProperty("fields");
            Assert.Equal(JsonValueKind.True, fields2.GetProperty("repoWatchEnabled").ValueKind);
            Assert.Equal("0", fields2.GetProperty("repoWatchIntervalMin").GetString());
            Assert.Equal("o/r", fields2.GetProperty("repoWatchRepos").GetString());
        }

        /// <summary>
        /// The interval select names the option list the payload sends; a typo here leaves it empty with no error.
        /// Falsified: data-options="repoWatchInterval" on the page turned this red.
        /// </summary>
        [Fact]
        public void The_interval_select_uses_the_options_the_payload_sends()
        {
            var m = Regex.Match(Page(), @"<select[^>]*data-field=""repoWatchIntervalMin""[^>]*data-options=""([A-Za-z]+)""");
            Assert.True(m.Success, "The repoWatchIntervalMin select was not found on the page.");
            var json = ProjectSettingsPayload.BuildLoad(SampleProject(), null, new ProjectSettingsOptions(), isDark: true);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("options").TryGetProperty(m.Groups[1].Value, out _),
                $"The page asks for options '{m.Groups[1].Value}', which the load message does not carry.");
        }

        [Fact]
        public void Save_parses_booleans_lists_and_absent_lists()
        {
            const string json = @"{""type"":""save"",
                ""fields"":{""name"":""N"",""isPinned"":true,""gitAutoCommit"":false,""claudeProfile"":""""},
                ""paths"":[{""id"":7,""pathType"":""docs"",""pathValue"":""C:\\d"",""description"":""""}],
                ""mcpServers"":[]}";
            using var doc = JsonDocument.Parse(json);
            var u = ProjectSettingsPayload.ParseSave("p-1", doc.RootElement);

            Assert.Equal("p-1", u.ProjectId);
            Assert.Equal("true", u.Fields["isPinned"]);
            Assert.Equal("false", u.Fields["gitAutoCommit"]);
            Assert.Equal("", u.Fields["claudeProfile"]);
            Assert.Equal(7, u.Paths.Single().Id);
            Assert.Empty(u.McpServers);   // sent empty: clear the table
            Assert.Null(u.Agents);        // not sent: leave the table alone
        }

        /// <summary>
        /// Every data-field on the page must be a field the service accepts, or EVERY save is rejected
        /// ("cannot be saved here"); and every editable field must be on the page, or it is silently
        /// uneditable. Falsified both ways: adding data-field="path" to an input, and deleting the
        /// buildCommand input, each turned this red.
        /// </summary>
        [Fact]
        public void Page_fields_are_exactly_the_editable_fields()
        {
            var onPage = Regex.Matches(Page(), @"data-field=""([A-Za-z]+)""").Select(m => m.Groups[1].Value).ToList();
            Assert.True(onPage.Count > 10, "Extracted too few data-field attributes; the page was not read.");
            Assert.Equal(onPage.Count, onPage.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(ProjectSettingsService.EditableFields.OrderBy(n => n, StringComparer.Ordinal),
                         onPage.OrderBy(n => n, StringComparer.Ordinal));
        }

        /// <summary>
        /// Every message type the page posts has a handler case in the form, and the form handles nothing
        /// the page never sends. Falsified both ways: renaming the page's 'syncMcp' post, and adding a
        /// case "unused" to the form, each turned this red.
        /// </summary>
        [Fact]
        public void Page_messages_and_form_handlers_match()
        {
            var sent = Regex.Matches(Page(), @"post\(\{\s*type:\s*'([A-Za-z]+)'").Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(Page(), @"out = \{\s*type:\s*'([A-Za-z]+)'").Select(m => m.Groups[1].Value))
                .Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();

            string form = ReadStripped("ProjectSettings", "ProjectSettingsForm.cs");
            int start = form.IndexOf("void OnWebMessageReceived", StringComparison.Ordinal);
            int end = form.IndexOf("private void Notice", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "Could not find OnWebMessageReceived in the form.");
            var handled = Regex.Matches(form.Substring(start, end - start), @"case ""([A-Za-z]+)"":")
                .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();

            Assert.True(sent.Count >= 8, $"Extracted too few page message types: {string.Join(", ", sent)}");
            Assert.Equal(handled, sent);
        }
    }
}
