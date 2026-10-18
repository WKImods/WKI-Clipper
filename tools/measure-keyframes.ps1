<#
.SYNOPSIS
  Measures whether forcing keyframes at the replay buffer's segment boundaries costs quality or bitrate.

.DESCRIPTION
  The buffer cuts segments with ffmpeg's segment muxer, which can only cut at a keyframe. With the encoder's own
  GOP the segments drift past their nominal length; forcing a keyframe at every boundary (or a fixed GOP) makes every
  segment exactly one segment long - if the encoder honours it and the extra IDR frames do not hurt. This script
  encodes one source clip four ways with the buffer's own encoder arguments and compares them:

    A  EncoderDefault                 (today)
    B  ForcedAtSegmentBoundary        (-force_key_frames expr + forced-IDR flag + -segment_time_delta)
    C  ForcedBoundaryWithSegmentGop   (B + -g fps*segment seconds: the encoder's own keyframes coincide with the forced ones)
    D  FixedOneSecondGop              (-g fps + -segment_time_delta)

  Per variant two encodes of the same excerpt: (a) segmented exactly like the buffer, (b) one continuous file
  (frame-aligned with the source, used for the quality metrics).

  Acceptance (variant vs A), from the Phase 4 spec:
    (1) >= 95 % of the segments (the last one excluded) last segment seconds +- 1 frame
    (2) VMAF drop <= 0.5   (no libvmaf: SSIM-All drop <= 0.002 AND PSNR drop <= 0.3 dB)
    (3) bitrate within +-3 % of A
  Winner: C if it passes and has <= B's IDRs per minute; else B; else D; none -> keep EncoderDefault.

  Needs PowerShell 5.1 and an ffmpeg with the buffer's encoder (h264_amf etc.); no admin rights. Reads the app's
  settings.json read-only; writes only below -OutDir (never into the clips folder).

.PARAMETER Source      Source clip. Default: the newest Clip_*.mp4 under the clips folder that is long enough.
.PARAMETER SettingsPath settings.json to read codec/fps/quality/clips folder from. Default: %APPDATA%\WKI_Clipper\settings.json.
.PARAMETER Ffmpeg      ffmpeg.exe. Default: next to the installed app, the WinGet Gyan.FFmpeg package, then PATH.
.PARAMETER OutDir      Where the encodes and keyframes-report.json go. Default: %TEMP%\wki_keyframes.
.PARAMETER Bitrate     Video bits/s. Default: computed from the settings like FFmpegCommandBuilder.EffectiveBitrate.
.PARAMETER Codec       Encoder. Default: Video.Codec from the settings.
.PARAMETER SegmentSeconds Segment length of the buffer (default 5).
.PARAMETER Skip        Seconds cut off the start of the source (default 2; the spec's 10 needs a clip over 70 s).
.PARAMETER Length      Seconds encoded per variant (default 60, reduced to what the source has).
.PARAMETER Variants    Subset of A,B,C,D (default all).
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$SettingsPath = (Join-Path $env:APPDATA 'WKI_Clipper\settings.json'),
    [string]$Ffmpeg,
    [string]$OutDir = (Join-Path $env:TEMP 'wki_keyframes'),
    [long]$Bitrate = 0,
    [string]$Codec,
    [int]$Fps = 0,
    [int]$SegmentSeconds = 5,
    [double]$Skip = 2,
    [double]$Length = 60,
    [int]$MonitorWidth = 0,
    [int]$MonitorHeight = 0,
    [string[]]$Variants = @('A', 'B', 'C', 'D')
)

$ErrorActionPreference = 'Stop'
$inv = [System.Globalization.CultureInfo]::InvariantCulture

# ---------- helpers ----------

function Find-Ffmpeg {
    $c = @()
    if ($env:LOCALAPPDATA) {
        $c += Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Programs\WKI Clipper\Assets\ffmpeg') -Filter ffmpeg.exe -ErrorAction SilentlyContinue
        $c += Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue
    }
    $first = $c | Select-Object -First 1
    if ($first) { return $first.FullName }
    $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'ffmpeg.exe not found - pass -Ffmpeg'
}

# One process, one argument string (PowerShell 5.1 mangles embedded quotes when it builds the command line itself).
function Invoke-Ff([string]$ffArgs) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:Ffmpeg
    $psi.Arguments = $ffArgs
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = $true
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $p.WaitForExit()
    $sw.Stop()
    [pscustomobject]@{ ExitCode = $p.ExitCode; StdErr = $errTask.Result; StdOut = $outTask.Result; Seconds = $sw.Elapsed.TotalSeconds }
}

