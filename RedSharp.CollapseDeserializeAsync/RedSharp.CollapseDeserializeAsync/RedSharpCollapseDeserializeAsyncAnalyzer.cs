using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace RedSharp.CollapseDeserializeAsync {
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class RedSharpCollapseDeserializeAsyncAnalyzer : DiagnosticAnalyzer {
        public const string DiagnosticId = "RedSharp";

        private const string Deserialize = "Deserialize";
        private const string JsonSerializer = "JsonSerializer";
        private const string SystemTextJson = "System.Text.Json";
        private const string Category = "Naming";


        // You can change these strings in the Resources.resx file. If you do not want your analyzer to be localize-able, you can use regular strings for Title and MessageFormat.
        // See https://github.com/dotnet/roslyn/blob/main/docs/analyzers/Localizing%20Analyzers.md for more on localization
        private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.AnalyzerTitle), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.AnalyzerMessageFormat), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.AnalyzerDescription), Resources.ResourceManager, typeof(Resources));

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true, description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return ImmutableArray.Create(Rule); } }

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ForEachStatement);
            context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.NamedType);
        }

        private static void AnalyzeSymbol(SymbolAnalysisContext context) {
            // You don't need a symbol analyzer for this rule.
            // Keep this empty (or remove RegisterSymbolAction + this method entirely)
            // so it doesn't produce unrelated diagnostics.
        }

        private static void Analyze(SyntaxNodeAnalysisContext context) {
            // We registered for ForEachStatement
            if (context.Node is not ForEachStatementSyntax forEach) {
                return;
            }

            // Only handle: foreach (...) { ... }
            if (forEach.Statement is not BlockSyntax block) {
                return;
            }

            // We want a very specific pattern:
            //
            // foreach (var key in filteredKeys) {
            //     var data = await DownloadBytesAsync(key, ct);
            //     var value = JsonSerializer.Deserialize<T>(data, _jsonOptions());
            //     if (value != null) results.Add(value);
            // }
            //
            // We'll match this conservatively so we don't break code.

            if (block.Statements.Count < 3) {
                return;
            }

            // 1) data declaration
            if (block.Statements[0] is not LocalDeclarationStatementSyntax dataDecl ||
            !TryGetSingleLocal(dataDecl, out var dataVarName, out ExpressionSyntax dataInit)) {
                return;
            }

            // data init must contain an await expression (DownloadBytesAsync(...))
            if (!ContainsAwait(dataInit)) {
                return;
            }

            // 2) value declaration
            if (block.Statements[1] is not LocalDeclarationStatementSyntax valueDecl ||
            !TryGetSingleLocal(valueDecl, out var valueVarName, out ExpressionSyntax valueInit)) {
                return;
            }

            // value init should be a JsonSerializer.Deserialize<...>(dataVarName, ...)
            if (!LooksLikeJsonDeserialize(valueInit, dataVarName)) {
                return;
            }

            // 3) if (value != null) results.Add(value);
            if (block.Statements[2] is not IfStatementSyntax ifStmt) {
                return;
            }

            if (!IsNotNullCheckOf(ifStmt.Condition, valueVarName)) {
                return;
            }

            if (!IsAddOfVariable(ifStmt.Statement, valueVarName)) {
                return;
            }

            // Optional: semantic sanity check that "Deserialize" is System.Text.Json.JsonSerializer.Deserialize
            if (!IsSystemTextJsonDeserialize(context, valueInit)) {
                return;
            }

            // If we got here, we matched the pattern.
            // Report diagnostic on the foreach keyword (nice UX for lightbulb)
            context.ReportDiagnostic(Diagnostic.Create(Rule, forEach.ForEachKeyword.GetLocation()));
        }

        private static bool TryGetSingleLocal(
            LocalDeclarationStatementSyntax decl,
            out string variableName,
            out ExpressionSyntax initializer) {
            variableName = string.Empty;
            initializer = null;

            SeparatedSyntaxList<VariableDeclaratorSyntax>? vars = decl.Declaration?.Variables;
            if (vars is null || vars.Value.Count != 1) {
                return false;
            }

            VariableDeclaratorSyntax v = vars.Value[0];
            if (v.Identifier.ValueText is null || v.Initializer?.Value is not ExpressionSyntax init) {
                return false;
            }

            variableName = v.Identifier.ValueText;
            initializer = init;
            return true;
        }

        private static bool ContainsAwait(ExpressionSyntax expr) => expr.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>().Any();

        private static bool LooksLikeJsonDeserialize(ExpressionSyntax expr, string dataVarName) {
            // We’re looking for something like:
            // JsonSerializer.Deserialize<T>(data, _jsonOptions())
            //
            // Be flexible with whitespace/qualified names etc.
            if (expr is not InvocationExpressionSyntax invocation) {
                return false;
            }

            // Expression could be:
            // - JsonSerializer.Deserialize<T>
            // - System.Text.Json.JsonSerializer.Deserialize<T>
            // - Deserialize<T> (if using static import) - we allow but confirm semantically later
            if (invocation.Expression is not ExpressionSyntax target) {
                return false;
            }

            // Must have at least 1 argument, first one should reference dataVarName
            if (invocation.ArgumentList.Arguments.Count < 1) {
                return false;
            }

            ExpressionSyntax firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (!ReferencesIdentifier(firstArg, dataVarName)) {
                return false;
            }

            // Must be generic invocation (Deserialize<...>)
            // MemberAccess: Something.Deserialize<T>
            if (target is MemberAccessExpressionSyntax ma) {
                return ma.Name is GenericNameSyntax gn &&
                   gn.Identifier.ValueText == "Deserialize" &&
                   gn.TypeArgumentList.Arguments.Count == 1;
            }

            // IdentifierName: Deserialize<T> (static using)
            if (target is GenericNameSyntax gng) {
                return gng.Identifier.ValueText == "Deserialize" &&
                   gng.TypeArgumentList.Arguments.Count == 1;
            }

            return false;
        }

        private static bool ReferencesIdentifier(ExpressionSyntax expr, string ident) => expr.DescendantNodesAndSelf()
                       .OfType<IdentifierNameSyntax>()
                       .Any(id => id.Identifier.ValueText == ident);

        private static bool IsNotNullCheckOf(ExpressionSyntax condition, string varName) {
            // Support:
            // value != null
            // value is not null
            // value is { }  (pattern non-null)
            //
            // Be conservative but useful.

            // value != null
            if (condition is BinaryExpressionSyntax bin &&
                bin.IsKind(SyntaxKind.NotEqualsExpression)) {
                return IsIdentifier(bin.Left, varName) && IsNullLiteral(bin.Right) ||
                       IsIdentifier(bin.Right, varName) && IsNullLiteral(bin.Left);
            }

            // value is not null / value is { }
            if (condition is IsPatternExpressionSyntax isPattern) {
                if (!IsIdentifier(isPattern.Expression, varName)) {
                    return false;
                }

                PatternSyntax p = isPattern.Pattern;

                // "is not null"
                var node = root.FindNode(diagnostic.Location.SourceSpan);
                var forEach = node.FirstAncestorOrSelf<ForEachStatementSyntax>();
                if (forEach is null)
                    return;

                // "is { }"
                if (p is RecursivePatternSyntax rp &&
                rp.PropertyPatternClause is { } pp &&
                pp.Subpatterns.Count == 0) {
                    return true;
                }
            }

            return false;
        }

        private static bool IsIdentifier(ExpressionSyntax expr, string varName) 
            => expr is IdentifierNameSyntax id && id.Identifier.ValueText == varName;

        private static bool IsNullLiteral(ExpressionSyntax expr) 
            => expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression);

        private static bool IsAddOfVariable(StatementSyntax stmt, string varName) {
            // Accept:
            // results.Add(value);
            // { results.Add(value); }
            if (stmt is BlockSyntax b) {
                if (b.Statements.Count != 1) {
                    return false;
                }

                stmt = b.Statements[0];
            }

            if (stmt is not ExpressionStatementSyntax exprStmt) {
                return false;
            }

            if (exprStmt.Expression is not InvocationExpressionSyntax inv) {
                return false;
            }

            if (inv.Expression is not MemberAccessExpressionSyntax ma) {
                return false;
            }

            if (ma.Name.Identifier.ValueText != "Add") {
                return false;
            }

            if (inv.ArgumentList.Arguments.Count != 1) {
                return false;
            }

            return IsIdentifier(inv.ArgumentList.Arguments[0].Expression, varName);
        }

        /// <summary>
        /// Determines whether the specified expression represents a call to
        /// System.Text.Json.JsonSerializer.Deserialize<T>(...) within the given analysis context.
        /// </summary>
        /// <remarks>This method checks for invocations of the generic Deserialize method on
        /// System.Text.Json.JsonSerializer by examining the semantic model. It does not consider other deserialization
        /// methods or libraries.</remarks>
        /// <param name="context">The analysis context used to resolve semantic information for the expression.</param>
        /// <param name="valueInit">The expression to analyze for a System.Text.Json.JsonSerializer.Deserialize<T>(...) invocation.</param>
        /// <returns>true if the expression is a System.Text.Json.JsonSerializer.Deserialize<T>(...) call; otherwise, false.</returns>
        private static bool IsSystemTextJsonDeserialize(SyntaxNodeAnalysisContext context, ExpressionSyntax valueInit) {
            if (valueInit is not InvocationExpressionSyntax inv) {
                return false;
            }

            IMethodSymbol symbol = 
                context.SemanticModel.GetSymbolInfo(inv, 
                context.CancellationToken).Symbol as IMethodSymbol;
            if (symbol is null) {
                return false;
            }

            // We only accept System.Text.Json.JsonSerializer.Deserialize<T>(...)
            if (symbol.Name != Deserialize) {
                return false;
            }

            INamedTypeSymbol containing = symbol.ContainingType;
            return containing is not null
                   && containing.Name == JsonSerializer
                   && containing.ContainingNamespace?.ToDisplayString() == SystemTextJson;
        }
    }
}
