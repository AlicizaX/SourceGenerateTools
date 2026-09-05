using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

[Generator]
public sealed class UIMetaSourceGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) =>
        context.RegisterForSyntaxNotifications(() => new GeneratorSupport.Receiver());

    public void Execute(GeneratorExecutionContext context)
    {
        var window = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIWindow`1");
        var widget = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIWidget`1");
        var windowAttribute = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.WindowAttribute");
        var updateAttribute = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIUpdateAttribute");
        var layerType = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UILayer");
        if (window == null || widget == null || layerType == null) return;
        var defaultLayer = (IFieldSymbol)layerType.GetMembers("UI").Single();
        var lines = new List<string>();
        foreach (var type in GeneratorSupport.Candidates(context))
        {
            // Resolve the constructed framework base, not an arbitrary intermediate Holder argument.
            INamedTypeSymbol? actualBase = type.BaseType;
            while (actualBase != null && !SymbolEqualityComparer.Default.Equals(actualBase.OriginalDefinition, window)
                && !SymbolEqualityComparer.Default.Equals(actualBase.OriginalDefinition, widget)) actualBase = actualBase.BaseType;
            if (actualBase == null || !GeneratorSupport.CanReference(context, type)) continue;
            var holder = actualBase.TypeArguments[0] as INamedTypeSymbol;
            if (holder == null || !GeneratorSupport.CanReference(context, holder)) continue;
            bool isWindow = SymbolEqualityComparer.Default.Equals(actualBase.OriginalDefinition, window);
            var attribute = isWindow ? GeneratorSupport.Attribute(type, windowAttribute) : null;
            int layer = Convert.ToInt32(attribute?.ConstructorArguments[0].Value ?? defaultLayer.ConstantValue, CultureInfo.InvariantCulture);
            int cache = attribute == null ? 0 : Convert.ToInt32(attribute.ConstructorArguments[1].Value, CultureInfo.InvariantCulture);
            bool update = GeneratorSupport.Attribute(type, updateAttribute) != null;
            lines.Add($"UIMetaRegistry.Register(typeof({GeneratorSupport.Name(type)}), typeof({GeneratorSupport.Name(holder)}), (UILayer){layer.ToString(CultureInfo.InvariantCulture)}, {cache.ToString(CultureInfo.InvariantCulture)}, {(update ? "true" : "false")});");
        }
        GeneratorSupport.Emit(context, "UIMetaRegistry", lines);
    }
}
