using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Editing;
using System;
using System.Threading.Tasks;

namespace AvaloniaAnalyzers
{
    static class DependencyPropertyConverter
    {
        public static void ReplaceMember(DocumentEditor editor, SemanticModel model, VariableDeclaratorSyntax original, InvocationExpressionSyntax avaloniaInvocation)
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

        public static InvocationExpressionSyntax GenerateBasicInvocation(SyntaxGenerator generator, ISymbol fieldSymbol, InvocationExpressionSyntax initializer, string dependencyPropertyMethodName)
        {
            var avaloniaNamespace = generator.IdentifierName("Avalonia");
            var avaloniaPropertyIdentifier = generator.MemberAccessExpression(avaloniaNamespace, "AvaloniaProperty");
            var avaloniaPropertyMethodName = dependencyPropertyMethodName.EndsWith("ReadOnly") ?
                dependencyPropertyMethodName.Remove(dependencyPropertyMethodName.Length - "ReadOnly".Length) // Can remove if/when we add support for read-only properties
                : dependencyPropertyMethodName;
            var attachedOwner = (TypeSyntax)generator.TypeExpression(fieldSymbol.ContainingType);
            var propertyType = (initializer.ArgumentList.Arguments[1].Expression as TypeOfExpressionSyntax)?.Type;
            if (propertyType == null)
            {
                propertyType = (TypeSyntax)generator.TypeExpression(SpecialType.System_Object);
            }
            var ownerHostType = (initializer.ArgumentList.Arguments[2].Expression as TypeOfExpressionSyntax)?.Type;
            if (ownerHostType == null)
            {
                ownerHostType = attachedOwner;
            }

            var typeArguments = avaloniaPropertyMethodName.Contains("Attached") ?
                new[] { attachedOwner, ownerHostType, propertyType }
                : new[] { ownerHostType, propertyType };
            var methodIdentifier = generator.GenericName(avaloniaPropertyMethodName, typeArguments);
            var methodExpression = generator.MemberAccessExpression(avaloniaPropertyIdentifier, methodIdentifier);
            var avaloniaInvocation = (InvocationExpressionSyntax)generator.InvocationExpression(methodExpression, initializer.ArgumentList.Arguments[0]);
            return avaloniaInvocation;
        }

        public static ExpressionSyntax CreateCombinedCoerceValidate(SyntaxGenerator generator, SemanticModel semanticModel, ExpressionSyntax coerceCallbackSyntax, ExpressionSyntax validationCallbackSyntax)
        {
            var objArgument = generator.IdentifierName("obj");
            var valArgument = generator.IdentifierName("val");

            var parameters = new[]
            {
                generator.LambdaParameter("obj"),
                generator.LambdaParameter("val")
            };
            var statements = new List<SyntaxNode>();
            if (coerceCallbackSyntax != null)
            {
                statements.Add(generator.AssignmentStatement(valArgument,
                    generator.InvocationExpression(coerceCallbackSyntax, objArgument, valArgument))
               );
            }

            if (validationCallbackSyntax != null)
            {
                statements.Add(generator.IfStatement(generator.LogicalNotExpression(generator.InvocationExpression(validationCallbackSyntax, valArgument)),
                    new[]
                    {
                        generator.ThrowStatement(generator.ObjectCreationExpression(semanticModel.GetSpeculativeTypeInfo(0, generator.MemberAccessExpression(generator.IdentifierName("System"), "ArgumentException"), SpeculativeBindingOption.BindAsExpression).Type))
                    }));
            }
            statements.Add(generator.ReturnStatement(valArgument));
            return (ExpressionSyntax)generator.ValueReturningLambdaExpression(parameters, statements.ToArray());
        }

        public static StatementSyntax BuildChangedHandler(SyntaxGenerator generator, INamedTypeSymbol containingType, VariableDeclaratorSyntax declaration, ArgumentSyntax changedCallback)
        {
            var addClassHandlerName = generator.GenericName("AddClassHandler", containingType);
            var propertyIdentifier = generator.IdentifierName(declaration.Identifier.ToString());
            var memberAccess = generator.MemberAccessExpression(propertyIdentifier, "Changed");
            var addClassHandlerMethod = generator.MemberAccessExpression(memberAccess, addClassHandlerName);
            var classHandlerInvocation = generator.InvocationExpression(addClassHandlerMethod, changedCallback.Expression);
            return (StatementSyntax)generator.ExpressionStatement(classHandlerInvocation);
        }

