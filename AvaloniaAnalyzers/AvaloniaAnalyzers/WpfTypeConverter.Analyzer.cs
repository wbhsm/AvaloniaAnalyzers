using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AvaloniaAnalyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class WpfTypeConverterAnalyzer : DiagnosticAnalyzer
    {
        private static readonly string[] WpfAssemblyNames = new[] { "WindowsBase", "PresentationCore", "PresentationFramework", "System.Windows" };

        public const string DiagnosticId = "WpfTypeToAvaloniaType";
        internal static readonly LocalizableString Title = "This type is a WPF type.";
        internal static readonly LocalizableString MessageFormat = "'{0}' is a WPF type.";
        internal const string Category = "AvaloniaConversion";

        internal static DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Warning, true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return ImmutableArray.Create(Rule); } }

        public override void Initialize(AnalysisContext context)
        {
            context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
            context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
            context.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
            context.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
        }

        private static void AnalyzeField(SymbolAnalysisContext context)
        {
            var fieldSymbol = (IFieldSymbol)context.Symbol;
            if (IsWpfType(fieldSymbol.Type))
            {
                var variableDeclaratorSyntax = (VariableDeclaratorSyntax)fieldSymbol.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken);
                var declaration = variableDeclaratorSyntax.Parent as VariableDeclarationSyntax;
                context.ReportDiagnostic(Diagnostic.Create(Rule, declaration.Type.GetLocation(), declaration.Type.ToString()));
            }
        }

        private static void AnalyzeProperty(SymbolAnalysisContext context)
        {
            var propertySymbol = (IPropertySymbol)context.Symbol;
            if (IsWpfType(propertySymbol.Type))
            {
                var propertySyntax = (PropertyDeclarationSyntax)propertySymbol.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken);
                context.ReportDiagnostic(Diagnostic.Create(Rule, propertySyntax.Type.GetLocation(), propertySyntax.Type.ToString()));
            }
            ReportParameterDiagnostics(context, propertySymbol.Parameters);
        }

        private static void AnalyzeMethod(SymbolAnalysisContext context)
        {
            var methodSymbol = (IMethodSymbol)context.Symbol;
            var parameters = methodSymbol.Parameters;
            ReportParameterDiagnostics(context, parameters);
        }

        private static void ReportParameterDiagnostics(SymbolAnalysisContext context, ImmutableArray<IParameterSymbol> parameters)
        {
            foreach (var param in parameters)
            {
                if (IsWpfType(param.Type) && !param.DeclaringSyntaxReferences.IsEmpty)
                {
                    //Report diagnostic
                    var paramSyntax = (ParameterSyntax)param.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken);
                    var paramTypeSyntax = paramSyntax.Type;
                    context.ReportDiagnostic(Diagnostic.Create(Rule, paramTypeSyntax.GetLocation(), paramTypeSyntax.ToString()));
                }
            }
        }

        private static void AnalyzeType(SymbolAnalysisContext context)
        {
            var typeSymbol = (ITypeSymbol)context.Symbol;
            if (IsWpfType(typeSymbol.BaseType))
            {
                foreach (var syntaxReference in typeSymbol.DeclaringSyntaxReferences)
                {
                    var classSyntax = (ClassDeclarationSyntax)syntaxReference.GetSyntax(context.CancellationToken);
                    var baseTypes = classSyntax.BaseList?.Types;
                    if (baseTypes.HasValue)
                    {
                        var semanticModel = context.Compilation.GetSemanticModel(classSyntax.SyntaxTree);
                        var typeToFlag = baseTypes.Value.FirstOrDefault(baseType => semanticModel.GetTypeInfo(baseType.Type).Type == typeSymbol.BaseType);
                        if (typeToFlag != null)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Rule, typeToFlag.GetLocation(), typeToFlag.ToString())); 
                        }
                    } 
                }
            }
        }

        private static bool IsWpfType(ITypeSymbol type)
        {
            var assemblyName = type.ContainingAssembly?.Name;
            return WpfAssemblyNames.Contains(assemblyName);
        }
    }
}