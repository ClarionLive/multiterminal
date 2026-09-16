using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.API.Controllers;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins what <c>POST /api/spawn/terminal</c> forwards to the spawn callback (task 77d1182f).
    ///
    /// <para>The controller used to hardcode <c>spawnerName: "ClaudeRemote"</c>, so a terminal
    /// spawned BY an agent reported the phone app as its parent, and <c>initialPrompt: null</c>,
    /// so the job had to be typed in afterwards through a chunking submit path that was observed
    /// truncating a command mid-word. These facts drive the real controller with the real
    /// <see cref="SpawnService"/> — whose callback is a settable property — and assert on what
    /// reaches the callback, which is exactly the boundary the old literals sat on.</para>
    /// </summary>
    public class SpawnControllerContractTests
    {
        [Fact]
        public async Task Omitted_spawner_name_falls_back_to_ClaudeRemote_for_the_phone_app()
        {
            var (controller, captured) = Harness();

            var result = await controller.SpawnTerminal(new SpawnTerminalRequest { AgentName = "Helper" });

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal("ClaudeRemote", captured.SpawnerName);
        }

        [Fact]
        public async Task Provided_spawner_name_reaches_the_child_verbatim()
        {
            var (controller, captured) = Harness();

            await controller.SpawnTerminal(new SpawnTerminalRequest { AgentName = "Helper", SpawnerName = "Alice" });

            Assert.Equal("Alice", captured.SpawnerName);
        }

        [Fact]
        public async Task Whitespace_spawner_name_is_treated_as_omitted()
        {
            // A blank name would otherwise surface as MULTITERMINAL_SPAWNER="   " — neither the
            // phone app nor an agent — and the SessionStart hook's isSpawnedAgent check would
            // misfire on it.
            var (controller, captured) = Harness();

            await controller.SpawnTerminal(new SpawnTerminalRequest { AgentName = "Helper", SpawnerName = "   " });

            Assert.Equal("ClaudeRemote", captured.SpawnerName);
        }

        [Fact]
        public async Task Initial_prompt_reaches_the_callback_verbatim()
        {
            // The controller forwards; it does not reshape. Line-break collapsing is MainForm's
            // delivery concern (MainForm's delivery path), not the API's, so a multi-line prompt must
            // arrive here exactly as sent.
            var (controller, captured) = Harness();
            string prompt = "Sweep the env of this terminal.\nReport MULTITERMINAL_* to Alice.";

            await controller.SpawnTerminal(new SpawnTerminalRequest { AgentName = "Helper", InitialPrompt = prompt });

            Assert.Equal(prompt, captured.InitialPrompt);
        }

        [Fact]
        public async Task Omitted_initial_prompt_is_null_not_empty()
        {
            // null means "no job to deliver"; MainForm keys its delivery on that. An empty string
            // would queue a delivery that types nothing but still presses Enter.
            var (controller, captured) = Harness();

            await controller.SpawnTerminal(new SpawnTerminalRequest { AgentName = "Helper" });

            Assert.Null(captured.InitialPrompt);
        }

        [Fact]
        public async Task Whitespace_initial_prompt_is_treated_as_omitted()
        {
            var (controller, captured) = Harness();

            await controller.SpawnTerminal(new SpawnTerminalRequest { AgentName = "Helper", InitialPrompt = " \n " });

            Assert.Null(captured.InitialPrompt);
        }

        [Fact]
        public async Task Initial_prompt_over_the_cap_is_rejected_before_any_spawn()
        {
            // Pipeline Run 1 (Codex security), set when the prompt was TYPED into the helper one character at
            // a time and trace-logged by the terminal control, so an unbounded value is a cheap local
            // DoS and a log-leak channel. The cap must refuse BEFORE the callback — no pane, no job.
            var (controller, captured) = Harness();
            string tooLong = new string('x', SpawnController.MaxInitialPromptChars + 1);

            var result = await controller.SpawnTerminal(new SpawnTerminalRequest { AgentName = "Helper", InitialPrompt = tooLong });

            var problem = Assert.IsType<ObjectResult>(result);
            Assert.Equal(400, problem.StatusCode);
            Assert.False(captured.Reached, "an over-cap prompt must be refused before the spawn callback runs");
        }

        [Fact]
        public async Task Initial_prompt_at_the_cap_is_accepted()
        {
            var (controller, captured) = Harness();
            string atCap = new string('x', SpawnController.MaxInitialPromptChars);

            var result = await controller.SpawnTerminal(new SpawnTerminalRequest { AgentName = "Helper", InitialPrompt = atCap });

            Assert.IsType<OkObjectResult>(result);
            Assert.True(captured.Reached);
        }

        [Fact]
        public async Task Response_reports_the_registered_name_and_says_the_helper_is_not_ready()
        {
            // Pipeline Run 1 (cross-model adversary + debugger): success means the PANE exists, not
            // that the helper booted, and the name in the response is the one the broker actually
            // registered — a held name comes back suffixed. Both must be visible to the caller.
            var captured = new Captured();
            var service = new SpawnService
            {
                OnSpawnRequested = (agentName, agentType, workingDir, initialPrompt, spawnerName) =>
                {
                    captured.Reached = true;
                    return Task.FromResult((true, "doc-9", (string)null, agentName + "-2"));
                },
            };
            var controller = new SpawnController(service, projectDatabase: null, broker: null);

            var result = await controller.SpawnTerminal(new SpawnTerminalRequest { AgentName = "Helper", SpawnerName = "Alice" });

            var ok = Assert.IsType<OkObjectResult>(result);
            var body = ok.Value;
            Assert.Equal("Helper-2", ReadProp(body, "terminalName"));
            Assert.Equal("Helper", ReadProp(body, "requestedName"));
            Assert.Equal("doc-9", ReadProp(body, "docId"));
            Assert.Equal(false, ReadProp(body, "ready"));
            Assert.Contains("channel port", (string)ReadProp(body, "readiness"), System.StringComparison.Ordinal);
        }

        private static object ReadProp(object anonymous, string name)
            => anonymous.GetType().GetProperty(name)?.GetValue(anonymous);

        private sealed class Captured
        {
            public string SpawnerName { get; set; }

            public string InitialPrompt { get; set; }

            public bool Reached { get; set; }
        }

        private static (SpawnController Controller, Captured Captured) Harness()
        {
            var captured = new Captured();
            var service = new SpawnService
            {
                OnSpawnRequested = (agentName, agentType, workingDir, initialPrompt, spawnerName) =>
                {
                    captured.SpawnerName = spawnerName;
                    captured.InitialPrompt = initialPrompt;
                    captured.Reached = true;
                    return Task.FromResult((true, "doc-1", (string)null, agentName));
                },
            };

            // ProjectDatabase is only consulted when a ProjectId is supplied; these requests
            // never supply one, so null is honest rather than a stub.
            return (new SpawnController(service, projectDatabase: null, broker: null), captured);
        }
    }
}
