using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AvaloniaAnalyzers
{
    class DependencyPropertyFixAllProvider : FixAllProvider
    {
        public override async Task<CodeAction> GetFixAsync(FixAllContext fixAllContext)
        {
            if (fixAllContext.Scope == FixAllScope.Document)
            {
                var diagnostics = await fixAllContext.GetDocumentDiagnosticsAsync(fixAllContext.Document);

                return CodeAction.Create(
                    "Convert all DependencyProperties in a document",
                    c => ConvertDocumentAsync(fixAllContext.Document, diagnostics, c)
                    );
            }
            else if (fixAllContext.Scope == FixAllScope.Project)
            {
                var documentDiagnostics = new Dictionary<Document, IEnumerable<Diagnostic>>();
                foreach (var document in fixAllContext.Project.Documents)
                {
                    documentDiagnostics.Add(document, await fixAllContext.GetDocumentDiagnosticsAsync(document));
                }
                return CodeAction.Create(
                    "Convert all DependencyProperties in a solution",
                    c => ConvertSolutionAsync(fixAllContext.Solution, documentDiagnostics, c)
                    );
            }
            else if (fixAllContext.Scope == FixAllScope.Solution)
            {
                var documentDiagnostics = new Dictionary<Document, IEnumerable<Diagnostic>>();
                foreach (var document in fixAllContext.Solution.Projects.SelectMany(project => project.Documents))
                {
                    documentDiagnostics.Add(document, await fixAllContext.GetDocumentDiagnosticsAsync(document));
                }
                return CodeAction.Create(
                    "Convert all DependencyProperties in a solution",
                    c => ConvertSolutionAsync(fixAllContext.Solution, documentDiagnostics, c)
                    );
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        public override IEnumerable<FixAllScope> GetSupportedFixAllScopes()
        {
            return new[] { FixAllScope.Document, FixAllScope.Project, FixAllScope.Solution };
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

        private static async Task<Solution> ConvertSolutionAsync(Solution s, IDictionary<Document, IEnumerable<Diagnostic>> documentDiagnostics, CancellationToken c)
        {
            var changedDocuments = await Task.WhenAll(documentDiagnostics.Select(kvp => ConvertDocumentAsync(kvp.Key, kvp.Value, c)));
            foreach (var doc in changedDocuments)
            {
                s = s.WithDocumentSyntaxRoot(doc.Id, await doc.GetSyntaxRootAsync(c));
            }
            return s;
        }
    }
}
