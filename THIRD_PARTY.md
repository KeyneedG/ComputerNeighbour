# Переносимые компоненты

Приложение использует неизменённые готовые сборки, загружаемые `Setup.cmd`. Точные ссылки, версии, размеры и ожидаемые SHA-256 находятся в `models/dependencies.json`. Фактические контрольные суммы загрузок записываются в `models/installed-lock.json`. Если издатель не опубликовал SHA-256, зафиксированная после загрузки сумма проверяет последующую целостность, но сама по себе не подтверждает происхождение файла.

| Компонент | Источник | Лицензия / примечание |
| --- | --- | --- |
| llama.cpp b12110 | https://github.com/ggml-org/llama.cpp/releases/tag/b12110 | MIT. Стабильная версия v0.9.0 ссылается на эту бинарную сборку. |
| Qwen3.5-4B Q4_K_M и F16 projector (по умолчанию) | https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/tree/e87f176479d0855a907a41277aca2f8ee7a09523 | Apache-2.0 исходной Qwen/Qwen3.5-4B: https://huggingface.co/Qwen/Qwen3.5-4B/blob/main/LICENSE. Модель и projector из одной зафиксированной ревизии; SHA-256 проверены по LFS. Карточка, лицензия и происхождение сохранены рядом с моделью. |
| Qwen2.5-VL-3B-Instruct Q4_K_M и Q8 projector (старый ручной fallback) | https://huggingface.co/ggml-org/Qwen2.5-VL-3B-Instruct-GGUF | Старые файлы сохранены без изменения и больше не являются стандартной загрузкой. Они остаются под Qwen Research License: https://huggingface.co/Qwen/Qwen2.5-VL-3B-Instruct/blob/main/LICENSE. |
| whisper.cpp v1.9.2 | https://github.com/ggml-org/whisper.cpp/releases/tag/v1.9.2 | MIT. Многоязычная base, не base.en; понимает русский и английский. |
| Whisper base | https://huggingface.co/ggerganov/whisper.cpp | Модель OpenAI Whisper, MIT: https://github.com/openai/whisper/blob/main/LICENSE. |
| speech.cpp 0.10.1 | https://github.com/nyosegawa/speech.cpp/releases/tag/v0.10.1 | Переносимая Windows-сборка для Qwen3-TTS, MIT. |
| Qwen3-TTS 1.7B CustomVoice Q5_K | https://huggingface.co/sakasegawa/Qwen3-TTS-12Hz-1.7B-CustomVoice-GGUF | GGUF модели [Qwen](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice) под Apache 2.0. Голос по умолчанию Ono Anna. |
| Piper 2023.11.14-2 | https://github.com/rhasspy/piper/releases/tag/2023.11.14-2 | Архивная переносимая Windows-сборка исходного Piper, MIT. Встроенные зависимости, включая espeak-ng, имеют собственные лицензии; сохраняйте содержимое архива при распространении. Новый Piper https://github.com/OHF-Voice/piper1-gpl имеет GPLv3 и другой цикл поставки. |
| en_US-lessac-medium | https://huggingface.co/rhasspy/piper-voices/tree/main/en/en_US/lessac/medium | Женский английский голос. Карточка модели скачивается рядом с ONNX. |
| .NET 10 | https://dotnet.microsoft.com/ | MIT и лицензии компонентов, см. LICENSE.txt / ThirdPartyNotices.txt в опубликованном runtime. |
| Microsoft VC++ v14 x64 CRT 14.51.36247 | https://aka.ms/vc14/vc_redist.x64.exe | Microsoft Software License Terms: https://aka.ms/VCRedistLicense. Официальный подписанный пакет сохранён; SHA-256 и прямая ссылка зафиксированы в manifest. Из CAB извлекаются локальные DLL, EXE/MSI не запускаются. Копия страницы лицензии: `models/licenses/Microsoft-VCRedist-LICENSE.html`. |

