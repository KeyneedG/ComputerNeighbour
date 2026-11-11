from contextlib import contextmanager
import logging
from logging.handlers import RotatingFileHandler
import os
from queue import Empty, Queue
import re
import sys
from threading import Thread
import time
import tomllib

from .agent import Agent
from .state import ROOT, Store

LOG = logging.getLogger(__name__)
EOF = object()


def settings():
    with (ROOT / "settings.toml").open("rb") as file:
        cfg = tomllib.load(file)
    cycle = cfg["cycle"]
    if not 0 <= cycle["wake_at"] < cycle["sleep_at"] <= 1:
        raise ValueError("Нужно 0 ≤ wake_at < sleep_at ≤ 1")
    if any(cycle[key] <= 0 for key in ("idle_seconds", "awake_hours", "sleep_hours")):
        raise ValueError("Интервалы цикла должны быть положительными")
    if any(rule["half_life_minutes"] <= 0 for rule in cfg["physiology"].values()):
        raise ValueError("Периоды полураспада должны быть положительными")
    if any(cfg["lora"][key] <= 0 for key in ("min_new", "validation", "epochs", "max_tokens", "rank")):
        raise ValueError("Размеры выборок и параметры LoRA должны быть положительными")
    return cfg


def display(text):
    clean = re.sub(r"[\x00-\x08\x0b-\x1f\x7f-\x9f]", "", str(text))
    print(clean, flush=True)


@contextmanager
def session_lock(path):
    if os.name != "nt":
        raise RuntimeError("Для запуска предусмотрена Windows")
    import msvcrt

    with path.open("a+b") as stream:
        stream.seek(0)
        try:
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise RuntimeError("Экземпляр Амадеи уже работает") from error
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def read_console(incoming):
    for line in sys.stdin:
        if line.strip():
            incoming.put(line.strip()[:3000])
    incoming.put(EOF)


def continuous(agent):
    incoming = Queue()
    Thread(target=read_console, args=(incoming,), name="console", daemon=True).start()
    display(f"{agent.settings['name']} · Можно написать сообщение")
    interval = agent.settings["cycle"]["idle_seconds"]
    due = time.monotonic() + interval
    interactive = True
    while True:
        try:
            agent.desktop_events()
        except Exception as error:
            LOG.exception("Ошибка рабочего стола")
            display(str(error))
        if incoming.empty() and agent.browser.active:
            try:
                agent.browser_events()
            except Exception as error:
                LOG.exception("Ошибка браузера")
                if agent.browser.active:
                    agent.browser.pause(str(error))
        remaining = max(0, due - time.monotonic())
        timeout = min(remaining, 0.5) if agent.desktop.pending or agent.browser.active else remaining
        try:
            line = incoming.get(timeout=timeout)
        except Empty:
            line = None
        if line is EOF:
            interactive = False
            continue
        if line is None and time.monotonic() < due:
            continue
        try:
            if not (agent.desktop.pending or agent.browser.active) or line is not None:
                agent.turn(line)
        except Exception as error:
            LOG.exception("Ошибка цикла")
            if interactive or line is not None:
                display(f"{type(error).__name__}: {error}")
        if incoming.empty() and not agent.desktop.pending and not agent.browser.active:
            try:
                agent.maintain(lambda: not incoming.empty(), idle=line is None)
            except Exception:
                LOG.exception("Ошибка фонового обслуживания")
        due = time.monotonic() + interval


def main():
    cfg = settings()
    directory = ROOT / "state"
    directory.mkdir(exist_ok=True)
    logging.basicConfig(
        level=logging.INFO,
        handlers=[RotatingFileHandler(directory / "amadea.log", maxBytes=2_000_000,
                                     backupCount=2, encoding="utf-8")],
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )
    with session_lock(directory / "session.lock"):
        store = Store(directory / "memory.sqlite3")
        agent = Agent(store, cfg, display)
        try:
            continuous(agent)
        except KeyboardInterrupt:
            pass
        finally:
            agent.voice.close()
            agent.browser.close()
            store.db.close()
