# Mandarin DAC Bridge for Windows: the one-line installer.
#
#   powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/windows/install.ps1 | iex"
#
# Downloads the bridge (one native program) and its own small ffmpeg with SoX
# (dev/design/install.md) from the repository's latest GitHub Release
# into %LOCALAPPDATA%\Mandarin-DAC-Bridge, checked against that release's
# SHA256SUMS (from 1.1.26; an earlier release is installed unchecked: one that
# differs is refused, and nothing is changed), starts it now and at every sign-in
# (a hidden scheduled task), and opens its page. If there is no Release yet, or no
# build in it for this PC, it is built here from the source (a private .NET SDK is
# downloaded for it); then, as for an earlier release without it, ffmpeg with SoX
# comes from winget (Gyan.FFmpeg), if none is there: building ffmpeg needs MSYS2.
# A Gyan.FFmpeg installed before is left alone: something else may use it.
#
# Windows 10 or 11, x64 or ARM64. Run it again to update; settings are kept.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$OwnerRepo = 'meltface-80/Mandarin-DAC-Bridge'
$AppDir = Join-Path $env:LOCALAPPDATA 'Mandarin-DAC-Bridge'
$Bin = Join-Path $AppDir 'mandarin-dac-bridge.exe'
$Data = Join-Path $AppDir 'data'
$Log = Join-Path $Data 'bridge.log'
$Task = 'Mandarin DAC Bridge'
$Port = 55500

function Say($m) { Write-Host "`n$m" -ForegroundColor Yellow }

# The latest release's tag, from where github.com/<repo>/releases/latest leads: '' when there is no
# release; throws when GitHub can't be reached.
function Get-LatestTag($Repo) {
    $req = [System.Net.HttpWebRequest]::Create("$Repo/releases/latest")
    $req.AllowAutoRedirect = $false
    $req.Method = 'HEAD'
    $res = $req.GetResponse()
    try { $to = [string]$res.Headers['Location'] } finally { $res.Close() }
    if ($to -match '/releases/tag/([^/?#]+)$') { return $Matches[1] }
    return ''
}

# A URL into a file: 200 when it came, 404 when there is none; throws on anything else.
function Get-File($Url, $File) {
    try { Invoke-WebRequest $Url -OutFile $File -UseBasicParsing; return 200 }
    catch {
        $r = $_.Exception.Response
        if ($r -and [int]$r.StatusCode -eq 404) { return 404 }
        throw
    }
}

# A file's SHA-256 against its line in a SHA256SUMS: 0 it matches, 1 it doesn't (or SHA256SUMS has no line
# for it: release.yml lists every build). A file that can't be read throws.
function Test-Sum($File, $Name, $Sums) {
    $line = Get-Content $Sums | Where-Object { ($_ -split '\s+')[1] -in @($Name, "*$Name") } | Select-Object -First 1
    if (-not $line) { return 1 }
    $want = ($line -split '\s+')[0].ToLowerInvariant()
    $got = (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($got -eq $want) { return 0 } else { return 1 }
}

$rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
Say "Installing Mandarin DAC Bridge ($rid). Leave this window open."

# ffmpeg, the decoder, built with SoX: a track at a rate the DAC can't take is resampled with SoX or
# refused (Audio/Soxr.cs). The bridge's own comes with each release that carries it. Without it: winget's
# Gyan.FFmpeg and Chocolatey's ffmpeg-full (Gyan's full build) have SoX; the essentials build
# (Gyan.FFmpeg.Essentials, Chocolatey's ffmpeg) doesn't. The bridge picks its own, else the one with SoX.
function Find-Ffmpegs {
    $all = @(Get-Command ffmpeg -All -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
    $links = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ffmpeg.exe'
    if (Test-Path $links) { $all += $links }
    return @($all | Where-Object { $_ } | Select-Object -Unique)
}
# Whether an ffmpeg has SoX, from ffmpeg -buildconf, read as a process (both streams at once, so neither
# fills up; and not through PowerShell, which turns a native program's stderr into errors), for 10 s at most.
function Test-Sox($ffmpeg) {
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo $ffmpeg, '-hide_banner -buildconf'
        $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $p = [System.Diagnostics.Process]::Start($psi)
        $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit(10000)) { $p.Kill(); return $false }
        return ($out.Result + $err.Result).Contains('--enable-libsoxr')
    } catch { return $false }
}
function Install-Ffmpeg {
    if (Find-Ffmpegs | Where-Object { Test-Sox $_ }) { return }
    Say 'Installing ffmpeg with SoX (winget)…'
    $said = 'no winget'
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install --id Gyan.FFmpeg -e --silent --accept-source-agreements --accept-package-agreements | Out-Null
        $said = "winget's answer: $LASTEXITCODE"
    }
    # winget may have put it on the user's PATH rather than in its Links folder: read the PATH afresh.
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    $found = Find-Ffmpegs
    if (-not $found) { Write-Host "ffmpeg could not be installed ($said). Install it (https://www.gyan.dev/ffmpeg/builds/, the full build) and run this again."; exit 1 }
    if (-not ($found | Where-Object { Test-Sox $_ })) {
        Write-Host "ffmpeg with SoX could not be installed ($said), so a track at a rate the DAC can't take will be refused."
        Write-Host 'If Gyan.FFmpeg.Essentials is installed, remove it (winget uninstall Gyan.FFmpeg.Essentials) and run this again.'
    }
}

