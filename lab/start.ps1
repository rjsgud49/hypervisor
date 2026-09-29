# Lab start — Sub OS up, Windows Main unchanged
param(
    [switch]$Build
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Test-Docker {
    try {
        docker info 1>$null 2>$null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

if (-not (Test-Docker)) {
    Write-Host "Docker Desktop is not running. Start it, then re-run .\start.ps1" -ForegroundColor Yellow
    exit 1
}

Write-Host "[lab] starting sub-os (reversible; stop.ps1 restores)..." -ForegroundColor Cyan
if ($Build) {
    docker compose build --pull
}
docker compose up -d --build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$ok = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $c.Connect("127.0.0.1", 19800)
        $c.Close()
        $ok = $true
        break
    } catch {
        Start-Sleep -Milliseconds 500
    }
}

if (-not $ok) {
    Write-Host "[lab] channel port 19800 not ready. Check: docker compose logs" -ForegroundColor Red
    exit 1
}

Write-Host "[lab] READY" -ForegroundColor Green
Write-Host "  channel : 127.0.0.1:19800"
Write-Host "  ping    : python main\channel_ping.py"
Write-Host "  h3      : .\h3-smoke.ps1"
Write-Host "  stop    : .\stop.ps1"
