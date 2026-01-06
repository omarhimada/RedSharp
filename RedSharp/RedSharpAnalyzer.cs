using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RedSharp
{
    /// <summary>
    /// Analyzes C# code to identify nested foreach loops that may indicate potential performance issues due to O(n*m)
    /// complexity.
    /// </summary>
    /// <remarks>This analyzer reports an informational diagnostic when a foreach statement directly contains
    /// another foreach statement within its body. The diagnostic is intended as a heuristic to help developers identify
    /// code patterns that could lead to inefficient iteration, such as nested enumeration over large collections. Not
    /// all nested foreach loops are problematic; the analyzer is designed to prompt review and consideration of
    /// alternative approaches, such as using sets, dictionaries, or LINQ methods like SelectMany where
    /// appropriate.</remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RedSharpAnalyzer : DiagnosticAnalyzer {
        public const string DiagnosticId = @"Too Many Loops";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Nested foreach loop detected",
            messageFormat: "Nested foreach loops can indicate O(n*m) behavior; consider restructuring or using a set/dictionary / SelectMany when appropriate.",
            category: "Performance",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "Warns when a foreach contains another foreach. This is a heuristic; not always a problem."
        );

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeForeach, SyntaxKind.ForEachStatement);
        }

        /// <summary>
        /// Analyzes a foreach statement to identify nested foreach loops and reports a diagnostic if a nested foreach
        /// is found.
        /// </summary>
        /// <remarks>This method is intended to be used as part of a Roslyn analyzer to detect nested
        /// foreach statements, which may indicate code that could be refactored for clarity or performance. The
        /// diagnostic is reported on the inner foreach statement to assist with code fixes.</remarks>
        /// <param name="context">The analysis context containing the syntax node to analyze and semantic information for the current
        /// analysis.</param>
        private static void AnalyzeForeach(SyntaxNodeAnalysisContext context) {
            var outer = (ForEachStatementSyntax)context.Node;

            // Only analyze blocks (keeps the heuristic simple)
            if (!(outer.Statement is BlockSyntax outerBlock))
                return;

            // Find any nested foreach inside the outer foreach body
            ForEachStatementSyntax nested = outerBlock.DescendantNodes().OfType<ForEachStatementSyntax>().FirstOrDefault();
            if (nested is null)
                return;

            // Basic sanity: both expressions should be "foreachable" (IEnumerable / pattern-based foreach)
            // If Roslyn can get a type, check it's not null. This doesn't prove complexity—just avoids nonsense.
            ITypeSymbol outerType = context.SemanticModel.GetTypeInfo(outer.Expression).Type;
            ITypeSymbol innerType = context.SemanticModel.GetTypeInfo(nested.Expression).Type;

            if (outerType is null || innerType is null)
                return;

            // Report on the inner foreach (so codefix can target it)
            Diagnostic diagnostic = Diagnostic.Create(Rule, nested.ForEachKeyword.GetLocation());
            context.ReportDiagnostic(diagnostic);
        }
    }
}
