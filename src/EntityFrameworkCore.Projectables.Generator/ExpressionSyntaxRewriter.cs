﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityFrameworkCore.Projectables.Generator
{

    public class ExpressionSyntaxRewriter : CSharpSyntaxRewriter
    {
        readonly INamedTypeSymbol _targetTypeSymbol;
        readonly SemanticModel _semanticModel;
        readonly NullConditionalRewriteSupport _nullConditionalRewriteSupport;
        readonly SourceProductionContext _context;
        readonly Stack<ExpressionSyntax> _conditionalAccessExpressionsStack = new();
        
        public ExpressionSyntaxRewriter(INamedTypeSymbol targetTypeSymbol, NullConditionalRewriteSupport nullConditionalRewriteSupport, SemanticModel semanticModel, SourceProductionContext context)
        {
            _targetTypeSymbol = targetTypeSymbol;
            _nullConditionalRewriteSupport = nullConditionalRewriteSupport;
            _semanticModel = semanticModel;
            _context = context;
        }

        private SyntaxNode? VisitThisBaseExpression(CSharpSyntaxNode node)
        {
            // Swap out the use of this and base to @this and keep leading and trailing trivias
            return SyntaxFactory.IdentifierName("@this")
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }
        
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var expressionSyntax = (ExpressionSyntax?)Visit(node.Expression) ?? throw new ArgumentNullException("expression");
        
            var syntaxNode = Visit(node.Name);
        
            // Prevents invalid cast when visiting a QualifiedNameSyntax
            if (syntaxNode is QualifiedNameSyntax qst)
            {
                syntaxNode = qst.Right;
            }
            
            return node.Update(expressionSyntax, VisitToken(node.OperatorToken), (SimpleNameSyntax)syntaxNode);
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            // Fully qualify extension method calls
            if (node.Expression is MemberAccessExpressionSyntax memberAccessExpressionSyntax)
            {
                var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
                if (symbol is IMethodSymbol { IsExtensionMethod: true } methodSymbol)
                {
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.ParseName(methodSymbol.ContainingType.ToDisplayString(NullableFlowState.None, SymbolDisplayFormat.FullyQualifiedFormat)),
                            memberAccessExpressionSyntax.Name
                        ),
                        node.ArgumentList.WithArguments(
                            ((ArgumentListSyntax)VisitArgumentList(node.ArgumentList)!).Arguments.Insert(0, SyntaxFactory.Argument(
                                    (ExpressionSyntax)Visit(memberAccessExpressionSyntax.Expression)
                                )
                            )
                        )
                    );
                }
            }

            return base.VisitInvocationExpression(node);
        }

        public override SyntaxNode? VisitInterpolation(InterpolationSyntax node)
        {
            // Visit the expression first
            var targetExpression = (ExpressionSyntax)Visit(node.Expression);
            
            // Check if the expression already has parentheses
            if (targetExpression is ParenthesizedExpressionSyntax)
            {
                return node.WithExpression(targetExpression);
            }
            
            // Create a new expression wrapped in parentheses
            var newExpression = SyntaxFactory.ParenthesizedExpression(targetExpression);
            
            return node.WithExpression(newExpression);
        }

        public override SyntaxNode? VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
        {
            var targetExpression = (ExpressionSyntax)Visit(node.Expression);

            _conditionalAccessExpressionsStack.Push(targetExpression);

            if (_nullConditionalRewriteSupport == NullConditionalRewriteSupport.None)
            {
                var diagnostic = Diagnostic.Create(Diagnostics.NullConditionalRewriteUnsupported, node.GetLocation(), node);
                _context.ReportDiagnostic(diagnostic);

                // Return the original node, do not attempt further rewrites
                return node;
            }

            else if (_nullConditionalRewriteSupport is NullConditionalRewriteSupport.Ignore)
            {
                // Ignore the conditional accesss and simply visit the WhenNotNull expression
                return Visit(node.WhenNotNull);
            }

            else if (_nullConditionalRewriteSupport is NullConditionalRewriteSupport.Rewrite)
            {
                var typeInfo = _semanticModel.GetTypeInfo(node);

                // Do not translate until we can resolve the target type
                if (typeInfo.ConvertedType is not null)
                {
                    // Translate null-conditional into a conditional expression, wrapped inside parenthesis
                    return SyntaxFactory.ParenthesizedExpression(
                        SyntaxFactory.ConditionalExpression(
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.NotEqualsExpression,
                            targetExpression.WithTrailingTrivia(SyntaxFactory.Whitespace(" ")),
                            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression).WithLeadingTrivia(SyntaxFactory.Whitespace(" "))
                       ).WithTrailingTrivia(SyntaxFactory.Whitespace(" ")),
                       SyntaxFactory.ParenthesizedExpression(
                           (ExpressionSyntax)Visit(node.WhenNotNull)
                       ).WithLeadingTrivia(SyntaxFactory.Whitespace(" ")).WithTrailingTrivia(SyntaxFactory.Whitespace(" ")),
                        SyntaxFactory.CastExpression(
                            SyntaxFactory.ParseName(typeInfo.ConvertedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                        ).WithLeadingTrivia(SyntaxFactory.Whitespace(" "))
                    ).WithLeadingTrivia(node.GetLeadingTrivia()).WithTrailingTrivia(node.GetTrailingTrivia()));
                }
            }

            return base.VisitConditionalAccessExpression(node);

        }

        public override SyntaxNode? VisitSwitchExpression(SwitchExpressionSyntax node)
        {
            // Reverse arms order to start from the default value
            var arms = node.Arms.Reverse();

            ExpressionSyntax? currentExpression = null;

            foreach (var arm in arms)
            {
                var armExpression = (ExpressionSyntax)Visit(arm.Expression);
                
                // Handle fallback value
                if (currentExpression == null)
                {
                    currentExpression = arm.Pattern is DiscardPatternSyntax 
                        ? armExpression
                        : SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

                    continue;
                }
                
                // Handle each arm, only if it's a constant expression
                if (arm.Pattern is ConstantPatternSyntax constant)
                {
                    ExpressionSyntax expression = SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, (ExpressionSyntax)Visit(node.GoverningExpression), constant.Expression);
                    
                    // Add the when clause as a AND expression
                    if (arm.WhenClause != null)
                    {
                        expression = SyntaxFactory.BinaryExpression(
                            SyntaxKind.LogicalAndExpression, 
                            expression,
                            (ExpressionSyntax)Visit(arm.WhenClause.Condition)
                        );
                    }
                    
                    currentExpression = SyntaxFactory.ConditionalExpression(
                        expression,
                        armExpression,
                        currentExpression
                    );

                    continue;
                }

                if (arm.Pattern is DeclarationPatternSyntax declaration)
                {
                    var getTypeExpression = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        (ExpressionSyntax)Visit(node.GoverningExpression),
                        SyntaxFactory.IdentifierName("GetType")
                    );

                    var getTypeCall = SyntaxFactory.InvocationExpression(getTypeExpression);
                    var typeofExpression = SyntaxFactory.TypeOfExpression(declaration.Type);
                    var equalsExpression = SyntaxFactory.BinaryExpression(
                        SyntaxKind.EqualsExpression,
                        getTypeCall,
                        typeofExpression
                    );

                    ExpressionSyntax condition = equalsExpression;
                    if (arm.WhenClause != null)
                    {
                        condition = SyntaxFactory.BinaryExpression(
                            SyntaxKind.LogicalAndExpression, 
                            equalsExpression,
                            (ExpressionSyntax)Visit(arm.WhenClause.Condition)
                        );
                    }

                    var modifiedArmExpression = ReplaceVariableWithCast(armExpression, declaration, node.GoverningExpression);
                    currentExpression = SyntaxFactory.ConditionalExpression(
                        condition,
                        modifiedArmExpression,
                        currentExpression
                    );

                    continue;
                }

                throw new InvalidOperationException(
                    $"Switch expressions rewriting supports only constant values and declaration patterns (Type var). " +
                    $"Unsupported pattern: {arm.Pattern.GetType().Name}"
                );
            }
            
            return currentExpression;
        }

        public override SyntaxNode? VisitMemberBindingExpression(MemberBindingExpressionSyntax node)
        {
            if (_conditionalAccessExpressionsStack.Count > 0)
            {
                var targetExpression = _conditionalAccessExpressionsStack.Pop();

                return _nullConditionalRewriteSupport switch {
                    NullConditionalRewriteSupport.Ignore => SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, targetExpression, node.Name),
                    NullConditionalRewriteSupport.Rewrite => SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, targetExpression, node.Name),
                    _ => node
                };
            }

            return base.VisitMemberBindingExpression(node);
        }

        public override SyntaxNode? VisitElementBindingExpression(ElementBindingExpressionSyntax node)
        {
            if (_conditionalAccessExpressionsStack.Count > 0)
            {
                var targetExpression = _conditionalAccessExpressionsStack.Pop();

                return _nullConditionalRewriteSupport switch {
                    NullConditionalRewriteSupport.Ignore => SyntaxFactory.ElementAccessExpression(targetExpression, node.ArgumentList),
                    NullConditionalRewriteSupport.Rewrite => SyntaxFactory.ElementAccessExpression(targetExpression, node.ArgumentList),
                    _ => Visit(node)
                };
            }

            return base.VisitElementBindingExpression(node);
        }

        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
        {
            // Swap out the use of this to @this
            return VisitThisBaseExpression(node);
        }

        public override SyntaxNode? VisitBaseExpression(BaseExpressionSyntax node)
        {
            // Swap out the use of this to @this
            return VisitThisBaseExpression(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is not null)
            {
                var operation = node switch { { Parent: { } parent } when parent.IsKind(SyntaxKind.InvocationExpression) => _semanticModel.GetOperation(node.Parent),
                    _ => _semanticModel.GetOperation(node!)
                };

                if (operation is IMemberReferenceOperation memberReferenceOperation)
                {
                    var memberAccessCanBeQualified = node switch { { Parent: { Parent: { } parent } } when parent.IsKind(SyntaxKind.ObjectInitializerExpression) => false,
                        _ => true
                    };

                    if (memberAccessCanBeQualified)
                    {
                        // if this operation is targeting an instance member on our targetType implicitly
                        if (memberReferenceOperation.Instance is { IsImplicit: true } && SymbolEqualityComparer.Default.Equals(memberReferenceOperation.Instance.Type, _targetTypeSymbol))
                        {
                            return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("@this"),
                                node.WithoutLeadingTrivia()
                            ).WithLeadingTrivia(node.GetLeadingTrivia());
                        }

                        // if this operation is targeting a static member on our targetType implicitly
                        if (memberReferenceOperation.Instance is null && SymbolEqualityComparer.Default.Equals(memberReferenceOperation.Member.ContainingType, _targetTypeSymbol))
                        {
                            return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.ParseTypeName(_targetTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                                node.WithoutLeadingTrivia()
                            ).WithLeadingTrivia(node.GetLeadingTrivia());
                        }
                    }
                }
                else if (operation is IInvocationOperation invocationOperation)
                {
                    // if this operation is targeting an instance method on our targetType implicitly
                    if (invocationOperation.Instance is { IsImplicit: true } && SymbolEqualityComparer.Default.Equals(invocationOperation.Instance.Type, _targetTypeSymbol))
                    {
                        return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("@this"),
                            node.WithoutLeadingTrivia()
                        ).WithLeadingTrivia(node.GetLeadingTrivia());
                    }

                    // if this operation is targeting a static method on our targetType implicitly
                    if (invocationOperation.Instance is null && SymbolEqualityComparer.Default.Equals(invocationOperation.TargetMethod.ContainingType, _targetTypeSymbol))
                    {
                        return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.ParseTypeName(_targetTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                            node.WithoutLeadingTrivia()
                        ).WithLeadingTrivia(node.GetLeadingTrivia());
                    }
                }

                // if this node refers to a named type which is not yet fully qualified, we want to fully qualify it
                if (symbol.Kind is SymbolKind.NamedType && node.Parent?.Kind() is not SyntaxKind.QualifiedName)
                {
                    return SyntaxFactory.ParseTypeName(
                        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    ).WithLeadingTrivia(node.GetLeadingTrivia());
                }
            }

            return base.VisitIdentifierName(node);
        }

        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(node);

            if (symbolInfo.Symbol is not null)
            {
                if (symbolInfo.Symbol.Kind is SymbolKind.NamedType)
                {
                    var typeInfo = _semanticModel.GetTypeInfo(node);

                    if (typeInfo.Type is not null)
                    {
                        return SyntaxFactory.ParseTypeName(
                            typeInfo.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        );
                    }
                }
            }

            return base.VisitQualifiedName(node);
        }

        public override SyntaxNode? VisitNullableType(NullableTypeSyntax node)
        {
            var typeInfo = _semanticModel.GetTypeInfo(node);
            if (typeInfo.Type is not null)
            {
                if (typeInfo.Type.TypeKind is not TypeKind.Struct)
                {
                    return Visit(node.ElementType)
                        .WithLeadingTrivia(node.GetLeadingTrivia())
                        .WithTrailingTrivia(node.GetTrailingTrivia());
                }
            }

            return base.VisitNullableType(node);
        }
        
        private ExpressionSyntax ReplaceVariableWithCast(ExpressionSyntax expression, DeclarationPatternSyntax declaration, ExpressionSyntax governingExpression)
        {
            if (declaration.Designation is SingleVariableDesignationSyntax variableDesignation)
            {
                var variableName = variableDesignation.Identifier.ValueText;
        
                var castExpression = SyntaxFactory.ParenthesizedExpression(
                    SyntaxFactory.CastExpression(
                        declaration.Type,
                        (ExpressionSyntax)Visit(governingExpression)
                    )
                );

                var rewriter = new VariableReplacementRewriter(variableName, castExpression);
                return (ExpressionSyntax)rewriter.Visit(expression);
            }

            return expression;
        }
    }
}
