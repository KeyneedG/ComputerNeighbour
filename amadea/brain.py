import gc
import json
import re

from .state import ROOT, encode


class ContextFull(ValueError):
    pass


def chat_messages(instruction, payload, image=None):
    content = encode(payload)
    content = re.sub(r"<(\|[^<>]*\|)>", r"＜\1＞", content)
    if image is not None:
        content = [{"type": "image", "image": image}, {"type": "text", "text": content}]
    return [{"role": "system", "content": instruction}, {"role": "user", "content": content}]


def parse_object(text):
    text = text.rsplit("</think>", 1)[-1].strip()
    if text.startswith("```") and text.endswith("```"):
        text = text.partition("\n")[2][:-3].strip()
    value = json.loads(text, parse_constant=lambda value: None)
    if not isinstance(value, dict):
        raise ValueError("Модель должна вернуть объект JSON")
    return value


def load_base(settings):
    import torch
    from transformers import AutoProcessor, BitsAndBytesConfig, Qwen3_5ForConditionalGeneration

    location = ROOT / "models" / "brain"
    if not (location / "model.safetensors.index.json").is_file():
        raise FileNotFoundError("Веса Amadeus отсутствуют. Запустите setup.cmd")
    device = settings["device"]
    if device == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA недоступна. Проверьте драйвер NVIDIA.")
    if device == "cuda":
        torch.set_float32_matmul_precision("high")
        if settings.get("fast_kernels", False):
            from .acceleration import enable
            enable(torch)
    dtype = torch.bfloat16 if device == "cuda" else torch.float32
    options = {"device_map": {"": device}, "dtype": dtype, "local_files_only": True,
               "trust_remote_code": False, "use_safetensors": True}
    if settings["quantize"] and device == "cuda":
        options["quantization_config"] = BitsAndBytesConfig(
            load_in_4bit=True, bnb_4bit_quant_type="nf4", bnb_4bit_use_double_quant=True,
            bnb_4bit_compute_dtype=dtype,
        )
    processor = AutoProcessor.from_pretrained(location, local_files_only=True, trust_remote_code=False)
    return Qwen3_5ForConditionalGeneration.from_pretrained(location, **options), processor


class Brain:
    def __init__(self, store, settings):
        self.store = store
        self.settings = settings
        self.model = self.processor = self.adapter = None

    def reset(self):
        self.model = self.processor = self.adapter = None
        gc.collect()
        import torch
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    def load(self):
        adapter = self.store.active_adapter()
        if self.model is not None and self.adapter == adapter:
            return
        if self.model is not None:
            self.reset()
        model, processor = load_base(self.settings)
        if adapter:
            from peft import PeftModel
            model = PeftModel.from_pretrained(model, self.store.adapter_dir / adapter)
        self.model, self.processor, self.adapter = model.eval(), processor, adapter

    def ask_json(self, instruction, payload, *, image=None, temperature=None):
        import torch

        self.load()
        tokenizer = self.processor.tokenizer
        messages = chat_messages(instruction, payload, image)
        inputs = self.processor.apply_chat_template(
            messages, tokenize=True, add_generation_prompt=True, enable_thinking=False,
            return_dict=True, return_tensors="pt",
        ).to(self.model.device)
        length = inputs["input_ids"].shape[-1]
        limit = self.settings["context_tokens"] - length
        if limit < 48:
            raise ContextFull(f"Контекст занят ({length} токенов). Увеличьте brain.context_tokens")
        temperature = self.settings["temperature"] if temperature is None else temperature
        temperatures = (temperature, 0.0) if temperature > 0 else (0.0,)
        for attempt in temperatures:
            with torch.inference_mode():
                output = self.model.generate(
                    **inputs, max_new_tokens=min(limit, self.settings["response_tokens"]),
                    do_sample=attempt > 0, temperature=attempt or 1.0,
                    pad_token_id=tokenizer.eos_token_id,
                )
            try:
                return parse_object(tokenizer.decode(output[0, length:], skip_special_tokens=True))
            except ValueError:
                if attempt == 0:
                    raise
