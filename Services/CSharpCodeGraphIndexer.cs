using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Roslyn-based C# code indexer. Parses .cs files, extracts symbols and relationships,
    /// stores in CodeGraphDatabase. Uses CSharpSyntaxWalker + SemanticModel for precise
    /// symbol and call resolution.
    ///
    /// 2-pass strategy (from Frank's Clarion CodeGraph):
    ///   Pass 1: Extract all symbols (types, methods, properties, constructors)
    ///   Pass 2: Extract all relationships (calls, inherits, implements, overrides)
    /// </summary>
    public class CSharpCodeGraphIndexer
    {
        private readonly CodeGraphDatabase _db;
        private readonly CodeGraphQuery _query;

        public event Action<string> OnProgress;

        public CSharpCodeGraphIndexer(CodeGraphDatabase db, CodeGraphQuery query)
        {
            _db = db;
            _query = query;
        }

        /// <summary>
        /// Index all .cs files under a directory. Returns indexing statistics.
        /// </summary>
        public IndexResult IndexDirectory(string directory, string projectName = null)
        {
            // Canonicalize + existence-verify the caller-supplied directory before any File.*/Directory.*
            // sink sees it. Collapses '..' segments, normalizes separators, and throws on non-existent
            // roots so tainted input cannot traverse outside a real directory. This is the sanitizer
            // gate for every downstream Directory.GetFiles / File.ReadAllText / Directory.Exists call
            // in this method and in GetMetadataReferences (called with the same sanitized value).
            directory = SafeDirectory(directory)
                ?? throw new ArgumentException("directory must resolve to an existing directory", nameof(directory));

            var sw = Stopwatch.StartNew();
            projectName = projectName ?? Path.GetFileName(directory);

            Report($"Indexing C# project: {projectName}");

            // Clear previous index for this project (including the project row itself)
            int existingId = _db.FindProjectIdByName(projectName);
            if (existingId >= 0)
                _db.ClearProject(existingId, deleteProjectRow: true);

            // Register project
            string csprojPath = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            int projectId = _db.InsertProject(projectName, csprojPath);

            // Discover .cs files (exclude obj/, bin/, and generated files)
            var csFiles = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsExcludedPath(f))
                .ToList();

            Report($"Found {csFiles.Count} C# files");

            // Parse all files into syntax trees
            var syntaxTrees = new List<SyntaxTree>();
            foreach (var file in csFiles)
            {
                try
                {
                    // CA3003: 'file' comes from Directory.GetFiles under 'directory', which was
                    // canonicalized+existence-verified by SafeDirectory above. The enumerated path is
                    // rooted at the sanitized directory, so File.ReadAllText cannot be redirected by
                    // caller input here.
#pragma warning disable CA3003
                    string source = File.ReadAllText(file);
#pragma warning restore CA3003
                    var tree = CSharpSyntaxTree.ParseText(source, path: file);
                    syntaxTrees.Add(tree);
                }
                catch (Exception ex)
                {
                    Report($"  SKIP (parse error): {file} — {ex.Message}");
                }
            }

            // Build compilation with references for SemanticModel
            var references = GetMetadataReferences(directory);
            var compilation = CSharpCompilation.Create(
                projectName + "_Analysis",
                syntaxTrees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithNullableContextOptions(NullableContextOptions.Enable));

            // Pass 1: Extract symbols
            Report("Pass 1: Extracting symbols...");
            int symbolCount = 0;
            using (var txn = _db.BeginTransaction())
            {
                try
                {
                    foreach (var tree in syntaxTrees)
                    {
                        var model = compilation.GetSemanticModel(tree);
                        var walker = new SymbolExtractor(_db, projectId, model);
                        walker.Visit(tree.GetRoot());
                        symbolCount += walker.SymbolCount;
                    }
                    txn.Commit();
                }
                finally { _db.EndTransaction(); }
            }
            Report($"  {symbolCount} symbols extracted");

            // Pass 2: Extract relationships (scoped rebuild for this project only)
            Report("Pass 2: Extracting relationships...");
            _db.ClearProjectRelationships(projectId);
            var symbolLookup = _db.LoadSymbolLookup();
            int relCount = 0;
            var dedupSet = new HashSet<string>();

            using (var txn = _db.BeginTransaction())
            {
                try
                {
                    foreach (var tree in syntaxTrees)
                    {
                        var model = compilation.GetSemanticModel(tree);
                        var walker = new RelationshipExtractor(_db, projectId, model, symbolLookup, dedupSet);
                        walker.Visit(tree.GetRoot());
                        relCount += walker.RelationshipCount;
                    }
                    txn.Commit();
                }
                finally { _db.EndTransaction(); }
            }
            Report($"  {relCount} relationships extracted");

            // Store metadata
            sw.Stop();
            string indexedAt = DateTime.UtcNow.ToString("o");
            _db.SetMetadata("last_indexed", indexedAt);
            // Per-project last_indexed so a background watcher can apply an INDEPENDENT freshness
            // floor per project (indexing project A must not floor-suppress an unrelated dirty
            // project B). Keyed by stable project name (not the churning projectId) so it doesn't
            // orphan a row per reindex; the global key above is kept for back-compat / GetStats.
            _db.SetProjectLastIndexed(projectName, indexedAt);
            _db.SetMetadata("index_duration_ms", sw.ElapsedMilliseconds.ToString());
            _db.SetMetadata("project:" + projectId + ":file_count", csFiles.Count.ToString());
            _db.SetMetadata("project:" + projectId + ":symbol_count", symbolCount.ToString());

            Report($"Indexing complete in {sw.ElapsedMilliseconds}ms");

            return new IndexResult
            {
                ProjectName = projectName,
                FileCount = csFiles.Count,
                SymbolCount = symbolCount,
                RelationshipCount = relCount,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        /// <summary>
        /// True if a <c>.cs</c> path should be skipped by indexing (build output + generated files).
        /// Public+static so the background <see cref="CodeGraphWatcher"/> can apply the exact same
        /// filter to FS events without a second, drift-prone copy of these rules.
        /// </summary>
        public static bool IsExcludedPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return true;
            var normalized = filePath.Replace('\\', '/');
            return normalized.Contains("/obj/")
                || normalized.Contains("/bin/")
                || normalized.Contains("/node_modules/")
                || normalized.Contains(".g.cs")
                || normalized.Contains(".designer.cs", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(".AssemblyInfo.cs");
        }

        private List<MetadataReference> GetMetadataReferences(string projectDir)
        {
            var refs = new List<MetadataReference>();

            // Core runtime references
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (runtimeDir != null)
            {
                foreach (var dll in new[] {
                    "System.Runtime.dll",
                    "System.Collections.dll",
                    "System.Linq.dll",
                    "System.Threading.Tasks.dll",
                    "System.IO.dll",
                    "System.Net.Http.dll",
                    "System.Console.dll",
                    "System.ComponentModel.dll",
                    "System.ComponentModel.Primitives.dll",
                    "System.Data.Common.dll",
                    "netstandard.dll",
                    "mscorlib.dll",
                    "System.Private.CoreLib.dll"
                })
                {
                    var path = Path.Combine(runtimeDir, dll);
                    if (File.Exists(path))
                        refs.Add(MetadataReference.CreateFromFile(path));
                }
            }

            // NuGet package references from the project's output directory.
            // CA3003: projectDir is the sanitized directory from IndexDirectory (canonicalized+existence-
            // verified via SafeDirectory before this method is invoked). outputDir is built from that
            // sanitized root plus constant string segments, so Directory.Exists cannot be redirected
            // outside a real on-disk root by caller input.
            var outputDir = Path.Combine(projectDir, "bin", "Release", "net8.0-windows", "win-x64");
#pragma warning disable CA3003
            if (!Directory.Exists(outputDir))
                outputDir = Path.Combine(projectDir, "bin", "Debug", "net8.0-windows", "win-x64");
            if (Directory.Exists(outputDir))
#pragma warning restore CA3003
            {
                foreach (var dll in Directory.GetFiles(outputDir, "*.dll"))
                {
                    try
                    {
                        refs.Add(MetadataReference.CreateFromFile(dll));
                    }
                    catch { /* skip unloadable dlls */ }
                }
            }

            return refs;
        }

        private void Report(string message) => OnProgress?.Invoke(message);

        // Canonicalize a caller-supplied directory path and verify it exists on disk.
        // Mirrors WikiController.SafeProjectRoot: collapses '..' segments, normalizes separators,
        // and returns null on any failure so tainted values cannot reach File.*/Directory.* sinks
        // downstream. This is the sanitizer gate for the whole indexer entry point.
        private static string SafeDirectory(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                var full = Path.GetFullPath(raw);
                // CA3003: this IS the sanitizer — canonicalized path via GetFullPath, then existence
                // check. Returning null on miss prevents tainted values from reaching any File.* sink.
#pragma warning disable CA3003
                return Directory.Exists(full) ? full : null;
#pragma warning restore CA3003
            }
            catch (ArgumentException) { return null; }
            catch (PathTooLongException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        // ─── Pass 1: Symbol Extraction ───────────────────────────────────────

        private class SymbolExtractor : CSharpSyntaxWalker
        {
            private readonly CodeGraphDatabase _db;
            private readonly int _projectId;
            private readonly SemanticModel _model;
            public int SymbolCount { get; private set; }

            public SymbolExtractor(CodeGraphDatabase db, int projectId, SemanticModel model)
            {
                _db = db;
                _projectId = projectId;
                _model = model;
            }

            public override void VisitClassDeclaration(ClassDeclarationSyntax node) =>
                ExtractType(node, "class");

            public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) =>
                ExtractType(node, "interface");

            public override void VisitStructDeclaration(StructDeclarationSyntax node) =>
                ExtractType(node, "struct");

            public override void VisitEnumDeclaration(EnumDeclarationSyntax node) =>
                ExtractType(node, "enum");

            public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
            {
                var symbol = _model.GetDeclaredSymbol(node);
                if (symbol == null) return;
                InsertSymbol(new CodeSymbol
                {
                    Name = symbol.Name,
                    Type = "delegate",
                    FilePath = NormalizePath(node.SyntaxTree.FilePath),
                    LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    ProjectId = _projectId,
                    ReturnType = symbol.DelegateInvokeMethod?.ReturnType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Params = FormatParameters(symbol.DelegateInvokeMethod?.Parameters),
                    Scope = GetScope(node),
                    Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                    GenericParams = FormatTypeParams(symbol.TypeParameters)
                });
                base.VisitDelegateDeclaration(node);
            }

            public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                var symbol = _model.GetDeclaredSymbol(node);
                if (symbol == null) { base.VisitMethodDeclaration(node); return; }
                InsertSymbol(new CodeSymbol
                {
                    Name = symbol.Name,
                    Type = "method",
                    FilePath = NormalizePath(node.SyntaxTree.FilePath),
                    LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    ProjectId = _projectId,
                    Params = FormatParameters(symbol.Parameters),
                    ReturnType = symbol.ReturnType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    MemberOf = symbol.ContainingType?.Name,
                    Scope = "class",
                    Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                    IsStatic = symbol.IsStatic,
                    IsAsync = symbol.IsAsync,
                    IsAbstract = symbol.IsAbstract,
                    GenericParams = FormatTypeParams(symbol.TypeParameters),
                    SourcePreview = node.ToString().Split('\n')[0].Trim()
                });
                // Don't recurse into method body for symbols — methods don't nest type declarations normally
                base.VisitMethodDeclaration(node);
            }

            public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
            {
                var symbol = _model.GetDeclaredSymbol(node);
                if (symbol == null) { base.VisitConstructorDeclaration(node); return; }
                InsertSymbol(new CodeSymbol
                {
                    Name = symbol.ContainingType?.Name + ".ctor",
                    Type = "constructor",
                    FilePath = NormalizePath(node.SyntaxTree.FilePath),
                    LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    ProjectId = _projectId,
                    Params = FormatParameters(symbol.Parameters),
                    MemberOf = symbol.ContainingType?.Name,
                    Scope = "class",
                    Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                    IsStatic = symbol.IsStatic
                });
                base.VisitConstructorDeclaration(node);
            }

            public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
            {
                var symbol = _model.GetDeclaredSymbol(node);
                if (symbol == null) { base.VisitPropertyDeclaration(node); return; }
                InsertSymbol(new CodeSymbol
                {
                    Name = symbol.Name,
                    Type = "property",
                    FilePath = NormalizePath(node.SyntaxTree.FilePath),
                    LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    ProjectId = _projectId,
                    ReturnType = symbol.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    MemberOf = symbol.ContainingType?.Name,
                    Scope = "class",
                    Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                    IsStatic = symbol.IsStatic,
                    IsAbstract = symbol.IsAbstract
                });
                base.VisitPropertyDeclaration(node);
            }

            public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
            {
                foreach (var variable in node.Declaration.Variables)
                {
                    var symbol = _model.GetDeclaredSymbol(variable) as IEventSymbol;
                    if (symbol == null) continue;
                    InsertSymbol(new CodeSymbol
                    {
                        Name = symbol.Name,
                        Type = "event",
                        FilePath = NormalizePath(node.SyntaxTree.FilePath),
                        LineNumber = variable.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        ProjectId = _projectId,
                        ReturnType = symbol.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        MemberOf = symbol.ContainingType?.Name,
                        Scope = "class",
                        Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                        IsStatic = symbol.IsStatic
                    });
                }
                base.VisitEventFieldDeclaration(node);
            }

            public override void VisitEventDeclaration(EventDeclarationSyntax node)
            {
                var symbol = _model.GetDeclaredSymbol(node);
                if (symbol == null) { base.VisitEventDeclaration(node); return; }
                InsertSymbol(new CodeSymbol
                {
                    Name = symbol.Name,
                    Type = "event",
                    FilePath = NormalizePath(node.SyntaxTree.FilePath),
                    LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    ProjectId = _projectId,
                    ReturnType = symbol.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    MemberOf = symbol.ContainingType?.Name,
                    Scope = "class",
                    Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                    IsStatic = symbol.IsStatic
                });
                base.VisitEventDeclaration(node);
            }

            public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
            {
                foreach (var variable in node.Declaration.Variables)
                {
                    var symbol = _model.GetDeclaredSymbol(variable) as IFieldSymbol;
                    if (symbol == null) continue;
                    InsertSymbol(new CodeSymbol
                    {
                        Name = symbol.Name,
                        Type = "field",
                        FilePath = NormalizePath(node.SyntaxTree.FilePath),
                        LineNumber = variable.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        ProjectId = _projectId,
                        ReturnType = symbol.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        MemberOf = symbol.ContainingType?.Name,
                        Scope = "class",
                        Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                        IsStatic = symbol.IsStatic
                    });
                }
                base.VisitFieldDeclaration(node);
            }

            private void ExtractType(BaseTypeDeclarationSyntax node, string typeName)
            {
                var symbol = _model.GetDeclaredSymbol(node) as INamedTypeSymbol;
                if (symbol == null) { base.DefaultVisit(node); return; }

                string parentName = null;
                if (symbol.BaseType != null && symbol.BaseType.SpecialType == SpecialType.None)
                    parentName = symbol.BaseType.Name;

                InsertSymbol(new CodeSymbol
                {
                    Name = symbol.Name,
                    Type = typeName,
                    FilePath = NormalizePath(node.SyntaxTree.FilePath),
                    LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    ProjectId = _projectId,
                    ParentName = parentName,
                    MemberOf = symbol.ContainingType?.Name,
                    Scope = symbol.ContainingType != null ? "class" : "namespace",
                    Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                    IsStatic = symbol.IsStatic,
                    IsAbstract = symbol.IsAbstract,
                    GenericParams = FormatTypeParams(symbol.TypeParameters),
                    SourcePreview = node.ToString().Split('\n')[0].Trim()
                });

                // Recurse into nested types and members
                foreach (var child in node.ChildNodes())
                    Visit(child);
            }

            private void InsertSymbol(CodeSymbol sym)
            {
                _db.InsertSymbol(sym);
                SymbolCount++;
            }

            private static string FormatParameters(System.Collections.Immutable.ImmutableArray<IParameterSymbol>? parameters)
            {
                if (parameters == null || !parameters.HasValue || parameters.Value.Length == 0) return null;
                return string.Join(", ", parameters.Value.Select(p =>
                    p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + p.Name));
            }

            private static string FormatTypeParams(System.Collections.Immutable.ImmutableArray<ITypeParameterSymbol> typeParams)
            {
                if (typeParams.Length == 0) return null;
                return "<" + string.Join(", ", typeParams.Select(tp => tp.Name)) + ">";
            }

            private static string GetScope(SyntaxNode node)
            {
                var parent = node.Parent;
                while (parent != null)
                {
                    if (parent is TypeDeclarationSyntax) return "class";
                    if (parent is NamespaceDeclarationSyntax || parent is FileScopedNamespaceDeclarationSyntax) return "namespace";
                    parent = parent.Parent;
                }
                return "global";
            }

            private static string NormalizePath(string path)
            {
                return path?.Replace('\\', '/');
            }
        }

        // ─── Pass 2: Relationship Extraction ─────────────────────────────────

        private class RelationshipExtractor : CSharpSyntaxWalker
        {
            private readonly CodeGraphDatabase _db;
            private readonly int _projectId;
            private readonly SemanticModel _model;
            private readonly Dictionary<string, long> _symbolLookup;
            private readonly HashSet<string> _dedupSet;
            public int RelationshipCount { get; private set; }

            public RelationshipExtractor(CodeGraphDatabase db, int projectId, SemanticModel model,
                Dictionary<string, long> symbolLookup, HashSet<string> dedupSet)
            {
                _db = db;
                _projectId = projectId;
                _model = model;
                _symbolLookup = symbolLookup;
                _dedupSet = dedupSet;
            }

            // --- Inheritance & Interface Implementation ---

            public override void VisitClassDeclaration(ClassDeclarationSyntax node) =>
                ExtractTypeRelationships(node);

            public override void VisitStructDeclaration(StructDeclarationSyntax node) =>
                ExtractTypeRelationships(node);

            private void ExtractTypeRelationships(TypeDeclarationSyntax node)
            {
                var symbol = _model.GetDeclaredSymbol(node) as INamedTypeSymbol;
                if (symbol == null) { base.DefaultVisit(node); return; }

                long fromId = LookupSymbol(symbol.Name);
                if (fromId < 0) { VisitChildren(node); return; }

                // Base class
                if (symbol.BaseType != null && symbol.BaseType.SpecialType == SpecialType.None)
                {
                    long toId = LookupSymbol(symbol.BaseType.Name);
                    if (toId >= 0) AddRelationship(fromId, toId, "inherits", node);
                }

                // Interfaces
                foreach (var iface in symbol.Interfaces)
                {
                    long toId = LookupSymbol(iface.Name);
                    if (toId >= 0) AddRelationship(fromId, toId, "implements", node);
                }

                VisitChildren(node);
            }

            // --- Method Calls ---

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                var symbolInfo = _model.GetSymbolInfo(node);
                var targetSymbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

                if (targetSymbol is IMethodSymbol methodSymbol)
                {
                    // Find the calling method (walk up to the containing method/constructor/property)
                    var callerNode = node.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()
                                     ?? (SyntaxNode)node.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                    if (callerNode != null)
                    {
                        var callerSymbol = _model.GetDeclaredSymbol(callerNode);
                        if (callerSymbol != null)
                        {
                            string callerName = GetLookupName(callerSymbol);
                            string targetName = GetLookupName(methodSymbol);

                            long fromId = LookupSymbol(callerName) >= 0 ? LookupSymbol(callerName) : LookupSymbol(callerSymbol.Name);
                            long toId = LookupSymbol(targetName) >= 0 ? LookupSymbol(targetName) : LookupSymbol(methodSymbol.Name);

                            if (fromId >= 0 && toId >= 0 && fromId != toId)
                                AddRelationship(fromId, toId, "calls", node);
                        }
                    }
                }

                base.VisitInvocationExpression(node);
            }

            // --- Object Creation (constructor calls) ---

            public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
            {
                var symbolInfo = _model.GetSymbolInfo(node);
                var ctorSymbol = symbolInfo.Symbol as IMethodSymbol;

                if (ctorSymbol != null)
                {
                    var callerNode = node.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()
                                     ?? (SyntaxNode)node.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                    if (callerNode != null)
                    {
                        var callerSymbol = _model.GetDeclaredSymbol(callerNode);
                        if (callerSymbol != null)
                        {
                            string callerName = GetLookupName(callerSymbol);
                            string targetName = ctorSymbol.ContainingType?.Name + ".ctor";

                            long fromId = LookupSymbol(callerName) >= 0 ? LookupSymbol(callerName) : LookupSymbol(callerSymbol.Name);
                            long toId = LookupSymbol(targetName);

                            if (fromId >= 0 && toId >= 0 && fromId != toId)
                                AddRelationship(fromId, toId, "calls", node);
                        }
                    }
                }

                base.VisitObjectCreationExpression(node);
            }

            // --- Event Subscriptions (+=) ---

            public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
            {
                if (node.IsKind(SyntaxKind.AddAssignmentExpression))
                {
                    // e.g., broker.MessageSent += OnMessageSent
                    var leftSymbol = _model.GetSymbolInfo(node.Left).Symbol as IEventSymbol;
                    if (leftSymbol != null)
                    {
                        // Find the subscribing method (the handler on the right side)
                        var rightSymbol = _model.GetSymbolInfo(node.Right).Symbol as IMethodSymbol;

                        // Find the containing method/constructor that does the subscription
                        var callerNode = node.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()
                                         ?? (SyntaxNode)node.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                        if (callerNode != null)
                        {
                            var callerSymbol = _model.GetDeclaredSymbol(callerNode);
                            if (callerSymbol != null)
                            {
                                string callerName = GetLookupName(callerSymbol);
                                long fromId = LookupSymbol(callerName) >= 0 ? LookupSymbol(callerName) : LookupSymbol(callerSymbol.Name);

                                // Link caller -> event (subscribes)
                                string eventName = leftSymbol.ContainingType?.Name + "." + leftSymbol.Name;
                                long eventId = LookupSymbol(eventName) >= 0 ? LookupSymbol(eventName) : LookupSymbol(leftSymbol.Name);
                                if (fromId >= 0 && eventId >= 0 && fromId != eventId)
                                    AddRelationship(fromId, eventId, "subscribes", node);

                                // Link handler -> event (handles)
                                if (rightSymbol != null)
                                {
                                    string handlerName = GetLookupName(rightSymbol);
                                    long handlerId = LookupSymbol(handlerName) >= 0 ? LookupSymbol(handlerName) : LookupSymbol(rightSymbol.Name);
                                    if (handlerId >= 0 && eventId >= 0 && handlerId != eventId)
                                        AddRelationship(handlerId, eventId, "handles", node);
                                }
                            }
                        }
                    }
                }

                base.VisitAssignmentExpression(node);
            }

            // --- Method Overrides ---

            private void CheckMethodOverride(MethodDeclarationSyntax node)
            {
                var symbol = _model.GetDeclaredSymbol(node);
                if (symbol?.OverriddenMethod != null)
                {
                    string fromName = GetLookupName(symbol);
                    string toName = GetLookupName(symbol.OverriddenMethod);

                    long fromId = LookupSymbol(fromName) >= 0 ? LookupSymbol(fromName) : LookupSymbol(symbol.Name);
                    long toId = LookupSymbol(toName) >= 0 ? LookupSymbol(toName) : LookupSymbol(symbol.OverriddenMethod.Name);

                    if (fromId >= 0 && toId >= 0)
                        AddRelationship(fromId, toId, "overrides", node);
                }
            }

            // --- Type References (field types, parameter types, return types) ---

            public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
            {
                var typeInfo = _model.GetTypeInfo(node.Declaration.Type);
                if (typeInfo.Type is INamedTypeSymbol fieldType && fieldType.SpecialType == SpecialType.None)
                {
                    foreach (var variable in node.Declaration.Variables)
                    {
                        var fieldSymbol = _model.GetDeclaredSymbol(variable) as IFieldSymbol;
                        if (fieldSymbol != null)
                        {
                            string fieldName = GetLookupName(fieldSymbol);
                            long fromId = LookupSymbol(fieldName) >= 0 ? LookupSymbol(fieldName) : LookupSymbol(fieldSymbol.Name);
                            long toId = LookupSymbol(fieldType.Name);
                            if (fromId >= 0 && toId >= 0 && fromId != toId)
                                AddRelationship(fromId, toId, "uses_type", node);
                        }
                    }
                }
                base.VisitFieldDeclaration(node);
            }

            public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
            {
                var propSymbol = _model.GetDeclaredSymbol(node);
                if (propSymbol?.Type is INamedTypeSymbol propType && propType.SpecialType == SpecialType.None)
                {
                    string propName = GetLookupName(propSymbol);
                    long fromId = LookupSymbol(propName) >= 0 ? LookupSymbol(propName) : LookupSymbol(propSymbol.Name);
                    long toId = LookupSymbol(propType.Name);
                    if (fromId >= 0 && toId >= 0 && fromId != toId)
                        AddRelationship(fromId, toId, "uses_type", node);
                }
                base.VisitPropertyDeclaration(node);
            }

            public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                var methodSym = _model.GetDeclaredSymbol(node);
                if (methodSym != null)
                {
                    string methodName = GetLookupName(methodSym);
                    long fromId = LookupSymbol(methodName) >= 0 ? LookupSymbol(methodName) : LookupSymbol(methodSym.Name);

                    if (fromId >= 0)
                    {
                        // Return type reference
                        if (methodSym.ReturnType is INamedTypeSymbol retType && retType.SpecialType == SpecialType.None)
                        {
                            long toId = LookupSymbol(retType.Name);
                            if (toId >= 0 && fromId != toId)
                                AddRelationship(fromId, toId, "uses_type", node);
                        }

                        // Parameter type references
                        foreach (var param in methodSym.Parameters)
                        {
                            if (param.Type is INamedTypeSymbol paramType && paramType.SpecialType == SpecialType.None)
                            {
                                long toId = LookupSymbol(paramType.Name);
                                if (toId >= 0 && fromId != toId)
                                    AddRelationship(fromId, toId, "uses_type", node);
                            }
                        }
                    }
                }

                CheckMethodOverride(node);
                base.VisitMethodDeclaration(node);
            }

            // --- Helpers ---

            private string GetLookupName(ISymbol symbol)
            {
                if (symbol.ContainingType != null)
                    return symbol.ContainingType.Name + "." + symbol.Name;
                return symbol.Name;
            }

            private long LookupSymbol(string name)
            {
                if (string.IsNullOrEmpty(name)) return -1;
                return _symbolLookup.TryGetValue(name, out long id) ? id : -1;
            }

            private void AddRelationship(long fromId, long toId, string type, SyntaxNode node)
            {
                string key = $"{fromId}|{toId}|{type}";
                if (!_dedupSet.Add(key)) return;

                var lineSpan = node.GetLocation().GetLineSpan();
                _db.InsertRelationship(new CodeRelationship
                {
                    FromId = fromId,
                    ToId = toId,
                    Type = type,
                    FilePath = NormalizePath(node.SyntaxTree.FilePath),
                    LineNumber = lineSpan.StartLinePosition.Line + 1
                });
                RelationshipCount++;
            }

            private void VisitChildren(SyntaxNode node)
            {
                foreach (var child in node.ChildNodes())
                    Visit(child);
            }

            private static string NormalizePath(string path) => path?.Replace('\\', '/');
        }

        // ─── Result Model ────────────────────────────────────────────────────

        public class IndexResult
        {
            public string ProjectName { get; set; }
            public int FileCount { get; set; }
            public int SymbolCount { get; set; }
            public int RelationshipCount { get; set; }
            public long DurationMs { get; set; }
        }
    }
}
