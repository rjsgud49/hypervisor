# H3 smoke: Sub marker must NOT appear in Windows process list
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$running = docker compose ps --status running -q 2>$null
if (-not $running) {
    Write-Host "Sub is not running. Start with .\start.ps1 first." -ForegroundColor Yellow
    exit 1
}

Write-Host "[H3] ensuring marker inside sub..." -ForegroundColor Cyan
docker compose exec -T sub-os sh -c 'pgrep -f hv_sub_marker >/dev/null || (cp -f /bin/sleep /tmp/hv_sub_marker; /tmp/hv_sub_marker 86400 &)' | Out-Null

$inside = docker compose exec -T sub-os sh -c 'pgrep -af hv_sub_marker || true'
Write-Host "[H3] inside Sub:`n$inside"

Write-Host "[H3] Windows tasklist search for hv_sub_marker..." -ForegroundColor Cyan
$hit = tasklist /FI "IMAGENAME eq hv_sub_marker*" 2>$null | Select-String -Pattern "hv_sub_marker"
# also scan command lines if possible
$cmdHit = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'hv_sub_marker' -or ($_.CommandLine -and $_.CommandLine -match 'hv_sub_marker') }

if ($hit -or $cmdHit) {
    Write-Host "FAIL: marker visible on Windows (unexpected for Lab H3)" -ForegroundColor Red
    $cmdHit | Format-Table ProcessId, Name, CommandLine -AutoSize
    exit 1
}

Write-Host "PASS: hv_sub_marker not visible to Windows processes (Lab H3 partial)." -ForegroundColor Green
Write-Host "Note: docker.exe / vmmem may still be visible — H1 is relaxed in Lab Mode." -ForegroundColor DarkYellow
