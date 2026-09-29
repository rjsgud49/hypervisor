# Lab status
$ErrorActionPreference = "Continue"
Set-Location $PSScriptRoot

Write-Host "=== docker compose ===" -ForegroundColor Cyan
docker compose ps 2>&1

Write-Host "`n=== channel 127.0.0.1:19800 ===" -ForegroundColor Cyan
try {
    $c = New-Object System.Net.Sockets.TcpClient
    $iar = $c.BeginConnect("127.0.0.1", 19800, $null, $null)
    $ok = $iar.AsyncWaitHandle.WaitOne(500, $false)
    if ($ok -and $c.Connected) {
        Write-Host "OPEN"
    } else {
        Write-Host "CLOSED"
    }
    $c.Close()
} catch {
    Write-Host "CLOSED ($($_.Exception.Message))"
}

Write-Host "`n=== lab containers ===" -ForegroundColor Cyan
docker ps -a --filter "label=joy.hypervisor.lab=1" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}" 2>&1
