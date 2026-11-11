param([switch]$PackagesOnly, [switch]$ModelsOnly)
$ErrorActionPreference = 'Stop'
$project = $PSScriptRoot
$runtime = Join-Path $project '.runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
$uvPath = Join-Path $runtime 'uv.exe'
$env:UV_PYTHON_INSTALL_DIR = Join-Path $runtime 'python'
$env:UV_CACHE_DIR = Join-Path $runtime 'uv-cache'
$env:UV_PYTHON_NO_REGISTRY = '1'
$env:UV_LINK_MODE = 'copy'
$env:UV_HTTP_TIMEOUT = '90'
if (!(Test-Path -LiteralPath $uvPath)) {
    $uvRelease = Invoke-RestMethod 'https://pypi.org/pypi/uv/0.13.7/json'
    $uvWheel = $uvRelease.urls | Where-Object filename -like '*win_amd64.whl' | Select-Object -First 1
    $uvZip = Join-Path $runtime 'uv.zip'
    Invoke-WebRequest -Uri $uvWheel.url -OutFile $uvZip
    if ((Get-FileHash -LiteralPath $uvZip -Algorithm SHA256).Hash -ne $uvWheel.digests.sha256) {
        throw 'uv checksum mismatch'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($uvZip)
    try {
        $exe = $archive.Entries | Where-Object { $_.FullName.EndsWith('/uv.exe') } | Select-Object -First 1
        [IO.Compression.ZipFileExtensions]::ExtractToFile($exe, $uvPath, $true)
    } finally {
        $archive.Dispose()
    }
    Remove-Item -LiteralPath $uvZip
}
$pythonPath = Join-Path $env:UV_PYTHON_INSTALL_DIR 'cpython-3.12.12-windows-x86_64-none\python.exe'
if (!(Test-Path -LiteralPath $pythonPath)) {
    & $uvPath python install 3.12.12 --no-config --no-bin --no-registry
    if ($LASTEXITCODE) { throw 'Portable Python installation failed' }
}
& $pythonPath (Join-Path $project 'prepare.py') $(if ($PackagesOnly) { '--packages-only' } elseif ($ModelsOnly) { '--models-only' })
if ($LASTEXITCODE) { exit $LASTEXITCODE }