Для запуска модели с изображениями нужен соответствующий projector `models/llm/qwen35-4b/mmproj-F16.gguf`. Сам файл языковой модели не заменяет vision encoder. Общая загрузка стандартной конфигурации — около 5,3 ГБ. После распаковки и публикации потребуется дополнительное место. Сохранённый старый fallback занимает дополнительно около 2,8 ГБ. Базовый backend Vulkan работает через уже установленный видеодрайвер, отдельный CUDA Toolkit не нужен. На машине без подходящего GPU запустите `Setup.cmd -Backend cpu` и задайте `model.gpuLayers: 0` и `audio.qwenTtsDevice: "cpu"`.

## Повторная сборка и перенос

Дополнительный `Setup.cmd -Only Qwen3.8` устанавливает переносимую [Ollama v0.40.15](https://github.com/ollama/ollama/releases/tag/v0.40.15) и [Qwen3.8-27B Q4_K_M](https://ollama.com/library/qwen3.8:27b) под Apache-2.0. Установка проверяет SHA-256 архива, манифеста и каждого слоя модели. Версии и контрольные суммы записаны в `scripts/Setup-Deep.ps1`.

`Setup.cmd -Only Abliteration` устанавливает Q4_K_M версии Qwen3.5-4B Amadeus и Huihui Qwen3.8-27B с лицензией Apache-2.0. Скрипты `scripts/Setup-Abliteration.ps1` и `scripts/Setup-Deep.ps1` проверяют SHA-256 весов, проектора, манифеста Ollama и его слоёв. [Зафиксированная ревизия Amadeus](https://huggingface.co/Jitheswar/Qwen3.5-4B-Amadeus/tree/08856cd2f7cef9e1ece851e6eacc12d304ab8ca1) · [Карточка Huihui](https://ollama.com/huihui_ai/Qwen3.8-abliterated:27b)

Дополнительный `Setup.cmd -Only LoRA` устанавливает CUDA PyTorch, Transformers, PEFT, bitsandbytes, Accelerate и GGUF в отдельное окружение `runtimes/lora/python`. Версии зафиксированы в `scripts/Setup-Lora.ps1`. Тренер использует [Qwen3.5-4B](https://huggingface.co/Qwen/Qwen3.5-4B/tree/851bf6e806efd8d0a36b00ddf55e13ccb7b8cd0a) под Apache-2.0. Скрипт проверяет SHA-256 весов и скачивает лицензию рядом с ними.

`Build.cmd` использует имеющийся .NET 10 SDK; если его нет, скачивает приватный официальный SDK в `runtimes/dotnet`, проверяя SHA-512 из release metadata Microsoft. Публикация содержит .NET runtime, поэтому на другом компьютере SDK и установленный .NET не требуются. Windows и видеодрайвер остаются системными зависимостями. Само приложение не устанавливает службы, задачи планировщика, ключи реестра или модели за пределами этой папки. ОС, антивирус и драйверы могут вести свои системные журналы и кэши — переносимое приложение не может запретить это.

Примеры: `Setup.cmd -SkipBuild`, `Setup.cmd -Only Whisper -SkipBuild`, `Setup.cmd -Only Runtimes`, `Setup.cmd -SkipModels`, `Build.cmd -SkipTests`. Все собственные временные файлы, кэши NuGet/.NET и архивы располагаются внутри `ComputerNeighbour`.

Статический аудит PE import/delay-import таблиц обнаружил обязательные MSVCP140, VCRUNTIME140, VCRUNTIME140_1 и VCOMP140 в сторонних движках. Поэтому `Setup.cmd` также извлекает минимальный x64 VC++ CRT из официального CAB в `runtimes/vcruntime` и размещает эти библиотеки рядом с каждым native EXE. Системный Visual C++ Redistributable не устанавливается. Происхождение и суммы отдельных DLL находятся в `runtimes/vcruntime/files.json`. Повторить аудит можно командой `python scripts/Inspect-NativeImports.py`. Обычный запуск и стандартная установка работают без Python.
