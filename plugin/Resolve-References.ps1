function Resolve-FanControlReferences {
    param([string]$FanControlDirectory, [string]$SystemEventsAssembly)
    $directory = (Resolve-Path -LiteralPath $FanControlDirectory).Path
    $api = Join-Path $directory 'FanControl.Plugins.dll'
    if (-not (Test-Path -LiteralPath $api)) { throw 'FanControl.Plugins.dll is missing from the supplied directory.' }
    if (-not $SystemEventsAssembly) {
        $runtimeRoot = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
        $runtime = Get-ChildItem -LiteralPath $runtimeRoot -Directory |
            Where-Object Name -Match '^10\.\d+\.\d+$' |
            Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if (-not $runtime) { throw 'Install the .NET 10 Desktop Runtime or supply -SystemEventsAssembly.' }
        $SystemEventsAssembly = Join-Path $runtime.FullName 'Microsoft.Win32.SystemEvents.dll'
    }
    if (-not (Test-Path -LiteralPath $SystemEventsAssembly)) { throw 'Microsoft.Win32.SystemEvents.dll is missing.' }
    [pscustomobject]@{Api = $api; SystemEvents = (Resolve-Path -LiteralPath $SystemEventsAssembly).Path; Library = (Join-Path $directory 'FanControl.Library.dll')}
}
