<#
.SYNOPSIS
    打安装包：把发布出来的单文件 EXE 复制成带安装界面的 Setup（同一个二进制）。

.DESCRIPTION
    安装界面本身就在 TidyFlow.exe 里：文件名以 TidyFlow-Setup 开头（或带 --install）时进安装模式。
    所以安装包 = 一份改名副本，不需要额外运行时，体积与主程序一致。

      安装（界面） : TidyFlow-Setup-0.6.0.exe
      静默安装     : TidyFlow-Setup-0.6.0.exe --install --silent --dir="D:\Apps\DeskFlow" --autostart=0 --shortcuts=1
      卸载（界面） : "%LOCALAPPDATA%\Programs\DeskFlow\TidyFlow.exe" --uninstall
      静默卸载     : "%LOCALAPPDATA%\Programs\DeskFlow\TidyFlow.exe" --uninstall --silent --keepdata=1

.PARAMETER Msi
    额外产出企业部署用的 MSI（需要 wix 工具）。

.EXAMPLE
    powershell -File build/pack-installer.ps1
    powershell -File build/pack-installer.ps1 -Msi
#>
[CmdletBinding()]
param(
    [string] $Version = '0.8.10',
    [switch] $SkipExe,
    [switch] $Msi
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $exe = Join-Path $root 'dist\TidyFlow.exe'
    if (-not $SkipExe) {
        & (Join-Path $PSScriptRoot 'pack.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'pack.ps1 失败' }
    }

    if (-not (Test-Path $exe)) { throw "找不到 $exe" }

    $setup = Join-Path $root "dist\TidyFlow-Setup-$Version.exe"
    Copy-Item $exe $setup -Force
    $mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host ""
    Write-Host "安装包: $setup ($mb MB)" -ForegroundColor Green
    Write-Host ('双击安装（有界面）: "' + $setup + '"')
    Write-Host ('静默安装:           "' + $setup + '" --install --silent --dir="%LOCALAPPDATA%\Programs\DeskFlow" --autostart=1 --shortcuts=1')
    Write-Host ('卸载:               "%LOCALAPPDATA%\Programs\DeskFlow\TidyFlow.exe" --uninstall')

    if ($Msi) {
        $wix = Get-Command wix -ErrorAction SilentlyContinue
        if (-not $wix) { throw '找不到 wix，先执行: dotnet tool install --global wix --version 4.0.6' }

        $msiOut = Join-Path $root "dist\TidyFlow-Setup-$Version.msi"
        wix build 'installer\DeskFlow.wxs' -arch x64 -d "SourceExe=$exe" -o $msiOut
        if ($LASTEXITCODE -ne 0) { throw "wix build 失败（退出码 $LASTEXITCODE）" }
        Write-Host "MSI: $msiOut" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}










