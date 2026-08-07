$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'src\KeychronMicMute\KeychronMicMute.csproj'
$Publish = Join-Path $RepoRoot 'artifacts\publish'
$InstallDir = Join-Path $env:LOCALAPPDATA 'KeychronMicMute'
$Exe = Join-Path $InstallDir 'KeychronMicMute.exe'

Write-Host 'Publishing KeychronMicMute...'
dotnet publish $Project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $Publish

Get-Process KeychronMicMute -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $Publish 'KeychronMicMute.exe') $Exe -Force

$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $RunKey -Name 'KeychronMicMute' -Value ('"' + $Exe + '"') -PropertyType String -Force | Out-Null
Start-Process $Exe

Write-Host "Installed and started: $Exe"
Write-Host "Log: $InstallDir\helper.log"