        public static ConstructorDeclarationSyntax GenerateEmptyStaticConstructor(SyntaxGenerator generator, INamedTypeSymbol containingType)
        {
            return (ConstructorDeclarationSyntax)generator.ConstructorDeclaration(containingType.Name, modifiers: DeclarationModifiers.Static);
        }

        public static async Task<Tuple<ConverterProcessingResult, ExpressionSyntax>> ProcessMetadata(SyntaxGenerator generator, SemanticModel semanticModel, ISymbol fieldSymbol, CancellationToken cancellationToken)
        {
            var declarator = (VariableDeclaratorSyntax) await fieldSymbol.DeclaringSyntaxReferences[0].GetSyntaxAsync(cancellationToken);
            ExpressionSyntax coerceCallbackSyntax = null;
            var initializer = (InvocationExpressionSyntax)declarator.Initializer.Value;
            var metadataInitializer = (ObjectCreationExpressionSyntax)initializer.ArgumentList.Arguments[3].Expression;
            var metadataChanges = new ConverterProcessingResult();
            if (metadataInitializer.ArgumentList.Arguments.Any())
            {
                var currentArgument = 0;
                var firstArgument = metadataInitializer.ArgumentList.Arguments[currentArgument];
                var firstArgumentType = semanticModel.GetTypeInfo(firstArgument.Expression, cancellationToken).ConvertedType;
                if (firstArgumentType.ToDisplayString() == "System.Windows.PropertyChangedCallback") // Null is a delegate
                {
                    metadataChanges = metadataChanges.AddStaticConstructorStatements(
                        BuildChangedHandler(generator, fieldSymbol.ContainingType, declarator, firstArgument));
                    ++currentArgument;
                    if (metadataInitializer.ArgumentList.Arguments.Count > currentArgument)
                    {
                        coerceCallbackSyntax = metadataInitializer.ArgumentList.Arguments[currentArgument].Expression;
                        currentArgument++;
                    }
                }
                else
                {
                    metadataChanges = metadataChanges.AddArguments(metadataInitializer.ArgumentList.Arguments[currentArgument]);
                    ++currentArgument;
                    if (metadataInitializer.ArgumentList.Arguments.Count > currentArgument)
                    {
                        var metadataType = semanticModel.GetTypeInfo(metadataInitializer, cancellationToken).Type;
                        var secondArgument = metadataInitializer.ArgumentList.Arguments[currentArgument].Expression;
                        var secondArgumentType = semanticModel.GetTypeInfo(secondArgument, cancellationToken).ConvertedType;
                        if (secondArgumentType.ToString() == "System.Windows.FrameworkPropertyMetadataOptions")
                        {
                            metadataChanges = metadataChanges.AppendResult(ProcessFrameworkMetadataOptions(
                                generator, semanticModel, secondArgument, secondArgumentType, fieldSymbol.Name, cancellationToken));
                            ++currentArgument;
                        }
                    }
                    if (metadataInitializer.ArgumentList.Arguments.Count > currentArgument)
                    {
                        var changedCallback = metadataInitializer.ArgumentList.Arguments[currentArgument];
                        var containingType = fieldSymbol.ContainingType;
                        metadataChanges = metadataChanges.AddStaticConstructorStatements(
                            BuildChangedHandler(generator, containingType, declarator, changedCallback));
                        ++currentArgument;
                    }
                    if (metadataInitializer.ArgumentList.Arguments.Count > currentArgument)
                    {
                        coerceCallbackSyntax = metadataInitializer.ArgumentList.Arguments[currentArgument].Expression;
                        currentArgument++;
                    }
                    // We do not have support for the remaining metadata items (disable animation, update source trigger) so we stop processing here.
                }
            }
            return Tuple.Create(metadataChanges, coerceCallbackSyntax);
        }


