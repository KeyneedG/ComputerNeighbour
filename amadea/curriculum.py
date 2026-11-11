"""Generate varied chat pages, train visual actions, and evaluate unseen layouts."""

import hashlib
import io
import json
import os
import random

from .brain import Brain
from .browser import POLICY, editor_state, payload, perform, predict
from .console import session_lock, settings
from .lora import Trainer
from .state import ROOT, Store, encode


def document(seed):
    rng = random.Random(seed)
    left = rng.randint(30, 140)
    width = rng.randint(440, 590)
    top = rng.randint(310, 465)
    dark = rng.choice((True, False))
    background, foreground, surface = ("#14202b", "#edf1f6", "#243644") if dark else ("#f3f4f7", "#172630", "#ffffff")
    a, b = rng.randint(12, 90), rng.randint(10, 70)
    question = rng.choice((f"What is {a} + {b}?", f"Compute {a} plus {b}.", f"Сколько будет {a} + {b}?"))
    answer = f"{a} + {b} = {a + b}."
    heading = rng.choice(("Research chat", "Ask a tutor", "Learning space", "Диалог"))
    placeholder = rng.choice(("Write a question", "Ask anything", "Your message", "Введите вопрос"))
    button = rng.choice(("Send", "Ask", "Отправить", "→"))
    source = f"""<!doctype html><html><meta charset="utf-8"><style>
    *{{box-sizing:border-box}}body{{margin:0;background:{background};color:{foreground};font:18px Inter,sans-serif}}
    header{{padding:25px 34px;border-bottom:1px solid #7d8b9638;font-size:25px}}
    main{{margin:24px {left}px;width:{width}px}}.bubble{{padding:20px;background:{surface};border-radius:14px;margin-bottom:12px}}
    #status{{color:#8095aa;padding:20px}}#answer{{line-height:1.6}}
    form{{position:absolute;left:{left}px;top:{top}px;width:{width}px;display:flex;gap:12px;padding:12px;background:{surface};border-radius:16px}}
    input{{width:100%;min-width:0;padding:15px;border:2px solid #7b899a;border-radius:9px;font:17px Inter,sans-serif;background:{surface};color:{foreground}}}
    input:focus{{outline:3px solid #3190fd;outline-offset:2px}}button{{background:#326cba;color:white;border:0;border-radius:10px;padding:14px;font-size:17px}}
    [hidden]{{display:none!important}}</style>
    <header>{heading}</header><main>
    <div id="intro">Start a conversation with the tutor.</div>
    <div id="sent" class="bubble" hidden></div>
    <div id="status" hidden>Generating response…</div>
    <div id="answer" class="bubble" hidden></div></main>
    <form><input placeholder="{placeholder}" aria-label="Question"><button>{button}</button></form>
    <script>
    window.expected={json.dumps(answer)};window.submissions=[];
    document.querySelector('form').addEventListener('submit',event=>{{
      event.preventDefault();let input=document.querySelector('input');if(!input.value.trim())return;
      window.submissions.push(input.value);document.querySelector('#intro').hidden=true;
      document.querySelector('#sent').textContent=input.value;document.querySelector('#sent').hidden=false;
      input.value='';document.querySelector('#status').hidden=false;
      document.querySelector('#answer').hidden=true;window.ready=false;
    }});
    window.showAnswer=()=>{{document.querySelector('#status').hidden=true;
      let el=document.querySelector('#answer');el.textContent=window.expected;el.hidden=false;window.ready=true;}};
    </script></html>"""
    return source, question, answer


def screenshot(page):
    from PIL import Image
    return Image.open(io.BytesIO(page.screenshot())).convert("RGB")


def build(store, cfg, announce):
    from playwright.sync_api import sync_playwright

    directory = store.directory / "browser-training"
    directory.mkdir(exist_ok=True)
    training = cfg["browser_training"]
    total = training["layouts"] + training["held_out_layouts"]
    with sync_playwright() as engine:
        browser = engine.chromium.launch(headless=True)
        page = browser.new_page(viewport={"width": cfg["browser"]["width"], "height": cfg["browser"]["height"]})
        for layout in range(total):
            held_out = layout >= training["layouts"]
            source, question, answer = document(training["seed"] + layout)
            group = f"browser-curriculum:{training['seed']}:{layout}"
            existing = list(store.db.execute(
                "SELECT image, payload FROM examples WHERE source=? AND held_out=?", (group, int(held_out))
            ))
            if len(existing) == 5 and all(
                (store.directory / row["image"]).is_file()
                and json.loads(row["payload"])["viewport"] == page.viewport_size
                for row in existing
            ):
                continue
            page.set_content(source)
            url = f"https://lesson-{layout}.example/chat"
            history = []
            bounds = page.locator("input").bounding_box()
            box = page.viewport_size
            click = {
                "action": "click",
                "x": round((bounds["x"] + bounds["width"] / 2) * 1000 / (box["width"] - 1)),
                "y": round((bounds["y"] + bounds["height"] / 2) * 1000 / (box["height"] - 1)),
            }
            actions = [click, {"action": "type", "text": question}, {"action": "key", "key": "Enter"},
                       {"action": "wait"}, {"action": "finish", "answer": answer}]
            for step, action in enumerate(actions):
                observation = payload(question, url, (box["width"], box["height"]), history)
                observation["editor"] = editor_state(page, question)
                expected = {"action": action["action"], "input_bounds": bounds, "answer": answer}
                fingerprint = hashlib.sha256(
                    encode([training["seed"], layout, step, action, box, source, POLICY]).encode()
                ).hexdigest()
                path = directory / f"{fingerprint}.png"
                if not path.exists():
                    page.screenshot(path=path)
                with store.db:
                    store.db.execute(
                        """INSERT OR IGNORE INTO examples
                           (source,question,answer,evidence,held_out,fingerprint,instruction,payload,image)
                           VALUES (?,?,?,?,?,?,?,?,?)""",
                        (group, question, encode(action),
                         encode(expected), int(held_out), fingerprint, POLICY, encode(observation),
                         path.relative_to(store.directory).as_posix()),
                    )
                perform(page, action, question)
                if action["action"] == "wait":
                    page.evaluate("window.showAnswer()")
                history.append({"action": action, "outcome": "executed"})
        browser.close()
    announce(f"Учебные страницы: {training['layouts']}, отложенные: {training['held_out_layouts']}.")


