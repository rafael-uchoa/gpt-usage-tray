$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskExe = Join-Path $taskRoot 'bin\GPTUsageTray.exe'
$taskFake = Join-Path $PSScriptRoot 'FakeCodex.exe'
$taskMode = Join-Path $PSScriptRoot 'mode.txt'
$taskStatusPath = Join-Path $env:LOCALAPPDATA 'GPTUsageTray\status.json'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $taskCompiler /nologo /target:exe /out:$taskFake /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'FakeCodex.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake helper build failed' }
if (Get-Process GPTUsageTray -ErrorAction SilentlyContinue) { throw 'Quit GPT Usage Tray before integration testing.' }
$taskSavedExe = $env:GPT_USAGE_CODEX_EXE
$taskSavedMode = $env:GPT_USAGE_TEST_MODE
$taskReport = [System.Collections.Generic.List[string]]::new()
function Wait-State([scriptblock]$Predicate) {
    $taskDeadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        try { $taskState = Get-Content -LiteralPath $taskStatusPath -Raw | ConvertFrom-Json; if ((& $Predicate $taskState) -and $taskState.appPid -eq $taskApp.Id) { return $taskState } } catch { }
    } while ((Get-Date) -lt $taskDeadline)
    throw 'Tray state did not match expectation within 15 seconds'
}
function Mode([string]$Mode) {
    [IO.File]::WriteAllText($taskMode, $Mode)
    Start-Process -FilePath $taskExe -ArgumentList '--refresh' -WindowStyle Hidden -Wait
}
try {
    [IO.File]::WriteAllText($taskMode, 'online')
    $env:GPT_USAGE_CODEX_EXE = $taskFake
    $env:GPT_USAGE_TEST_MODE = $taskMode
    $taskApp = Start-Process -FilePath $taskExe -WindowStyle Hidden -PassThru
    $taskState = Wait-State { param($s) $s.authenticated -and $s.remaining -eq 57 -and !$s.stale }
    $taskReport.Add('PASS authenticated weekly percentage: 57')
    Mode 'offline'
    $null = Wait-State { param($s) $s.stale -and $s.authenticated -and !$s.signedOut -and $s.remaining -eq 57 }
    $taskReport.Add('PASS network failure preserves 57 and authenticated state, marks stale')
    Mode 'online'
    $taskState = Wait-State { param($s) !$s.stale -and $s.remaining -eq 57 }
    $taskReport.Add('PASS recovery restores fresh reading')
    $taskOldHelper = $taskState.helperPid
    Stop-Process -Id $taskOldHelper
    Mode 'online'
    $null = Wait-State { param($s) !$s.stale -and $s.remaining -eq 57 -and $s.helperPid -ne $taskOldHelper -and $s.helperPid -gt 0 }
    $taskReport.Add('PASS terminated helper is automatically replaced')
    Mode 'expired'
    $null = Wait-State { param($s) $s.signedOut -and !$s.authenticated -and $null -eq $s.remaining }
    $taskReport.Add('PASS expired authentication clears percentage and selects lock')
    Mode 'signed-out'
    $null = Wait-State { param($s) $s.signedOut -and $s.message -eq 'Not authenticated' }
    $taskReport.Add('PASS signed-out account is explicitly displayed')
    Mode 'empty'
    $null = Wait-State { param($s) $s.authenticated -and !$s.signedOut -and $null -eq $s.remaining }
    $taskReport.Add('PASS missing rate windows never invent a percentage')
    Mode 'online'
    $taskState = Wait-State { param($s) $s.authenticated -and $s.remaining -eq 57 }
    Start-Process -FilePath $taskExe -WindowStyle Hidden -Wait
    if (@(Get-Process GPTUsageTray).Count -ne 1) { throw 'Single-instance check failed' }
    $taskReport.Add('PASS launching twice keeps one tray instance')
} finally {
    Start-Process -FilePath $taskExe -ArgumentList '--quit' -WindowStyle Hidden -Wait
    if ($taskApp) { $null = $taskApp.WaitForExit(5000) }
    $env:GPT_USAGE_CODEX_EXE = $taskSavedExe
    $env:GPT_USAGE_TEST_MODE = $taskSavedMode
}
if ($taskState.helperPid -gt 0 -and (Get-Process -Id $taskState.helperPid -ErrorAction SilentlyContinue)) { throw 'Helper did not exit with tray' }
$taskReport.Add('PASS graceful quit removes owned helper')
$taskReport | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'results.txt')
$taskReport
