﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MoveDeclarationNearReference;

namespace Microsoft.CodeAnalysis.CSharp.MoveDeclarationNearReference
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = PredefinedCodeRefactoringProviderNames.MoveDeclarationNearReference), Shared]
    [ExtensionOrder(After = PredefinedCodeRefactoringProviderNames.InlineTemporary)]
    internal partial class CSharpMoveDeclarationNearReferenceCodeRefactoringProvider :
        AbstractMoveDeclarationNearReferenceCodeRefactoringProvider<
            CSharpMoveDeclarationNearReferenceCodeRefactoringProvider,
            StatementSyntax,
            LocalDeclarationStatementSyntax,
            VariableDeclaratorSyntax>
    {
        protected override bool IsMeaningfulBlock(SyntaxNode node)
        {
            return node is AnonymousFunctionExpressionSyntax ||
                   node is LocalFunctionStatementSyntax ||
                   node is CommonForEachStatementSyntax ||
                   node is ForStatementSyntax ||
                   node is WhileStatementSyntax ||
                   node is DoStatementSyntax ||
                   node is CheckedStatementSyntax;
        }

        protected override SyntaxNode GetVariableDeclaratorSymbolNode(VariableDeclaratorSyntax variableDeclarator)
            => variableDeclarator;

        protected override bool IsValidVariableDeclarator(VariableDeclaratorSyntax variableDeclarator)
            => true;

        protected override SyntaxToken GetIdentifierOfVariableDeclarator(VariableDeclaratorSyntax variableDeclarator)
            => variableDeclarator.Identifier;

        protected override LocalDeclarationStatementSyntax CreateMergedDeclarationStatement(
            LocalDeclarationStatementSyntax localDeclaration, StatementSyntax statementSyntax)
        {
            var assignExpression = (AssignmentExpressionSyntax)((ExpressionStatementSyntax)statementSyntax).Expression;
            var declaration = localDeclaration.Declaration;
            var declarator = declaration.Variables[0];

            var newLocalDeclaration = localDeclaration.ReplaceNode(
                declarator, 
                declarator.WithInitializer(
                    SyntaxFactory.EqualsValueClause(
                        assignExpression.OperatorToken,
                        assignExpression.Right)));

            var totalLeadingTrivia = GetLeadingTrivia(localDeclaration, statementSyntax);
            return newLocalDeclaration.WithLeadingTrivia(totalLeadingTrivia);
        }

        private SyntaxTriviaList GetLeadingTrivia(StatementSyntax statement1, StatementSyntax statement2)
        {
            var result = new List<SyntaxTrivia>();

            result.AddRange(statement2.GetLeadingTrivia().TakeWhile(t => t.IsWhitespaceOrEndOfLine()));
            result.AddRange(statement1.GetLeadingTrivia().SkipWhile(t => t.IsWhitespaceOrEndOfLine()));
            result.AddRange(statement2.GetLeadingTrivia().SkipWhile(t => t.IsWhitespaceOrEndOfLine()));

            return new SyntaxTriviaList(result);

            //var index = list.Count - 1;
            //while (index >= 0 && list[index].IsWhitespace())
            //{
            //    index--;
            //}

            //return new SyntaxTriviaList(list.Take(index + 1));
        }

        protected override async Task<bool> TypesAreCompatibleAsync(
            Document document, ILocalSymbol localSymbol,
            LocalDeclarationStatementSyntax declarationStatement,
            SyntaxNode right, CancellationToken cancellationToken)
        {
            var type = declarationStatement.Declaration.Type;
            if (type.IsVar)
            {
                // Type inference.  Only merge if types match.
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var rightType = semanticModel.GetTypeInfo(right, cancellationToken);
                return Equals(localSymbol.Type, rightType.Type);
            }

            return true;
        }
    }
}