def correct(action, expected, observation):
    kind = expected["action"]
    if action.get("action") != kind:
        return False
    if kind == "click":
        size = observation["viewport"]
        bounds = expected["input_bounds"]
        x, y = action.get("x"), action.get("y")
        return (
            type(x) in (int, float) and type(y) in (int, float)
            and bounds["x"] <= x * (size["width"] - 1) / 1000 <= bounds["x"] + bounds["width"]
            and bounds["y"] <= y * (size["height"] - 1) / 1000 <= bounds["y"] + bounds["height"]
        )
    if kind == "type":
        return action.get("text") == observation["question"]
    if kind == "key":
        return action.get("key") == "Enter"
    if kind == "finish":
        answer = action.get("answer")
        return isinstance(answer, str) and expected["answer"] in answer
    return True


class Audit:
    def __init__(self, store, cfg, announce):
        self.store, self.cfg, self.announce = store, cfg, announce
        self.baseline = None

    def __call__(self, model, processor, phase):
        from PIL import Image

        brain = Brain(self.store, self.cfg["brain"])
        brain.model, brain.processor, brain.adapter = model, processor, self.store.active_adapter()
        rows = list(self.store.db.execute(
            "SELECT * FROM examples WHERE held_out=1 AND source LIKE 'browser-curriculum:%' ORDER BY id LIMIT 10"
        ))
        results = []
        for row in rows:
            observation = json.loads(row["payload"])
            with Image.open(self.store.directory / row["image"]) as image:
                try:
                    action = predict(brain, image.convert("RGB"), observation)
                    passed = correct(action, json.loads(row["evidence"]), observation)
                except ValueError:
                    action, passed = {}, False
            results.append({"id": row["id"], "correct": passed, "action": action})
            self.announce(f"Проверка {phase}: {len(results)}/{len(rows)}, {passed}, {action}")
        count = sum(row["correct"] for row in results)
        if phase == "before":
            self.baseline = count
        result = {"correct": count, "total": len(rows),
                  "passed": count >= max(self.baseline or 0, len(rows) - 2)}
        (self.store.directory / "browser-training" / f"audit-{phase}.json").write_text(
            encode({"metrics": result, "predictions": results}), encoding="utf-8"
        )
        return result


def end_to_end(store, cfg, announce):
    from playwright.sync_api import sync_playwright

    brain = Brain(store, cfg["brain"])
    trials = []
    with sync_playwright() as engine:
        browser = engine.chromium.launch(headless=True)
        page = browser.new_page(viewport={"width": cfg["browser"]["width"], "height": cfg["browser"]["height"]})
        for trial in range(2):
            source, question, expected = document(cfg["browser_training"]["seed"] + 1000 + trial)
            page.set_content(source)
            history = []
            success = False
            for step in range(10):
                if page.evaluate("window.submissions.length > 0") and step > 3:
                    page.evaluate("window.showAnswer()")
                observation = payload(
                    question, "https://unseen.example/chat", (cfg["browser"]["width"], cfg["browser"]["height"]),
                    history[-cfg["browser"]["history_steps"]:], steps_left=10-step,
                )
                observation["editor"] = editor_state(page, question)
                action = predict(brain, screenshot(page), observation)
                announce(f"Новый макет {trial + 1}, шаг {step + 1}: {action}")
                if action.get("action") == "finish":
                    submitted = page.evaluate("window.submissions")
                    actual = action.get("answer")
                    success = (question in submitted and page.evaluate("window.ready === true")
                               and isinstance(actual, str) and expected in actual)
                    break
                try:
                    perform(page, action, question)
                    outcome = "executed"
                except ValueError as error:
                    outcome = str(error)
                history.append({"action": action, "outcome": outcome})
            trials.append({"question": question, "success": success, "history": history})
        browser.close()
    brain.reset()
    store.set("browser_end_to_end", trials)
    return trials


def main():
    os.environ["PLAYWRIGHT_BROWSERS_PATH"] = str(ROOT / ".runtime" / "browsers")
    cfg = settings()
    directory = ROOT / "state"
    directory.mkdir(exist_ok=True)
    announce = lambda text: print(text, flush=True)
    with session_lock(directory / "session.lock"):
        store = Store(directory / "memory.sqlite3")
        try:
            build(store, cfg, announce)
            options = {**cfg["lora"], **{key: cfg["browser_training"][key] for key in
                                       ("epochs", "learning_rate", "max_regression")}}
            cursor = store.get("training_cursor", 0)
            options["min_new"] = store.db.execute(
                "SELECT count(*) FROM examples WHERE held_out=0 AND id>?", (cursor,)
            ).fetchone()[0]
            options["validation"] = store.db.execute(
                "SELECT count(*) FROM examples WHERE held_out=1"
            ).fetchone()[0]
            if options["min_new"]:
                brain = Brain(store, cfg["brain"])
                trainer = Trainer(store, brain, options)
                result = trainer.run(lambda: False, announce, audit=Audit(store, cfg, announce))
                announce(result)
                store.event("lora", result)
            announce(encode(end_to_end(store, cfg, announce)))
        finally:
            store.db.close()


if __name__ == "__main__":
    main()
