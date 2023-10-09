using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace StringBuilderMisuseAnalyzer;

[ExportCodeFixProvider("C#")]
public class StringBuilderMisuseCodeFixer : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(StringBuilderMisuseAnalyzer.DiagnosticDescriptor.Id);

    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    class Rewriter(ISet<SyntaxNode> nodesToDelete, string result, IEnumerable<SyntaxNode> nodesToOverwrite) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node) =>
            nodesToDelete.Contains(node) ? null : base.VisitLocalDeclarationStatement(node);

        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node) =>
            nodesToDelete.Contains(node) ? null : base.VisitExpressionStatement(node);

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (!nodesToOverwrite.Contains(node))
                return base.VisitInvocationExpression(node);

            var parentWithNeededTrivia = node.Ancestors().FirstOrDefault(n => n is LocalDeclarationStatementSyntax);
            var leadingTrivia = parentWithNeededTrivia.GetLeadingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
            var leadingTriviaString = leadingTrivia.ToString() + "    ";
            return ParseExpression($$$""""
                {{{(result.Contains("{{") ? "$$" : null)}}}"""
                {{{string.Join("\r\n", result.Split(new string[] { "\r\n" }, StringSplitOptions.None)
                    .Select(r => $"{leadingTriviaString}{r}"))}}}
                {{{leadingTriviaString}}}"""
                """").WithAdditionalAnnotations(Formatter.Annotation);
        }
    }

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        if (await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false) is not { } root
            || await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false) is not { } semanticModel)
        {
            return;
        }

        // find the block
        if (root.FindNode(context.Span) is not VariableDeclaratorSyntax declarator
            || semanticModel.GetDeclaredSymbol(declarator) is not ILocalSymbol localSymbol
            || declarator.Parent is not VariableDeclarationSyntax vds
            || vds.Parent is not LocalDeclarationStatementSyntax lds
            || lds.Parent?.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() is not { } mds
            || mds.Body is null)
        {
            return;
        }

        // code fixer
        foreach (var diagnostic in context.Diagnostics)
            context.RegisterCodeFix(CodeAction.Create("Replace StringBuilder misuse with string interpolation", async ct =>
            {
                // remove the declaration
                HashSet<SyntaxNode> nodesToRemove = [lds];
                HashSet<SyntaxNode> nodesToOverwrite = [];
                var sb = new StringBuilder();

                // iterate over the appends and ToString
                foreach (var ins in mds.Body.DescendantNodes().OfType<IdentifierNameSyntax>())
                    if (semanticModel.GetSymbolInfo(ins) is { } variableSymbolInfo
                        && variableSymbolInfo.Symbol is ILocalSymbol variableSymbol
                        && SymbolEqualityComparer.Default.Equals(localSymbol, variableSymbol))
                    {
                        if (ins.Parent is MemberAccessExpressionSyntax memberAccessExpression
                            && memberAccessExpression.Name.ToString() is { } maeName
                            && maeName is "Append" or "AppendLine" or "AppendFormat"
                            && memberAccessExpression.Parent is InvocationExpressionSyntax invocationExpression
                            && ins.Ancestors().OfType<ExpressionStatementSyntax>().FirstOrDefault() is { } expressionStatement)
                        {
                            // an append, track the text and queue to remove
                            nodesToRemove.Add(expressionStatement);

                            if (maeName is "AppendLine" && invocationExpression.ArgumentList.Arguments.Count == 0)
                                sb.AppendLine();
                            else if (maeName is "AppendFormat" && invocationExpression.ArgumentList.Arguments.Count > 0)
                            {
                                if (invocationExpression.ArgumentList.Arguments[0] is ArgumentSyntax argument
                                    && argument.Expression is LiteralExpressionSyntax literalExpression
                                    && literalExpression.IsKind(SyntaxKind.StringLiteralExpression))
                                {
                                    var format = literalExpression.Token.ValueText;
                                    sb.Append(Regex.Replace(format, @"(?<!\\){(\d+)}", m =>
                                    {
                                        var idx = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                                        return invocationExpression.ArgumentList.Arguments.Count <= idx ? null
                                            : "{{" + invocationExpression.ArgumentList.Arguments[idx + 1] + "}}";
                                    }));
                                }
                            }
                            else if (maeName is not "AppendFormat" && invocationExpression.ArgumentList.Arguments.Count > 0)
                            {
                                if (invocationExpression.ArgumentList.Arguments[0] is ArgumentSyntax argument
                                    && argument.Expression is LiteralExpressionSyntax literalExpression
                                    && literalExpression.IsKind(SyntaxKind.StringLiteralExpression))
                                {
                                    sb.Append(literalExpression.Token.ValueText);
                                }
                                else
                                    sb.Append(invocationExpression.ArgumentList.Arguments[0]);
                                if (maeName is "AppendLine")
                                    sb.AppendLine();
                            }
                        }
                        else if (ins.Parent is MemberAccessExpressionSyntax memberAccessExpression2
                            && memberAccessExpression2.Name.ToString() is "ToString"
                            && memberAccessExpression2.Parent is InvocationExpressionSyntax invocationExpression2)
                        {
                            // ToString
                            nodesToOverwrite.Add(invocationExpression2);
                        }
                    }

                var newMds = new Rewriter(nodesToRemove, sb.ToString(), nodesToOverwrite).Visit(mds);
                return context.Document.WithSyntaxRoot(root.ReplaceNode(mds, newMds));
            }, StringBuilderMisuseAnalyzer.DiagnosticDescriptor.Id), diagnostic);
    }
}
