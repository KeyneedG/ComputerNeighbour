param([Parameter(ValueFromRemainingArguments=$true)][string[]]$AppArguments)
. (Join-Path $PSScriptRoot 'Portable.Common.ps1')
Initialize-PortableEnvironment
$exe = Get-PortablePath 'app/ComputerNeighbour.exe'
$installedAssembly = Get-PortablePath 'app/ComputerNeighbour.dll'
$stagedExe = Get-PortablePath 'data/app-update/ComputerNeighbour.exe'
$stagedAssembly = Get-PortablePath 'data/app-update/ComputerNeighbour.dll'
if ((Test-Path -LiteralPath $stagedExe -PathType Leaf) -and (Test-Path -LiteralPath $stagedAssembly -PathType Leaf)) {
    # A running instance can lock the installed assembly during publishing.
    $stagedVersion = [version](Get-Item -LiteralPath $stagedAssembly).VersionInfo.FileVersion
    if (-not (Test-Path -LiteralPath $installedAssembly -PathType Leaf) -or
        $stagedVersion -gt [version](Get-Item -LiteralPath $installedAssembly).VersionInfo.FileVersion) {
        $exe = $stagedExe
    }
}
if (-not (Test-Path -LiteralPath $exe)) { & (Join-Path $PSScriptRoot 'Build.ps1') }
Push-Location $script:NeighbourRoot
try { & $exe --root $script:NeighbourRoot @AppArguments; exit $LASTEXITCODE } finally { Pop-Location }
