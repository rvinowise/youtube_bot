<#
.SYNOPSIS
    Start the scraper detached, elevated (needed for VPN rotation).

.DESCRIPTION
    AmneziaWG's /installtunnelservice requires administrator rights, so the
    scraper must run elevated for `vpn.enabled` to do anything. This script
    checks elevation, builds Release once, and starts the exe detached with
    output redirected to work\run_out.log.

.EXAMPLE
    # from an elevated PowerShell:
    .\tools\run-scraper.ps1 -Topic immortality -Seeds "5Dwcn2_-jBA,KES9yb3o-4Q" -Delay 4000
#>
[CmdletBinding()]
param(
    [string]$Topic = 'immortality',
    [string]$Seeds = '',
    [int]$Related = 20,
    [int]$MaxPerChannel = 3,
    [int]$Delay = 4000,
    [int]$Limit = 0,
    [string]$Exclude = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Error "Not elevated. VPN rotation needs admin: start PowerShell as Administrator and run this script again."
    exit 1
}

Push-Location $root
try {
    dotnet build src\Scraping\Scraping.fsproj -c Release -v q --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    $exe = Join-Path $root 'src\Scraping\bin\Release\net10.0\Scraping.exe'
    $args = @('scan', '--topic', $Topic, '--related', $Related, '--max-per-channel', $MaxPerChannel, '--delay', $Delay, '--limit', $Limit)
    if ($Seeds) { $args += @('--seed-video', $Seeds) }
    if ($Exclude) { $args += @('--exclude', $Exclude) }

    $p = Start-Process -FilePath $exe -ArgumentList $args -WorkingDirectory $root `
        -RedirectStandardOutput (Join-Path $root 'work\run_out.log') `
        -RedirectStandardError (Join-Path $root 'work\run_err.log') `
        -WindowStyle Hidden -PassThru

    "started pid=$($p.Id) topic=$Topic seeds=$Seeds"
    "log: work\run_out.log   progress: work\progress.json"
    "stop: Stop-Process -Name Scraping"
}
finally {
    Pop-Location
}
