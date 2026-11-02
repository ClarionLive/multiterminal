using MultiTerminal.Services;

namespace MultiTerminal.Tests;

public sealed class CodexConfigApprovalTests
{
    private const string Begin = "# >>> MULTITERMINAL MANAGED — do not edit (begin) <<<";
    private const string End = "# <<< MULTITERMINAL MANAGED — do not edit (end) >>>";
    private const string ServersJson = """
        {"mcpServers":{"multiterminal":{"command":"node","args":["index.js"]},"other":{"command":"other"}}}
        """;
    private static readonly string[] ExpectedSafeTools =
    {
        "get_my_active_task",
        "get_active_worktree",
        "get_task_detail",
        "get_task_files",
        "get_task_relationships",
        "list_tasks",
        "list_terminals",
        "get_messages",
        "get_inbox",
        "get_my_spawn_job",
        "get_project",
        "list_projects",
        "list_project_notes",
        "search_code",
        "search_files",
        "search_code_graph",
        "get_file_symbols",
        "get_symbol_callers",
        "get_symbol_callees",
        "get_impact_analysis",
        "register_terminal",
        "send_message",
        "mark_inbox_read",
        "claim_task",
        "set_task_active",
        "update_task_plan",
        "update_task_continuation",
        "append_checklist_items",
        "link_task_file",
    };

    [Fact]
    public void BuildManagedTomlBlock_PreservesExistingApprovalValue()
    {
        string existing = ManagedBlock("""
            [mcp_servers.multiterminal.tools.owner_choice]
            approval_mode = "approve"
            """);

        string result = CodexConfigService.BuildManagedTomlBlock(ServersJson, existing);

        Assert.Contains("[mcp_servers.multiterminal.tools.owner_choice]", result, StringComparison.Ordinal);
        Assert.Contains("approval_mode = \"approve\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildManagedTomlBlock_PreservesNonApproveValue()
    {
        string existing = ManagedBlock("""
            [mcp_servers.multiterminal.tools.send_message]
            approval_mode = "prompt"
            """);

        string result = CodexConfigService.BuildManagedTomlBlock(ServersJson, existing);

        Assert.Contains("[mcp_servers.multiterminal.tools.send_message]", result, StringComparison.Ordinal);
        Assert.Contains("approval_mode = \"prompt\"", result, StringComparison.Ordinal);
        Assert.Equal(1, Count(result, "[mcp_servers.multiterminal.tools.send_message]"));
    }

    [Fact]
    public void BuildManagedTomlBlock_EmitsExactlyThePinnedSafeTools()
    {
        string result = CodexConfigService.BuildManagedTomlBlock(ServersJson);

        string prefix = "[mcp_servers.multiterminal.tools.";
        string[] actual = result.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal) && line.EndsWith(']'))
            .Select(line => line.Substring(prefix.Length, line.Length - prefix.Length - 1))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedSafeTools.OrderBy(name => name, StringComparer.Ordinal), actual);
    }

    [Fact]
    public void BuildManagedTomlBlock_PreservesUnknownBodyLinesExactly()
    {
        const string body = "  approval_mode   = \"prompt\"\nfuture_option = { mode = \"strict\" }\n# owner comment";
        string existing = ManagedBlock($"[mcp_servers.multiterminal.tools.send_message]\n{body}");

        string result = CodexConfigService.BuildManagedTomlBlock(ServersJson, existing);

        Assert.Contains($"[mcp_servers.multiterminal.tools.send_message]{Environment.NewLine}{body.Replace("\n", Environment.NewLine, StringComparison.Ordinal)}", result, StringComparison.Ordinal);
        Assert.Equal(1, Count(result, "[mcp_servers.multiterminal.tools.send_message]"));
    }

    [Fact]
    public void BuildManagedTomlBlock_DoesNotDuplicateSafeToolDefinedOutsideMarkers()
    {
        string outside = "[mcp_servers.multiterminal.tools.send_message]\napproval_mode = \"prompt\"\n";
        string existing = outside + ManagedBlock(string.Empty);

        string managed = CodexConfigService.BuildManagedTomlBlock(ServersJson, existing);
        string result = CodexConfigService.ReplaceOrAppendManagedBlock(existing, managed);

        Assert.Equal(1, Count(result, "[mcp_servers.multiterminal.tools.send_message]"));
        Assert.StartsWith(outside, result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildManagedTomlBlock_RejectsMalformedToolHeader()
    {
        string existing = ManagedBlock("""
            [mcp_servers.multiterminal.tools.not.a.valid.key]
            approval_mode = "approve"
            """);

        Assert.Throws<FormatException>(() => CodexConfigService.BuildManagedTomlBlock(ServersJson, existing));
    }

    [Fact]
    public void BuildManagedTomlBlock_DropsApprovalForRemovedServer()
    {
        string existing = ManagedBlock("""
            [mcp_servers.removed.tools.old_tool]
            approval_mode = "approve"
            """);

        string result = CodexConfigService.BuildManagedTomlBlock(ServersJson, existing);

        Assert.DoesNotContain("removed", result, StringComparison.Ordinal);
        Assert.DoesNotContain("old_tool", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebuild_IsIdempotentAndPreservesOutsideContent()
    {
        string outsideBefore = "owner_setting = true\n# >>> CLARIONASSISTANT MANAGED (begin) <<<\nkeep = \"yes\"\n# <<< CLARIONASSISTANT MANAGED (end) >>>\n";
        string outsideAfter = "owner_tail = \"untouched\"\n";
        string existing = outsideBefore + ManagedBlock("""
            [mcp_servers.other.tools.custom]
            approval_mode = "ask"
            """) + outsideAfter;

        string firstBlock = CodexConfigService.BuildManagedTomlBlock(ServersJson, existing);
        string first = CodexConfigService.ReplaceOrAppendManagedBlock(existing, firstBlock);
        string secondBlock = CodexConfigService.BuildManagedTomlBlock(ServersJson, first);
        string second = CodexConfigService.ReplaceOrAppendManagedBlock(first, secondBlock);

        Assert.Equal(first, second);
        Assert.StartsWith(outsideBefore, first, StringComparison.Ordinal);
        Assert.EndsWith(outsideAfter, first, StringComparison.Ordinal);
        Assert.Equal(1, Count(first, "[mcp_servers.other.tools.custom]"));
    }

    [Fact]
    public void BuildManagedTomlBlock_RejectsDuplicateApprovalTables()
    {
        string existing = ManagedBlock("""
            [mcp_servers.multiterminal.tools.send_message]
            approval_mode = "approve"

            [mcp_servers.multiterminal.tools.send_message]
            approval_mode = "prompt"
            """);

        Assert.Throws<FormatException>(() => CodexConfigService.BuildManagedTomlBlock(ServersJson, existing));
    }

    private static string ManagedBlock(string body) => $"{Begin}\n{body}\n{End}\n";

    private static int Count(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
