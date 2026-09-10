$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Path 'bin' -Force | Out-Null
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /win32manifest:app.manifest /out:bin\GPTUsageTray.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Core.dll Program.cs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }
