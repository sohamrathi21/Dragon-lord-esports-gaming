$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location $taskRoot
$env:N8N_USER_FOLDER=Join-Path $taskRoot '.runtime/n8n-data'
$env:N8N_LISTEN_ADDRESS='127.0.0.1'
$env:N8N_HOST='localhost'
$env:N8N_PORT='5678'
$env:N8N_DIAGNOSTICS_ENABLED='false'
node .runtime/n8n/node_modules/n8n/bin/n8n start
