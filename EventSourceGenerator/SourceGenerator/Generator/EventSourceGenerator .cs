using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AlicizaX.Event.SourceGenerators
{
    [Generator]
    public sealed class EventGenerator : IIncrementalGenerator
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
            title: "Prewarm requires IEventArgs",
            messageFormat: "Type {0} has [Prewarm] but does not implement IEventArgs",
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

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValuesProvider<EventScanResult> events = context.SyntaxProvider
                .CreateSyntaxProvider(
                    static (node, _) => IsCandidate(node),
                    static (ctx, _) => Scan(ctx))
                .Where(static result => result.HasValue)
                .Select(static (result, _) => result.Value);

            context.RegisterSourceOutput(events.Collect().Combine(context.CompilationProvider), static (spc, source) =>
            {
                Emit(spc, source.Right, source.Left);
            });
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
                if (name is "IEventArgs" or "IPayloadEventArgs" or "IEmptyEventArgs")
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

        private static EventScanResult? Scan(GeneratorSyntaxContext context)
        {
            if (context.Node is not StructDeclarationSyntax structDecl)
            {
                return null;
            }

            if (context.SemanticModel.GetDeclaredSymbol(structDecl) is not INamedTypeSymbol symbol)
            {
                return null;
            }

            INamedTypeSymbol eventArgs = context.SemanticModel.Compilation.GetTypeByMetadataName("AlicizaX.IEventArgs");
            INamedTypeSymbol payloadArgs = context.SemanticModel.Compilation.GetTypeByMetadataName("AlicizaX.IPayloadEventArgs");
            INamedTypeSymbol emptyArgs = context.SemanticModel.Compilation.GetTypeByMetadataName("AlicizaX.IEmptyEventArgs");
            INamedTypeSymbol prewarmAttribute = context.SemanticModel.Compilation.GetTypeByMetadataName("AlicizaX.PrewarmAttribute");
            if (eventArgs == null || prewarmAttribute == null)
            {
                return null;
            }

            bool isEventArgs = Implements(symbol, eventArgs);
            bool isPayload = payloadArgs != null && Implements(symbol, payloadArgs);
            bool isEmpty = emptyArgs != null && Implements(symbol, emptyArgs);
            bool hasPrewarm = TryGetPrewarmCapacity(symbol, prewarmAttribute, out int capacity);
            if (!isEventArgs && !hasPrewarm)
            {
                return null;
            }

            return new EventScanResult(
                symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                symbol.ToDisplayString(),
                capacity,
                hasPrewarm,
                isEventArgs,
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

        private static void Emit(SourceProductionContext context, Compilation compilation, ImmutableArray<EventScanResult> results)
        {
            if (results.IsDefaultOrEmpty)
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

                if (!result.IsEventArgs)
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
            sb.Append("namespace AlicizaX.Event.Generated.").AppendLine(assemblyName);
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
                bool isEventArgs,
                bool isPayload,
                bool isEmpty,
                bool hasInstanceFields,
                bool isOpenGeneric)
            {
                FullyQualifiedName = fullyQualifiedName;
                DisplayName = displayName;
                Capacity = capacity;
                HasPrewarm = hasPrewarm;
                IsEventArgs = isEventArgs;
                IsPayload = isPayload;
                IsEmpty = isEmpty;
                HasInstanceFields = hasInstanceFields;
                IsOpenGeneric = isOpenGeneric;
            }

            internal string FullyQualifiedName { get; }
            internal string DisplayName { get; }
            internal int Capacity { get; }
            internal bool HasPrewarm { get; }
            internal bool IsEventArgs { get; }
            internal bool IsPayload { get; }
            internal bool IsEmpty { get; }
            internal bool HasInstanceFields { get; }
            internal bool IsOpenGeneric { get; }
        }
    }
}
