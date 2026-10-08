<#
.SYNOPSIS
  Сборка Twitch Drops Miner .NET в один exe-файл.

.PARAMETER Mode
  All           — собрать оба варианта (по умолчанию).
  SelfContained — exe со встроенной средой .NET (~67 МБ), ничего устанавливать не нужно.
  Framework     — маленький exe (~1.5 МБ), нужен установленный .NET 10 Desktop Runtime.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build.ps1
  powershell -ExecutionPolicy Bypass -File build.ps1 -Mode Framework
#>
param(
    [ValidateSet('All', 'SelfContained', 'Framework')]
    [string]$Mode = 'All'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'Не найден .NET SDK. Установите его командой:' -ForegroundColor Red
    Write-Host '  winget install Microsoft.DotNet.SDK.10'
    exit 1
}

$modes = if ($Mode -eq 'All') { @('SelfContained', 'Framework') } else { @($Mode) }
foreach ($m in $modes) {
    $selfContained = if ($m -eq 'SelfContained') { 'true' } else { 'false' }
    $out = Join-Path $PSScriptRoot "dist\$m"
    Write-Host "Сборка ($m) -> $out" -ForegroundColor Cyan
    dotnet publish TwitchDropsMiner.csproj -c Release -r win-x64 --self-contained $selfContained -o $out
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $exe = Join-Path $out 'TwitchDropsMiner.exe'
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Готово: $exe ($size МБ)" -ForegroundColor Green
}
