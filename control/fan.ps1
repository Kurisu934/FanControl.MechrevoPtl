param(
    [ValidateSet('Read','Pwm','Rpm','Auto')][string]$Mode='Read',
    [Nullable[double]]$Percent=$null,
    [Nullable[int]]$Rpm0=$null,
    [Nullable[int]]$Rpm1=$null,
    [ValidateRange(1,300)][int]$Seconds=30
)
$ErrorActionPreference='Stop'
$logDir=Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logPath=Join-Path $logDir ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)+'.json')
$result=[ordered]@{Time=(Get-Date).ToString('o');Mode=$Mode;State='Starting';Error=$null;Samples=@();RestoreSent=$false}
function Save-Result {$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $logPath -Encoding UTF8}
Save-Result
$ownsMutex=$false;$ec=$null;$mutex=$null
try {
    if($Mode -eq 'Pwm' -and ($null -eq $Percent -or $Percent -lt 0 -or $Percent -gt 100 -or [double]::IsNaN($Percent))){throw 'Pwm requires -Percent 0..100.'}
    if($Mode -eq 'Rpm' -and ($null -eq $Rpm0 -and $null -eq $Rpm1)){throw 'Rpm requires -Rpm0 and/or -Rpm1.'}
    foreach($rpm in @($Rpm0,$Rpm1)){if($null -ne $rpm -and ($rpm -lt 0 -or $rpm -gt 8000)){throw 'RPM arguments must be between 0 and 8000.'}}
    if($Mode -ne 'Rpm' -and ($null -ne $Rpm0 -or $null -ne $Rpm1)){throw 'RPM arguments are only for Rpm mode.'}
    if($Mode -ne 'Pwm' -and $null -ne $Percent){throw 'Percent is only for Pwm mode.'}
    $board=Get-CimInstance Win32_BaseBoard
    $bios=Get-CimInstance Win32_BIOS
    if($board.Product -notmatch 'P916F-PTL' -or $bios.SMBIOSBIOSVersion -ne '1.01'){throw 'This protocol is validated only on P916F-PTL BIOS 1.01.'}
    $driverPath=Join-Path $env:SystemRoot 'System32\drivers\inpoutx64.sys'
    $expected='F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B'
    if((Get-FileHash -LiteralPath $driverPath -Algorithm SHA256).Hash -ne $expected){throw 'Installed I/O driver differs from the verified version.'}
    if(-not ('MechrevoPtlEc' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'MechrevoPtlEc.cs')}
    $mutex=[Threading.Mutex]::new($false,'Local\MechrevoPtlFanDiagnostic')
    try {$ownsMutex=$mutex.WaitOne($(if($Mode -eq 'Auto'){10000}else{0}))} catch [Threading.AbandonedMutexException] {$ownsMutex=$true}
    if(-not $ownsMutex){throw 'Another fan control operation is active.'}
    $ec=[MechrevoPtlEc]::new()
    $result.StartupDiscardedBytes=$ec.SynchronizeStartup()
    function Get-Sample([string]$Stage){[ordered]@{Time=(Get-Date).ToString('o');Stage=$Stage;Fan0Rpm=$ec.ReadRpm(0);Fan1Rpm=$ec.ReadRpm(1)}}
    $result.OriginalMode=$ec.ReadMode();$result.OriginalCustomFlag=$ec.ReadCustomFlag()
    if($result.OriginalMode -lt 1 -or $result.OriginalMode -gt 4){throw 'EC mode response does not match the verified protocol.'}
    if($Mode -eq 'Auto'){$ec.RestoreAutomatic();$result.RestoreSent=$true;$result.RestoreTime=(Get-Date).ToString('o')}
    $result.Samples+=Get-Sample 'Before'
    if($Mode -eq 'Pwm' -or $Mode -eq 'Rpm'){
        if($result.OriginalCustomFlag -ne 0){throw 'EC is already using a custom curve; this temporary tool expects automatic control.'}
        $result.State='Controlling';Save-Result
        try {
            if($Mode -eq 'Pwm'){$result.RawPwm=$ec.SetPwmPercent($Percent);$result.EffectivePercent=$result.RawPwm*100.0/255}
            if($Mode -eq 'Rpm'){
                if($null -ne $Rpm0){$result.TargetRpm0=$ec.SetRpm(0,$Rpm0)}
                if($null -ne $Rpm1){$result.TargetRpm1=$ec.SetRpm(1,$Rpm1)}
            }
            $timer=[Diagnostics.Stopwatch]::StartNew();Save-Result
            while($timer.Elapsed.TotalSeconds -lt $Seconds){Start-Sleep -Milliseconds 900;$sample=Get-Sample 'Manual';$result.Samples+=$sample;Save-Result;[pscustomobject]$sample|Format-Table -AutoSize}
        } finally {$ec.RestoreAutomatic();$result.RestoreSent=$true;$result.RestoreTime=(Get-Date).ToString('o');Save-Result}
        $result.Samples+=Get-Sample 'AfterRestore'
    }
    $result.FinalMode=$ec.ReadMode();$result.FinalCustomFlag=$ec.ReadCustomFlag();$result.State='Finished'
} catch {$result.State='Failed';$result.Error=$_.Exception.ToString()}
finally {
    if($ec){$ec.Dispose()}
    if($ownsMutex){$mutex.ReleaseMutex()}
    if($mutex){$mutex.Dispose()}
    Save-Result
}
$result|ConvertTo-Json -Depth 6
Write-Output ('Log: '+$logPath)
if($result.State -eq 'Failed'){exit 1}
