using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
            return new DependencyPropertyFixAllProvider();
        }

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            // TODO: Replace the following code with your own analysis, generating a CodeAction for each fix to suggest
            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;
            
            var declaration = (VariableDeclaratorSyntax)root.FindNode(diagnosticSpan);

            // Register a code action that will invoke the fix.
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: title,
                    createChangedDocument: c => ConvertProperty(context.Document, declaration, c),
                    equivalenceKey: title),
                diagnostic);
        }

        internal static async Task<Document> ConvertProperty(Document document, VariableDeclaratorSyntax declarator, CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken);

            var semanticModel = editor.SemanticModel;
            var fieldSymbol = semanticModel.GetDeclaredSymbol(declarator);

            var initializer = (InvocationExpressionSyntax)declarator.Initializer.Value;
            var originalRegisterMethodName = ((MemberAccessExpressionSyntax)initializer.Expression).Name.ToString();

            var avaloniaInvocation = GenerateBasicInvocation(editor.Generator, fieldSymbol, initializer, originalRegisterMethodName);

            var originalStaticConstructor = fieldSymbol.ContainingType.StaticConstructors.IsEmpty || fieldSymbol.ContainingType.StaticConstructors[0].DeclaringSyntaxReferences.IsEmpty ? null :
                (ConstructorDeclarationSyntax)await fieldSymbol.ContainingType.StaticConstructors[0].DeclaringSyntaxReferences[0].GetSyntaxAsync(cancellationToken);
            var staticConstructor = originalStaticConstructor == null ? GenerateEmptyStaticConstructor(editor.Generator, fieldSymbol.ContainingType) : originalStaticConstructor;

            ExpressionSyntax coerceCallbackSyntax = null;
            ExpressionSyntax validationCallbackSyntax = null;
            var changeList = new ConverterProcessingResult();

            if (initializer.ArgumentList.Arguments.Count > 3) // Have to break down metadata constructor
            {
                var results = await ProcessMetadata(editor.Generator, semanticModel, fieldSymbol, cancellationToken);
                changeList = changeList.AppendResult(results.Item1);
                coerceCallbackSyntax = results.Item2;
            }
            if (initializer.ArgumentList.Arguments.Count > 4)
            {
                validationCallbackSyntax = initializer.ArgumentList.Arguments[4].Expression;
            }

            if (coerceCallbackSyntax != null || validationCallbackSyntax != null)
            {
                var combinedCoerceValidateExpression = CreateCombinedCoerceValidate(editor.Generator, semanticModel, coerceCallbackSyntax, validationCallbackSyntax);
                changeList = changeList.AddArguments((ArgumentSyntax)editor.Generator.Argument("validate", RefKind.None, combinedCoerceValidateExpression));
            }
            avaloniaInvocation = avaloniaInvocation.AddArgumentListArguments(changeList.AdditionalInvocationArguments.ToArray())
                .WithAdditionalAnnotations(Formatter.Annotation);
            ReplaceMember(editor, semanticModel, declarator, avaloniaInvocation);
            staticConstructor = staticConstructor.AddBodyStatements(changeList.AdditionalStaticConstructorStatements.ToArray())
                .WithAdditionalAnnotations(Formatter.Annotation);
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