param([switch]$Abliterated)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Portable.Common.ps1')
Initialize-PortableEnvironment

$python = Get-PortablePath 'runtimes/lora/python/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $python)) {
    $systemPython = Get-Command python.exe -ErrorAction SilentlyContinue
    if (-not $systemPython) { throw 'Install Windows x64 Python 3.12, 3.13 or 3.14 to set up LoRA.' }
    & $systemPython.Source -c 'import sys; sys.exit(0 if (3, 12) <= sys.version_info[:2] <= (3, 14) and sys.maxsize > 2**32 else 1)'
    if ($LASTEXITCODE -ne 0) { throw 'LoRA setup needs Windows x64 Python 3.12, 3.13 or 3.14.' }
    & $systemPython.Source -m venv (Get-PortablePath 'runtimes/lora/python')
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the portable LoRA Python environment.' }
}

Write-Host 'LoRA setup downloads the CUDA trainer and about 9.3 GB of Qwen training weights into runtimes/lora.'
& $python -m pip install --disable-pip-version-check --no-cache-dir --index-url https://download.pytorch.org/whl/cu128 'torch==2.11.0+cu128'
if ($LASTEXITCODE -ne 0) { throw 'Could not install CUDA PyTorch.' }
& $python -m pip install --disable-pip-version-check --no-cache-dir 'transformers==5.22.2' 'peft==0.21.2' 'bitsandbytes==0.50.2' 'accelerate==1.15.0' 'gguf==0.19.0'
if ($LASTEXITCODE -ne 0) { throw 'Could not install the LoRA training dependencies.' }
& $python -c 'import bitsandbytes, gguf, peft, torch; from transformers import Qwen3_5ForConditionalGeneration; assert torch.cuda.is_available() and torch.cuda.is_bf16_supported()'
if ($LASTEXITCODE -ne 0) { throw 'LoRA training needs a working CUDA installation and a bfloat16-capable NVIDIA GPU.' }

$base = 'runtimes/lora/base'
$source = 'https://huggingface.co/Qwen/Qwen3.5-4B/resolve/851bf6e806efd8d0a36b00ddf55e13ccb7b8cd0a'
$weights = [ordered]@{
    'model.safetensors-00001-of-00002.safetensors' = '26a93f066e1916adb13453dae5a0c707c0fbc71299ed98779571a907b8e74c61'
    'model.safetensors-00002-of-00002.safetensors' = 'cb544bd9bfae93dc59b0f22b292f5933573854a7f9b97835c67060d7d910e188'
    'tokenizer.json' = '5f9e4d4901a92b997e463c1f46055088b6cca5ca61a6522d1b9f64c4bb81cb42'
}
$metadata = @('config.json', 'chat_template.jinja', 'tokenizer_config.json', 'model.safetensors.index.json', 'LICENSE')
if ($Abliterated) {
    $base = 'runtimes/lora/amadeus'
    $source = 'https://huggingface.co/Jitheswar/Qwen3.5-4B-Amadeus/resolve/08856cd2f7cef9e1ece851e6eacc12d304ab8ca1'
    $metadata = @()
    $weights = [ordered]@{
        'config.json' = 'a46fd54a8a68048b03522f7ced33f270110da53342c0bc80d060f101f1281c4b'
        'chat_template.jinja' = 'a4aee8afcf2e0711942cf848899be66016f8d14a889ff9ede07bca099c28f715'
        'tokenizer_config.json' = '66d4cdf8346fd22501506383d29f5eaedb43422039ad3450f878accf685b050c'
        'model.safetensors.index.json' = '7680be3a04a0bab63e078a5d6e2b84ad69164b3ac707532711d68288aa7bfd50'
        'LICENSE' = 'bbedc3fda3305820b977265f01b8619d87570a6739de3a5582c3464840f1e57a'
        'tokenizer.json' = 'a5cd9732badce41de57e6efce8302930ded1c1188c5f81feb2bd6c24c4a1941f'
        'model-00001-of-00002.safetensors' = '079560586dccafeff384a06ce493ee08f379c52c8cd7f4b7314b57fa27cc5d04'
        'model-00002-of-00002.safetensors' = '3ddb15184714e246d8b8f1ad8568888124793ffcff92a4e9dd8791f913f2f20b'
        'model-mtp.safetensors' = 'e5fc485d419f2c554169c1fc441f57a1a45a50673bdb4e41d3dd0261b21abe7c'
    }
}
foreach ($name in $metadata) {
    Receive-PortableFile "$source/${name}?download=true" "$base/$name" | Out-Null
}
foreach ($name in $weights.Keys) {
    Receive-PortableFile "$source/${name}?download=true" "$base/$name" $weights[$name] | Out-Null
}
Write-Host 'LoRA is ready. In the console, type /lora help.'
