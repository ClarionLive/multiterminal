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
        private readonly string _mcpConfig;
        private string _mcpOverride;

        public StandaloneLauncherTests()
        {
            _bin = Path.Combine(_root, "bin");
            _fake = Path.Combine(_root, "fake");
            _record = Path.Combine(_root, "record.txt");
            Directory.CreateDirectory(_fake);
            _mcpConfig = Path.Combine(_root, "mcp.json");
            File.WriteAllText(_mcpConfig, "{}");
            _mcpOverride = _mcpConfig; // never the developer's real %APPDATA% file

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
                "ARG -n", "ARG Filbert", "ARG --mcp-config", "ARG " + _mcpConfig,
                "ARG --settings", "ARG " + Path.Combine(_bin, StandaloneLauncher.SettingsFileName), "ARG --resume", "ARG abc",
            };
            Assert.Equal(expected, lines[..expected.Length]);
        }

        // Pipeline Run 1 (code-reviewer + debugger): a param() block made PowerShell bind claude's single-dash
        // flags to the script's own parameters (-c to -ClaudeArgs, -v to -Verbose, -p to -PipelineVariable).
        [Fact]
        public void Single_dash_claude_flags_reach_claude_unchanged()
        {
            Run("Filbert", "-c", "-p", "hi", "-v", "-n", "x");

            string[] lines = File.ReadAllLines(_record);
            Assert.Equal(new[] { "ARG -c", "ARG -p", "ARG hi", "ARG -v", "ARG -n", "ARG x" }, lines[6..12]);
            Assert.Contains("NAME [Filbert]", lines); // "-n x" after the name is claude's, not the session name
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
    }
}
