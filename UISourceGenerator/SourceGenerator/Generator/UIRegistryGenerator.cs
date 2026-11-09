using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

[Generator]
public sealed class UIRegistryGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) =>
        context.RegisterForSyntaxNotifications(() => new GeneratorSupport.Receiver());

    public void Execute(GeneratorExecutionContext context)
    {
        var window = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIWindow`1");
        var widget = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIWidget`1");
        var holder = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIHolderObjectBase");
        var windowAttribute = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.WindowAttribute");
        var updateAttribute = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIUpdateAttribute");
        var resourceAttribute = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIResAttribute");
        var layerType = context.Compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UILayer");
        if (window == null || widget == null || holder == null || layerType == null) return;
        var defaultLayer = (IFieldSymbol)layerType.GetMembers("UI").Single();
        var allLayer = (IFieldSymbol)layerType.GetMembers("All").Single();
        var resources = new List<string>();
        var views = new List<string>();
        var constructed = new List<string>();
        foreach (var type in GeneratorSupport.Candidates(context))
        {
            if (DerivesFrom(type, holder))
            {
                string? resource = BuildResourceLine(context, type, resourceAttribute);
                if (resource != null) resources.Add(resource);
                continue;
            }
            string? view = BuildViewLine(context, type, window, widget, windowAttribute, updateAttribute, defaultLayer, allLayer);
            if (view == null) continue;
            views.Add(view);
            constructed.Add(GeneratorSupport.Name(type));
        }
        GeneratorSupport.Emit(context, resources.Concat(views), constructed);
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (INamedTypeSymbol? current = type.BaseType; current != null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
        return false;
    }

    private static string? BuildResourceLine(GeneratorExecutionContext context, INamedTypeSymbol type, INamedTypeSymbol? resourceAttribute)
    {
        var attribute = GeneratorSupport.Attribute(type, resourceAttribute);
        if (attribute == null || !GeneratorSupport.CanReference(context, type)) return null;
        string location = SymbolDisplay.FormatLiteral((string?)attribute.ConstructorArguments[0].Value ?? string.Empty, true);
        string loadType = Convert.ToInt32(attribute.ConstructorArguments[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        return $"UIResRegistry.Register(typeof({GeneratorSupport.Name(type)}), {location}, (EUIResLoadType){loadType});";
    }

    private static string? BuildViewLine(GeneratorExecutionContext context, INamedTypeSymbol type,
        INamedTypeSymbol window, INamedTypeSymbol widget, INamedTypeSymbol? windowAttribute, INamedTypeSymbol? updateAttribute,
        IFieldSymbol defaultLayer, IFieldSymbol allLayer)
    {
        // Resolve the constructed framework base, not an arbitrary intermediate Holder argument.
        INamedTypeSymbol? actualBase = type.BaseType;
        while (actualBase != null && !SymbolEqualityComparer.Default.Equals(actualBase.OriginalDefinition, window)
            && !SymbolEqualityComparer.Default.Equals(actualBase.OriginalDefinition, widget)) actualBase = actualBase.BaseType;
        if (actualBase == null || !GeneratorSupport.CanReference(context, type)) return null;
        var holder = actualBase.TypeArguments[0] as INamedTypeSymbol;
        if (holder == null || !GeneratorSupport.CanReference(context, holder)) return null;
        if (!GeneratorSupport.HasPublicDefaultConstructor(context, type)) return null;
        bool isWindow = SymbolEqualityComparer.Default.Equals(actualBase.OriginalDefinition, window);
        var attribute = isWindow ? GeneratorSupport.Attribute(type, windowAttribute) : null;
        int layer = Convert.ToInt32(attribute?.ConstructorArguments[0].Value ?? defaultLayer.ConstantValue, CultureInfo.InvariantCulture);
        if (layer == Convert.ToInt32(allLayer.ConstantValue, CultureInfo.InvariantCulture))
        {
            GeneratorSupport.ReportInvalidLayer(context, type, attribute!);
            return null;
        }
        int cache = attribute == null ? 0 : Convert.ToInt32(attribute.ConstructorArguments[1].Value, CultureInfo.InvariantCulture);
        bool update = GeneratorSupport.Attribute(type, updateAttribute) != null;
        string name = GeneratorSupport.Name(type);
        return $"UIMetaRegistry.Register(typeof({name}), typeof({GeneratorSupport.Name(holder)}), (UILayer){layer.ToString(CultureInfo.InvariantCulture)}, {cache.ToString(CultureInfo.InvariantCulture)}, {(update ? "true" : "false")});";
    }
}
