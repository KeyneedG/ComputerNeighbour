param([string]$PackageRelativePath = 'runtimes/downloads/VC_redist.x64.exe')
. (Join-Path $PSScriptRoot 'Portable.Common.ps1')
Initialize-PortableEnvironment
$package = Get-PortablePath $PackageRelativePath
$signature = Get-AuthenticodeSignature -LiteralPath $package
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*O=Microsoft Corporation*') { throw 'The VC++ runtime archive must have a valid Microsoft signature.' }
$destination = Get-PortablePath 'runtimes/vcruntime'
$archiveHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$marker = Join-Path $destination '.archive-sha256'
$ready = (Test-Path -LiteralPath $marker) -and (Get-Content -LiteralPath $marker -Raw).Trim() -eq $archiveHash -and (Test-Path -LiteralPath (Join-Path $destination 'vcomp140.dll'))
if (-not $ready) {
    $stage = Get-PortablePath ('data/tmp/vcruntime-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        # Read the Burn bundle as data. Neither the EXE nor any MSI is executed.
        $bytes = [IO.File]::ReadAllBytes($package)
        $haystack = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
        $index = 0
        $cabinetDirectories = @()
        while (($index = $haystack.IndexOf('MSCF', $index, [StringComparison]::Ordinal)) -ge 0) {
            $size = [BitConverter]::ToUInt32($bytes, $index + 8)
            if ($size -gt 36 -and $index + $size -le $bytes.Length -and $bytes[$index + 24] -eq 3 -and $bytes[$index + 25] -eq 1) {
                $cab = [byte[]]::new($size)
                [Buffer]::BlockCopy($bytes, $index, $cab, 0, $size)
                $name = 'cab' + $cabinetDirectories.Count
                $cabPath = Join-Path $stage ($name + '.cab')
                [IO.File]::WriteAllBytes($cabPath, $cab)
                $directory = Join-Path $stage $name
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
                & "$env:SystemRoot\System32\expand.exe" $cabPath '-F:*' $directory | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Unable to extract the VC++ bundle cabinet.' }
                $cabinetDirectories += $directory
            }
            $index += 4
        }
        $minimumPayload = $null
        foreach ($directory in $cabinetDirectories) {
            $manifestFile = Join-Path $directory '0'
            if (Test-Path -LiteralPath $manifestFile) {
                $manifest = [xml](Get-Content -LiteralPath $manifestFile -Raw)
                $minimumPayload = $manifest.BurnManifest.Payload | Where-Object { $_.FilePath -like '*vcRuntimeMinimum_amd64*cab1.cab' } | Select-Object -First 1
                if ($minimumPayload) { break }
            }
        }
        if (-not $minimumPayload) { throw 'The Microsoft bundle has no minimum x64 runtime payload.' }
        $minimumCabinet = $null
        foreach ($directory in $cabinetDirectories) {
            $candidate = Join-Path $directory $minimumPayload.SourcePath
            if (Test-Path -LiteralPath $candidate) { $minimumCabinet = $candidate; break }
        }
        if (-not $minimumCabinet) { throw 'Cannot locate the embedded x64 CRT cabinet.' }
        $minimumDirectory = Join-Path $stage 'minimum'
        New-Item -ItemType Directory -Path $minimumDirectory -Force | Out-Null
        & "$env:SystemRoot\System32\expand.exe" $minimumCabinet '-F:*' $minimumDirectory | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to extract the x64 CRT cabinet.' }
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $records = @()
        foreach ($dll in Get-ChildItem -LiteralPath $minimumDirectory -File -Filter '*.dll_amd64') {
            $name = $dll.Name -replace '_amd64$', ''
            $output = Join-Path $destination $name
            Copy-Item -LiteralPath $dll.FullName -Destination $output -Force
            $records += [ordered]@{ name=$name; version=$dll.VersionInfo.FileVersion; sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        foreach ($required in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','vcomp140.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $destination $required))) { throw "Extracted package is missing $required" }
        }
        $records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'files.json') -Encoding UTF8
        Set-Content -LiteralPath $marker -Value $archiveHash -Encoding ASCII
    } finally {
        $resolved = [IO.Path]::GetFullPath($stage)
        if ($resolved.StartsWith((Get-PortablePath 'data/tmp') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
foreach ($runtime in @('runtimes/llama','runtimes/whisper','runtimes/piper')) {
    $directory = Get-PortablePath $runtime
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Get-ChildItem -LiteralPath $destination -File -Filter '*.dll' | Copy-Item -Destination $directory -Force
}
Write-Host 'Microsoft VC++ CRT is available app-locally; no system installation was performed.'
