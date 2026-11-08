param([switch]$SkipModels, [switch]$Abliterated)
. (Join-Path $PSScriptRoot 'Portable.Common.ps1')

$runtimeVersion = 'v0.40.15'
$runtimeZip = Receive-PortableFile "https://github.com/ollama/ollama/releases/download/$runtimeVersion/ollama-windows-amd64.zip" `
    "runtimes/downloads/ollama-$runtimeVersion-windows-amd64.zip" '9ca74a7ab66ec86870b1eecac7df160df08dc4e3a291ab5d2d59bcf9310221c6'
Expand-PortableRuntime $runtimeZip 'runtimes/ollama' 'ollama.exe'
if ($SkipModels) { return }

$repository = 'library/qwen3.8'
$manifestName = 'qwen3.8-27b-manifest.json'
$manifestHash = 'aaee06c39dcf2437cde036998d960e1fc1494b8191be7cc9657d01e509097813'
if ($Abliterated) {
    $repository = 'huihui_ai/qwen3.8-abliterated'
    $manifestName = 'qwen3.8-abliterated-27b-manifest.json'
    $manifestHash = 'bdc5e13cd1645bc409e95a7d9e864dc3ce3e5fe5beeb24a43c193e8041edd3e0'
}
$registry = "https://registry.ollama.ai/v2/$repository"
$manifest = Receive-PortableFile "$registry/manifests/27b" "runtimes/downloads/$manifestName" $manifestHash
$model = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
foreach ($layer in @($model.config) + @($model.layers)) {
    if ($layer.digest -notmatch '^sha256:[a-f0-9]{64}$') { throw 'Unexpected Ollama layer digest.' }
    $name = $layer.digest.Replace(':','-')
    Receive-PortableFile "$registry/blobs/$($layer.digest)" "runtimes/ollama/models/blobs/$name" $layer.digest.Substring(7) | Out-Null
}

# A manifest makes the model visible to Ollama. Publish it only once all layers are verified.
$installed = Get-PortablePath "runtimes/ollama/models/manifests/registry.ollama.ai/$repository/27b"
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($installed)) -Force | Out-Null
Copy-Item -LiteralPath $manifest -Destination ($installed + '.part') -Force
Move-Item -LiteralPath ($installed + '.part') -Destination $installed -Force
Write-Host "Local deep analysis is ready with ${repository}:27b."
