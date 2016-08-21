using System;
using System.Composition;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using Microsoft.CodeAnalysis.Formatting;

namespace AvaloniaAnalyzers
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(WpfTypeConverterFixer)), Shared]
    public class WpfTypeConverterFixer : CodeFixProvider
    {
        private const string title = "Convert to equivalent Avalonia Type";

        public sealed override ImmutableArray<string> FixableDiagnosticIds
        {
            get { return ImmutableArray.Create(WpfTypeConverterAnalyzer.DiagnosticId); }
        }

        public sealed override FixAllProvider GetFixAllProvider()
        {
            return WellKnownFixAllProviders.BatchFixer;
        }

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;

            var type = root.FindNode(diagnosticSpan);

            // Register a code action that will invoke the fix.
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: title,
                    createChangedDocument: c => ConvertType(context.Document, type, c),
                    equivalenceKey: title),
                diagnostic);
        }

        private async Task<Document> ConvertType(Document document, SyntaxNode type, CancellationToken c)
        {
            var editor = await DocumentEditor.CreateAsync(document, c);
            if (type is BaseTypeSyntax)
            {
                type = ((BaseTypeSyntax)type).Type;
            }
            var originalTypeSymbol = editor.SemanticModel.GetTypeInfo(type, c).Type;
            SyntaxNode newTypeSyntax = null;
            var avaloniaNamespace = editor.Generator.IdentifierName("Avalonia")
                .WithAdditionalAnnotations(Annotations.NamespaceImportAnnotation);
            var avaloniaControlsNamespace = editor.Generator.MemberAccessExpression(avaloniaNamespace, "Controls")
                .WithAdditionalAnnotations(Annotations.NamespaceImportAnnotation);
            var avaloniaControlsPrimitivesNamespace = editor.Generator.MemberAccessExpression(avaloniaControlsNamespace, "Primitives")
                .WithAdditionalAnnotations(Annotations.NamespaceImportAnnotation);
            var avaloniaControlsShapesNamespace = editor.Generator.MemberAccessExpression(avaloniaControlsNamespace, "Shapes")
                .WithAdditionalAnnotations(Annotations.NamespaceImportAnnotation);
            var avaloniaMediaNamespace = editor.Generator.MemberAccessExpression(avaloniaNamespace, "Media")
                .WithAdditionalAnnotations(Annotations.NamespaceImportAnnotation);

            if (originalTypeSymbol.ToDisplayString() == "System.Windows.DependencyObject")
            {
                newTypeSyntax = editor.Generator.MemberAccessExpression(avaloniaNamespace, "AvaloniaObject");
            }
            else if (originalTypeSymbol.ToDisplayString() == "System.Windows.DependencyProperty")
            {
                newTypeSyntax = editor.Generator.MemberAccessExpression(avaloniaNamespace, "AvaloniaProperty");
            }
            else if (originalTypeSymbol.ToDisplayString() == "System.Windows.UIElement"
                || originalTypeSymbol.ToDisplayString() == "System.Windows.FrameworkElement")
            {
                newTypeSyntax = editor.Generator.MemberAccessExpression(avaloniaControlsNamespace, "Control");
            }
            else if (originalTypeSymbol.ToDisplayString() == "System.Windows.Controls.Control")
            {
                newTypeSyntax = editor.Generator.MemberAccessExpression(avaloniaControlsPrimitivesNamespace, "TemplatedControl");
            }
            else if (originalTypeSymbol.ToDisplayString() == "System.Windows.Window")
            {
                newTypeSyntax = editor.Generator.MemberAccessExpression(avaloniaControlsNamespace, "Window");
            }
            else if (originalTypeSymbol.ContainingNamespace.ToDisplayString() == "System.Windows.Controls")
            {
                newTypeSyntax = editor.Generator.MemberAccessExpression(avaloniaControlsNamespace, originalTypeSymbol.Name);
            }
            else if (originalTypeSymbol.ContainingNamespace.ToDisplayString() == "System.Windows.Media")
            {
                newTypeSyntax = editor.Generator.MemberAccessExpression(avaloniaMediaNamespace, originalTypeSymbol.Name);
            }
            else if (originalTypeSymbol.ContainingNamespace.ToDisplayString() == "System.Windows.Shapes")
            {
                newTypeSyntax = editor.Generator.MemberAccessExpression(avaloniaControlsShapesNamespace, originalTypeSymbol.Name);
            }

            if (newTypeSyntax != null)
            {
                var newSymbol = editor.SemanticModel.GetSpeculativeTypeInfo(0, newTypeSyntax, SpeculativeBindingOption.BindAsExpression).Type;
                if (newSymbol != null)
                {
                    editor.ReplaceNode(type, editor.Generator.TypeExpression(newSymbol).WithAdditionalAnnotations(Formatter.Annotation));
                }
            }
            return await ImportAdder.AddImportsAsync(editor.GetChangedDocument(), Annotations.NamespaceImportAnnotation, cancellationToken: c);
        }
    }
}