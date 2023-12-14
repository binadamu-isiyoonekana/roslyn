﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.UseCollectionExpression;

using static SyntaxFactory;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed partial class CSharpUseCollectionExpressionForArrayDiagnosticAnalyzer()
    : AbstractCSharpUseCollectionExpressionDiagnosticAnalyzer(
        IDEDiagnosticIds.UseCollectionExpressionForArrayDiagnosticId,
        EnforceOnBuildValues.UseCollectionExpressionForArray)
{
    protected override void InitializeWorker(CodeBlockStartAnalysisContext<SyntaxKind> context, INamedTypeSymbol? expressionType)
    {
        context.RegisterSyntaxNodeAction(context => AnalyzeArrayInitializerExpression(context, expressionType), SyntaxKind.ArrayInitializerExpression);
        context.RegisterSyntaxNodeAction(context => AnalyzeArrayCreationExpression(context, expressionType), SyntaxKind.ArrayCreationExpression);
    }

    private void AnalyzeArrayCreationExpression(SyntaxNodeAnalysisContext context, INamedTypeSymbol? expressionType)
    {
        var semanticModel = context.SemanticModel;
        var syntaxTree = semanticModel.SyntaxTree;
        var arrayCreationExpression = (ArrayCreationExpressionSyntax)context.Node;
        var cancellationToken = context.CancellationToken;

        // Don't analyze arrays with initializers here, they're handled in AnalyzeArrayInitializerExpression instead.
        if (arrayCreationExpression.Initializer != null)
            return;

        // no point in analyzing if the option is off.
        var option = context.GetAnalyzerOptions().PreferCollectionExpression;
        if (!option.Value || ShouldSkipAnalysis(context, option.Notification))
            return;

        // Analyze the statements that follow to see if they can initialize this array.
        var matches = TryGetMatches(semanticModel, arrayCreationExpression, expressionType, cancellationToken);
        if (matches.IsDefault)
            return;

        ReportArrayCreationDiagnostics(context, syntaxTree, option, arrayCreationExpression);
    }

    public static ImmutableArray<CollectionExpressionMatch<StatementSyntax>> TryGetMatches(
        SemanticModel semanticModel,
        ArrayCreationExpressionSyntax expression,
        INamedTypeSymbol? expressionType,
        CancellationToken cancellationToken)
    {
        // we have `new T[...] ...;` defer to analyzer to find the items that follow that may need to
        // be added to the collection expression.
        var matches = UseCollectionExpressionHelpers.TryGetMatches(
            semanticModel,
            expression,
            expressionType,
            static e => e.Type,
            static e => e.Initializer,
            cancellationToken);
        if (matches.IsDefault)
            return default;

        if (!UseCollectionExpressionHelpers.CanReplaceWithCollectionExpression(
                semanticModel, expression, expressionType, skipVerificationForReplacedNode: true, cancellationToken))
        {
            return default;
        }

        return matches;
    }

    public static ImmutableArray<CollectionExpressionMatch<StatementSyntax>> TryGetMatches(
        SemanticModel semanticModel,
        ImplicitArrayCreationExpressionSyntax expression,
        INamedTypeSymbol? expressionType,
        CancellationToken cancellationToken)
    {
        // if we have `new[] { ... }` we have no subsequent matches to add to the collection. All values come
        // from within the initializer.
        if (!UseCollectionExpressionHelpers.CanReplaceWithCollectionExpression(
                semanticModel, expression, expressionType, skipVerificationForReplacedNode: true, cancellationToken))
        {
            return default;
        }

        return ImmutableArray<CollectionExpressionMatch<StatementSyntax>>.Empty;
    }

    private void AnalyzeArrayInitializerExpression(SyntaxNodeAnalysisContext context, INamedTypeSymbol? expressionType)
    {
        var semanticModel = context.SemanticModel;
        var syntaxTree = semanticModel.SyntaxTree;
        var initializer = (InitializerExpressionSyntax)context.Node;
        var cancellationToken = context.CancellationToken;

        // no point in analyzing if the option is off.
        var option = context.GetAnalyzerOptions().PreferCollectionExpression;
        if (!option.Value || ShouldSkipAnalysis(context, option.Notification))
            return;

        var isConcreteOrImplicitArrayCreation = initializer.Parent is ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax;

        // a naked `{ ... }` can only be converted to a collection expression when in the exact form `x = { ... }`
        if (!isConcreteOrImplicitArrayCreation && initializer.Parent is not EqualsValueClauseSyntax)
            return;

        var arrayCreationExpression = isConcreteOrImplicitArrayCreation
            ? (ExpressionSyntax)initializer.GetRequiredParent()
            : initializer;

        // Have to actually examine what would happen when we do the replacement, as the replaced value may interact
        // with inference based on the values within.
        var replacementCollectionExpression = CollectionExpression(
            SeparatedList<CollectionElementSyntax>(initializer.Expressions.Select(ExpressionElement)));

        if (!UseCollectionExpressionHelpers.CanReplaceWithCollectionExpression(
                semanticModel, arrayCreationExpression, replacementCollectionExpression,
                expressionType, skipVerificationForReplacedNode: true, cancellationToken))
        {
            return;
        }

        if (isConcreteOrImplicitArrayCreation)
        {
            var matches = initializer.Parent switch
            {
                ArrayCreationExpressionSyntax arrayCreation => TryGetMatches(semanticModel, arrayCreation, expressionType, cancellationToken),
                ImplicitArrayCreationExpressionSyntax arrayCreation => TryGetMatches(semanticModel, arrayCreation, expressionType, cancellationToken),
                _ => throw ExceptionUtilities.Unreachable(),
            };

            if (matches.IsDefault)
                return;

            ReportArrayCreationDiagnostics(context, syntaxTree, option, arrayCreationExpression);
        }
        else
        {
            Debug.Assert(initializer.Parent is EqualsValueClauseSyntax);
            // int[] = { 1, 2, 3 };
            //
            // In this case, we always have a target type, so it should always be valid to convert this to a collection expression.
            context.ReportDiagnostic(DiagnosticHelper.Create(
                Descriptor,
                initializer.OpenBraceToken.GetLocation(),
                option.Notification,
                additionalLocations: ImmutableArray.Create(initializer.GetLocation()),
                properties: null));
        }
    }

    private void ReportArrayCreationDiagnostics(SyntaxNodeAnalysisContext context, SyntaxTree syntaxTree, CodeStyleOption2<bool> option, ExpressionSyntax expression)
    {
        var location = expression.GetFirstToken().GetLocation();
        var additionalLocations = ImmutableArray.Create(expression.GetLocation());
        var fadingLocations = ImmutableArray.Create(
            syntaxTree.GetLocation(TextSpan.FromBounds(
                expression.SpanStart,
                expression is ArrayCreationExpressionSyntax arrayCreationExpression
                    ? arrayCreationExpression.Type.Span.End
                    : ((ImplicitArrayCreationExpressionSyntax)expression).CloseBracketToken.Span.End)));

        context.ReportDiagnostic(DiagnosticHelper.CreateWithLocationTags(
            Descriptor,
            location,
            option.Notification,
            additionalLocations,
            fadingLocations,
            properties: null));
    }
}
