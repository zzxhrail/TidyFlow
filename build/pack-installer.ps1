<#
.SYNOPSIS
    打 MSI 安装包（WiX v7，per-user，无需管理员）。

.DESCRIPTION
    1) 先跑 pack.ps1 出单文件 EXE；
    2) 用 wix build 把 EXE + 快捷方式打成 dist\DeskFlow-Setup-<版本>.msi。
    安装位置 %LOCALAPPDATA%\Programs\DeskFlow，用户数据仍在 %LOCALAPPDATA%\DeskFlow（卸载不删数据）。

.EXAMPLE
    powershell -File build/pack-installer.ps1
#>
[CmdletBinding()]
param(
    [string] $Version = '0.5.1',
    [switch] $SkipExe
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $SkipExe) {
        & (Join-Path $PSScriptRoot 'pack.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'pack.ps1 失败' }
    }

    $wix = Get-Command wix -ErrorAction SilentlyContinue
    if (-not $wix) {
        throw '找不到 wix 命令。先执行: dotnet tool install --global wix'
    }

    $out = Join-Path $root "dist\DeskFlow-Setup-$Version.msi"
    $exe = Join-Path $root 'dist\DeskFlow.exe'
    if (-not (Test-Path $exe)) { throw "找不到 $exe，先跑 pack.ps1" }

    wix build "installer\DeskFlow.wxs" -arch x64 -d "SourceExe=$exe" -o $out
    if ($LASTEXITCODE -ne 0) { throw "wix build 失败（退出码 $LASTEXITCODE）" }

    $mb = [math]::Round((Get-Item $out).Length / 1MB, 1)
    Write-Host ""
    Write-Host "安装包: $out ($mb MB)" -ForegroundColor Green
    Write-Host ('静默安装: msiexec /i "' + $out + '" /qn')
    Write-Host ('静默卸载: msiexec /x "' + $out + '" /qn')
}
finally {
    Pop-Location
}
