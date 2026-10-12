param([string]$Configuration = 'Release', [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\SessionForge-win-x64'))
# Builds a self-contained win-x64 distribution of SessionForge.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet publish "$root\src\SessionForge.App" -c $Configuration -r win-x64 --self-contained true -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
"Published to $((Resolve-Path $Output).Path)"
