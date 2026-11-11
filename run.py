import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parent
RUNTIME = ROOT / ".runtime"


def main():
    profile = "voice" if sys.argv[1:2] == ["--voice-worker"] else "brain"
    sys.path.insert(1, str(RUNTIME / profile))
    cache = RUNTIME / "cache"
    cache.mkdir(parents=True, exist_ok=True)
    for name in ("HF_HOME", "TORCH_HOME", "XDG_CACHE_HOME", "NUMBA_CACHE_DIR"):
        os.environ[name] = str(cache / name.lower())
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1"
    os.environ["PLAYWRIGHT_BROWSERS_PATH"] = str(RUNTIME / "browsers")
    if profile == "voice":
        from amadea.voice import worker
        worker()
    else:
        if not (RUNTIME / "brain" / ".installed").is_file():
            raise SystemExit("Запустите setup.cmd для подготовки среды.")
        from amadea.console import main as console
        console()


if __name__ == "__main__":
    main()
