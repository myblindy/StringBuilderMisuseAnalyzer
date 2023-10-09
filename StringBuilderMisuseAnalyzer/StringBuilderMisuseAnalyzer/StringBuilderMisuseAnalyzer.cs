using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace StringBuilderMisuseAnalyzer;

[DiagnosticAnalyzer("C#")]
public class StringBuilderMisuseAnalyzer : DiagnosticAnalyzer
{
    public static DiagnosticDescriptor DiagnosticDescriptor = new(
        "MBSBM01", "StringBuilder Misuse", "StringBuilder misused, use interpolated strings instead",
        "Performance", DiagnosticSeverity.Warning, true);
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(DiagnosticDescriptor);

    enum ParserState { Created, Appended, ToString }

    static readonly ImmutableHashSet<string> appendFunctions = ImmutableHashSet.Create("Append", "AppendLine", "AppendFormat");

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterCodeBlockAction(context =>
        {
            if (context.CodeBlock is BaseMethodDeclarationSyntax mds && mds.Body is not null)
            {
                Dictionary<ILocalSymbol, ParserState> trackedVariables = new(SymbolEqualityComparer.Default);
                foreach (var lds in mds.Body.Statements.OfType<LocalDeclarationStatementSyntax>())
                    foreach (var v in lds.Declaration.Variables)
                        if (context.SemanticModel.GetDeclaredSymbol(v) is ILocalSymbol variableSymbol
                            && variableSymbol.Type.ToString() is "System.Text.StringBuilder")
                        {
                            trackedVariables.Add(variableSymbol, ParserState.Created);
                        }

                // check for variable reads
                foreach (var ins in mds.Body.DescendantNodes().OfType<IdentifierNameSyntax>())
                    if (context.SemanticModel.GetSymbolInfo(ins) is { } variableSymbolInfo
                        && variableSymbolInfo.Symbol is ILocalSymbol variableSymbol
                        && trackedVariables.TryGetValue(variableSymbol, out var state))
                    {
                        if (ins.Parent is MemberAccessExpressionSyntax memberAccessExpression
                            && memberAccessExpression.Name.ToString() is { } maeName)
                            if (appendFunctions.Contains(maeName))
                            {
                                if (state is ParserState.Created)
                                    trackedVariables[variableSymbol] = ParserState.Appended;
                                else if (state is ParserState.ToString)
                                    trackedVariables.Remove(variableSymbol);
                            }
                            else if (maeName is "ToString")
                                trackedVariables[variableSymbol] = ParserState.ToString;
                            else
                                trackedVariables.Remove(variableSymbol);
                    }

                // emit diagnostics for each fully tracked variable
                foreach (var kvp in trackedVariables)
                    if (kvp.Value is ParserState.ToString)
                        context.ReportDiagnostic(Diagnostic.Create(SupportedDiagnostics[0], kvp.Key.Locations[0]));
            }
        });
    }
}
