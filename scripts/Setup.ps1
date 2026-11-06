param(
    [ValidateSet('vulkan','cpu')][string]$Backend = 'vulkan',
    [ValidateSet('All','Runtimes','Models','Llama','Whisper','Piper')][string]$Only = 'All',
    [switch]$SkipModels,
    [switch]$SkipBuild
)
. (Join-Path $PSScriptRoot 'Portable.Common.ps1')
Initialize-PortableEnvironment
$manifestPath = Get-PortablePath 'models/dependencies.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$lockPath = Get-PortablePath 'models/installed-lock.json'
$installed = @()
if (Test-Path -LiteralPath $lockPath) { $installed = @(Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json) }
$total = 0L
foreach ($item in $manifest.artifacts) {
    if ($item.backend -and $item.backend -ne $Backend) { continue }
    if ($Only -eq 'Runtimes' -and $item.kind -ne 'runtime') { continue }
    if ($Only -eq 'Models' -and $item.kind -ne 'model') { continue }
    if ($Only -in @('Llama','Whisper','Piper') -and $item.component -ne $Only.ToLowerInvariant() -and $item.component -ne 'common') { continue }
    if ($SkipModels -and $item.kind -eq 'model') { continue }
    $total += $item.bytes
}
Write-Host ('Portable setup: about {0:N2} GB selected, already present files are verified and reused.' -f ($total / 1GB))
foreach ($item in $manifest.artifacts) {
    if ($item.backend -and $item.backend -ne $Backend) { continue }
    if ($Only -eq 'Runtimes' -and $item.kind -ne 'runtime') { continue }
    if ($Only -eq 'Models' -and $item.kind -ne 'model') { continue }
    if ($Only -in @('Llama','Whisper','Piper') -and $item.component -ne $Only.ToLowerInvariant() -and $item.component -ne 'common') { continue }
    if ($SkipModels -and $item.kind -eq 'model') { continue }
    $file = Receive-PortableFile $item.url $item.path $item.sha256
    if ($item.kind -eq 'runtime') {
        if ($item.archiveType -eq 'vc-redist') { & (Join-Path $PSScriptRoot 'Install-LocalVcRuntime.ps1') -PackageRelativePath $item.path }
        else { Expand-PortableRuntime $file $item.extractTo $item.executable }
    }
    $record = [ordered]@{ id=$item.id; path=$item.path; url=$item.url; sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); bytes=(Get-Item -LiteralPath $file).Length; verifiedAt=[DateTimeOffset]::Now.ToString('o') }
    $installed = @($installed | Where-Object { $_.id -ne $item.id }) + $record
    $installed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($lockPath + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($lockPath + '.tmp') -Destination $lockPath -Force
}
Write-Host ('Dependencies are ready. Backend: ' + $Backend)
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1'); if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
