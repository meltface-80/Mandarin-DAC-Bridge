# Removes Mandarin DAC Bridge from Windows, and gives the DACs back to Windows.
#
#   powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/meltface-80/Mandarin-DAC-Bridge/main/tools/windows/uninstall.ps1 | iex"
#
# ffmpeg stays (other programs may use it): winget uninstall Gyan.FFmpeg removes it.
$ErrorActionPreference = 'SilentlyContinue'
$Task = 'Mandarin DAC Bridge'
Stop-ScheduledTask -TaskName $Task
Unregister-ScheduledTask -TaskName $Task -Confirm:$false
Get-Process mandarin-dac-bridge | Stop-Process -Force
Remove-NetFirewallRule -DisplayName 'Mandarin DAC Bridge'
Start-Sleep 1
Remove-Item (Join-Path $env:LOCALAPPDATA 'Mandarin-DAC-Bridge') -Recurse -Force
Write-Host 'Mandarin DAC Bridge is removed. The DACs are Windows'' again.'
