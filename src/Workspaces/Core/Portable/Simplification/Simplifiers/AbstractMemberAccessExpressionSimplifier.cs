﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;

namespace Microsoft.CodeAnalysis.Simplification.Simplifiers
{
    internal abstract class AbstractMemberAccessExpressionSimplifier<
        TExpressionSyntax,
        TMemberAccessExpressionSyntax,
        TThisExpressionSyntax>
        where TExpressionSyntax : SyntaxNode
        where TMemberAccessExpressionSyntax : TExpressionSyntax
        where TThisExpressionSyntax : TExpressionSyntax
    {
        protected abstract ISyntaxFacts SyntaxFacts { get; }
        protected abstract ISpeculationAnalyzer GetSpeculationAnalyzer(
            SemanticModel semanticModel, TMemberAccessExpressionSyntax memberAccessExpression, CancellationToken cancellationToken);

        public bool ShouldSimplifyThisMemberAccessExpression(
            TMemberAccessExpressionSyntax? memberAccessExpression,
            SemanticModel semanticModel,
            SimplifierOptions simplifierOptions,
            out ReportDiagnostic severity,
            CancellationToken cancellationToken)
        {
            severity = default;
            if (memberAccessExpression is null)
                return false;

            var syntaxFacts = this.SyntaxFacts;
            var thisExpression = syntaxFacts.GetExpressionOfMemberAccessExpression(memberAccessExpression);
            if (!syntaxFacts.IsThisExpression(thisExpression))
                return false;

            var symbolInfo = semanticModel.GetSymbolInfo(memberAccessExpression, cancellationToken);
            if (symbolInfo.Symbol == null)
                return false;

            var optionValue = simplifierOptions.QualifyMemberAccess(symbolInfo.Symbol.Kind);
            if (optionValue == null)
                return false;

            var speculationAnalyzer = GetSpeculationAnalyzer(semanticModel, memberAccessExpression, cancellationToken);
            var newSymbolInfo = speculationAnalyzer.SpeculativeSemanticModel.GetSymbolInfo(speculationAnalyzer.ReplacedExpression, cancellationToken);
            if (!symbolInfo.Symbol.Equals(newSymbolInfo.Symbol, SymbolEqualityComparer.IncludeNullability))
                return false;

            severity = optionValue.Notification.Severity;
            return !semanticModel.SyntaxTree.OverlapsHiddenPosition(memberAccessExpression.Span, cancellationToken);
        }
    }
}
