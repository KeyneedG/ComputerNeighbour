param([Parameter(ValueFromRemainingArguments=$true)][string[]]$AppArguments)
. (Join-Path $PSScriptRoot 'Portable.Common.ps1')
Initialize-PortableEnvironment
$exe = Get-PortablePath 'app/ComputerNeighbour.exe'
if (-not (Test-Path -LiteralPath $exe)) { & (Join-Path $PSScriptRoot 'Build.ps1') }
Push-Location $script:NeighbourRoot
try { & $exe --root $script:NeighbourRoot @AppArguments; exit $LASTEXITCODE } finally { Pop-Location }
