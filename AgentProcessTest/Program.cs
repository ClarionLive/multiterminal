using System;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Models;
using MultiTerminal.Services;

namespace AgentProcessTest
{
    /// <summary>
    /// Standalone console test for AgentProcess — proves the piped stream-json protocol works end-to-end.
    /// 1) Spawns AgentProcess with a simple prompt
    /// 2) Logs all AgentMessages as they arrive
    /// 3) Sends a follow-up message after first result
    /// 4) Waits for second result
    /// 5) Calls StopAsync and reports clean shutdown
    /// </summary>
    class Program
    {
        private static int _resultCount = 0;
        private static readonly TaskCompletionSource<bool> _firstResultTcs = new TaskCompletionSource<bool>();
        private static readonly TaskCompletionSource<bool> _secondResultTcs = new TaskCompletionSource<bool>();

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== AgentProcess End-to-End Test ===");
            Console.WriteLine();

            var agent = new AgentProcess();

            // Subscribe to live messages
            agent.MessageReceived += OnMessageReceived;
            agent.ProcessExited += (s, exitCode) =>
            {
                Console.WriteLine();
                Console.WriteLine($"[PROCESS] Exited with code {exitCode}");
            };

            try
            {
                // Step 1: Spawn with initial prompt
                string prompt = "Say hello and tell me what 2+2 is. Keep your response brief.";
                Console.WriteLine($"[TEST] Spawning AgentProcess with prompt: \"{prompt}\"");
                Console.WriteLine();

                await agent.SpawnAsync(
                    prompt: prompt,
                    workingDir: Environment.CurrentDirectory,
                    permissionMode: "bypassPermissions");

                Console.WriteLine($"[TEST] Process started (PID: {agent.ProcessId})");
                Console.WriteLine($"[TEST] Waiting for first result...");
                Console.WriteLine();

                // Step 2: Wait for first result (with timeout)
                var firstResult = await WaitWithTimeout(_firstResultTcs.Task, TimeSpan.FromSeconds(60));
                if (!firstResult)
                {
                    Console.WriteLine("[TEST] TIMEOUT waiting for first result!");
                    return;
                }

                Console.WriteLine();
                Console.WriteLine($"[TEST] First result received! SessionId: {agent.SessionId ?? "(not yet extracted)"}");
                Console.WriteLine($"[TEST] Total messages so far: {agent.Messages.Count}");
                Console.WriteLine();

                // Step 3: Send follow-up message
                string followUp = "Now tell me what 10 * 10 is. One sentence only.";
                Console.WriteLine($"[TEST] Sending follow-up: \"{followUp}\"");
                Console.WriteLine();

                await agent.SendMessageAsync(followUp);

                // Step 4: Wait for second result
                Console.WriteLine($"[TEST] Waiting for second result...");
                var secondResult = await WaitWithTimeout(_secondResultTcs.Task, TimeSpan.FromSeconds(60));
                if (!secondResult)
                {
                    Console.WriteLine("[TEST] TIMEOUT waiting for second result!");
                    return;
                }

                Console.WriteLine();
                Console.WriteLine($"[TEST] Second result received!");
                Console.WriteLine($"[TEST] Total messages: {agent.Messages.Count}");
                Console.WriteLine($"[TEST] SessionId: {agent.SessionId ?? "(none)"}");
                Console.WriteLine();

                // Step 5: Stop gracefully
                Console.WriteLine("[TEST] Calling StopAsync...");
                await agent.StopAsync(timeoutMs: 10000);
                Console.WriteLine("[TEST] StopAsync completed — clean shutdown.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TEST] ERROR: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                agent.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine("=== Test Complete ===");

            // Print summary
            Console.WriteLine();
            Console.WriteLine("--- Message Summary ---");
            var messages = agent.Messages;
            Console.WriteLine($"Total messages: {messages.Count}");
            int assistantCount = 0, toolCount = 0, thinkingCount = 0, deltaCount = 0, resultCount = 0, errorCount = 0, otherCount = 0;
            foreach (var m in messages)
            {
                switch (m.Type)
                {
                    case AgentMessageType.Assistant: assistantCount++; break;
                    case AgentMessageType.ToolUse:
                    case AgentMessageType.ToolResult: toolCount++; break;
                    case AgentMessageType.Thinking: thinkingCount++; break;
                    case AgentMessageType.StreamDelta: deltaCount++; break;
                    case AgentMessageType.Result: resultCount++; break;
                    case AgentMessageType.Error: errorCount++; break;
                    default: otherCount++; break;
                }
            }
            Console.WriteLine($"  Assistant: {assistantCount}");
            Console.WriteLine($"  Tool (use+result): {toolCount}");
            Console.WriteLine($"  Thinking deltas: {thinkingCount}");
            Console.WriteLine($"  Stream deltas: {deltaCount}");
            Console.WriteLine($"  Results: {resultCount}");
            Console.WriteLine($"  Errors: {errorCount}");
            Console.WriteLine($"  Other: {otherCount}");
            Console.WriteLine($"SessionId: {agent.SessionId ?? "(none)"}");
        }

        private static void OnMessageReceived(object sender, AgentMessage msg)
        {
            string prefix = $"[{msg.Type,-12}]";

            switch (msg.Type)
            {
                case AgentMessageType.System:
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"{prefix} {Truncate(msg.Content, 120)}");
                    break;

                case AgentMessageType.Assistant:
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"{prefix} {Truncate(msg.Content, 200)}");
                    break;

                case AgentMessageType.Thinking:
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write($"{prefix} {Truncate(msg.Content, 80)}");
                    break;

                case AgentMessageType.StreamDelta:
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write(msg.Content);
                    break;

                case AgentMessageType.ToolUse:
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"{prefix} {msg.ToolName}: {Truncate(msg.Content, 100)}");
                    break;

                case AgentMessageType.ToolResult:
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine($"{prefix} {msg.ToolName}: {Truncate(msg.Content, 100)}");
                    break;

                case AgentMessageType.Result:
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine();
                    Console.WriteLine($"{prefix} Turn complete.{(msg.SessionId != null ? $" SessionId: {msg.SessionId}" : "")}");
                    _resultCount++;
                    if (_resultCount == 1) _firstResultTcs.TrySetResult(true);
                    if (_resultCount == 2) _secondResultTcs.TrySetResult(true);
                    break;

                case AgentMessageType.Error:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"{prefix} {Truncate(msg.Content, 200)}");
                    break;

                default:
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine($"{prefix} {Truncate(msg.Content, 100)}");
                    break;
            }

            Console.ResetColor();
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            text = text.Replace("\n", "\\n").Replace("\r", "");
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "...";
        }

        private static async Task<bool> WaitWithTimeout(Task<bool> task, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout));
            return completed == task && task.Result;
        }
    }
}