# 1. The bridge and its ffmpeg: fetched (and checked) or built first; one already running is stopped only
#    to put the new ones in place, so a download refused or failed leaves it as it was.
New-Item -ItemType Directory -Force -Path $Data | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("mdb-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null
function Stop-Unreachable($e) { Write-Host "Couldn't download from GitHub ($e). Check this PC's connection and run this again (nothing was changed)."; exit 1 }
try {
    Say 'Downloading Mandarin DAC Bridge…'
    $name = "mandarin-dac-bridge-$rid.zip"
    $zip = Join-Path $tmp 'bridge.zip'
    $sums = Join-Path $tmp 'SHA256SUMS'
    try { $tag = Get-LatestTag "https://github.com/$OwnerRepo" } catch { Stop-Unreachable $_.Exception.Message }
    $got = 404
    if ($tag) {
        $at = "https://github.com/$OwnerRepo/releases/download/$tag"   # the build and its sums from the same release
        try { $got = Get-File "$at/$name" $zip } catch { Stop-Unreachable $_.Exception.Message }
    }
    if ($got -eq 200) {
        try { $listed = Get-File "$at/SHA256SUMS" $sums } catch { Stop-Unreachable $_.Exception.Message }
        if ($listed -eq 404) {
            Say "This release ($tag) has no checksums (it came before they did): installing it unchecked."
        } elseif ((Test-Sum $zip $name $sums) -eq 0) {
            Say "Checked against the release's checksum."
        } else {
            Write-Host "The downloaded build doesn't match the release's checksum, so it isn't installed (nothing was changed). Try again later; if it happens again, please report it."
            exit 1
        }
        Expand-Archive $zip -DestinationPath $tmp -Force
        $new = Join-Path $tmp 'mandarin-dac-bridge.exe'
        if (-not (Test-Path $new)) { Write-Host "The release's build has no mandarin-dac-bridge.exe in it. Nothing was changed."; exit 1 }
        $ownFfmpeg = Join-Path $tmp 'ffmpeg'   # a release that carries its own ffmpeg
        if (-not (Test-Path (Join-Path $ownFfmpeg 'bin\ffmpeg.exe'))) { $ownFfmpeg = $null }
    } else {
        Say 'No ready-made build for this PC yet: building it here (a few minutes, once)…'
        $dotnet = Join-Path $AppDir '.dotnet'
        if (-not (Test-Path (Join-Path $dotnet 'dotnet.exe'))) {
            $installer = Join-Path $tmp 'dotnet-install.ps1'
            Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
            & $installer -Channel 10.0 -InstallDir $dotnet -NoPath
        }
        $src = Join-Path $tmp 'src.zip'
        Invoke-WebRequest "https://codeload.github.com/$OwnerRepo/zip/refs/heads/main" -OutFile $src -UseBasicParsing
        Expand-Archive $src -DestinationPath $tmp -Force
        $root = Get-ChildItem $tmp -Directory | Where-Object Name -like 'Mandarin-DAC-Bridge-*' | Select-Object -First 1
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
        # Native AOT needs Visual Studio's C++ tools; without them, one self-contained program instead.
        & (Join-Path $dotnet 'dotnet.exe') publish (Join-Path $root.FullName 'src\MandarinDacBridge') -c Release -r $rid -o (Join-Path $tmp 'out') --nologo -v quiet
        if ($LASTEXITCODE -ne 0) {
            & (Join-Path $dotnet 'dotnet.exe') publish (Join-Path $root.FullName 'src\MandarinDacBridge') -c Release -r $rid -o (Join-Path $tmp 'out') --nologo -v quiet `
                -p:PublishAot=false -p:SelfContained=true -p:PublishSingleFile=true
        }
        $new = Join-Path $tmp 'out\mandarin-dac-bridge.exe'
        $ownFfmpeg = $null
    }
    if (-not $ownFfmpeg) { Install-Ffmpeg }
    # A second run: the one running is replaced now (a running program can't be written over).
    Stop-ScheduledTask -TaskName $Task -ErrorAction SilentlyContinue
    Get-Process mandarin-dac-bridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Copy-Item $new $Bin -Force
    $ffDir = Join-Path $AppDir 'ffmpeg'
    if ($ownFfmpeg) {
        # A track still decoding holds its ffmpeg.exe open: only the bridge's own are stopped.
        Get-Process ffmpeg -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($ffDir, [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force -ErrorAction SilentlyContinue
        # A stopped process lets go of its file a moment later: tried for a few seconds, and never copied
        # into a folder still there (it would land in ffmpeg\ffmpeg).
        foreach ($i in 1..10) {
            Remove-Item $ffDir -Recurse -Force -ErrorAction SilentlyContinue
            if (-not (Test-Path $ffDir)) { break }
            Start-Sleep -Milliseconds 500
        }
        if (Test-Path $ffDir) { Write-Host "The bridge's old ffmpeg ($ffDir) couldn't be replaced: something still has it open. Close it, or restart the PC, and run this again."; exit 1 }
        Copy-Item $ownFfmpeg $ffDir -Recurse -Force
    }
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

Say 'The USB DACs it can see:'
& $Bin --list

# 3. Allowed through Windows Firewall (UPnP discovery and the page), when this window may.
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($admin) {
    Remove-NetFirewallRule -DisplayName 'Mandarin DAC Bridge' -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName 'Mandarin DAC Bridge' -Direction Inbound -Program $Bin -Action Allow -Profile Private,Domain | Out-Null
} else {
    Write-Host 'When Windows asks whether to let mandarin-dac-bridge on your network, allow it on private networks.'
}

# 4. Started now and at every sign-in: a hidden scheduled task, its output in data\bridge.log.
$cmd = "& '$Bin' *>> '$Log'"
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command `"$cmd`"" -WorkingDirectory $AppDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $Task -Action $action -Trigger $trigger -Settings $settings -Description 'Mandarin DAC Bridge: exclusive, bit-perfect USB DACs for Audirvana, Mandarin and more' -Force | Out-Null
Start-ScheduledTask -TaskName $Task

Say 'Starting Mandarin DAC Bridge…'
$up = $false
foreach ($i in 1..30) {
    try { Invoke-WebRequest "http://localhost:$Port/api/health" -UseBasicParsing -TimeoutSec 2 | Out-Null; $up = $true; break } catch { Start-Sleep 1 }
}
if (-not $up) { Write-Host "It didn't start. What it said is in $Log"; exit 1 }
Start-Process "http://localhost:$Port"

$ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.PrefixOrigin -ne 'WellKnown' -and $_.IPAddress -notlike '169.*' } | Select-Object -First 1).IPAddress
Say "Mandarin DAC Bridge $(& $Bin --version) is running."
Write-Host "  On this PC:    http://localhost:$Port"
if ($ip) { Write-Host "  On your phone: http://${ip}:$Port" }
Write-Host ''
Write-Host 'Each USB DAC is now on your network as "<its name> (Bridge)". Choose it in Audirvana, Mandarin, JRiver, foobar2000 (UPnP)…'
Write-Host 'If a DAC says "Waiting", another program has it in exclusive mode: point that program at the bridge instead.'
if ($ownFfmpeg -and (Test-Path (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ffmpeg.exe'))) {
    Write-Host 'The bridge has its own ffmpeg now; the one from winget (Gyan.FFmpeg) was left as it is, as something else may use it.'
    Write-Host 'If nothing does: winget uninstall Gyan.FFmpeg'
}
Write-Host "To remove it: powershell -ExecutionPolicy Bypass -c `"irm https://raw.githubusercontent.com/$OwnerRepo/main/tools/windows/uninstall.ps1 | iex`""
