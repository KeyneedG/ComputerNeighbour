import math
import json
import random
import time
from uuid import uuid4

from .brain import chat_messages, load_base


class TrainingInterrupted(Exception):
    pass


class Trainer:
    def __init__(self, store, brain, settings):
        self.store = store
        self.brain = brain
        self.settings = settings

    def rows(self):
        cursor = self.store.get("training_cursor", 0)
        cfg = self.settings
        query = "SELECT * FROM examples WHERE "
        fresh = list(self.store.db.execute(
            query + "held_out = 0 AND id > ? ORDER BY id LIMIT ?", (cursor, cfg["min_new"])
        ))
        validation = list(self.store.db.execute(
            query + "held_out = 1 ORDER BY id LIMIT ?", (cfg["validation"],)
        ))
        replay = list(self.store.db.execute(
            query + "held_out = 0 AND id <= ? ORDER BY id DESC LIMIT ?", (cursor, cfg["replay"])
        ))
        return fresh, validation, replay

    def run(self, stop, announce, audit=None):
        cfg = self.settings
        if not cfg["enabled"]:
            return "LoRA отключена."
        fresh, validation, replay = self.rows()
        if len(fresh) < cfg["min_new"] or len(validation) < cfg["validation"]:
            return f"Датасет LoRA: новых {len(fresh)}/{cfg['min_new']}, контрольных {len(validation)}/{cfg['validation']}."
        self.brain.reset()
        try:
            return self.fit(fresh, validation, replay, stop, announce, audit)
        except TrainingInterrupted:
            return "Обучение прервано входящим сообщением. Текущий адаптер сохранён."
        finally:
            self.brain.reset()

    def fit(self, fresh, validation, replay, stop, announce, audit=None):
        import torch
        from peft import LoraConfig, PeftModel, get_peft_model
        from torch.nn.functional import cross_entropy

        cfg = self.settings
        parent = self.store.active_adapter()
        directory = self.store.adapter_dir
        cutoff = fresh[-1]["id"]
        announce(f"LoRA: {len(fresh)} новых, {len(replay)} повторных, {len(validation)} контрольных.")
        if stop():
            raise TrainingInterrupted
        base, processor = load_base(self.brain.settings)
        for parameter in base.parameters():
            parameter.requires_grad_(False)
        base.gradient_checkpointing_enable(gradient_checkpointing_kwargs={"use_reentrant": False})
        if parent:
            model = PeftModel.from_pretrained(base, directory / parent, is_trainable=True)
        else:
            model = get_peft_model(base, LoraConfig(
                r=cfg["rank"], lora_alpha=2 * cfg["rank"], lora_dropout=0.0,
                target_modules=["q_proj", "k_proj", "v_proj", "o_proj", "gate_proj", "up_proj", "down_proj"],
                task_type="CAUSAL_LM",
            ))
        tokenizer = processor.tokenizer

        def tokens(row):
            if row["instruction"]:
                image = None
                if row["image"]:
                    from PIL import Image
                    path = (self.store.directory / row["image"]).resolve()
                    if not path.is_relative_to(self.store.directory.resolve()):
                        raise ValueError("Снимок обучения должен находиться в папке состояния")
                    with Image.open(path) as file:
                        image = file.convert("RGB")
                messages = chat_messages(row["instruction"], json.loads(row["payload"]), image)
                inputs = processor.apply_chat_template(
                    messages, tokenize=True, add_generation_prompt=True, enable_thinking=False,
                    return_dict=True, return_tensors="pt",
                )
            else:
                inputs = tokenizer.apply_chat_template(
                    [{"role": "user", "content": row["question"]}], tokenize=True,
                    add_generation_prompt=True, enable_thinking=False,
                    return_dict=True, return_tensors="pt",
                )
            target = tokenizer.encode(row["answer"] + tokenizer.eos_token, add_special_tokens=False)
            limit = cfg.get("vision_tokens", cfg["max_tokens"]) if row["image"] else cfg["max_tokens"]
            if inputs["input_ids"].shape[-1] + len(target) > limit:
                return None
            inputs["input_ids"] = torch.cat(
                [inputs["input_ids"], torch.tensor([target], dtype=torch.long)], dim=1
            )
            inputs["attention_mask"] = torch.ones_like(inputs["input_ids"])
            if "mm_token_type_ids" in inputs:
                inputs["mm_token_type_ids"] = torch.cat([
                    inputs["mm_token_type_ids"], torch.zeros((1, len(target)), dtype=torch.long)
                ], dim=1)
            return dict(inputs), len(target)

        def loss(example):
            if stop():
                raise TrainingInterrupted
            sequence, target_length = example
            inputs = {name: value.to(model.device) for name, value in sequence.items()}
            logits = model(**inputs, use_cache=False, logits_to_keep=target_length + 1).logits
            return cross_entropy(logits[0, :-1].float(), inputs["input_ids"][0, -target_length:])

        def evaluate(data):
            model.eval()
            with torch.no_grad():
                return sum(loss(row).item() for row in data) / len(data)

        new = [item for row in fresh if (item := tokens(row))]
        check = [item for row in validation if (item := tokens(row))]
        train = new + [item for row in replay if (item := tokens(row))]
        if not new or not check:
            raise ValueError("Увеличьте lora.max_tokens для обучающей и контрольной выборок")
        sample = new[: min(4, len(new))]
        before, learn_before = evaluate(check), evaluate(sample)
        audit_before = audit(model.eval(), processor, "before") if audit else None
        optimizer = torch.optim.AdamW(
            [p for p in model.parameters() if p.requires_grad], lr=cfg["learning_rate"]
        )
        order = random.Random(cutoff)
        model.train()
        for epoch in range(cfg["epochs"]):
            order.shuffle(train)
            for index, example in enumerate(train):
                optimizer.zero_grad(set_to_none=True)
                step = loss(example)
                if not math.isfinite(step.item()):
                    raise ValueError("Потеря LoRA стала нечисловой")
                step.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0, error_if_nonfinite=True)
                optimizer.step()
                if index % 8 == 0:
                    announce(f"LoRA: эпоха {epoch + 1}/{cfg['epochs']}, шаг {index + 1}/{len(train)}, ошибка {step.item():.4f}")
        after, learn_after = evaluate(check), evaluate(sample)
        audit_after = audit(model.eval(), processor, "after") if audit else None
        if stop():
            raise TrainingInterrupted
        metrics = f"контроль {before:.4f} → {after:.4f}, новые {learn_before:.4f} → {learn_after:.4f}"
        if audit:
            self.store.set("browser_training_audit", {"before": audit_before, "after": audit_after})
            announce(f"Проверка действий: {audit_before} → {audit_after}")
        if not all(map(math.isfinite, (before, after, learn_before, learn_after))) or (
            after > before + cfg["max_regression"] or learn_after >= learn_before
            or (audit is not None and not audit_after["passed"])
        ):
            self.store.set("training_cursor", cutoff)
            return f"Кандидат LoRA отклонён ({metrics})."
        identifier = f"{time.strftime('%Y%m%d-%H%M%S')}-{uuid4().hex[:8]}"
        candidate = directory / (".candidate-" + identifier)
        model.save_pretrained(candidate, safe_serialization=True, save_embedding_layers=False)
        candidate.rename(directory / identifier)
        self.store.promote(identifier, parent, cutoff, before, after)
        return f"Активирован LoRA {identifier} ({metrics})."
