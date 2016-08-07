using Microsoft.CodeAnalysis.CodeFixes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AvaloniaAnalyzers
{
    class DependencyPropertyFixAllProvider : FixAllProvider
    {
        public override async Task<CodeAction> GetFixAsync(FixAllContext fixAllContext)
        {
            var diagnostics = await fixAllContext.GetDocumentDiagnosticsAsync(fixAllContext.Document);

            return CodeAction.Create(
                "Convert all DependencyProperties in a document",
                c => ConvertDocumentAsync(fixAllContext.Document, diagnostics, c)
                );
        }

        public override IEnumerable<FixAllScope> GetSupportedFixAllScopes()
        {
            return new[] { FixAllScope.Document };
        }

        private static async Task<Document> ConvertDocumentAsync(Document d, IEnumerable<Diagnostic> diagnostics, CancellationToken c)
        {
            var changedDoc = d;
            var originalRoot = await changedDoc.GetSyntaxRootAsync(c).ConfigureAwait(false);

            var originalNodes = diagnostics.Select(diagnostic => originalRoot.FindNode(diagnostic.Location.SourceSpan)).ToList();

            var trackedRoot = originalRoot.TrackNodes(originalNodes);
            
            var diagnosticsWithNodes = diagnostics.Zip(originalNodes,
                (a, b) => Tuple.Create(a, (VariableDeclaratorSyntax)b));

            changedDoc = d.WithSyntaxRoot(trackedRoot);
            foreach (var diagnostic in diagnosticsWithNodes)
            {
                var declaration = (await changedDoc.GetSyntaxRootAsync(c)).GetCurrentNode(diagnostic.Item2);
                changedDoc = await DependencyPropertyConverterFixer.ConvertProperty(changedDoc, declaration, c);
            }

            return changedDoc;
        }
    }
}
