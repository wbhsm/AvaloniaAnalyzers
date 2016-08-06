using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Editing;


namespace AvaloniaAnalyzers
{
    static class DependencyPropertyConverter
    {
        public static void ReplaceMember(DocumentEditor editor, SemanticModel model, VariableDeclaratorSyntax original, InvocationExpressionSyntax avaloniaInvocation, bool isAttached)
        {
            var parent = (VariableDeclarationSyntax)original.Parent;
            if (parent.Variables.Count > 1)
            {
                editor.ReplaceNode(original.Initializer.Value, avaloniaInvocation);
            }
            else
            {
                var replacedParent = parent
                    .WithType((TypeSyntax)editor.Generator.TypeExpression(model.GetSpeculativeTypeInfo(original.SpanStart, avaloniaInvocation, SpeculativeBindingOption.BindAsExpression).Type))
                    .WithVariables(parent.Variables.Replace(parent.Variables[0], original.WithInitializer(original.Initializer.WithValue(avaloniaInvocation))));
                var fieldDeclaration = parent.Parent as FieldDeclarationSyntax;
                editor.ReplaceNode(fieldDeclaration, fieldDeclaration.WithDeclaration(replacedParent));
            }
        }

        public static ExpressionSyntax CreateCombinedCoerceValidate(SyntaxGenerator generator, SemanticModel semanticModel, ExpressionSyntax coerceCallbackSyntax, ExpressionSyntax validationCallbackSyntax)
        {
            var parameters = new[]
            {
                generator.LambdaParameter("o"),
                generator.LambdaParameter("v")
            };
            var statements = new List<SyntaxNode>();
            if (coerceCallbackSyntax != null)
            {
                statements.Add(generator.AssignmentStatement(generator.IdentifierName("v"),
                    generator.InvocationExpression(coerceCallbackSyntax, generator.IdentifierName("o"), generator.IdentifierName("v")))
               );
            }

            if (validationCallbackSyntax != null)
            {
                statements.Add(generator.IfStatement(generator.LogicalNotExpression(generator.InvocationExpression(validationCallbackSyntax, generator.IdentifierName("v"))),
                    new[]
                    {
                        generator.ThrowStatement(generator.ObjectCreationExpression(semanticModel.GetSpeculativeTypeInfo(0, generator.MemberAccessExpression(generator.IdentifierName("System"), "ArgumentException"), SpeculativeBindingOption.BindAsExpression).Type))
                    }));
            }
            statements.Add(generator.ReturnStatement(generator.IdentifierName("v")));
            return (ExpressionSyntax)generator.ValueReturningLambdaExpression(parameters, statements.ToArray());
        }

        public static ConstructorDeclarationSyntax AddChangedHandlerToStaticConstructor(SyntaxGenerator generator, ConstructorDeclarationSyntax staticConstructor, INamedTypeSymbol containingType, VariableDeclaratorSyntax declaration, ArgumentSyntax changedCallback)
        {
            var addClassHandlerName = generator.GenericName("AddClassHandler", containingType);
            var propertyIdentifier = generator.IdentifierName(declaration.Identifier.ToString());
            var memberAccess = generator.MemberAccessExpression(propertyIdentifier, "Changed");
            var addClassHandlerMethod = generator.MemberAccessExpression(memberAccess, addClassHandlerName);
            var classHandlerInvocation = generator.InvocationExpression(addClassHandlerMethod, changedCallback.Expression);
            staticConstructor = staticConstructor.AddBodyStatements((StatementSyntax)generator.ExpressionStatement(classHandlerInvocation));
            return staticConstructor;
        }

        public static ConstructorDeclarationSyntax GenerateEmptyStaticConstructor(DocumentEditor editor, INamedTypeSymbol containingType)
        {
            return (ConstructorDeclarationSyntax)editor.Generator.ConstructorDeclaration(containingType.Name, modifiers: DeclarationModifiers.Static);
        }

        public static InvocationExpressionSyntax ProcessFrameworkMetadataOptions(
            DocumentEditor editor, SemanticModel semanticModel, InvocationExpressionSyntax avaloniaInvocation,
            ExpressionSyntax metadataOptions, ITypeSymbol metadataOptionsType, CancellationToken cancellationToken)
        {
            var inheritsMember = (IFieldSymbol)metadataOptionsType.GetMembers("Inherits")[0];
            var bindsTwoWayByDefault = (IFieldSymbol)metadataOptionsType.GetMembers("BindsTwoWayByDefault")[0];
            var optionsValue = semanticModel.GetConstantValue(metadataOptions, cancellationToken);
            if (OptionalHasEnumFlag(optionsValue, inheritsMember))
            {
                avaloniaInvocation =
                    avaloniaInvocation.AddArgumentListArguments(
                        (ArgumentSyntax)editor.Generator.Argument("inherits", RefKind.None, editor.Generator.TrueLiteralExpression()));
            }
            if (OptionalHasEnumFlag(optionsValue, bindsTwoWayByDefault))
            {
                var bindingMode = editor.Generator.MemberAccessExpression(
                    editor.Generator.MemberAccessExpression(
                        editor.Generator.MemberAccessExpression(editor.Generator.IdentifierName("Avalonia")
                            , "Data")
                        , "BindingMode")
                    , "TwoWay");
                avaloniaInvocation = avaloniaInvocation.AddArgumentListArguments(
                    (ArgumentSyntax)editor.Generator.Argument("defaultBindingMode", RefKind.None, bindingMode));
            }

            return avaloniaInvocation;
        }

        public static bool OptionalHasEnumFlag(Optional<object> optional, IFieldSymbol flagField)
        {
            return optional.HasValue && (((int)optional.Value & ((int)flagField.ConstantValue)) != 0);
        }
    }
}
