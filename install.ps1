$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$taskInstall = Join-Path $env:LOCALAPPDATA 'Programs\GPTUsageTray'
$taskSource = Join-Path $PSScriptRoot 'bin\GPTUsageTray.exe'
$taskTarget = Join-Path $taskInstall 'GPTUsageTray.exe'
New-Item -ItemType Directory -Path $taskInstall -Force | Out-Null
if (Get-Process GPTUsageTray -ErrorAction SilentlyContinue) {
    Start-Process -FilePath $taskSource -ArgumentList '--quit' -WindowStyle Hidden -Wait
    Get-Process GPTUsageTray -ErrorAction SilentlyContinue | Wait-Process -Timeout 10
}
Copy-Item -LiteralPath $taskSource -Destination $taskTarget -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $taskInstall -Force
$taskRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $taskRunKey -Name 'GPTUsageTray' -Value ('"' + $taskTarget + '"') -PropertyType String -Force | Out-Null
# Preserve the old entry so replacement is reversible. Do not uninstall CodexBar.
$taskOldStartup = Get-ItemPropertyValue -Path $taskRunKey -Name 'CodexBar' -ErrorAction SilentlyContinue
if ($taskOldStartup) {
    $taskBackup = Join-Path $env:LOCALAPPDATA 'GPTUsageTray\codexbar-startup-backup.txt'
    if (!(Test-Path -LiteralPath $taskBackup)) { [IO.File]::WriteAllText($taskBackup, $taskOldStartup) }
    Remove-ItemProperty -Path $taskRunKey -Name 'CodexBar'
}
$taskShell = New-Object -ComObject WScript.Shell
$taskLinkPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'GPT Usage Tray.lnk'
$taskLink = $taskShell.CreateShortcut($taskLinkPath)
$taskLink.TargetPath = $taskTarget
$taskLink.WorkingDirectory = $taskInstall
$taskLink.Description = 'Remaining Codex usage in the Windows system tray'
$taskLink.Save()
Start-Process -FilePath $taskTarget -WorkingDirectory $taskInstall -WindowStyle Hidden
# Ask Windows to keep this application's icon in the visible tray.
# Only touch the entry whose executable exactly matches this installation.
$taskDeadline = (Get-Date).AddSeconds(5)
do {
    $taskOwnIcons = @(Get-ChildItem 'HKCU:\Control Panel\NotifyIconSettings' -ErrorAction SilentlyContinue | Where-Object { (Get-ItemProperty -LiteralPath $_.PSPath).ExecutablePath -eq $taskTarget })
    if ($taskOwnIcons.Count -gt 0) {
        foreach ($taskOwnIcon in $taskOwnIcons) { New-ItemProperty -LiteralPath $taskOwnIcon.PSPath -Name 'IsPromoted' -PropertyType DWord -Value 1 -Force | Out-Null }
        break
    }
    Start-Sleep -Milliseconds 200
} while ((Get-Date) -lt $taskDeadline)
Write-Output ('Installed and started: ' + $taskTarget)
