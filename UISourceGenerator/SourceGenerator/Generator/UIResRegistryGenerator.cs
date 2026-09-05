using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

[Generator]
public sealed class UIResRegistryGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) =>
        context.RegisterForSyntaxNotifications(() => new GeneratorSupport.Receiver());

    public void Execute(GeneratorExecutionContext context)
    {
        var holder = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIHolderObjectBase");
        var resourceAttribute = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIResAttribute");
        if (holder == null || resourceAttribute == null) return;
        var lines = new List<string>();
        foreach (var type in GeneratorSupport.Candidates(context))
        {
            INamedTypeSymbol? current = type.BaseType;
            while (current != null && !SymbolEqualityComparer.Default.Equals(current, holder)) current = current.BaseType;
            if (current == null) continue;
            var attribute = GeneratorSupport.Attribute(type, resourceAttribute);
            if (attribute == null || !GeneratorSupport.CanReference(context, type)) continue;
            string location = SymbolDisplay.FormatLiteral((string?)attribute.ConstructorArguments[0].Value ?? string.Empty, true);
            string loadType = Convert.ToInt32(attribute.ConstructorArguments[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            lines.Add($"UIResRegistry.Register(typeof({GeneratorSupport.Name(type)}), {location}, (EUIResLoadType){loadType});");
        }
        GeneratorSupport.Emit(context, "UIResRegistry", lines);
    }
}
