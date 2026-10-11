#requires -Version 7.6
param(
    [Parameter(Mandatory)][string]$FanControlDirectory,
    [string]$SystemEventsAssembly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-References.ps1')
$resolved = Resolve-FanControlReferences $FanControlDirectory $SystemEventsAssembly
$api = $resolved.Api
$SystemEventsAssembly = $resolved.SystemEvents
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
$sourcePaths = @(
    (Join-Path $PSScriptRoot 'AssemblyInfo.cs'),
    (Join-Path $PSScriptRoot 'Protocol.cs'),
    (Join-Path $PSScriptRoot 'Plugin.cs'),
    (Join-Path $PSScriptRoot '..\control\MechrevoPtlEc.cs')
)
$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion(
    [Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Latest)
$trees = [Microsoft.CodeAnalysis.SyntaxTree[]]@(
    foreach ($path in $sourcePaths) {
        [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
            [IO.File]::ReadAllText($path), $parseOptions, [IO.Path]::GetFullPath($path))
    }
)
$referencePaths = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName) + @($api, $SystemEventsAssembly)
$references = [Microsoft.CodeAnalysis.MetadataReference[]]@(
    foreach ($path in $referencePaths) { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path) }
)
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
    [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithOptimizationLevel(
    [Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithNullableContextOptions(
    [Microsoft.CodeAnalysis.NullableContextOptions]::Enable).WithPlatform(
    [Microsoft.CodeAnalysis.Platform]::X64).WithDeterministic($true)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('FanControl.MechrevoPtl', $trees, $references, $options)
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$output = Join-Path $dist 'FanControl.MechrevoPtl.dll'
$temporary = Join-Path $dist ([guid]::NewGuid().ToString('N') + '.tmp')
try {
    $stream = [IO.File]::Create($temporary)
    try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
    $issues = @($result.Diagnostics | Where-Object Severity -in @('Warning', 'Error'))
    if (-not $result.Success -or $issues.Count) { throw ($issues -join [Environment]::NewLine) }
    Move-Item -LiteralPath $temporary -Destination $output -Force
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
Get-FileHash -LiteralPath $output -Algorithm SHA256
