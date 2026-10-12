#requires -Version 7.6
param(
    [switch]$ExerciseControl,
    [Parameter(Mandatory)][string]$FanControlDirectory,
    [string]$SystemEventsAssembly
)
$ErrorActionPreference = 'Stop'
if (Get-Process FanControl -ErrorAction SilentlyContinue) { throw 'Exit FanControl before this test.' }
. (Join-Path $PSScriptRoot 'Resolve-References.ps1')
$resolved = Resolve-FanControlReferences $FanControlDirectory $SystemEventsAssembly
$api = $resolved.Api
$dll = Join-Path $PSScriptRoot 'dist\FanControl.MechrevoPtl.dll'
Add-Type -Path $api, $resolved.SystemEvents, $dll, $resolved.Library
$references = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName) + @($api, $resolved.Library, $dll)
Add-Type -Path (Join-Path $PSScriptRoot 'tests\HostStubs.cs') -ReferencedAssemblies $references
$logger = [SmokeLogger]::new()
$plugin = [FanControl.MechrevoPtl.Plugin]::new($logger)
$result = [ordered]@{
    Time = (Get-Date).ToString('o'); State = 'Starting'; ExerciseControl = $ExerciseControl.IsPresent
    PluginSHA256 = (Get-FileHash -LiteralPath $dll).Hash; Samples = @(); Error = $null; CloseSucceeded = $false
}
$container = $null
function Sample([string]$stage) {
    $plugin.Update()
    $result.Samples += [ordered]@{
        Time = (Get-Date).ToString('o'); Stage = $stage
        Pwm = $container.ControlSensors[0].Value
        Fan0Rpm = $container.FanSensors[0].Value; Fan1Rpm = $container.FanSensors[1].Value
    }
}
$testError = $null
try {
    $plugin.Initialize()
    $container = [SmokeContainer]::new(); $plugin.Load($container)
    if ($container.ControlSensors.Count -ne 1 -or $container.FanSensors.Count -ne 2) { throw 'Unexpected sensors.' }
    $result.ControlId = $container.ControlSensors[0].Id
    $result.FanIds = @($container.FanSensors | ForEach-Object Id)
    $wrappedControl = [FanControl.Domain.BackendProviders.Plugin.PluginControlSensor]::new($container.ControlSensors[0], $plugin.Name)
    $wrappedFan = [FanControl.Domain.BackendProviders.Plugin.PluginSensor]::new($container.FanSensors[0], $plugin.Name, [FanControl.Domain.BackendSensorType]::RPM)
    $result.PairedFanIdentifier = $wrappedControl.PairedFanSensor.Identifier
    if ($result.PairedFanIdentifier -ne $wrappedFan.Identifier) { throw 'Host automatic RPM pairing does not match.' }
    Sample 'ReadOnly'
    if ($null -ne $container.ControlSensors[0].Value) { throw 'Read-only initialization activated PWM.' }
    if ($ExerciseControl) {
        $manualConfig = [HostManualChecks]::Config(50)
        $hostControl = [HostManualChecks]::Create($wrappedControl, $manualConfig)
        try {
        foreach ($percent in @(50, 75)) {
            $manualConfig.ManualControlValue = $percent
            $hostControl.UpdateConfig($manualConfig)
            for ($i = 0; $i -lt 8; $i++) { $hostControl.Update(); Start-Sleep -Seconds 1; Sample "Pwm$percent" }
        }
        $at50 = @($result.Samples | Where-Object Stage -eq 'Pwm50')[-1]
        $at75 = @($result.Samples | Where-Object Stage -eq 'Pwm75')[-1]
        if ($at50.Fan0Rpm -lt 3000 -or $at50.Fan1Rpm -lt 3000 -or
            $at75.Fan0Rpm -lt ($at50.Fan0Rpm + 500) -or $at75.Fan1Rpm -lt ($at50.Fan1Rpm + 500)) {
            throw 'RPM did not respond as expected to the verified 50/75% settings.'
        }
        } finally { $hostControl.Dispose() }
        $result.HostManualControl = 'Passed: Control.Update at 50% and 75%'
        $container.ControlSensors[0].Reset()
        Sample 'AfterReset'
        if ($null -ne $container.ControlSensors[0].Value) { throw 'Reset retained a manual value.' }
        $container.ControlSensors[0].Set(50)
        Start-Sleep -Seconds 3; Sample 'BeforeClose'
    }
    $plugin.Close(); $plugin.Close()
    $plugin.Initialize()
    $container = [SmokeContainer]::new(); $plugin.Load($container)
    Sample 'ReopenedReadOnly'
    if ($null -ne $container.ControlSensors[0].Value) { throw 'Refresh retained manual control.' }
    if ($ExerciseControl) { Start-Sleep -Seconds 8; Sample 'AfterCloseAutomatic' }
    $results = Join-Path $PSScriptRoot 'results'
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    $hostLogger = [FanControl.Domain.FileLogger]::new((Join-Path $results 'host-backend'))
    $backend = [FanControl.Domain.BackendProviders.Plugin.PluginBackendProvider]::new([FanControl.Plugins.IPlugin3]$plugin, $hostLogger)
    try {
        if (-not $backend.Open()) { throw 'FanControl backend failed to open the plugin.' }
        $backend.Update()
        $result.HostBackendSensors = @($backend.AllSensors | Select-Object Identifier, Name, Value)
        if (@($backend.AllSensors).Count -ne 3 -or @($backend.ControlSensors).Count -ne 1) { throw 'FanControl backend did not expose the expected sensors.' }
    } finally { $backend.Close() }
    $result.State = 'Passed'
} catch {
    $testError = $_; $result.State = 'Failed'; $result.Error = $_.Exception.ToString()
} finally {
    try { $plugin.Close(); $result.CloseSucceeded = $true }
    catch { $result.State = 'Failed'; $result.CloseError = $_.Exception.ToString(); if (-not $testError) { $testError = $_ } }
    $result.Messages = @($logger.Messages)
    $results = Join-Path $PSScriptRoot 'results'
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    $file = Join-Path $results ('hardware-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $file -Encoding UTF8
}
$result | ConvertTo-Json -Depth 5
if ($testError) { throw $testError }
