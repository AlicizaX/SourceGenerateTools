param(
    [string]$UnityEditor,
    [string]$UnityProject
)
$ErrorActionPreference = 'Stop'
$generatorRoot = $PSScriptRoot
$outputDirectory = Join-Path $generatorRoot 'bin/Release'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

if (!$UnityEditor) {
    dotnet build (Join-Path $generatorRoot 'SourceGenerator/UISourceGenerator.csproj') -c Release -o $outputDirectory
} else {
    # SDK-free build, using the Unity compiler but Roslyn 3.9 and netstandard2.0 reference assemblies.
    $packageCache = Join-Path ([IO.Path]::GetTempPath()) 'alicizax-ui-generator-build'
    New-Item -ItemType Directory -Force -Path $packageCache | Out-Null
    $packages = @{
        'microsoft.codeanalysis.common' = '3.9.0'
        'microsoft.codeanalysis.csharp' = '3.9.0'
        'system.collections.immutable' = '5.0.0'
        'netstandard.library' = '2.0.3'
    }
    foreach ($name in $packages.Keys) {
        $version = $packages[$name]
        $destination = Join-Path $packageCache "$name.$version"
        if (!(Test-Path -LiteralPath $destination)) {
            $archive = Join-Path $packageCache "$name.zip"
            Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$name/$version/$name.$version.nupkg" -OutFile $archive
            Expand-Archive -LiteralPath $archive -DestinationPath $destination
        }
    }
    $references = @((Get-ChildItem -LiteralPath (Join-Path $packageCache 'netstandard.library.2.0.3/build/netstandard2.0/ref') -Filter '*.dll').FullName)
    $references += Join-Path $packageCache 'microsoft.codeanalysis.common.3.9.0/lib/netstandard2.0/Microsoft.CodeAnalysis.dll'
    $references += Join-Path $packageCache 'microsoft.codeanalysis.csharp.3.9.0/lib/netstandard2.0/Microsoft.CodeAnalysis.CSharp.dll'
    $references += Join-Path $packageCache 'system.collections.immutable.5.0.0/lib/netstandard2.0/System.Collections.Immutable.dll'
    $arguments = @('/nologo', '/target:library', '/nostdlib+', '/langversion:9', '/nullable:enable', '/deterministic+', '/optimize+', ('/out:"' + (Join-Path $outputDirectory 'UISourceGenerator.dll') + '"'))
    $arguments += $references | ForEach-Object { '/reference:"' + $_ + '"' }
    $arguments += (Get-ChildItem -LiteralPath (Join-Path $generatorRoot 'SourceGenerator/Generator') -Filter '*.cs' | Sort-Object Name | ForEach-Object { '"' + $_.FullName + '"' })
    $responseFile = Join-Path $packageCache 'build.rsp'
    $arguments | Set-Content -LiteralPath $responseFile -Encoding utf8
    $compiler = Join-Path (Split-Path -Parent $UnityEditor) 'Data/DotNetSdkRoslyn/csc.dll'
    dotnet $compiler ('@' + $responseFile)
}
if ($LASTEXITCODE -ne 0) { throw 'Generator build failed.' }
if ($UnityProject) {
    $plugin = Join-Path $UnityProject 'Packages/com.alicizax.unity.framework/Plugins/SourceGenerators/UI/UISourceGenerator.dll'
    Copy-Item -LiteralPath (Join-Path $outputDirectory 'UISourceGenerator.dll') -Destination $plugin
    Get-FileHash -LiteralPath $plugin -Algorithm SHA256
}