        public static ConverterProcessingResult ProcessFrameworkMetadataOptions(
            SyntaxGenerator generator, SemanticModel semanticModel, 
            ExpressionSyntax metadataOptions, ITypeSymbol metadataOptionsType, string propertyName, CancellationToken cancellationToken)
        {
            var inheritsMember = (IFieldSymbol)metadataOptionsType.GetMembers("Inherits")[0];
            var bindsTwoWayByDefault = (IFieldSymbol)metadataOptionsType.GetMembers("BindsTwoWayByDefault")[0];
            var affectsMeasure = (IFieldSymbol)metadataOptionsType.GetMembers("AffectsMeasure")[0];
            var affectsArrange = (IFieldSymbol)metadataOptionsType.GetMembers("AffectsArrange")[0];
            var affectsRender = (IFieldSymbol)metadataOptionsType.GetMembers("AffectsRender")[0];

            var optionsValue = semanticModel.GetConstantValue(metadataOptions, cancellationToken);
            var additionalArguments = new List<ArgumentSyntax>();
            var additionalStatements = new List<StatementSyntax>();
            if (OptionalHasEnumFlag(optionsValue, inheritsMember))
            {
                additionalArguments.Add((ArgumentSyntax)generator.Argument("inherits", RefKind.None, generator.TrueLiteralExpression()));
            }
            if (OptionalHasEnumFlag(optionsValue, bindsTwoWayByDefault))
            {
                var bindingMode = generator.MemberAccessExpression(
                    generator.MemberAccessExpression(
                        generator.MemberAccessExpression(generator.IdentifierName("Avalonia")
                            , "Data")
                        , "BindingMode")
                    , "TwoWay");
                additionalArguments.Add((ArgumentSyntax)generator.Argument("defaultBindingMode", RefKind.None, bindingMode));
            }
            if(OptionalHasEnumFlag(optionsValue, affectsArrange))
            {
                additionalStatements.Add((StatementSyntax)generator.ExpressionStatement(
                    generator.InvocationExpression(
                        generator.IdentifierName("AffectsArrange"), generator.IdentifierName(propertyName))));
            }
            if (OptionalHasEnumFlag(optionsValue, affectsMeasure))
            {
                additionalStatements.Add((StatementSyntax)generator.ExpressionStatement(
                    generator.InvocationExpression(
                        generator.IdentifierName("AffectsMeasure"), generator.IdentifierName(propertyName))));
            }
            if (OptionalHasEnumFlag(optionsValue, affectsRender))
            {
                additionalStatements.Add((StatementSyntax)generator.ExpressionStatement(
                    generator.InvocationExpression(
                        generator.IdentifierName("AffectsRender"), generator.IdentifierName(propertyName))));
            }

            return new ConverterProcessingResult(additionalArguments, additionalStatements);
        }

        private static bool OptionalHasEnumFlag(Optional<object> optional, IFieldSymbol flagField)
        {
            return optional.HasValue && (((int)optional.Value & ((int)flagField.ConstantValue)) != 0);
        }


    }

    class ConverterProcessingResult
    {
        public ConverterProcessingResult()
        {
        }

        public ConverterProcessingResult(IEnumerable<ArgumentSyntax> invocationArguments, IEnumerable<StatementSyntax> staticConstructorStatements)
        {
            AdditionalInvocationArguments = invocationArguments;
            AdditionalStaticConstructorStatements = staticConstructorStatements;
        }

        public IEnumerable<ArgumentSyntax> AdditionalInvocationArguments { get; } = new List<ArgumentSyntax>();

        public IEnumerable<StatementSyntax> AdditionalStaticConstructorStatements { get; } = new List<StatementSyntax>();

        public ConverterProcessingResult AddArguments(params ArgumentSyntax[] arguments)
        {
            return new ConverterProcessingResult(AdditionalInvocationArguments.Concat(arguments).ToList(), AdditionalStaticConstructorStatements);
        }

        public ConverterProcessingResult AddStaticConstructorStatements(params StatementSyntax[] statements)
        {
            return new ConverterProcessingResult(AdditionalInvocationArguments, AdditionalStaticConstructorStatements.Concat(statements).ToList());
        }

        public ConverterProcessingResult AppendResult(ConverterProcessingResult otherResult)
        {
            return new ConverterProcessingResult(AdditionalInvocationArguments.Concat(otherResult.AdditionalInvocationArguments).ToList(),
                AdditionalStaticConstructorStatements.Concat(otherResult.AdditionalStaticConstructorStatements).ToList());
        }
    }
}
