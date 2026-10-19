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
