param([switch]$SkipModels)
. (Join-Path $PSScriptRoot 'Portable.Common.ps1')

if ($SkipModels) {
    & (Join-Path $PSScriptRoot 'Setup-Deep.ps1') -SkipModels
    return
}

$source = 'https://huggingface.co/Jitheswar/Qwen3.5-4B-Amadeus/resolve/08856cd2f7cef9e1ece851e6eacc12d304ab8ca1'
Receive-PortableFile "$source/Qwen3.5-4B-Amadeus-Q4_K_M.gguf" 'models/llm/qwen35-4b/Qwen3.5-4B-Amadeus-Q4_K_M.gguf' `
    'e633719c3230abfecccf461ecd061034cf2a13006ce7b181be93dbbec976a53c' | Out-Null
Receive-PortableFile "$source/mmproj-Qwen3.5-4B-Amadeus-F16.gguf" 'models/llm/qwen35-4b/mmproj-Qwen3.5-4B-Amadeus-F16.gguf' `
    '8652745fd215c90e816bcae68d7fb8fd6c7979d0c3c0334032ec664d900c09c3' | Out-Null
& (Join-Path $PSScriptRoot 'Setup-Deep.ps1') -Abliterated

# Activate the pair only after both checkpoints have been verified.
New-Item -ItemType File -Path (Get-PortablePath 'runtimes/abliteration.installed') -Force | Out-Null
Write-Host 'Abliterated models are installed. Restart ComputerNeighbour to use them, or set model.abliterated=false to use the original models.'
