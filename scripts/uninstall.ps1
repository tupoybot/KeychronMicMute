$ErrorActionPreference = 'Stop'
$InstallDir = Join-Path $env:LOCALAPPDATA 'KeychronMicMute'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process KeychronMicMute -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path $RunKey -Name 'KeychronMicMute' -ErrorAction SilentlyContinue
Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'KeychronMicMute removed.'