function Get-DurationSeconds([string]$file) {
    $r = Invoke-Ff ('-hide_banner -i "{0}"' -f $file)
    if ($r.StdErr -match 'Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)') {
        return [double]$Matches[1] * 3600 + [double]$Matches[2] * 60 + [double]::Parse($Matches[3], $inv)
    }
    return $null
}

# Same table as QualityPresets.ComputeBitrate + EffectiveBitrate (Native scales the WQHD base by the monitor's pixels).
function Get-EffectiveBitrate($video, [int]$monW, [int]$monH) {
    if ($video.Quality -eq 'Custom') { return [long]$video.Bitrate }
    $rows = @{ Niedrig = @(8, 14, 25); Mittel = @(15, 25, 45); Hoch = @(25, 40, 80); SehrHoch = @(40, 70, 150) }
    $q = [string]$video.Quality
    if (-not $rows.ContainsKey($q)) { $q = 'Mittel' }
    $tiers = @{ FullHD = 0; WQHD = 1; UHD = 2; Native = 1 }
    $res = [string]$video.Resolution
    $tier = 1
    if ($tiers.ContainsKey($res)) { $tier = $tiers[$res] }
    $bps = [long]($rows[$q][$tier] * 1000000)
    if ($res -ne 'Native') { return $bps }
    $pixels = [double]$monW * $monH
    $factor = [Math]::Min(2.2, [Math]::Max(0.6, $pixels / (2560.0 * 1440.0)))
    return [long]($bps * $factor)
}

function Get-ForcedIdrFlag([string]$codec) {
    if ($codec -like '*_amf*') { return '-forced_idr 1' }
    if ($codec -like '*_qsv*') { return '-forced_idr 1' }
    if ($codec -like '*_nvenc*') { return '-forced-idr 1' }
    if ($codec -eq 'libx264' -or $codec -eq 'libx265') { return '-forced-idr 1' }
    return ''
}

# Mirrors KeyframePolicy.EncoderArgs / SegmentTimeDelta.
function Get-VariantArgs([string]$variant, [string]$codec, [int]$fps, [int]$seg) {
    $delta = '{0:0.######}' -f (0.5 / $fps)
    $delta = $delta.Replace(',', '.')
    $forced = ('-force_key_frames "expr:gte(t,n_forced*{0})" {1}' -f $seg, (Get-ForcedIdrFlag $codec)).Trim()
    switch ($variant) {
        'A' { return @{ Encoder = ''; Delta = $null } }
        'B' { return @{ Encoder = $forced; Delta = $delta } }
        'C' { return @{ Encoder = ('{0} -g {1}' -f $forced, ($fps * $seg)); Delta = $delta } }
        'D' {
            $gop = '-g {0}' -f $fps
            if ($codec -like '*hevc_amf*') { $gop += ' -gops_per_idr 1' }
            return @{ Encoder = $gop; Delta = $delta }
        }
    }
}

# Mirrors FFmpegCommandBuilder.SegmentMuxerArgs.
function Get-SegmentMuxerArgs([int]$seg, [int]$wrap, $delta) {
    $s = '-f segment -segment_time {0} -segment_wrap {1} ' -f $seg, $wrap
    if ($delta) { $s += '-segment_time_delta {0} ' -f $delta }
    return $s + '-reset_timestamps 1 -segment_format mp4 '
}

function Get-RateControl([string]$codec, [long]$bps) {
    if ($codec -like '*_amf*') { return ('-quality balanced -rc cbr -b:v {0}' -f $bps) }
    if ($codec -like '*_nvenc*') { return ('-preset p4 -rc cbr -b:v {0}' -f $bps) }
    if ($codec -like '*_qsv*') { return ('-preset medium -b:v {0}' -f $bps) }
    return ('-preset veryfast -tune zerolatency -b:v {0} -pix_fmt yuv420p' -f $bps)
}

