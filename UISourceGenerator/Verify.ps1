
$ErrorActionPreference='Stop'
$references=@((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName)+(Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')+(Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
Add-Type -ReferencedAssemblies $references -CompilerOptions '/nowarn:1701' -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
public static class UIImplementationProbe {
 public static string Run(string root, string dll) {
  var refs=((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator).Concat(new[]{typeof(CSharpCompilation).Assembly.Location,typeof(Compilation).Assembly.Location}).Distinct().Select(x=>MetadataReference.CreateFromFile(x)).ToArray();
  var paths=new[]{"SourceGenerator/Generator/GeneratorSupport.cs","SourceGenerator/Generator/UIRegistryGenerator.cs"};
  var gc=CSharpCompilation.Create("UIAuditGenerators",paths.Select(x=>CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,x)))),refs,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
  using var stream=new MemoryStream();
  var emit=gc.Emit(stream);
  if(!emit.Success) return string.Join("\n",emit.Diagnostics.Where(x=>x.Severity==DiagnosticSeverity.Error));
  var assembly=string.IsNullOrEmpty(dll) ? Assembly.Load(stream.ToArray()) : Assembly.LoadFrom(dll);
  var gens=new[]{"UIRegistryGenerator"}.Select(x=>(ISourceGenerator)Activator.CreateInstance(assembly.GetType(x))).ToArray();
  var prelude=@"
namespace UnityEngine { public enum RuntimeInitializeLoadType {BeforeSceneLoad} public class RuntimeInitializeOnLoadMethodAttribute:System.Attribute {public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType t){}} }
namespace AlicizaX.UI.Runtime {
public abstract class UIBase{} public abstract class UIHolderObjectBase{}
public abstract class UIWindow<T>:UIBase where T:UIHolderObjectBase{}
public abstract class UIWidget<T>:UIBase where T:UIHolderObjectBase{}
public abstract class UITabWindow<T>:UIWindow<T> where T:UIHolderObjectBase{}
public enum UILayer{Background,Scene,UI,Popup,Tips,Top,All} public enum EUIResLoadType:byte{Resources,AssetBundle}
public class WindowAttribute:System.Attribute{public WindowAttribute(UILayer layer,int cacheTime=0){}}
public class UIUpdateAttribute:System.Attribute{}
public class UIResAttribute:System.Attribute{public UIResAttribute(string location,EUIResLoadType loadType){}}
public static class UIMetaRegistry{public static void Register(System.Type a,System.Type b,UILayer c=UILayer.UI,int d=0,bool e=false,System.Func<UIBase> f=null){}}
public static class UIResRegistry{public static void Register(System.Type a,string b,EUIResLoadType c){}}
}";
  var cases=new Dictionary<string,string>{
  ["Baseline"]="[UIRes(\"Good\",EUIResLoadType.AssetBundle)] public class H:UIHolderObjectBase{public const string ResTag=\"Good\";} public class W:UIWindow<H>{}",
  ["AttributeWithoutResTag"]="[UIRes(\"Good\",EUIResLoadType.AssetBundle)] public class H:UIHolderObjectBase{} public class W:UIWindow<H>{}",
  ["AttributeResTagMismatch"]="[UIRes(\"Actual\",EUIResLoadType.AssetBundle)] public class H:UIHolderObjectBase{public const string ResTag=\"Different\";} public class W:UIWindow<H>{}",
  ["IntermediateOtherHolder"]="public class Actual:UIHolderObjectBase{} public class Other:UIHolderObjectBase{} public abstract class Middle<T>:UIWindow<Actual> where T:UIHolderObjectBase{} public class W:Middle<Other>{}",
  ["GenericOuter"]="public class H:UIHolderObjectBase{} public class Outer<T>{public class W:UIWindow<H>{}}",
  ["UpdateWithoutWindowAttribute"]="public class H:UIHolderObjectBase{} [UIUpdate] public class W:UIWindow<H>{}",
  ["WindowAttributes"]="public class H:UIHolderObjectBase{} [Window(UILayer.Popup,7),UIUpdate] public class W:UIWindow<H>{}",
  ["Partial"]="public class H:UIHolderObjectBase{} public partial class W:UIWindow<H>{} public partial class W:UIWindow<H>{}",
  ["Ordering"]="public class H:UIHolderObjectBase{} public class Z:UIWindow<H>{} public class A:UIWindow<H>{}",
  ["EscapedResource"]="[UIRes(\"a\\\\b\\\"c\\n\",EUIResLoadType.Resources)] public class H:UIHolderObjectBase{} public class W:UIWindow<H>{}",
  ["PrivateNested"]="public class H:UIHolderObjectBase{} public class Outer{private class W:UIWindow<H>{}}",
  ["Factory"]="public class H:UIHolderObjectBase{} public class W:UIWindow<H>{internal W(){}}",
  ["PrivateConstructor"]="public class H:UIHolderObjectBase{} public class W:UIWindow<H>{private W(){}}",
  ["ParameterConstructor"]="public class H:UIHolderObjectBase{} public class W:UIWindow<H>{public W(int value){}}",
  ["LayerAll"]="public class H:UIHolderObjectBase{} [Window(UILayer.All,1)] public class W:UIWindow<H>{}",
  ["ResourceAndViewOnce"]="[UIRes(\"Good\",EUIResLoadType.AssetBundle)] public class H:UIHolderObjectBase{} public class W:UIWindow<H>{}"
  };
  var output=new List<string>();
  foreach(var item in cases){
   var input=CSharpCompilation.Create("Case_"+item.Key,new[]{CSharpSyntaxTree.ParseText("using AlicizaX.UI.Runtime;"+item.Value),CSharpSyntaxTree.ParseText(prelude)},refs,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
   var driver=CSharpGeneratorDriver.Create(gens).RunGeneratorsAndUpdateCompilation(input,out var updated,out var diagnostics);
   var errors=updated.GetDiagnostics().Concat(diagnostics).Where(x=>x.Severity==DiagnosticSeverity.Error).ToArray();
   var expectedError=item.Key=="GenericOuter"?"UI002":item.Key=="PrivateConstructor"||item.Key=="ParameterConstructor"?"UI004":item.Key=="LayerAll"?"UI005":null;
   if(expectedError!=null) {
    if(errors.Length!=1 || errors[0].Id!=expectedError || errors[0].Location==Location.None) throw new Exception("Expected located "+expectedError+": "+item.Key);
   } else if(errors.Length!=0) throw new Exception(item.Key+": "+string.Join("\n",errors.Select(x=>x.ToString())));
   if(item.Key=="PrivateNested" && !diagnostics.Any(x=>x.Id=="UI003" && x.Severity==DiagnosticSeverity.Warning && x.Location!=Location.None)) throw new Exception("Expected located UI003");
   if(item.Key=="PrivateNested" && driver.GetRunResult().GeneratedTrees.Any(x=>x.ToString().Contains("global::Outer.W"))) throw new Exception("UI003 type must not be registered");
   var generated=string.Join("\n",driver.GetRunResult().GeneratedTrees.Select(x=>x.ToString()));
   if(item.Key=="AttributeResTagMismatch" && (!generated.Contains("\"Actual\"") || generated.Contains("ResTag"))) throw new Exception("Resource attribute mismatch");
   if(item.Key=="IntermediateOtherHolder" && !generated.Contains("typeof(global::W), typeof(global::Actual)")) throw new Exception("Wrong holder base");
   if(item.Key=="UpdateWithoutWindowAttribute" && !generated.Contains(", 0, true, static () => new global::W());")) throw new Exception("Missing update flag");
   if(item.Key=="Partial" && generated.Split(new[]{"typeof(global::W),"},StringSplitOptions.None).Length!=2) throw new Exception("Duplicate partial registration");
   if(item.Key=="Ordering" && generated.IndexOf("typeof(global::A)",StringComparison.Ordinal)>generated.IndexOf("typeof(global::Z)",StringComparison.Ordinal)) throw new Exception("Unsorted output");
   if(item.Key=="EscapedResource" && !generated.Contains(Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral("a\\b\"c\n",true))) throw new Exception("Resource string changed");
   if(item.Key=="WindowAttributes" && !generated.Contains(", (UILayer)3, 7, true, static () => new global::W());")) throw new Exception("Wrong WindowAttribute values");
   if(item.Key=="Factory" && !generated.Contains("static () => new global::W()")) throw new Exception("Missing factory");
   if(item.Key=="Baseline" && (!generated.Contains("internal static void RegisterAll()") || !generated.Contains("internal static class UIGeneratedRegistry"))) throw new Exception("RegisterAll must be internal");
   if(item.Key=="ResourceAndViewOnce" && (generated.Split(new[]{"UIResRegistry.Register("},StringSplitOptions.None).Length!=2 || generated.Split(new[]{"UIMetaRegistry.Register("},StringSplitOptions.None).Length!=2 || generated.IndexOf("UIResRegistry.Register(",StringComparison.Ordinal)>generated.IndexOf("UIMetaRegistry.Register(",StringComparison.Ordinal))) throw new Exception("Expected one ordered registration each");
   var reverse=CSharpCompilation.Create("Reverse", input.SyntaxTrees.Reverse(),refs,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
   var again=CSharpGeneratorDriver.Create(gens).RunGeneratorsAndUpdateCompilation(reverse,out var reversed,out var ignored);
   if(generated!=string.Join("\n",again.GetRunResult().GeneratedTrees.Select(x=>x.ToString()))) throw new Exception("Nondeterministic generation: "+item.Key);
   output.Add(item.Key+": PASS");
   output.Add(generated);

  }
  return string.Join("\n",output);
 }
}
'@
$sourceResults = [UIImplementationProbe]::Run($PSScriptRoot, $null)
$dllResults = [UIImplementationProbe]::Run($PSScriptRoot, (Join-Path $PSScriptRoot 'bin/Release/UISourceGenerator.dll'))
if ($sourceResults -ne $dllResults) { throw 'Source and built DLL differ.' }
$sourceResults -split "\r?\n" | Where-Object { $_.EndsWith(": PASS") }
'Source and DLL agree. Probe host: ' + [Microsoft.CodeAnalysis.Compilation].Assembly.GetName().Version

