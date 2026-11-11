import io
import json
import logging
import os
from pathlib import Path
from queue import Empty, Full, Queue
import subprocess
import sys
from threading import Lock, Thread

from .state import ROOT

LOG = logging.getLogger(__name__)


class Voice:
    def __init__(self, settings):
        self.settings = settings
        self.enabled = settings["enabled"]
        self.queue = Queue(maxsize=2)
        self.process = None
        self.lock = Lock()
        self.closed = False
        self.thread = Thread(target=self.send, name="voice", daemon=True)
        self.thread.start()

    def say(self, text, mood):
        if not self.enabled:
            return
        job = {"text": text[:self.settings["max_chars"]], "mood": mood, "settings": self.settings}
        try:
            self.queue.put_nowait(job)
        except Full:
            LOG.info("Очередь озвучки занята")

    def send(self):
        while (job := self.queue.get()) is not None:
            try:
                with self.lock:
                    if self.closed:
                        return
                    if self.process is None or self.process.poll() is not None:
                        self.process = subprocess.Popen(
                            [sys.executable, str(ROOT / "run.py"), "--voice-worker"],
                            cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                            encoding="utf-8", env={**os.environ, "PYTHONUTF8": "1"},
                        )
                    process = self.process
                process.stdin.write(json.dumps(job, ensure_ascii=False) + "\n")
                process.stdin.flush()
                result = json.loads(process.stdout.readline())
                if not result.get("ok"):
                    LOG.warning("Озвучка: %s", result.get("error"))
            except (OSError, ValueError):
                LOG.exception("Процесс озвучки завершился")

    def close(self):
        with self.lock:
            self.closed = True
            if self.process and self.process.poll() is None:
                self.process.terminate()
        while True:
            try:
                self.queue.get_nowait()
            except Empty:
                break
        self.queue.put_nowait(None)
        self.thread.join(timeout=1)


def render(model, job):
    import soundfile

    cfg = job["settings"]
    mood = job.get("mood") or {}
    style = (
        f"Natural conversational delivery. Warmth {mood.get('affiliation', 0.5):.2f}, "
        f"energy {mood.get('arousal', 0.5):.2f}, positivity {mood.get('valence', 0.5):.2f}."
    )
    wavs, rate = model.generate_custom_voice(
        text=job["text"], language=cfg["language"], speaker=cfg["speaker"], instruct=style
    )
    stream = io.BytesIO()
    soundfile.write(stream, wavs[0], rate, format="WAV", subtype="PCM_16")
    return stream.getvalue()


def worker():
    # Reserve stdout for the one-line IPC replies.
    output = sys.stdout
    sys.stdout = sys.stderr
    model = None
    for line in sys.stdin:
        try:
            job = json.loads(line)
            cfg = job["settings"]
            if model is None:
                import torch
                from qwen_tts import Qwen3TTSModel
                torch.set_num_threads(cfg["threads"])
                device = cfg["device"]
                model = Qwen3TTSModel.from_pretrained(
                    ROOT / "models" / "voice", device_map=device,
                    dtype=torch.float32 if device == "cpu" else torch.bfloat16,
                    attn_implementation="sdpa", local_files_only=True,
                )
                if cfg["speaker"].lower() not in model.get_supported_speakers():
                    raise ValueError(f"Голос {cfg['speaker']} отсутствует")
            sound = render(model, job)
            if "wav" in job:
                Path(job["wav"]).write_bytes(sound)
            else:
                import winsound
                winsound.PlaySound(sound, winsound.SND_MEMORY)
            result = {"ok": True}
        except Exception as error:
            result = {"ok": False, "error": str(error)}
        print(json.dumps(result, ensure_ascii=False), file=output, flush=True)