# ---------- setup ----------

if (-not $Ffmpeg) { $Ffmpeg = Find-Ffmpeg }
$script:Ffmpeg = $Ffmpeg
Write-Host "ffmpeg: $Ffmpeg"

$settings = $null
if (Test-Path $SettingsPath) {
    $settings = Get-Content $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host "settings: $SettingsPath"
} else {
    Write-Warning "No settings.json at $SettingsPath - using defaults (h264_amf, 60 fps, Mittel/Native)."
}

if (-not $Codec) { if ($settings) { $Codec = [string]$settings.Video.Codec } else { $Codec = 'h264_amf' } }
if ($Fps -le 0) { if ($settings) { $Fps = [int]$settings.Video.Framerate } else { $Fps = 60 } }
if ($Bitrate -le 0) {
    $video = if ($settings) { $settings.Video } else { [pscustomobject]@{ Quality = 'Mittel'; Resolution = 'Native'; Bitrate = 25000000 } }
    if ($MonitorWidth -le 0 -or $MonitorHeight -le 0) {
        Add-Type -AssemblyName System.Windows.Forms
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $MonitorWidth = $b.Width
        $MonitorHeight = $b.Height
    }
    $Bitrate = Get-EffectiveBitrate $video $MonitorWidth $MonitorHeight
}
Write-Host ("encoder: {0}, {1} fps, {2:0.0} Mbit/s, segments {3} s" -f $Codec, $Fps, ($Bitrate / 1e6), $SegmentSeconds)

if (-not $Source) {
    if (-not $settings) { throw 'No -Source given and no settings.json to find the clips folder.' }
    $clips = [Environment]::ExpandEnvironmentVariables([string]$settings.Output.ClipsFolder)
    $need = $Skip + 30
    $found = $null
    foreach ($f in (Get-ChildItem $clips -Recurse -Filter 'Clip_*.mp4' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 40)) {
        $d = Get-DurationSeconds $f.FullName
        if ($d -and $d -ge $need) { $found = $f.FullName; break }
    }
    if (-not $found) { throw "No Clip_*.mp4 of at least $need s under $clips - pass -Source." }
    $Source = $found
}
$srcDuration = Get-DurationSeconds $Source
if (-not $srcDuration) { throw "Cannot read the duration of $Source" }
$Length = [Math]::Min($Length, $srcDuration - $Skip - 1)
if ($Length -lt 30) { throw ("Source has only {0:0.0} s after skipping {1} s - too short for a meaningful run." -f ($srcDuration - $Skip), $Skip) }
Write-Host ("source: {0} ({1:0.0} s), using {2:0.0} s from {3} s" -f $Source, $srcDuration, $Length, $Skip)

New-Item -ItemType Directory -Force $OutDir | Out-Null
$hasVmaf = ((Invoke-Ff '-hide_banner -filters').StdOut -match 'libvmaf')
Write-Host ("quality metric: {0}" -f ($(if ($hasVmaf) { 'VMAF' } else { 'SSIM + PSNR' })))

