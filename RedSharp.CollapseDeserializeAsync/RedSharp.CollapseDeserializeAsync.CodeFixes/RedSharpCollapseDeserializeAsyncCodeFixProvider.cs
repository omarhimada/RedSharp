using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using RedSharp.CollapseDeserializeAsync;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RedSharp {
    /// <summary>
    /// Provides code fixes for diagnostics that identify opportunities to collapse or parallelize deserialization logic
    /// in foreach loops within C# code.
    /// </summary>
    /// <remarks>This code fix provider registers multiple code actions that refactor foreach loops performing
    /// deserialization, enabling more concise or parallelized patterns. It supports batch fixing across documents,
    /// projects, or solutions, and is intended for use with diagnostics produced by the
    /// RedSharpCollapseDeserializeAsyncAnalyzer. The provider does not modify code directly; instead, it registers code
    /// fixes for the IDE to present to the user.</remarks>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CollapseDeserializeAsyncFix)), Shared]
    public sealed class CollapseDeserializeAsyncFix : CodeFixProvider {
        private const string _collapseIntoPatternMatchingIf = "Collapse into pattern-matching if";
        private const string _parallelizeDownloadsAndDeserialize = "Parallelize downloads + deserialize";
        private const string _collapseDeserializeLoop = "Collapse deserialize loop";
        private const string _collapseDeserializeLoopKey = "CollapseDeserializeLoop";

        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(RedSharpCollapseDeserializeAsyncAnalyzer.DiagnosticId);

        /// <summary>
        /// Gets the fix all provider that enables batch fixing of code issues across multiple scopes.
        /// </summary>
        /// <remarks>Use the returned provider to apply code fixes to all occurrences of an issue within a
        /// document, project, or solution. This facilitates efficient bulk code cleanup.</remarks>
        /// <returns>A <see cref="FixAllProvider"/> instance that supports batch fixing operations.</returns>
        public override FixAllProvider GetFixAllProvider()
            => WellKnownFixAllProviders.BatchFixer;

        /// <summary>
        /// Registers one or more code fixes for the specified diagnostic context asynchronously.
        /// </summary>
        /// <remarks>This method analyzes the diagnostic context and registers relevant code fixes that
        /// can be applied to the affected code. Code fixes are only registered if the context contains a matching
        /// diagnostic and code location. The method does not modify the document directly; it only registers available
        /// fixes for the IDE to present to the user.</remarks>
        /// <param name="context">The context that provides information about the diagnostics to fix and the document to apply fixes to.</param>
        /// <returns>A task that represents the asynchronous operation of registering code fixes.</returns>
        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            SyntaxNode root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
            if (root == null) {
                return;
            }

            Diagnostic diagnostic = context.Diagnostics[0];
            ForEachStatementSyntax forEach = root.FindNode(diagnostic.Location.SourceSpan)
                              .FirstAncestorOrSelf<ForEachStatementSyntax>();
            if (forEach == null) {
                return;
            }

            // Safe collapse
            context.RegisterCodeFix(
                CodeAction.Create(
                    _collapseIntoPatternMatchingIf,
                    ct => RewriteForeachAsync(context.Document, forEach, ct),
                    "CollapseFix"
                ),
                diagnostic
            );

            // Parallel rewrite
            context.RegisterCodeFix(
                CodeAction.Create(
                    _parallelizeDownloadsAndDeserialize,
                    ct => RewriteParallelAsync(context.Document, forEach, ct),
                    "ParallelFix"
                ),
                diagnostic
            );

            // Collapse deserialize loop
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: _collapseDeserializeLoop,
                    createChangedDocument: ct => ApplyFixAsync(context.Document, forEach, ct),
                    equivalenceKey: _collapseDeserializeLoopKey),
                diagnostic);
        }

        private static async Task<Document> ApplyFixAsync(Document document, ForEachStatementSyntax forEach, CancellationToken ct) {
            _ = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            DocumentEditor editor = await DocumentEditor.CreateAsync(document, ct).ConfigureAwait(false);

            if (!(forEach.Statement is BlockSyntax block) || block.Statements.Count < 3) {
                return document;
            }

            LocalDeclarationStatementSyntax dataDecl = (LocalDeclarationStatementSyntax)block.Statements[0];
            LocalDeclarationStatementSyntax valDecl = (LocalDeclarationStatementSyntax)block.Statements[1];
            IfStatementSyntax ifStmt = (IfStatementSyntax)block.Statements[2];

            EqualsValueClauseSyntax dataInit = dataDecl.Declaration.Variables[0].Initializer;
            EqualsValueClauseSyntax valueInit = valDecl.Declaration.Variables[0].Initializer;

            // Build: if ( <valueInit with data replaced by dataInit> is { } value) { results.Add(value); }
            // We’ll do a very simple text-based substitution for the demo.
            // For production: use SyntaxRewriter replacing IdentifierName(dataVar) with (dataInit).
            string dataVarName = dataDecl.Declaration.Variables[0].Identifier.ValueText;
            string valueVarName = valDecl.Declaration.Variables[0].Identifier.ValueText;

            string rewrittenValueInitText = valueInit.ToString().Replace(dataVarName, $"({dataInit})");
            ExpressionSyntax rewrittenValueInit = SyntaxFactory.ParseExpression(rewrittenValueInitText);

            RecursivePatternSyntax pattern = SyntaxFactory.RecursivePattern()
                .WithPropertyPatternClause(SyntaxFactory.PropertyPatternClause());

            IsPatternExpressionSyntax isPatternExpr =
                SyntaxFactory.IsPatternExpression(
                    rewrittenValueInit,
                    SyntaxFactory.RecursivePattern()
                        .WithPropertyPatternClause(
                            SyntaxFactory.PropertyPatternClause() // empty => `{ }`
                        )
                        .WithDesignation(
                            SyntaxFactory.SingleVariableDesignation(
                                SyntaxFactory.Identifier(valueVarName)
                            )
                        )
                );

            // Simpler: parse `(<expr>) is { } value`
            StatementSyntax collapsedIf = SyntaxFactory.ParseStatement($@"
                if ({rewrittenValueInit} is {{ }} {valueVarName})
                {NormalizeStatement(ifStmt.Statement)}
                ");

            // Replace the foreach body with a new block containing only collapsed if
            BlockSyntax newBlock = SyntaxFactory.Block((StatementSyntax)collapsedIf);

            editor.ReplaceNode(forEach, forEach.WithStatement(newBlock));

            return editor.GetChangedDocument();
        }

        /// <summary>
        /// Normalizes a statement syntax node by ensuring its string representation is enclosed in braces if it is not
        /// already a block.
        /// </summary>
        /// <remarks>This method is useful for generating consistent code output, especially when handling
        /// single-line or expression statements that may require block formatting.</remarks>
        /// <param name="stmt">The statement syntax node to normalize. If the node is not a block, it will be wrapped in braces in the
        /// returned string.</param>
        /// <returns>A string containing the normalized statement, enclosed in braces if necessary.</returns>
        private static string NormalizeStatement(StatementSyntax stmt) =>
            // preserve body; if it’s expression statement, wrap in braces to be safe
            stmt is BlockSyntax ? stmt.ToString() : "{\n" + stmt + "\n}";

        /// <summary>
        /// Rewrites the specified foreach statement in the document to perform parallel downloads and deserialization
        /// operations asynchronously.
        /// </summary>
        /// <remarks>This method replaces the body of the provided foreach statement with code that
        /// executes downloads and deserialization in parallel, improving performance for scenarios involving multiple
        /// asynchronous operations. The rewritten code uses Task.WhenAll to await all parallel tasks and aggregates
        /// non-null results.</remarks>
        /// <param name="document">The document containing the syntax tree to be modified.</param>
        /// <param name="forEach">The foreach statement syntax node to be replaced with a parallelized version.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is a new document with the rewritten
        /// foreach statement, or the original document if the syntax root is not available.</returns>
        private static async Task<Document> RewriteParallelAsync(
            Document document,
            ForEachStatementSyntax forEach,
            CancellationToken ct) {
            SyntaxNode root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root == null) {
                return document;
            }

            DocumentEditor editor = await DocumentEditor.CreateAsync(document, ct).ConfigureAwait(false);

            // Build the new code that does parallel downloads + deserialize
            StatementSyntax replacementBlock = SyntaxFactory.ParseStatement(@"
                {
                    var tasks = filteredKeys.Select(async key =>
                        JsonSerializer.Deserialize<T>(
                            await DownloadBytesAsync(key, ct),
                            _jsonOptions()
                        )
                    );

                    results.AddRange((await Task.WhenAll(tasks)).Where(v => v is not null)!);
                }
            ");

            // Replace the old foreach body
            editor.ReplaceNode(forEach.Statement, (BlockSyntax)replacementBlock);

            return editor.GetChangedDocument();
        }

        /// <summary>
        /// Rewrites the body of a foreach statement in the specified document to deserialize downloaded data and add
        /// the result to a collection.
        /// </summary>
        /// <remarks>The rewritten foreach body attempts to download bytes for each key and deserialize
        /// them using the specified type argument. If deserialization succeeds, the value is added to the results
        /// collection. If the type argument cannot be determined, no changes are made to the document.</remarks>
        /// <param name="document">The document containing the foreach statement to be rewritten.</param>
        /// <param name="forEach">The foreach statement syntax node to be transformed.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A new document with the rewritten foreach statement if the type argument for deserialization is found;
        /// otherwise, returns the original document.</returns>
        private async Task<Document> RewriteForeachAsync(
                Document document,
                ForEachStatementSyntax forEach,
                CancellationToken ct) {
            DocumentEditor editor = await DocumentEditor.CreateAsync(document, ct);

            TypeSyntax typeArg = TryGetDeserializeTypeArg(forEach);
            if (typeArg is null) {
                return document;
            }

            string typeText = typeArg.ToFullString().Trim();
            string keyIdent = forEach.Identifier.ValueText; // foreach (var key in ...)

            StatementSyntax newIf = SyntaxFactory.ParseStatement($@"
            if (await DownloadBytesAsync({keyIdent}, ct) is {{ }} data &&
                JsonSerializer.Deserialize<{typeText}>(data, _jsonOptions()) is {{ }} value)
            {{
                results.Add(value);
            }}
            ");

            editor.ReplaceNode(forEach.Statement, SyntaxFactory.Block(newIf));
            return editor.GetChangedDocument();
        }

        /// <summary>
        /// Attempts to retrieve the type argument used in a JsonSerializer.Deserialize<T>(...) invocation within the
        /// specified foreach statement.
        /// </summary>
        /// <remarks>This method searches for both qualified (JsonSerializer.Deserialize<T>(...)) and
        /// unqualified (Deserialize<T>(...)) invocations within the provided foreach statement. Only invocations with a
        /// single type argument are considered.</remarks>
        /// <param name="forEach">The foreach statement to search for a JsonSerializer.Deserialize<T>(...) invocation. Cannot be null.</param>
        /// <returns>The TypeSyntax representing the type argument of the first matching JsonSerializer.Deserialize<T>(...)
        /// invocation found; otherwise, null if no such invocation exists.</returns>
        private static TypeSyntax? TryGetDeserializeTypeArg(ForEachStatementSyntax forEach) {
            // Find an invocation that looks like:  JsonSerializer.Deserialize<Something>(...)
            InvocationExpressionSyntax inv = forEach.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(i => {
                    // JsonSerializer.Deserialize<...>(...)
                    if (i.Expression is MemberAccessExpressionSyntax ma &&
                        ma.Name is GenericNameSyntax gn &&
                        gn.Identifier.ValueText == "Deserialize" &&
                        gn.TypeArgumentList.Arguments.Count == 1) {
                        return true;
                    }

                    // Deserialize<...>(...)  (in case of using static)
                    if (i.Expression is GenericNameSyntax gn2 &&
                        gn2.Identifier.ValueText == "Deserialize" &&
                        gn2.TypeArgumentList.Arguments.Count == 1) {
                        return true;
                    }

                    return false;
                });

            if (inv is null) {
                return null;
            }

            if (inv.Expression is MemberAccessExpressionSyntax ma2 && ma2.Name is GenericNameSyntax g1) {
                return g1.TypeArgumentList.Arguments[0];
            }

            if (inv.Expression is GenericNameSyntax g2) {
                return g2.TypeArgumentList.Arguments[0];
            }

            return null;
        }
    }
}
