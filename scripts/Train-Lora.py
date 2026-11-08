"""Train and export a dialogue-only LoRA for the portable Qwen3.5-4B model."""

import argparse
import json
import math
import random
import re
import sys
from pathlib import Path

import gguf
import numpy as np
import torch
from peft import LoraConfig, get_peft_model, get_peft_model_state_dict
from transformers import AutoTokenizer, BitsAndBytesConfig, Qwen3_5ForConditionalGeneration


RANK = 8
ALPHA = 16
MAX_TOKENS = 384
MLP_MODULES = r"model\.language_model\.layers\.\d+\.mlp\.(gate_proj|up_proj|down_proj)"
PEFT_TENSOR = re.compile(r"base_model\.model\.model\.language_model\.layers\.(\d+)\.mlp\.(\w+)\.lora_([AB])\.weight")
VALIDATION_DIALOGUE = [
    {"prompt": "Do you remember the song I never told you the name of?",
     "reply": "I don't know the song yet. Is there a line or a melody you remember?"},
    {"prompt": "Could you slow down a bit? I'm trying to concentrate.",
     "reply": "Sure. I'll give you some quiet."},
    {"prompt": "What's an image you like for the start of autumn?",
     "reply": "One bright leaf caught in the spokes of a bicycle. It makes the season feel like it snuck up on the street."},
    {"prompt": "What is half of forty-six?",
     "reply": "Twenty-three."},
]


def tokenize_examples(tokenizer, request):
    system = {"role": "system", "content": f"You are {request['character']}, a fictional desktop companion. Reply in natural English."}
    examples = request["examples"]
    if not 4 <= len(examples) <= 32:
        raise ValueError("Training needs 4–32 approved examples.")
    tokenized = []
    for number, example in enumerate(examples, 1):
        prompt = example["prompt"].strip()
        reply = example["reply"].strip()
        if not prompt or not reply or any(token in prompt or token in reply for token in tokenizer.all_special_tokens):
            raise ValueError(f"Example {number} must contain ordinary dialogue without model control tokens.")
        messages = [system, {"role": "user", "content": prompt}]
        prefix = tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True, enable_thinking=False)
        full = tokenizer.apply_chat_template(messages + [{"role": "assistant", "content": reply}], tokenize=False)
        if not full.startswith(prefix):
            raise ValueError(f"Example {number} does not match the model's text chat template.")
        prompt_ids = tokenizer.encode(prefix, add_special_tokens=False)
        reply_ids = tokenizer.encode(full[len(prefix):].rstrip("\n"), add_special_tokens=False)
        if not reply_ids or len(prompt_ids) + len(reply_ids) > MAX_TOKENS:
            raise ValueError(f"Example {number} exceeds {MAX_TOKENS} tokens. Remove it and use a shorter exchange.")
        tokenized.append((prompt_ids + reply_ids, len(reply_ids)))
    return tokenized


def loss_for_example(model, example):
    ids, reply_length = example
    tokens = torch.tensor([ids], device="cuda", dtype=torch.long)
    # Use the prompt as context. Score only the final answer and its end token.
    logits = model(input_ids=tokens[:, :-1], use_cache=False, logits_to_keep=reply_length).logits
    return torch.nn.functional.cross_entropy(logits[0].float(), tokens[0, -reply_length:])


def validate_losses(model, examples):
    model.eval()
    with torch.inference_mode():
        values = [loss_for_example(model, example).item() for example in examples]
    if not all(math.isfinite(value) for value in values):
        raise ValueError("Validation loss was not finite.")
    return values


