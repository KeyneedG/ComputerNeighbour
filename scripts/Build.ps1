param([switch]$SkipTests)
. (Join-Path $PSScriptRoot 'Portable.Common.ps1')
Initialize-PortableEnvironment
$dotnet = Get-PortableDotnet
Push-Location $script:NeighbourRoot
try {
    $testProject = Get-PortablePath 'tests/ComputerNeighbour.Tests/ComputerNeighbour.Tests.csproj'
    if (-not $SkipTests -and (Test-Path -LiteralPath $testProject)) {
        & $dotnet run --project $testProject --configuration Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    & $dotnet publish (Get-PortablePath 'src/ComputerNeighbour/ComputerNeighbour.csproj') --configuration Release --runtime win-x64 --self-contained true --output (Get-PortablePath 'app') '-p:DebugType=None' '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Write-Host 'Portable application is ready: app/ComputerNeighbour.exe. Start it with Start.cmd.'
} finally { Pop-Location }
