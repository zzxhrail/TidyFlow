<#
.SYNOPSIS
    把 DeskFlow 打包成绿色单文件 EXE。

.DESCRIPTION
    默认 --self-contained + PublishSingleFile：目标机器不需要装 .NET 运行时，
    双击即用、可放 U 盘。体积约 70-90 MB（WPF 桌面运行时本身就有这么大），
    首次启动会解压到 %TEMP%\.net\ 下，第二次起秒开。

.EXAMPLE
    pwsh -File build/pack.ps1
    pwsh -File build/pack.ps1 -FrameworkDependent   # 体积 ~2MB，但要求目标机有 .NET 10 桌面运行时
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [switch] $FrameworkDependent,
    [string] $Output = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $project = Join-Path $root 'src/DeskFlow.App/DeskFlow.App.csproj'
    $selfContained = (-not $FrameworkDependent)

    Write-Host "发布 $project" -ForegroundColor Cyan
    Write-Host "  配置: $Configuration  运行时: $Runtime  自包含: $selfContained"

    $publishArgs = @(
        'publish', $project,
        '-c', $Configuration,
        '-r', $Runtime,
        "--self-contained=$($selfContained.ToString().ToLowerInvariant())",
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=none',
        '-p:SatelliteResourceLanguages=en',
        '-o', $Output
    )

    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

    $exe = Join-Path $root "$Output/DeskFlow.exe"
    if (-not (Test-Path $exe)) { throw "没有生成 $exe" }

    $sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ""
    Write-Host "产物: $exe ($sizeMb MB)" -ForegroundColor Green
    Write-Host "自检: $exe --run <目录> --out <输出目录>"
}
finally {
    Pop-Location
}