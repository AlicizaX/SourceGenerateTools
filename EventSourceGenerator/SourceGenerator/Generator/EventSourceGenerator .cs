using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AlicizaX.Event.SourceGenerators
{
    [Generator]
    public sealed class EventGenerator : ISourceGenerator
    {
        private static readonly DiagnosticDescriptor OpenGenericPrewarm = new DiagnosticDescriptor(
            id: "EVT001",
            title: "Prewarm cannot target an open generic event",
            messageFormat: "Type {0} is an open generic and will not be prewarmed",
            category: "Event",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor DualEventKind = new DiagnosticDescriptor(
            id: "EVT002",
            title: "Event cannot implement both payload and empty contracts",
            messageFormat: "Type {0} cannot implement both IPayloadEventArgs and IEmptyEventArgs",
            category: "Event",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor EmptyEventHasFields = new DiagnosticDescriptor(
            id: "EVT003",
            title: "Empty event cannot declare instance fields",
            messageFormat: "Type {0} implements IEmptyEventArgs but declares instance fields. Use IPayloadEventArgs instead",
            category: "Event",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor PrewarmWithoutEventArgs = new DiagnosticDescriptor(
            id: "EVT004",
            title: "Prewarm requires a payload or empty event",
            messageFormat: "Type {0} has [Prewarm] but does not implement IPayloadEventArgs or IEmptyEventArgs",
            category: "Event",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor InvalidPrewarmCapacity = new DiagnosticDescriptor(
            id: "EVT005",
            title: "Invalid Prewarm capacity",
            messageFormat: "Type {0} has an invalid Prewarm capacity {1}",
            category: "Event",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new SyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (context.SyntaxReceiver is not SyntaxReceiver receiver)
            {
                return;
            }

            List<EventScanResult> results = new List<EventScanResult>();
            foreach (StructDeclarationSyntax structDecl in receiver.Candidates)
            {
                EventScanResult? result = Scan(context.Compilation.GetSemanticModel(structDecl.SyntaxTree), structDecl);
                if (result.HasValue)
                {
                    results.Add(result.Value);
                }
            }

            Emit(context, context.Compilation, results);
        }

        private static bool IsCandidate(SyntaxNode node)
        {
            if (node is not StructDeclarationSyntax structDecl || structDecl.BaseList == null)
            {
                return false;
            }

            foreach (BaseTypeSyntax baseType in structDecl.BaseList.Types)
            {
                string name = GetSimpleName(baseType.Type);
                if (name is "IPayloadEventArgs" or "IEmptyEventArgs")
                {
                    return true;
                }
            }

            foreach (AttributeListSyntax attributeList in structDecl.AttributeLists)
            {
                foreach (AttributeSyntax attribute in attributeList.Attributes)
                {
                    string name = GetSimpleName(attribute.Name);
                    if (name is "Prewarm" or "PrewarmAttribute")
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string GetSimpleName(TypeSyntax type)
        {
            return type switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                _ => string.Empty
            };
        }

        private static EventScanResult? Scan(SemanticModel semanticModel, StructDeclarationSyntax structDecl)
        {
            if (semanticModel.GetDeclaredSymbol(structDecl) is not INamedTypeSymbol symbol)
            {
                return null;
            }

            INamedTypeSymbol payloadArgs = semanticModel.Compilation.GetTypeByMetadataName("AlicizaX.IPayloadEventArgs");
            INamedTypeSymbol emptyArgs = semanticModel.Compilation.GetTypeByMetadataName("AlicizaX.IEmptyEventArgs");
            INamedTypeSymbol prewarmAttribute = semanticModel.Compilation.GetTypeByMetadataName("AlicizaX.PrewarmAttribute");
            if (prewarmAttribute == null || (payloadArgs == null && emptyArgs == null))
            {
                return null;
            }

            bool isPayload = payloadArgs != null && Implements(symbol, payloadArgs);
            bool isEmpty = emptyArgs != null && Implements(symbol, emptyArgs);
            bool hasPrewarm = TryGetPrewarmCapacity(symbol, prewarmAttribute, out int capacity);
            if (!isPayload && !isEmpty && !hasPrewarm)
            {
                return null;
            }

            return new EventScanResult(
                symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                symbol.ToDisplayString(),
                capacity,
                hasPrewarm,
                isPayload,
                isEmpty,
                HasInstanceFields(symbol),
                IsOpenGeneric(symbol));
        }

        private static bool TryGetPrewarmCapacity(INamedTypeSymbol symbol, INamedTypeSymbol prewarmAttribute, out int capacity)
        {
            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, prewarmAttribute))
                {
                    continue;
                }

                if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int value)
                {
                    capacity = value;
                    return true;
                }
            }

            capacity = 0;
            return false;
        }

        private static bool Implements(INamedTypeSymbol symbol, INamedTypeSymbol interfaceType)
        {
            foreach (INamedTypeSymbol implemented in symbol.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(implemented, interfaceType))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasInstanceFields(INamedTypeSymbol symbol)
        {
            foreach (ISymbol member in symbol.GetMembers())
            {
                if (member is IFieldSymbol field && !field.IsStatic && !field.IsConst)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsOpenGeneric(INamedTypeSymbol symbol)
        {
            return symbol.IsGenericType && symbol.TypeArguments.Any(argument => argument.TypeKind == TypeKind.TypeParameter);
        }

        private static void Emit(GeneratorExecutionContext context, Compilation compilation, List<EventScanResult> results)
        {
            if (results.Count == 0)
            {
                return;
            }

            List<EventScanResult> prewarmItems = new List<EventScanResult>();
            HashSet<string> seen = new HashSet<string>();

            foreach (EventScanResult result in results)
            {
                if (!seen.Add(result.FullyQualifiedName))
                {
                    continue;
                }

                if (result.IsPayload && result.IsEmpty)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DualEventKind, Location.None, result.DisplayName));
                }

                if (result.IsEmpty && result.HasInstanceFields)
                {
                    context.ReportDiagnostic(Diagnostic.Create(EmptyEventHasFields, Location.None, result.DisplayName));
                }

                if (!result.HasPrewarm)
                {
                    continue;
                }

                if (!result.IsPayload && !result.IsEmpty)
                {
                    context.ReportDiagnostic(Diagnostic.Create(PrewarmWithoutEventArgs, Location.None, result.DisplayName));
                    continue;
                }

                if (result.IsOpenGeneric)
                {
                    context.ReportDiagnostic(Diagnostic.Create(OpenGenericPrewarm, Location.None, result.DisplayName));
                    continue;
                }

                if (result.Capacity <= 0)
                {
                    context.ReportDiagnostic(Diagnostic.Create(InvalidPrewarmCapacity, Location.None, result.DisplayName, result.Capacity));
                    continue;
                }

                prewarmItems.Add(result);
            }

            if (prewarmItems.Count == 0)
            {
                return;
            }

            context.AddSource("EventPrewarmRegister.g.cs", SourceText.From(GenerateSource(compilation, prewarmItems), Encoding.UTF8));
        }

        private static string GenerateSource(Compilation compilation, List<EventScanResult> items)
        {
            string assemblyName = SanitizeIdentifier(compilation.AssemblyName ?? "Assembly");
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("using AlicizaX;");
            sb.AppendLine();
            sb.Append("namespace AlicizaX.Generated.EventPrewarm.").AppendLine(assemblyName);
            sb.AppendLine("{");
            sb.AppendLine("    internal static class EventPrewarmRegister");
            sb.AppendLine("    {");
            sb.AppendLine("#if UNITY_EDITOR");
            sb.AppendLine("        [UnityEditor.InitializeOnEnterPlayMode]");
            sb.AppendLine("#endif");
            sb.AppendLine("        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterAssembliesLoaded)]");
            sb.AppendLine("        internal static void Prewarm()");
            sb.AppendLine("        {");

            foreach (EventScanResult item in items.OrderBy(result => result.FullyQualifiedName))
            {
                sb.Append("            EventInitialSize<").Append(item.FullyQualifiedName).Append(">.Size = ").Append(item.Capacity).AppendLine(";");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string SanitizeIdentifier(string value)
        {
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            if (sb.Length == 0 || char.IsDigit(sb[0]))
            {
                sb.Insert(0, '_');
            }

            return sb.ToString();
        }

        private readonly struct EventScanResult
        {
            internal EventScanResult(
                string fullyQualifiedName,
                string displayName,
                int capacity,
                bool hasPrewarm,
                bool isPayload,
                bool isEmpty,
                bool hasInstanceFields,
                bool isOpenGeneric)
            {
                FullyQualifiedName = fullyQualifiedName;
                DisplayName = displayName;
                Capacity = capacity;
                HasPrewarm = hasPrewarm;
                IsPayload = isPayload;
                IsEmpty = isEmpty;
                HasInstanceFields = hasInstanceFields;
                IsOpenGeneric = isOpenGeneric;
            }

            internal string FullyQualifiedName { get; }
            internal string DisplayName { get; }
            internal int Capacity { get; }
            internal bool HasPrewarm { get; }
            internal bool IsPayload { get; }
            internal bool IsEmpty { get; }
            internal bool HasInstanceFields { get; }
            internal bool IsOpenGeneric { get; }
        }

        private sealed class SyntaxReceiver : ISyntaxReceiver
        {
            internal List<StructDeclarationSyntax> Candidates { get; } = new List<StructDeclarationSyntax>();

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                if (IsCandidate(syntaxNode) && syntaxNode is StructDeclarationSyntax structDecl)
                {
                    Candidates.Add(structDecl);
                }
            }
        }
    }
}
