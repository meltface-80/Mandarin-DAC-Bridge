# Mandarin DAC Bridge for Windows: the one-line installer.
#
#   powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/windows/install.ps1 | iex"
#
# Installs ffmpeg with winget (if it isn't there), downloads the bridge (one
# native program) from the repository's latest GitHub Release into
# %LOCALAPPDATA%\Mandarin-DAC-Bridge, starts it now and at every sign-in
# (a hidden scheduled task), and opens its page. If there is no Release yet,
# it is built here from the source (a private .NET SDK is downloaded for it).
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

$rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
Say "Installing Mandarin DAC Bridge ($rid). Leave this window open."

# 1. ffmpeg, the decoder.
function Find-Ffmpeg {
    $c = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $links = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ffmpeg.exe'
    if (Test-Path $links) { return $links }
    return $null
}
if (-not (Find-Ffmpeg)) {
    Say 'Installing ffmpeg (winget)…'
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install --id Gyan.FFmpeg -e --silent --accept-source-agreements --accept-package-agreements | Out-Null
    }
    if (-not (Find-Ffmpeg)) { Write-Host 'ffmpeg could not be installed by winget. Install it (https://www.gyan.dev/ffmpeg/builds/) and run this again.'; exit 1 }
}

# 2. The bridge (stopped first if this is a second run).
Stop-ScheduledTask -TaskName $Task -ErrorAction SilentlyContinue
Get-Process mandarin-dac-bridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Data | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("mdb-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Say 'Downloading Mandarin DAC Bridge…'
    $url = "https://github.com/$OwnerRepo/releases/latest/download/mandarin-dac-bridge-$rid.zip"
    $zip = Join-Path $tmp 'bridge.zip'
    $got = $false
    try { Invoke-WebRequest $url -OutFile $zip -UseBasicParsing; Expand-Archive $zip -DestinationPath $tmp -Force; $got = Test-Path (Join-Path $tmp 'mandarin-dac-bridge.exe') } catch { $got = $false }
    if ($got) {
        Copy-Item (Join-Path $tmp 'mandarin-dac-bridge.exe') $Bin -Force
    } else {
        Say 'No ready-made build yet: building it here (a few minutes, once)…'
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
        Copy-Item (Join-Path $tmp 'out\mandarin-dac-bridge.exe') $Bin -Force
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
Write-Host "To remove it: powershell -ExecutionPolicy Bypass -c `"irm https://raw.githubusercontent.com/$OwnerRepo/main/tools/windows/uninstall.ps1 | iex`""
