using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins <see cref="StandaloneLauncher"/> (ticket 6c688883): <c>mtclaude &lt;Name&gt;</c> starts claude with the
    /// session name, MT's MCP config and the accept-messages settings, under this session's own MT identity.
    /// The behaviour facts run the real <c>mtclaude.cmd</c> against a fake <c>claude.cmd</c> that records its
    /// arguments and environment, with MT's REST address pointed at a closed port. The user PATH write and a
    /// real Claude session are executed live only.
    /// </summary>
    public sealed class StandaloneLauncherTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mt_mtclaude_" + Guid.NewGuid().ToString("N"));
        private readonly string _bin;
        private readonly string _fake;
        private readonly string _record;
        private readonly string _config;
        private readonly string _work;
        private readonly string _mcpConfig;
        private string _mcpOverride;
        private readonly string _pluginDir;
        private string _pluginOverride;

        public StandaloneLauncherTests()
        {
            _bin = Path.Combine(_root, "bin");
            _fake = Path.Combine(_root, "fake");
            _record = Path.Combine(_root, "record.txt");
            _config = Path.Combine(_root, "claude-config");
            _work = Path.Combine(_root, "Projects", "mrtStudio");
            Directory.CreateDirectory(_work);
            Directory.CreateDirectory(_fake);
            _mcpConfig = Path.Combine(_root, "mcp.json");
            File.WriteAllText(_mcpConfig, "{}");
            _mcpOverride = _mcpConfig; // never the developer's real %APPDATA% file
            _pluginDir = Path.Combine(_root, "plugin");
            Directory.CreateDirectory(_pluginDir);
            _pluginOverride = _pluginDir;

            // A claude.cmd that writes what it was given: each argument on its own line, then the env it saw. The values
            // are bracketed: "echo X 1>> file" would read "1>>" as a redirect of handle 1 and drop the value.
            File.WriteAllText(Path.Combine(_fake, "claude.cmd"),
                "@echo off\r\n" +
                "(for %%a in (%*) do @echo ARG %%~a) > \"" + _record + "\"\r\n" +
                "echo NAME [%MULTITERMINAL_NAME%]>> \"" + _record + "\"\r\n" +
                "echo STANDALONE [%MULTITERMINAL_STANDALONE%]>> \"" + _record + "\"\r\n" +
                "echo DOCID [%MULTITERMINAL_DOC_ID%]>> \"" + _record + "\"\r\n" +
                "echo NONCE [%MULTITERMINAL_LAUNCH_NONCE%]>> \"" + _record + "\"\r\n" +
                "exit /b 7\r\n");
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private int Run(params string[] args)
        {
            Assert.True(StandaloneLauncher.EnsureInstalled(_bin, addToUserPath: false));
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // As a person types it: cmd /s /c ""mtclaude.cmd" "arg 1" arg2". /s keeps cmd's quote stripping to
            // the one outer pair, so quoted arguments (and an empty one) reach the script intact.
            string quoted = string.Join(" ", Array.ConvertAll(args, x => "\"" + x + "\""));
            psi.Arguments = "/d /s /c \"\"" + Path.Combine(_bin, StandaloneLauncher.CommandFileName) + "\" " + quoted + "\"";
            psi.Environment["PATH"] = _fake + ";" + Environment.GetEnvironmentVariable("PATH");
            psi.Environment["MTCLAUDE_API"] = "http://127.0.0.1:9"; // nothing listens: the "MT not running" path
            psi.Environment["MULTITERMINAL_DOC_ID"] = "pane-doc-inherited";
            psi.Environment["MULTITERMINAL_LAUNCH_NONCE"] = "inherited-nonce";
            psi.Environment["MULTITERMINAL_NAME"] = "SomeonesPane";
            psi.Environment["MTCLAUDE_MCP_CONFIG"] = _mcpOverride;
            psi.Environment["MTCLAUDE_PLUGIN_DIR"] = _pluginOverride;
            psi.Environment["CLAUDE_CONFIG_DIR"] = _config; // transcripts for -c are looked up here, never in the real ~/.claude
            psi.WorkingDirectory = _work;

            using var p = Process.Start(psi);
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(60000), "mtclaude did not finish");
            return p.ExitCode;
        }

        [Fact]
        public void Starts_claude_with_the_name_MT_MCP_and_accept_settings_and_passes_the_rest_through()
        {
            int exit = Run("Filbert", "--resume", "abc");

            Assert.Equal(7, exit); // claude's own exit code comes back
            string[] lines = File.ReadAllLines(_record);
            var expected = new[]
            {
                "ARG -n", "ARG Filbert", "ARG --plugin-dir", "ARG " + _pluginDir, "ARG --mcp-config", "ARG " + _mcpConfig,
                "ARG --settings", "ARG " + Path.Combine(_bin, StandaloneLauncher.SettingsFileName), "ARG --resume", "ARG abc",
            };
            Assert.Equal(expected, lines[..expected.Length]);
        }

        // Pipeline Run 1 (code-reviewer + debugger): a param() block made PowerShell bind claude's single-dash
        // flags to the script's own parameters (-c to -ClaudeArgs, -v to -Verbose, -p to -PipelineVariable).
        [Fact]
        public void Single_dash_claude_flags_reach_claude_unchanged()
        {
            Run("Filbert", "-p", "hi", "-v", "-n", "x", "-r");

            string[] lines = File.ReadAllLines(_record);
            Assert.Equal(new[] { "ARG -p", "ARG hi", "ARG -v", "ARG -n", "ARG x", "ARG -r" }, lines[8..14]);
            Assert.Contains("NAME [Filbert]", lines); // "-n x" after the name is claude's, not the session name
        }

        // MT panes get the MT plugin with --plugin-dir (it is not enabled globally); a standalone session must too.
        [Fact]
        public void Without_the_plugin_folder_claude_starts_without_it()
        {
            _pluginOverride = Path.Combine(_root, "no-such-plugin");

            Run("Filbert");

            string record = File.ReadAllText(_record);
            Assert.DoesNotContain("--plugin-dir", record);
            Assert.Contains("ARG --mcp-config", record);
        }

        [Fact]
        public void The_plugin_folder_is_baked_in_as_a_quoted_literal()
        {
            string script = StandaloneLauncher.BuildScriptContents(@"C:\Users\O'Brien\plugins\multiterminal");

            Assert.Contains(@"else { 'C:\Users\O''Brien\plugins\multiterminal' }", script);
            Assert.DoesNotContain("__MT_PLUGIN_DIR__", script);
        }

        [Fact]
        public void Without_MT_MCP_config_claude_starts_without_it()
        {
            _mcpOverride = Path.Combine(_root, "no-such.json");

            Run("Filbert");

            string record = File.ReadAllText(_record);
            Assert.DoesNotContain("--mcp-config", record);
            Assert.Contains("ARG --settings", record);
        }

        [Fact]
        public void The_session_gets_its_own_MT_identity_and_drops_the_inherited_pane()
        {
            Run("Filbert");

            string record = File.ReadAllText(_record);
            Assert.True(record.Contains("NAME [Filbert]", StringComparison.Ordinal), record);
            Assert.True(record.Contains("STANDALONE [1]", StringComparison.Ordinal), record);
            Assert.DoesNotContain("pane-doc-inherited", record);
            Assert.DoesNotContain("inherited-nonce", record);
        }

        [Theory]
        [InlineData("")]
        [InlineData("-dash-first")]
        [InlineData("has space")]
        [InlineData("Unassigned")]
        public void A_bad_name_starts_nothing(string name)
        {
            int exit = Run(name);

            Assert.Equal(2, exit);
            Assert.False(File.Exists(_record), "claude was started");
        }

        [Fact]
        public void The_settings_accept_cross_session_messages()
        {
            using var doc = JsonDocument.Parse(StandaloneLauncher.SettingsContents);
            Assert.Equal("accept", doc.RootElement.GetProperty("crossSessionInbound").GetString());
        }

        [Fact]
        public void The_command_lives_outside_the_agents_shims_folder()
        {
            // The shims folder holds the bot-identity gh launcher; on the user's PATH it would capture their gh.
            Assert.NotEqual(
                Path.GetFullPath(AgentGitIdentityWiring.ResolveShimDirectory()).TrimEnd('\\'),
                Path.GetFullPath(StandaloneLauncher.ResolveBinDirectory()).TrimEnd('\\'),
                StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(null, @"C:\mt\bin", @"C:\mt\bin")]
        [InlineData("", @"C:\mt\bin", @"C:\mt\bin")]
        [InlineData(@"C:\a;C:\b", @"C:\mt\bin", @"C:\a;C:\b;C:\mt\bin")]
        [InlineData(@"C:\a;C:\b;", @"C:\mt\bin", @"C:\a;C:\b;C:\mt\bin")]
        [InlineData(@"%USERPROFILE%\x;C:\a", @"C:\mt\bin", @"%USERPROFILE%\x;C:\a;C:\mt\bin")]
        public void Path_gets_the_folder_appended(string current, string dir, string expected)
        {
            Assert.Equal(expected, StandaloneLauncher.PathWithEntry(current, dir));
        }

        [Theory]
        [InlineData(@"C:\a;C:\mt\bin")]
        [InlineData(@"C:\a;c:\MT\BIN\")]
        [InlineData(@"C:\a; ""C:\mt\bin"" ")]
        public void A_folder_already_on_the_path_is_not_added_again(string current)
        {
            Assert.Null(StandaloneLauncher.PathWithEntry(current, @"C:\mt\bin"));
        }

        // Pipeline Run 1 (debugger): an entry written with a variable is the same folder.
        [Fact]
        public void An_entry_written_with_a_variable_counts()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Assert.Null(StandaloneLauncher.PathWithEntry(@"C:\a;%APPDATA%\multiterminal\bin", Path.Combine(appData, "multiterminal", "bin")));
        }

        // A transcript in this folder's Claude project folder, written with the name lines Claude Code writes.
        private void Transcript(string sessionId, string agentName, string title, DateTime lastWriteUtc)
        {
            string folder = Path.Combine(_config, "projects", System.Text.RegularExpressions.Regex.Replace(_work, "[^A-Za-z0-9]", "-"));
            Directory.CreateDirectory(folder);
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}\n");
            if (title != null) sb.Append("{\"type\":\"custom-title\",\"customTitle\":\"").Append(title).Append("\",\"sessionId\":\"").Append(sessionId).Append("\"}\n");
            if (agentName != null) sb.Append("{\"type\":\"agent-name\",\"agentName\":\"").Append(agentName).Append("\",\"sessionId\":\"").Append(sessionId).Append("\"}\n");
            string path = Path.Combine(folder, sessionId + ".jsonl");
            File.WriteAllText(path, sb.ToString());
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }

        // Ticket a34ee35f: `mtclaude Vela -c` beside Ezra picked up Ezra's session (the folder's newest). It now
        // continues Vela's own newest session. Ezra's session, retitled "Vela" by that mistake, is still Ezra's.
        [Fact]
        public void Continue_resumes_this_names_newest_session_in_the_folder_not_the_folders_newest()
        {
            Transcript("aaaa-ezra", "Ezra", "Vela", DateTime.UtcNow.AddMinutes(-1));
            Transcript("bbbb-vela-new", "Vela", "Vela", DateTime.UtcNow.AddMinutes(-30));
            Transcript("cccc-vela-old", "Vela", "Vela", DateTime.UtcNow.AddHours(-5));

            Run("Vela", "-c", "-v");

            string[] lines = File.ReadAllLines(_record);
            Assert.Equal(new[] { "ARG --resume", "ARG bbbb-vela-new", "ARG -v" }, lines[8..11]);
            Assert.DoesNotContain("ARG -c", lines);
        }

        [Fact]
        public void Continue_with_no_session_of_that_name_starts_fresh_rather_than_taking_another_agents()
        {
            Transcript("aaaa-ezra", "Ezra", "Ezra", DateTime.UtcNow.AddMinutes(-1));

            Run("Vela", "--continue");

            string record = File.ReadAllText(_record);
            Assert.DoesNotContain("--resume", record);
            Assert.DoesNotContain("--continue", record);
            Assert.DoesNotContain("aaaa-ezra", record);
        }

        [Fact]
        public void A_transcript_without_an_agent_name_line_is_matched_by_its_title()
        {
            Transcript("dddd-titled", null, "Vela", DateTime.UtcNow.AddMinutes(-2));

            Run("Vela", "-c");

            Assert.Contains("ARG dddd-titled", File.ReadAllLines(_record));
        }

        private void RawTranscript(string sessionId, string contents, DateTime lastWriteUtc)
        {
            string folder = Path.Combine(_config, "projects", System.Text.RegularExpressions.Regex.Replace(_work, "[^A-Za-z0-9]", "-"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, sessionId + ".jsonl");
            File.WriteAllText(path, contents);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }

        // Pipeline Run 1 (Codex): the records are read as JSON, by property name. An agent-name record with other
        // field order or spacing still decides; the retitled "Vela" custom-title of Ezra's session never does.
        [Fact]
        public void Field_order_and_spacing_do_not_let_a_title_override_the_agent_name()
        {
            RawTranscript("eeee-ezra",
                "{ \"sessionId\": \"eeee-ezra\", \"agentName\": \"Ezra\",  \"type\": \"agent-name\" }\n" +
                "{\"type\":\"custom-title\",\"customTitle\":\"Vela\",\"sessionId\":\"eeee-ezra\"}\n",
                DateTime.UtcNow.AddMinutes(-1));

            Run("Vela", "-c");

            Assert.DoesNotContain("eeee-ezra", File.ReadAllText(_record));
        }

        // Pipeline Run 1 (Codex): a long stretch with no name record does not lose the session's owner.
        [Fact]
        public void A_name_record_far_from_the_end_is_still_found()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"type\":\"agent-name\",\"agentName\":\"Vela\",\"sessionId\":\"ffff-long\"}\n");
            string filler = "{\"type\":\"assistant\",\"message\":\"" + new string('x', 1000) + "\"}\n";
            for (int i = 0; i < 400; i++) sb.Append(filler); // ~400 KB after the name
            RawTranscript("ffff-long", sb.ToString(), DateTime.UtcNow.AddMinutes(-1));

            Run("Vela", "-c");

            Assert.Contains("ARG ffff-long", File.ReadAllLines(_record));
        }

        // Pipeline Run 1 (Codex): -c that is a flag's value, or after "--", is passed through, not taken as continue.
        [Theory]
        [InlineData("--model", "-c")]
        [InlineData("--", "-c")]
        public void A_c_that_is_not_the_continue_flag_reaches_claude_unchanged(string before, string c)
        {
            Transcript("gggg-vela", "Vela", "Vela", DateTime.UtcNow.AddMinutes(-1));

            Run("Vela", before, c);

            string[] lines = File.ReadAllLines(_record);
            Assert.DoesNotContain("ARG --resume", lines);
            Assert.Equal(new[] { "ARG " + before, "ARG " + c }, lines[8..10]);
        }

        // Pipeline Run 2 (Codex): Ezra's agent-name far from the end still beats a recent "Vela" title in the tail.
        [Fact]
        public void An_old_agent_name_beats_a_recent_title_in_a_long_transcript()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"type\":\"agent-name\",\"agentName\":\"Ezra\",\"sessionId\":\"hhhh-ezra\"}\n");
            string filler = "{\"type\":\"assistant\",\"message\":\"" + new string('x', 1000) + "\"}\n";
            for (int i = 0; i < 400; i++) sb.Append(filler); // ~400 KB: the agent-name is outside the last 256 KB
            sb.Append("{\"type\":\"custom-title\",\"customTitle\":\"Vela\",\"sessionId\":\"hhhh-ezra\"}\n");
            RawTranscript("hhhh-ezra", sb.ToString(), DateTime.UtcNow.AddMinutes(-1));

            Run("Vela", "-c");

            Assert.DoesNotContain("hhhh-ezra", File.ReadAllText(_record));
        }
    }
}
