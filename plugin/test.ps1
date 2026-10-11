#requires -Version 7.6
$ErrorActionPreference = 'Stop'
$dll = Join-Path $PSScriptRoot 'dist\FanControl.MechrevoPtl.dll'
Add-Type -Path $dll
$references = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName) + $dll
Add-Type -Path (Join-Path $PSScriptRoot 'tests\SessionTests.cs') -ReferencedAssemblies $references -CompilerOptions '/nullable:enable', '/warnaserror+'
$passed = [SessionTests]::Run()
$report = [ordered]@{Time = (Get-Date).ToString('o'); Passed = $passed.Count; Tests = $passed; PluginSHA256 = (Get-FileHash $dll).Hash}
$results = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $results 'session-tests.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 4
