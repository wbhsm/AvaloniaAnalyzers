using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    using static DependencyPropertyConverter;

    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DependencyPropertyConverterFixer)), Shared]
    public class DependencyPropertyConverterFixer : CodeFixProvider
    {
        private const string title = "Convert WPF DependencyProperty to AvaloniaProperty";

        public sealed override ImmutableArray<string> FixableDiagnosticIds
        {
            get { return ImmutableArray.Create(DependencyPropertyConverterAnalyzer.DiagnosticId); }
        }

        public sealed override FixAllProvider GetFixAllProvider()
        {
            // See https://github.com/dotnet/roslyn/blob/master/docs/analyzers/FixAllProvider.md for more information on Fix All Providers
            return WellKnownFixAllProviders.BatchFixer;
        }

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            // TODO: Replace the following code with your own analysis, generating a CodeAction for each fix to suggest
            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;

            // Find the type declaration identified by the diagnostic.
            var declaration = (VariableDeclaratorSyntax)root.FindNode(diagnosticSpan);

            // Register a code action that will invoke the fix.
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: title,
                    createChangedDocument: c => CreateDocument(context.Document, declaration, c),
                    equivalenceKey: title),
                diagnostic);
        }

        private static async Task<Document> CreateDocument(Document document, VariableDeclaratorSyntax declaration, CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(document);

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            var fieldSymbol = semanticModel.GetDeclaredSymbol(declaration);

            var initializer = (InvocationExpressionSyntax)declaration.Initializer.Value;
            var dependencyPropertyMethodName = ((MemberAccessExpressionSyntax)initializer.Expression).Name.ToString();

            var avaloniaNamespace = editor.Generator.IdentifierName("Avalonia");
            var avaloniaPropertyIdentifier = editor.Generator.MemberAccessExpression(avaloniaNamespace, "AvaloniaProperty");
            var avaloniaPropertyMethodName = dependencyPropertyMethodName.EndsWith("ReadOnly") ?
                dependencyPropertyMethodName.Remove(dependencyPropertyMethodName.Length - "ReadOnly".Length) // Can remove if/when we add support for read-only properties
                : dependencyPropertyMethodName;
            var attachedOwner = (TypeSyntax)editor.Generator.TypeExpression(fieldSymbol.ContainingType);
            var propertyType = (initializer.ArgumentList.Arguments[1].Expression as TypeOfExpressionSyntax)?.Type;
            if (propertyType == null)
            {
                propertyType = (TypeSyntax)editor.Generator.TypeExpression(SpecialType.System_Object);
            }
            var ownerHostType = (initializer.ArgumentList.Arguments[2].Expression as TypeOfExpressionSyntax)?.Type;
            if (ownerHostType == null)
            {
                ownerHostType = attachedOwner;
            }

            var typeArguments = avaloniaPropertyMethodName.Contains("Attached") ?
                new[] { attachedOwner, ownerHostType, propertyType }
                : new[] { ownerHostType, propertyType };
            var methodIdentifier = editor.Generator.GenericName(avaloniaPropertyMethodName, typeArguments);
            var methodExpression = editor.Generator.MemberAccessExpression(avaloniaPropertyIdentifier, methodIdentifier);
            var avaloniaInvocation = (InvocationExpressionSyntax)editor.Generator.InvocationExpression(methodExpression, initializer.ArgumentList.Arguments[0]);

            var originalStaticConstructor = fieldSymbol.ContainingType.StaticConstructors.IsEmpty || fieldSymbol.ContainingType.StaticConstructors[0].DeclaringSyntaxReferences.IsEmpty ? null :
                (ConstructorDeclarationSyntax)await fieldSymbol.ContainingType.StaticConstructors[0].DeclaringSyntaxReferences[0].GetSyntaxAsync(cancellationToken);
            var staticConstructor = originalStaticConstructor == null ? GenerateEmptyStaticConstructor(editor, fieldSymbol.ContainingType) : originalStaticConstructor;

            ExpressionSyntax coerceCallbackSyntax = null;
            ExpressionSyntax validationCallbackSyntax = null;


            if (initializer.ArgumentList.Arguments.Count > 3) // Have to break down metadata constructor
            {
                var metadataInitializer = (ObjectCreationExpressionSyntax)initializer.ArgumentList.Arguments[3].Expression;

                if (metadataInitializer.ArgumentList.Arguments.Any())
                {
                    var currentArgument = 0;
                    var firstArgument = metadataInitializer.ArgumentList.Arguments[currentArgument];
                    var firstArgumentType = semanticModel.GetTypeInfo(firstArgument.Expression, cancellationToken).ConvertedType;
                    if (firstArgumentType.ToDisplayString() == "System.Windows.PropertyChangedCallback") // Null is a delegate
                    {
                        staticConstructor = AddChangedHandlerToStaticConstructor(editor.Generator, staticConstructor, fieldSymbol.ContainingType, declaration, firstArgument);
                        ++currentArgument;
                        if (metadataInitializer.ArgumentList.Arguments.Count > currentArgument)
                        {
                            coerceCallbackSyntax = metadataInitializer.ArgumentList.Arguments[currentArgument].Expression;
                            currentArgument++;
                        }
                    }
                    else
                    {
                        avaloniaInvocation = avaloniaInvocation.AddArgumentListArguments(metadataInitializer.ArgumentList.Arguments[currentArgument]);
                        ++currentArgument;
                        if (metadataInitializer.ArgumentList.Arguments.Count > currentArgument)
                        {
                            var metadataType = semanticModel.GetTypeInfo(metadataInitializer, cancellationToken).Type;
                            var secondArgument = metadataInitializer.ArgumentList.Arguments[currentArgument].Expression;
                            var secondArgumentType = semanticModel.GetTypeInfo(secondArgument, cancellationToken).ConvertedType;
                            if (secondArgumentType.ToString() == "System.Windows.FrameworkPropertyMetadataOptions")
                            {
                                avaloniaInvocation = ProcessFrameworkMetadataOptions(
                                    editor, semanticModel, avaloniaInvocation, secondArgument, secondArgumentType, cancellationToken);
                                ++currentArgument;
                            }
                        }
                        if (metadataInitializer.ArgumentList.Arguments.Count > currentArgument)
                        {
                            var changedCallback = metadataInitializer.ArgumentList.Arguments[currentArgument];
                            var containingType = fieldSymbol.ContainingType;
                            var generator = editor.Generator;
                            staticConstructor = AddChangedHandlerToStaticConstructor(generator, staticConstructor, containingType, declaration, changedCallback);
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
            }
            if (initializer.ArgumentList.Arguments.Count > 4)
            {
                validationCallbackSyntax = initializer.ArgumentList.Arguments[4].Expression;
            }

            if (coerceCallbackSyntax != null || validationCallbackSyntax != null)
            {
                var combinedCoerceValidateExpression = CreateCombinedCoerceValidate(editor.Generator, semanticModel, coerceCallbackSyntax, validationCallbackSyntax);
                avaloniaInvocation = avaloniaInvocation.AddArgumentListArguments((ArgumentSyntax)editor.Generator.Argument("validate", RefKind.None, combinedCoerceValidateExpression));
            }

            ReplaceMember(editor, semanticModel, declaration, avaloniaInvocation, avaloniaPropertyMethodName.Contains("Attached"));
            if (originalStaticConstructor != null && originalStaticConstructor.Body.Statements.Count < staticConstructor.Body.Statements.Count)
            {
                editor.ReplaceNode(originalStaticConstructor, staticConstructor);
            }
            else if (staticConstructor.Body.Statements.Count > 0)
            {
                editor.AddMember(await fieldSymbol.ContainingType.DeclaringSyntaxReferences[0].GetSyntaxAsync(cancellationToken), staticConstructor);
            }

            return editor.GetChangedDocument();
        }
    }
}