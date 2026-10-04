$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location $taskRoot
$taskDotnet=Join-Path $taskRoot '.runtime/dotnet/dotnet.exe'
if(!(Test-Path $taskDotnet)){$taskDotnet='dotnet'}
if(!(Test-Path '.runtime/backend-config.json')){throw 'Create .runtime/backend-config.json as documented in README.md.'}
$env:DRAGON_CONFIG=Join-Path $taskRoot '.runtime/backend-config.json'
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:ASPNETCORE_URLS='http://127.0.0.1:5080'
& $taskDotnet run --project services/DragonLord.Api
