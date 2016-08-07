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
        }

        private static void AnalyzeMethod(SymbolAnalysisContext context)
        {
            var methodSymbol = (IMethodSymbol)context.Symbol;
            foreach (var param in methodSymbol.Parameters)
            {
                if(IsWpfType(param.Type))
                {
                    //Report diagnostic
                    var paramSyntax = (ParameterSyntax)param.DeclaringSyntaxReferences[0].GetSyntax();
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
                var classSyntax = (ClassDeclarationSyntax)typeSymbol.DeclaringSyntaxReferences[0].GetSyntax();
                var baseTypes = classSyntax.BaseList.Types;
                var semanticModel = context.Compilation.GetSemanticModel(classSyntax.SyntaxTree);
                var typeToFlag = baseTypes.First(baseType => semanticModel.GetTypeInfo(baseType.Type).Type == typeSymbol.BaseType);
                context.ReportDiagnostic(Diagnostic.Create(Rule, typeToFlag.GetLocation(), typeToFlag.ToString()));
            }
        }

        private static bool IsWpfType(ITypeSymbol type)
        {
            var assemblyName = type.ContainingAssembly.Name;
            return WpfAssemblyNames.Contains(assemblyName);
        }
    }
}