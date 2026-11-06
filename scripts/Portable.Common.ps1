$ErrorActionPreference = 'Stop'
$script:NeighbourRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-PortablePath([string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative)) { throw "Expected a relative portable path: $Relative" }
    $result = [IO.Path]::GetFullPath((Join-Path $script:NeighbourRoot $Relative))
    if (-not $result.StartsWith($script:NeighbourRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Path leaves ComputerNeighbour: $Relative" }
    return $result
}

function Initialize-PortableEnvironment {
    foreach ($dir in @('data/tmp','data/cache','data/cache/dotnet','data/cache/nuget','data/cache/huggingface','runtimes/downloads')) {
        New-Item -ItemType Directory -Path (Get-PortablePath $dir) -Force | Out-Null
    }
    $env:TEMP = Get-PortablePath 'data/tmp'
    $env:TMP = $env:TEMP
    $env:TMPDIR = $env:TEMP
    $env:DOTNET_CLI_HOME = Get-PortablePath 'data/cache/dotnet'
    $env:NUGET_PACKAGES = Get-PortablePath 'data/cache/nuget'
    $env:NUGET_HTTP_CACHE_PATH = Get-PortablePath 'data/cache/nuget-http'
    $env:NUGET_PLUGINS_CACHE_PATH = Get-PortablePath 'data/cache/nuget-plugins'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Get-PortablePath 'data/cache/dotnet-bundle'
    $env:DOTNET_DbgMiniDumpName = Get-PortablePath 'data/tmp/crash-%p.dmp'
    $env:HF_HOME = Get-PortablePath 'data/cache/huggingface'
    $env:XDG_CACHE_HOME = Get-PortablePath 'data/cache'
    $env:PYTHONPYCACHEPREFIX = Get-PortablePath 'data/cache/python'
    $env:PIP_CACHE_DIR = Get-PortablePath 'data/cache/pip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

function Receive-PortableFile([string]$Url, [string]$RelativePath, [string]$ExpectedHash = '', [string]$Algorithm = 'SHA256') {
    $target = Get-PortablePath $RelativePath
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    if (Test-Path -LiteralPath $target) {
        if (-not $ExpectedHash -or (Get-FileHash -LiteralPath $target -Algorithm $Algorithm).Hash -eq $ExpectedHash) { return $target }
        throw "Checksum mismatch: $target. Move the damaged file away and retry."
    }
    $partial = $target + '.part'
    Write-Host "Downloading $RelativePath"
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source --fail --location --retry 3 --retry-delay 2 --continue-at - --output $partial $Url
        if ($LASTEXITCODE -eq 33) {
            Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
            & $curl.Source --fail --location --retry 3 --output $partial $Url
        }
        if ($LASTEXITCODE -ne 0) { throw "Download failed ($LASTEXITCODE). Rerun Setup.cmd to resume: $Url" }
    } else {
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $partial
    }
    $actual = (Get-FileHash -LiteralPath $partial -Algorithm $Algorithm).Hash.ToLowerInvariant()
    if ($ExpectedHash -and $actual -ne $ExpectedHash.ToLowerInvariant()) { throw "Checksum mismatch for $RelativePath; the .part file was kept for inspection." }
    Move-Item -LiteralPath $partial -Destination $target -Force
    return $target
}

function Expand-PortableRuntime([string]$Archive, [string]$Destination, [string]$Executable) {
    $destinationPath = Get-PortablePath $Destination
    $markerPath = Join-Path $destinationPath '.archive-sha256'
    $archiveHash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash
    if ((Test-Path -LiteralPath (Join-Path $destinationPath $Executable)) -and (Test-Path -LiteralPath $markerPath) -and (Get-Content -LiteralPath $markerPath -Raw).Trim() -eq $archiveHash) { return }
    $stage = Get-PortablePath ('data/tmp/extract-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        Expand-Archive -LiteralPath $Archive -DestinationPath $stage -Force
        $exe = Get-ChildItem -LiteralPath $stage -Recurse -File -Filter $Executable | Select-Object -First 1
        if (-not $exe) { throw "Archive has no $Executable" }
        New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
        Get-ChildItem -LiteralPath $exe.DirectoryName -Force | Copy-Item -Destination $destinationPath -Recurse -Force
        Set-Content -LiteralPath $markerPath -Value $archiveHash -Encoding ASCII
    } finally {
        $resolved = [IO.Path]::GetFullPath($stage)
        if ($resolved.StartsWith((Get-PortablePath 'data/tmp') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}

function Get-PortableDotnet {
    $local = Get-PortablePath 'runtimes/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installed) {
        $versions = & $installed.Source --list-sdks
        if ($versions -match '^10\.') { return $installed.Source }
    }
    Write-Host 'No .NET 10 SDK found. Downloading a private SDK into runtimes/dotnet.'
    $metadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $version = $metadata.'latest-sdk'
    $sdk = $null
    foreach ($release in $metadata.releases) {
        foreach ($candidate in $release.sdks) { if ($candidate.version -eq $version) { $sdk = $candidate; break } }
        if ($sdk) { break }
    }
    $file = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name.EndsWith('.zip') } | Select-Object -First 1
    if (-not $file) { throw 'Unable to locate the official .NET 10 SDK ZIP.' }
    $archive = Receive-PortableFile $file.url ('runtimes/downloads/dotnet-sdk-' + $version + '-win-x64.zip') $file.hash 'SHA512'
    Expand-Archive -LiteralPath $archive -DestinationPath (Get-PortablePath 'runtimes/dotnet') -Force
    [ordered]@{ version=$version; url=$file.url; sha512=$file.hash } | ConvertTo-Json | Set-Content -LiteralPath (Get-PortablePath 'runtimes/dotnet-source.json') -Encoding UTF8
    return $local
}