# Constant frame rate input: the capture the buffer encodes is CFR, an app-recorded clip is not.
$decode = '-hide_banner -loglevel error -ss {0} -t {1} -i "{2}" -an -vf fps={3}' -f `
    $Skip.ToString($inv), $Length.ToString($inv), $Source, $Fps
$frame = 1.0 / $Fps

# ---------- encode + measure ----------

$results = @()
foreach ($v in $Variants) {
    $va = Get-VariantArgs $v $Codec $Fps $SegmentSeconds
    $vdir = Join-Path $OutDir $v
    if (Test-Path $vdir) { Remove-Item $vdir -Recurse -Force }
    New-Item -ItemType Directory -Force $vdir | Out-Null
    $rc = Get-RateControl $Codec $Bitrate
    $enc = ('-c:v {0} {1} {2}' -f $Codec, $rc, $va.Encoder).Trim()

    Write-Host "== Variant $v :: $enc"
    $seg = Invoke-Ff ('{0} {1} {2}"{3}\seg_%03d.mp4"' -f $decode, $enc, (Get-SegmentMuxerArgs $SegmentSeconds 100 $va.Delta), $vdir)
    if ($seg.ExitCode -ne 0) { Write-Warning ("segmented encode failed: " + $seg.StdErr); continue }
    $cont = Join-Path $OutDir ($v + '.mp4')
    $con = Invoke-Ff ('{0} {1} -f mp4 -y "{2}"' -f $decode, $enc, $cont)
    if ($con.ExitCode -ne 0) { Write-Warning ("continuous encode failed: " + $con.StdErr); continue }

    # (1) segment durations (video only, so a duration is a whole number of frames)
    $segFiles = Get-ChildItem $vdir -Filter 'seg_*.mp4' | Sort-Object Name
    $durs = @()
    foreach ($f in $segFiles) { $durs += (Get-DurationSeconds $f.FullName) }
    $judged = @()
    if ($durs.Count -gt 1) { $judged = $durs[0..($durs.Count - 2)] }
    $exact = @($judged | Where-Object { [Math]::Abs($_ - $SegmentSeconds) -le ($frame + 0.006) }).Count   # ffmpeg prints centiseconds
    $pctExact = if ($judged.Count -gt 0) { 100.0 * $exact / $judged.Count } else { 0 }

    # IDR count per minute of the continuous file
    $idrRun = Invoke-Ff ('-hide_banner -skip_frame nokey -i "{0}" -vf showinfo -f null -' -f $cont)
    $idrCount = ([regex]::Matches($idrRun.StdErr, 'iskey:1')).Count
    $contDur = Get-DurationSeconds $cont
    $idrPerMin = if ($contDur) { $idrCount / ($contDur / 60.0) } else { 0 }

    # bitrate of the continuous file
    $bytes = (Get-Item $cont).Length
    $kbps = if ($contDur) { $bytes * 8.0 / $contDur / 1000.0 } else { 0 }

    # quality of the continuous file against the same excerpt of the source
    $vmaf = $null; $ssim = $null; $psnr = $null
    $ref = ('-ss {0} -t {1} -i "{2}"' -f $Skip.ToString($inv), $Length.ToString($inv), $Source)
    $prep = '[0:v]setpts=PTS-STARTPTS[d];[1:v]fps={0},setpts=PTS-STARTPTS[r];' -f $Fps
    if ($hasVmaf) {
        $q = Invoke-Ff ('-hide_banner -i "{0}" {1} -lavfi "{2}[d][r]libvmaf=n_threads=8" -f null -' -f $cont, $ref, $prep)
        if ($q.StdErr -match 'VMAF score:\s*([0-9.]+)') { $vmaf = [double]::Parse($Matches[1], $inv) }
    }
    $s = Invoke-Ff ('-hide_banner -i "{0}" {1} -lavfi "{2}[d][r]ssim" -f null -' -f $cont, $ref, $prep)
    if ($s.StdErr -match 'SSIM.*All:([0-9.]+)') { $ssim = [double]::Parse($Matches[1], $inv) }
    $p = Invoke-Ff ('-hide_banner -i "{0}" {1} -lavfi "{2}[d][r]psnr" -f null -' -f $cont, $ref, $prep)
    if ($p.StdErr -match 'PSNR.*average:([0-9.]+)') { $psnr = [double]::Parse($Matches[1], $inv) }

    $results += [pscustomobject]@{
        Variant        = $v
        EncoderArgs    = $va.Encoder
        SegmentDelta   = $va.Delta
        Segments       = $durs.Count
        SegmentSeconds = ($durs | ForEach-Object { [Math]::Round($_, 2) })
        PctExactLength = [Math]::Round($pctExact, 1)
        IdrPerMinute   = [Math]::Round($idrPerMin, 1)
        Kbps           = [Math]::Round($kbps, 0)
        Vmaf           = $vmaf
        Ssim           = $ssim
        Psnr           = $psnr
        EncodeSeconds  = [Math]::Round($seg.Seconds, 1)
        EncodeSecondsContinuous = [Math]::Round($con.Seconds, 1)
    }
}

# ---------- verdict ----------

$a = $results | Where-Object { $_.Variant -eq 'A' } | Select-Object -First 1
foreach ($r in $results) {
    $bitrateDelta = $null; $vmafDrop = $null; $ssimDrop = $null; $psnrDrop = $null
    $c1 = $r.PctExactLength -ge 95
    $c2 = $null; $c3 = $null
    if ($a) {
        $bitrateDelta = if ($a.Kbps) { 100.0 * ($r.Kbps - $a.Kbps) / $a.Kbps } else { 0 }
        $c3 = [Math]::Abs($bitrateDelta) -le 3
        if ($r.Vmaf -ne $null -and $a.Vmaf -ne $null) {
            $vmafDrop = $a.Vmaf - $r.Vmaf
            $c2 = $vmafDrop -le 0.5
        } elseif ($r.Ssim -ne $null -and $a.Ssim -ne $null -and $r.Psnr -ne $null -and $a.Psnr -ne $null) {
            $ssimDrop = $a.Ssim - $r.Ssim
            $psnrDrop = $a.Psnr - $r.Psnr
            $c2 = ($ssimDrop -le 0.002) -and ($psnrDrop -le 0.3)
        }
    }
    $pass = ($r.Variant -ne 'A') -and $c1 -and ($c2 -eq $true) -and ($c3 -eq $true)
    $r | Add-Member -NotePropertyName BitrateDeltaPct -NotePropertyValue $(if ($bitrateDelta -ne $null) { [Math]::Round($bitrateDelta, 2) } else { $null })
    $r | Add-Member -NotePropertyName VmafDrop -NotePropertyValue $(if ($vmafDrop -ne $null) { [Math]::Round($vmafDrop, 3) } else { $null })
    $r | Add-Member -NotePropertyName SsimDrop -NotePropertyValue $(if ($ssimDrop -ne $null) { [Math]::Round($ssimDrop, 5) } else { $null })
    $r | Add-Member -NotePropertyName PsnrDrop -NotePropertyValue $(if ($psnrDrop -ne $null) { [Math]::Round($psnrDrop, 3) } else { $null })
    $r | Add-Member -NotePropertyName Crit1Length -NotePropertyValue $c1
    $r | Add-Member -NotePropertyName Crit2Quality -NotePropertyValue $c2
    $r | Add-Member -NotePropertyName Crit3Bitrate -NotePropertyValue $c3
    $r | Add-Member -NotePropertyName Passes -NotePropertyValue $pass
}

$byName = @{}
foreach ($r in $results) { $byName[$r.Variant] = $r }
$winner = 'EncoderDefault'
if ($byName.ContainsKey('C') -and $byName['C'].Passes -and (-not $byName.ContainsKey('B') -or -not $byName['B'].Passes -or $byName['C'].IdrPerMinute -le $byName['B'].IdrPerMinute)) {
    $winner = 'C (ForcedBoundaryWithSegmentGop)'
} elseif ($byName.ContainsKey('B') -and $byName['B'].Passes) {
    $winner = 'B (ForcedAtSegmentBoundary)'
} elseif ($byName.ContainsKey('D') -and $byName['D'].Passes) {
    $winner = 'D (FixedOneSecondGop)'
}

Write-Host ''
$results | Select-Object Variant, Segments, PctExactLength, IdrPerMinute, Kbps, BitrateDeltaPct, Vmaf, VmafDrop, Ssim, Psnr, EncodeSeconds, Passes |
    Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host "Winner: $winner"
if ($byName.ContainsKey('B') -and -not $byName['B'].Crit1Length) {
    Write-Host 'Note: B did not make the segments exact - the encoder ignores forced keyframes (decisive for variant D).'
}

$report = [pscustomobject]@{
    GeneratedUtc   = [DateTime]::UtcNow.ToString('o')
    Ffmpeg         = $Ffmpeg
    Source         = $Source
    SourceSeconds  = $srcDuration
    Skip           = $Skip
    Length         = $Length
    Codec          = $Codec
    Fps            = $Fps
    Bitrate        = $Bitrate
    SegmentSeconds = $SegmentSeconds
    Metric         = $(if ($hasVmaf) { 'VMAF' } else { 'SSIM+PSNR' })
    Winner         = $winner
    Results        = $results
}
$reportPath = Join-Path $OutDir 'keyframes-report.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -Path $reportPath -Encoding UTF8
Write-Host "Report: $reportPath"
