# Lab stop — remove Sub container/network; Windows returns to normal use
param(
    [switch]$Purge
)

$ErrorActionPreference = "Continue"
Set-Location $PSScriptRoot

Write-Host "[lab] stopping sub-os..." -ForegroundColor Cyan

if (Get-Command docker -ErrorAction SilentlyContinue) {
    docker compose down --remove-orphans
    if ($Purge) {
        Write-Host "[lab] purge local compose images..." -ForegroundColor Yellow
        docker compose down --rmi local --remove-orphans
    }
}

Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -match 'channel_ping\.py' } |
    ForEach-Object {
        Write-Host "[lab] stopping main agent PID $($_.ProcessId)"
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }

$left = docker ps -a --filter "name=hv-lab-sub" -q 2>$null
if ($left) {
    Write-Host "[lab] WARN: container still listed; try: docker rm -f hv-lab-sub" -ForegroundColor Yellow
} else {
    Write-Host "[lab] STOPPED — Windows Main unchanged (no boot/disk changes)." -ForegroundColor Green
}

if (-not $Purge) {
    Write-Host "  (images kept for faster next start; use .\stop.ps1 -Purge to delete images)"
}
