using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins WHO may call <c>MainForm.DeliverViaChannel</c> (ticket 0ff1b520, item 9).
    /// </summary>
    /// <remarks>
    /// <para>Before item 9, four MT-originated senders — task_active_changed, worktree_pruning and
    /// the two Oracle prompts — called the channel directly and skipped any terminal without a
    /// channel port. Chat already went native-first. So the channel looked retireable (chat worked
    /// without it) while those four would have gone silent the moment it was removed.</para>
    /// <para>Every sender now goes through <c>DeliverToSessionAsync</c>, which tries native ingress
    /// first. The only other permitted caller is the chat path, which has its own native-first
    /// block with message-id dedup. A new sender that calls the channel directly fails here.</para>
    /// <para>Roslyn invocation analysis, not a text scan: a comment naming DeliverViaChannel is
    /// neither a caller nor a non-caller, so neither census failure direction applies.</para>
    /// <para>When item 7 retires the channel, <c>DeliverViaChannel</c> is deleted and this fact
    /// fails on the extraction assert. Delete this file in the same change — the property it pins
    /// (nothing reaches the channel except through a native-first path) becomes vacuous.</para>
    /// </remarks>
    public class SessionDeliveryRouteTests
    {
        [Fact]
        public void Only_the_native_first_paths_call_DeliverViaChannel()
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(MainFormPath())).GetRoot();

            Assert.Contains(root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                m => m.Identifier.Text == "DeliverViaChannel");

            var callers = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is IdentifierNameSyntax id && id.Identifier.Text == "DeliverViaChannel")
                .Select(i => i.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.Text)
                .Distinct()
                .OrderBy(n => n)
                .ToArray();

            Assert.Equal(new[] { "DeliverToSessionAsync", "OnMcpMessageDelivery" }, callers);
        }

        private static string MainFormPath([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!, "MainForm.cs");
    }
}
