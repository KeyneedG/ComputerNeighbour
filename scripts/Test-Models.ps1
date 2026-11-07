param([int]$Port = 18083, [switch]$SkipLlama)
. (Join-Path $PSScriptRoot 'Portable.Common.ps1')
Initialize-PortableEnvironment
$configPath = Get-PortablePath 'config/appsettings.json'
if (-not (Test-Path -LiteralPath $configPath)) { throw 'Run Start.cmd --init first to create config/appsettings.json.' }
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$testDirectory = Get-PortablePath 'data/tmp/model-smoke'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$server = $null
try {
    $wav = Join-Path $testDirectory 'voice.wav'
    'Hello, my name is Mira. I can understand you and help you on this computer.' | & (Get-PortablePath 'runtimes/piper/piper.exe') --model (Get-PortablePath 'models/piper/en_US-lessac-medium.onnx') --output_file $wav
    if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $wav).Length -lt 1000) { throw 'Piper synthesis failed.' }
    & (Get-PortablePath 'runtimes/whisper/whisper-cli.exe') -m (Get-PortablePath 'models/whisper/ggml-base.bin') -f $wav -l auto -otxt -of (Join-Path $testDirectory 'transcript') -nt
    if ($LASTEXITCODE -ne 0) { throw 'Whisper transcription failed.' }
    $transcript = Get-Content -LiteralPath (Join-Path $testDirectory 'transcript.txt') -Raw
    if ($transcript -notmatch '(?i)hello' -or $transcript -notmatch '(?i)computer') { throw "Unexpected Piper-to-Whisper transcript: $transcript" }
    Write-Host ('PASS Piper -> Whisper: ' + $transcript.Trim())
    if (-not $SkipLlama) {
        $arguments = @('-m',('"' + (Get-PortablePath $config.model.modelFile) + '"'),'--mmproj',('"' + (Get-PortablePath $config.model.projectorFile) + '"'),'--host','127.0.0.1','--port',$Port.ToString(),'--n-gpu-layers',$config.model.gpuLayers.ToString(),'--ctx-size','4096','--parallel','1','--jinja','--no-webui','--image-min-tokens','1024','--image-max-tokens','1024')
        $server = Start-Process -FilePath (Get-PortablePath $config.model.executable) -ArgumentList $arguments -WorkingDirectory $script:NeighbourRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testDirectory 'server.out.log') -RedirectStandardError (Join-Path $testDirectory 'server.err.log')
        $baseUrl = 'http://127.0.0.1:' + $Port
        $ready = $false
        $deadline = [DateTimeOffset]::Now.AddSeconds(90)
        while ([DateTimeOffset]::Now -lt $deadline) {
            if ($server.HasExited) { throw ('llama-server exited: ' + (Get-Content -LiteralPath (Join-Path $testDirectory 'server.err.log') -Tail 15 | Out-String)) }
            try { $health = Invoke-RestMethod -Uri ($baseUrl + '/health') -TimeoutSec 2; if ($health.status -eq 'ok') { $ready = $true; break } } catch { }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw 'llama-server did not become ready in 90 seconds.' }
        $request = @{ model='local'; temperature=0; max_tokens=80; chat_template_kwargs=@{enable_thinking=$false}; messages=@(@{ role='system'; content='Always reply in English.' },@{ role='user'; content='Ответь по-английски одним коротким предложением: ты меня понимаешь? Answer briefly in English.' }) } | ConvertTo-Json -Depth 8
        $reply = Invoke-RestMethod -Uri ($baseUrl + '/v1/chat/completions') -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($request)) -TimeoutSec 90
        if (-not $reply.choices[0].message.content) { throw 'Empty text model response.' }
        if ($reply.choices[0].message.content -match '[А-Яа-яЁё]') { throw 'The model did not follow the English-only answer instruction.' }
        Write-Host ('PASS Russian input -> English output: ' + $reply.choices[0].message.content)
        Add-Type -AssemblyName System.Drawing
        $bitmap = New-Object System.Drawing.Bitmap 224,224
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $font = New-Object Drawing.Font 'Arial',36
        $pngPath = Join-Path $testDirectory 'red-test.png'
        try {
            $graphics.Clear([Drawing.Color]::Red)
            $graphics.DrawString('TEST',$font,[Drawing.Brushes]::White,20,75)
            $bitmap.Save($pngPath,[Drawing.Imaging.ImageFormat]::Png)
        } finally { $font.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
        $dataUrl = 'data:image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($pngPath))
        $request = @{ model='local'; temperature=0; max_tokens=80; chat_template_kwargs=@{enable_thinking=$false}; messages=@(@{ role='user'; content=@(@{type='text';text='What color is the background, and what word is written? Answer briefly in English.'},@{type='image_url';image_url=@{url=$dataUrl}})}) } | ConvertTo-Json -Depth 10
        $reply = Invoke-RestMethod -Uri ($baseUrl + '/v1/chat/completions') -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($request)) -TimeoutSec 120
        $text = $reply.choices[0].message.content
        if ($text -notmatch '(?i)red' -or $text -notmatch '(?i)test') { throw "Vision smoke test failed: $text" }
        Write-Host ('PASS vision: ' + $text)
    }
    Write-Host 'All selected model smoke tests passed. Test artifacts are in data/tmp/model-smoke.'
} finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
