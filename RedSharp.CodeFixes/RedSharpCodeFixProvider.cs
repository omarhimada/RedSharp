using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RedSharp {
    /// <summary>
    /// Provides a code fix for C# source that rewrites eligible nested foreach statements into a single statement using
    /// SelectMany and AddRange to improve code clarity and leverage LINQ.
    /// </summary>
    /// <remarks>This code fix provider analyzes diagnostics reported by RedSharpAnalyzer and, when a specific
    /// nested foreach pattern is detected, offers a refactoring to replace the loops with a more concise LINQ-based
    /// expression. The fix is only offered when the code matches a safe, well-defined pattern to avoid unintended
    /// changes. The provider supports batch fixing via the standard Fix All operation. The transformation does not add
    /// missing using directives for LINQ; ensure that 'using System.Linq;' is present in the document if
    /// required.</remarks>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RedSharpCodeFixProvider))]
    [Shared]
    public sealed class RedSharpCodeFixProvider : CodeFixProvider {
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(RedSharpAnalyzer.DiagnosticId);

        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            SyntaxNode root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null) {
                return;
            }

            Diagnostic diagnostic = context.Diagnostics.First();
            SyntaxToken token = root.FindToken(diagnostic.Location.SourceSpan.Start);

            // Find the nested foreach statement the diagnostic pointed at
            ForEachStatementSyntax nestedForeach = token.Parent?
                .AncestorsAndSelf()
                .OfType<ForEachStatementSyntax>()
                .FirstOrDefault();

            if (nestedForeach is null) {
                return;
            }

            // We need the outer foreach too
            ForEachStatementSyntax outerForeach = nestedForeach.Ancestors().OfType<ForEachStatementSyntax>().FirstOrDefault();
            if (outerForeach is null) {
                return;
            }

            // Only offer the fix when we match a very specific safe-ish shape.
            // outer: foreach (var a in OUTER) { foreach (var b in INNER) { result.Add(EXPR); } }
            if (!TryMatchAddPattern(outerForeach, nestedForeach, out IdentifierNameSyntax resultIdentifier, out ExpressionSyntax addArgumentExpr)) {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Rewrite to SelectMany + AddRange (heuristic)",
                    createChangedDocument: ct => RewriteToSelectManyAsync(context.Document, outerForeach, nestedForeach, resultIdentifier, addArgumentExpr, ct),
                    equivalenceKey: "RewriteToSelectManyAddRange"),
                diagnostic);
        }

        private static bool TryMatchAddPattern(
            ForEachStatementSyntax outer,
            ForEachStatementSyntax inner,
            out IdentifierNameSyntax resultIdentifier,
            out ExpressionSyntax addArgumentExpr) {
            resultIdentifier = null;
            addArgumentExpr = null;

            // Require outer body to be a block
            if (!(outer.Statement is BlockSyntax outerBlock)) {
                return false;
            }

            // Require inner body to be a block with exactly one statement: result.Add(<expr>);
            if (!(inner.Statement is BlockSyntax innerBlock)) {
                return false;
            }

            if (innerBlock.Statements.Count != 1) {
                return false;
            }

            ExpressionStatementSyntax statement0 = innerBlock.Statements[0] as ExpressionStatementSyntax;
            if (statement0 == null) {
                return false;
            }

            ExpressionStatementSyntax exprStmt = statement0;

            if (!(exprStmt.Expression is InvocationExpressionSyntax invocation)) {
                return false;
            }

            if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess)) {
                return false;
            }

            if (memberAccess.Name.Identifier.Text != "Add") {
                return false;
            }

            if (!(memberAccess.Expression is IdentifierNameSyntax resultId)) {
                return false;
            }

            SeparatedSyntaxList<ArgumentSyntax> args = invocation.ArgumentList.Arguments;
            if (args.Count != 1) {
                return false;
            }

            ExpressionSyntax argExpr = args[0].Expression;

            // Heuristic: expression should reference BOTH loop iteration identifiers somewhere
            string outerVar = outer.Identifier.ValueText;
            string innerVar = inner.Identifier.ValueText;

            bool containsOuter = argExpr.DescendantTokens().Any(t => t.ValueText == outerVar);
            bool containsInner = argExpr.DescendantTokens().Any(t => t.ValueText == innerVar);

            if (!containsOuter || !containsInner) {
                return false;
            }

            // Also require that the inner foreach is directly contained in the outer foreach body
            // (prevents weird nesting with other statements)
            if (!outerBlock.Statements.OfType<ForEachStatementSyntax>().Contains(inner)) {
                return false;
            }

            resultIdentifier = resultId;
            addArgumentExpr = argExpr;
            return true;
        }

        /// <summary>
        /// Rewrites a pair of nested foreach statements into a single statement that adds the result of a SelectMany
        /// LINQ query to the specified result collection.
        /// </summary>
        /// <remarks>This method transforms nested foreach loops into a single AddRange call using
        /// SelectMany and Select to improve code clarity and leverage LINQ. The method does not add missing using
        /// directives for LINQ; ensure that 'using System.Linq;' is present in the document if required.</remarks>
        /// <param name="document">The document containing the syntax tree to be rewritten.</param>
        /// <param name="outer">The outer foreach statement to be replaced.</param>
        /// <param name="inner">The inner foreach statement nested within the outer statement.</param>
        /// <param name="resultIdentifier">The identifier representing the collection to which the results will be added.</param>
        /// <param name="addArgumentExpr">The expression to be used as the result selector in the Select projection.</param>
        /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A new Document instance with the rewritten syntax tree if the transformation is applied; otherwise, the
        /// original document.</returns>
        private static async Task<Document> RewriteToSelectManyAsync(
            Document document,
            ForEachStatementSyntax outer,
            ForEachStatementSyntax inner,
            IdentifierNameSyntax resultIdentifier,
            ExpressionSyntax addArgumentExpr,
            CancellationToken cancellationToken) {
            SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) {
                return document;
            }

            string a = outer.Identifier.ValueText;
            string b = inner.Identifier.ValueText;

            InvocationExpressionSyntax selectMany = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    outer.Expression.Parenthesize(),
                    SyntaxFactory.IdentifierName("SelectMany")))
                .WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(
                                SyntaxFactory.SimpleLambdaExpression(
                                    SyntaxFactory.Parameter(SyntaxFactory.Identifier(a)),
                                    SyntaxFactory.InvocationExpression(
                                        SyntaxFactory.MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            inner.Expression.Parenthesize(),
                                            SyntaxFactory.IdentifierName("Select")))
                                    .WithArgumentList(
                                        SyntaxFactory.ArgumentList(
                                            SyntaxFactory.SingletonSeparatedList(
                                                SyntaxFactory.Argument(
                                                    SyntaxFactory.SimpleLambdaExpression(
                                                        SyntaxFactory.Parameter(SyntaxFactory.Identifier(b)),
                                                        addArgumentExpr))))))))));

            ExpressionStatementSyntax addRangeInvocation = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        resultIdentifier,
                        SyntaxFactory.IdentifierName("AddRange")))
                .WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(selectMany)))));

            // Replace the entire outer foreach statement with the AddRange statement
            SyntaxNode newRoot = root.ReplaceNode(outer, addRangeInvocation.WithTriviaFrom(outer));

            // Add using System.Linq; if missing (basic approach: let VS add it or rely on global usings)
            // We won’t force-inject it here to keep it simple.
            return document.WithSyntaxRoot(newRoot);
        }
    }

    internal static class SyntaxHelpers {
        public static ExpressionSyntax Parenthesize(this ExpressionSyntax expr)
            => expr is ParenthesizedExpressionSyntax ? expr : SyntaxFactory.ParenthesizedExpression(expr);
    }
}