def export_adapter(model, output):
    architecture = gguf.MODEL_ARCH.QWEN35
    name_map = gguf.get_tensor_name_map(architecture, model.config.text_config.num_hidden_layers)
    writer = gguf.GGUFWriter(output, gguf.MODEL_ARCH_NAMES[architecture])
    writer.add_type(gguf.GGUFType.ADAPTER)
    writer.add_string(gguf.Keys.Adapter.TYPE, "lora")
    writer.add_float32(gguf.Keys.Adapter.LORA_ALPHA, ALPHA)
    changed = False
    for name, tensor in get_peft_model_state_dict(model).items():
        match = PEFT_TENSOR.fullmatch(name)
        if match is None:
            raise ValueError(f"Unexpected trained tensor: {name}")
        layer, projection, side = match.groups()
        mapped = name_map.get_name(f"model.layers.{layer}.mlp.{projection}.weight", try_suffixes=(".weight",))
        if mapped is None:
            raise ValueError(f"Cannot map {name} to the portable Qwen3.5 model.")
        weights = tensor.detach().to(dtype=torch.float16, device="cpu").numpy()
        if not np.isfinite(weights).all():
            raise ValueError("Training produced non-finite adapter weights.")
        changed |= side == "B" and bool(np.any(weights))
        writer.add_tensor(f"{mapped}.lora_{side.lower()}", weights)
    if not changed:
        raise ValueError("Training did not change the adapter weights.")
    try:
        writer.write_header_to_file()
        writer.write_kv_data_to_file()
        writer.write_tensors_to_file(progress=False)
    finally:
        writer.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--validate", action="store_true")
    options = parser.parse_args()
    request = json.load(sys.stdin)
    if not torch.cuda.is_available() or not torch.cuda.is_bf16_supported():
        raise RuntimeError("LoRA needs a CUDA GPU with bfloat16 support.")
    if torch.cuda.mem_get_info()[0] < 5 * 1024**3:
        raise RuntimeError("LoRA needs at least 5 GiB of free GPU memory before loading the model.")

    torch.manual_seed(3407)
    torch.set_num_threads(4)
    tokenizer = AutoTokenizer.from_pretrained(options.base, local_files_only=True, trust_remote_code=False)
    examples = tokenize_examples(tokenizer, request)
    validation = tokenize_examples(tokenizer, {**request, "examples": VALIDATION_DIALOGUE}) if options.validate else []
    print(f"Loading the 4-bit training model for {len(examples)} dialogue examples.", flush=True)
    quantization = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_quant_type="nf4",
                                     bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.bfloat16)
    model = Qwen3_5ForConditionalGeneration.from_pretrained(
        options.base, local_files_only=True, use_safetensors=True, dtype=torch.bfloat16,
        quantization_config=quantization, device_map={"": 0}, attn_implementation="sdpa")
    model.requires_grad_(False)
    model.gradient_checkpointing_enable(gradient_checkpointing_kwargs={"use_reentrant": False})
    model = get_peft_model(model, LoraConfig(r=RANK, lora_alpha=ALPHA, target_modules=MLP_MODULES,
                                           lora_dropout=0.0, bias="none", task_type="CAUSAL_LM"))
    parameters = [parameter for parameter in model.parameters() if parameter.requires_grad]
    optimizer = torch.optim.AdamW(parameters, lr=8e-5, foreach=False)
    before = validate_losses(model, validation) if validation else []
    model.train()
    order = list(range(len(examples))) * 2
    random.Random(3407).shuffle(order)
    for step, index in enumerate(order, 1):
        optimizer.zero_grad(set_to_none=True)
        loss = loss_for_example(model, examples[index])
        value = loss.item()
        if not math.isfinite(value):
            raise ValueError("Training loss was not finite.")
        loss.backward()
        torch.nn.utils.clip_grad_norm_(parameters, 1.0, error_if_nonfinite=True)
        optimizer.step()
        if step == 1 or step % 4 == 0 or step == len(order):
            print(f"Training step {step}/{len(order)}. Loss {value:.3f}.", flush=True)

    if validation:
        after = validate_losses(model, validation)
        changes = [new - old for old, new in zip(before, after, strict=True)]
        print(f"Held-out dialogue loss: {sum(before) / len(before):.3f} -> {sum(after) / len(after):.3f}.", flush=True)
        if sum(changes) / len(changes) > 0.25 or max(changes) > 0.9:
            raise ValueError("The new adapter degraded held-out dialogue.")

    export_adapter(model, options.output)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, OSError) as error:
        print(f"LoRA training failed. {error}", file=sys.stderr)
        sys.exit(1)
