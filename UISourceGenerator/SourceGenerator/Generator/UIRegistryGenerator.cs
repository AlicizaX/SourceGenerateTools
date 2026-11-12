using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Registers every UI view and Holder resource of the assembly at startup. Everything the runtime needs is decided here,
/// at compile time: the factory that constructs the view, window or widget, layer and cache time, and whether the view
/// overrides OnUpdate.
/// </summary>
[Generator]
public sealed class UIRegistryGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context)
    {
        Compilation compilation = context.Compilation;
        var view = compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIBase");
        var window = compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIWindow`1");
        var widget = compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIWidget`1");
        var holder = compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIHolderObjectBase");
        if (view == null || window == null || widget == null || holder == null) return;
        var windowAttribute = compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.WindowAttribute");
        var resourceAttribute = compilation.GetTypeByMetadataName("AlicizaX.UI.Runtime.UIResAttribute");

        var classes = new List<INamedTypeSymbol>();
        GeneratorSupport.CollectClasses(compilation.Assembly.GlobalNamespace, classes);
        var resources = new List<string>();
        var views = new List<string>();
        foreach (INamedTypeSymbol type in classes)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            // Walk up to the framework base: a Holder, or the constructed UIWindow<>/UIWidget<> whose argument is the Holder type.
            for (INamedTypeSymbol? baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (Same(baseType, holder))
                {
                    AddLine(resources, BuildResourceLine(context, type, resourceAttribute));
                    break;
                }
                bool isWindow = Same(baseType.OriginalDefinition, window);
                if (!isWindow && !Same(baseType.OriginalDefinition, widget)) continue;
                AddLine(views, BuildViewLine(context, type, baseType, isWindow, view, windowAttribute));
                break;
            }
        }

        // Holder resources first, then views; ordinal order keeps the output stable between compilations.
        resources.Sort(StringComparer.Ordinal);
        views.Sort(StringComparer.Ordinal);
        resources.AddRange(views);
        GeneratorSupport.Emit(context, resources);
    }

    private static bool Same(ISymbol left, ISymbol right) => SymbolEqualityComparer.Default.Equals(left, right);

    private static void AddLine(List<string> lines, string? line)
    {
        if (line != null) lines.Add(line);
    }

    private static string? BuildResourceLine(GeneratorExecutionContext context, INamedTypeSymbol type, INamedTypeSymbol? resourceAttribute)
    {
        var attribute = GeneratorSupport.Attribute(type, resourceAttribute);
        if (attribute == null || !GeneratorSupport.CanReference(context, type)) return null;
        string location = SymbolDisplay.FormatLiteral((string?)attribute.ConstructorArguments[0].Value ?? string.Empty, true);
        return $"UIResRegistry.Register(typeof({GeneratorSupport.Name(type)}), {location}, (EUIResLoadType){Integer(attribute.ConstructorArguments[1])});";
    }

    private static string? BuildViewLine(GeneratorExecutionContext context, INamedTypeSymbol type, INamedTypeSymbol frameworkBase,
        bool isWindow, INamedTypeSymbol view, INamedTypeSymbol? windowAttribute)
    {
        if (!GeneratorSupport.CanReference(context, type)) return null;
        if (!(frameworkBase.TypeArguments[0] is INamedTypeSymbol holder) || !GeneratorSupport.CanReference(context, holder)) return null;
        if (!GeneratorSupport.HasAccessibleConstructor(context, type)) return null;
        string name = GeneratorSupport.Name(type);
        string types = $"<{name}, {GeneratorSupport.Name(holder)}>(() => new {name}(), ";
        string update = OverridesUpdate(type, view) ? "true" : "false";
        if (!isWindow) return $"UIMetaRegistry.RegisterWidget{types}{update});";
        var attribute = GeneratorSupport.Attribute(type, windowAttribute);
        string layer = attribute == null ? "UILayer.UI" : "(UILayer)" + Integer(attribute.ConstructorArguments[0]);
        string cache = attribute == null ? "UICacheTime.None" : Integer(attribute.ConstructorArguments[1]);
        return $"UIMetaRegistry.RegisterWindow{types}{layer}, {cache}, {update});";
    }

    /// <summary>A view takes part in the Update loop exactly when it, or a base class below UIBase, overrides OnUpdate.</summary>
    private static bool OverridesUpdate(INamedTypeSymbol type, INamedTypeSymbol view)
    {
        for (INamedTypeSymbol? current = type; current != null && !Same(current, view); current = current.BaseType)
            foreach (ISymbol member in current.GetMembers("OnUpdate"))
                if (member is IMethodSymbol { IsOverride: true }) return true;
        return false;
    }

    private static string Integer(TypedConstant constant) =>
        Convert.ToInt32(constant.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
}
