param(
    [ValidateSet('vulkan','cpu')][string]$Backend = 'vulkan',
    [ValidateSet('All','Runtimes','Models','Llama','Whisper','Piper','Qwen-TTS')][string]$Only = 'All',
    [switch]$SkipModels,
    [switch]$SkipBuild
)
. (Join-Path $PSScriptRoot 'Portable.Common.ps1')
Initialize-PortableEnvironment
$manifestPath = Get-PortablePath 'models/dependencies.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$lockPath = Get-PortablePath 'models/installed-lock.json'
$installed = @()
if (Test-Path -LiteralPath $lockPath) {
    # Windows PowerShell 5 can wrap an array returned directly inside @(...).
    # Enumerate the deserialized array explicitly so subsequent JSON stays flat.
    $loadedRecords = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    foreach ($loadedRecord in $loadedRecords) { $installed += $loadedRecord }
}
$total = 0L
$componentOnly = $Only -in @('Llama','Whisper','Piper','Qwen-TTS')
$includeCommon = $Only -ne 'Qwen-TTS'
foreach ($item in $manifest.artifacts) {
    if ($item.backend -and $item.backend -ne $Backend) { continue }
    if ($Only -eq 'Runtimes' -and $item.kind -ne 'runtime') { continue }
    if ($Only -eq 'Models' -and $item.kind -ne 'model') { continue }
    if ($componentOnly -and $item.component -ne $Only.ToLowerInvariant() -and -not ($includeCommon -and $item.component -eq 'common')) { continue }
    if ($SkipModels -and $item.kind -eq 'model') { continue }
    $total += $item.bytes
}
Write-Host ('Portable setup: about {0:N2} GB selected, already present files are verified and reused.' -f ($total / 1GB))
foreach ($item in $manifest.artifacts) {
    if ($item.backend -and $item.backend -ne $Backend) { continue }
    if ($Only -eq 'Runtimes' -and $item.kind -ne 'runtime') { continue }
    if ($Only -eq 'Models' -and $item.kind -ne 'model') { continue }
    if ($componentOnly -and $item.component -ne $Only.ToLowerInvariant() -and -not ($includeCommon -and $item.component -eq 'common')) { continue }
    if ($SkipModels -and $item.kind -eq 'model') { continue }
    $file = Receive-PortableFile $item.url $item.path $item.sha256
    if ($item.kind -eq 'runtime') {
        if ($item.archiveType -eq 'vc-redist') { & (Join-Path $PSScriptRoot 'Install-LocalVcRuntime.ps1') -PackageRelativePath $item.path }
        else { Expand-PortableRuntime $file $item.extractTo $item.executable }
    }
    $record = [ordered]@{ id=$item.id; path=$item.path; url=$item.url; sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); bytes=(Get-Item -LiteralPath $file).Length; verifiedAt=[DateTimeOffset]::Now.ToString('o') }
    $installed = @($installed | Where-Object { $_.id -ne $item.id }) + $record
    ConvertTo-Json -InputObject @($installed) -Depth 10 | Set-Content -LiteralPath ($lockPath + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($lockPath + '.tmp') -Destination $lockPath -Force
}
Write-Host ('Dependencies are ready. Backend: ' + $Backend)
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1'); if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
