using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace MultiTerminal.Tests
{
    public class GlossWriterLaunchContractTests
    {
        [Fact]
        public void Gloss_writer_uses_haiku_and_keeps_session_persistence_disabled()
        {
            string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "MainForm.cs"));
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            MethodDeclarationSyntax method = Assert.Single(
                root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                candidate => candidate.Identifier.ValueText == "SpawnGlossWriterAsync");
            string[] literals = method.DescendantNodes().OfType<LiteralExpressionSyntax>()
                .Where(literal => literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression))
                .Select(literal => literal.Token.ValueText)
                .ToArray();

            Assert.Contains("--model", literals);
            Assert.Contains("haiku", literals);
            Assert.Contains("--no-session-persistence", literals);
        }

        private static string RepositoryRoot([CallerFilePath] string sourceFile = null) =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));
    }
}
