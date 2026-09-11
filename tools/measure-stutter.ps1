#requires -Version 5.1
<#
Measures system-wide hitching objectively, so "it feels laggy" becomes a number.

How it works: sleep 10 ms in a loop and measure how long the sleep ACTUALLY took. On a
healthy machine the overshoot stays small. Every time the whole system stalls - a driver
DPC storm, a stuck capture pipeline, a scan loop - this thread is stalled too, and the
overshoot shows it. It measures the machine, not any particular process, which is exactly
what is needed when the cause outlives the process that triggered it.

Usage:
    powershell -ExecutionPolicy Bypass -File measure-stutter.ps1 [-Seconds 20] [-Label "before"]

Run it twice with the same duration - once in the suspected-bad state, once in the good
state - and compare. A visible hitch shows up as p99/max in the tens of milliseconds and a
non-zero "hitches > 50 ms" count.
#>
param(
    [int]$Seconds = 20,
    [string]$Label = ""
)

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$samples = New-Object System.Collections.Generic.List[double]
$deadline = (Get-Date).AddSeconds($Seconds)

Write-Host ("Messe {0} s{1} ..." -f $Seconds, $(if ($Label) { " [$Label]" } else { "" })) -ForegroundColor Cyan

while ((Get-Date) -lt $deadline) {
    $t0 = $sw.Elapsed.TotalMilliseconds
    Start-Sleep -Milliseconds 10
    $samples.Add($sw.Elapsed.TotalMilliseconds - $t0)
}

$sorted = $samples | Sort-Object
$n = $sorted.Count
function Pct([double]$p) { $sorted[[Math]::Min($n - 1, [int][Math]::Floor($p * $n))] }

$median = Pct 0.50
$p99    = Pct 0.99
$max    = $sorted[$n - 1]
$over50 = ($samples | Where-Object { $_ -gt 50 }).Count
$over100= ($samples | Where-Object { $_ -gt 100 }).Count

Write-Host ""
Write-Host ("Proben          : {0}" -f $n)
Write-Host ("Median          : {0:N1} ms   (erwartet ~10-16)" -f $median)
Write-Host ("p99             : {0:N1} ms" -f $p99)
Write-Host ("Maximum         : {0:N1} ms" -f $max)
Write-Host ("Haenger > 50 ms : {0}" -f $over50)  -ForegroundColor $(if ($over50 -gt 0) { "Yellow" } else { "Green" })
Write-Host ("Haenger > 100 ms: {0}" -f $over100) -ForegroundColor $(if ($over100 -gt 0) { "Red" } else { "Green" })
Write-Host ""
if ($over100 -gt 0) {
    Write-Host "=> Deutliche System-Haenger messbar." -ForegroundColor Red
} elseif ($over50 -gt 2) {
    Write-Host "=> Leichte Haenger messbar." -ForegroundColor Yellow
} else {
    Write-Host "=> Unauffaellig." -ForegroundColor Green
}
