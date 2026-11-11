"""Build the portable runtimes and verify pinned model downloads."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tomllib
from urllib.parse import quote
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parent
RUNTIME = ROOT / ".runtime"
SETTINGS = tomllib.loads((ROOT / "settings.toml").read_text(encoding="utf-8"))
PROFILES = {
    "brain": (
        "torch==2.9.1+cu128", "torchvision==0.24.1+cu128", "transformers==5.22.2",
        "peft==0.21.2", "bitsandbytes==0.50.2", "accelerate==1.12.0", "pillow==12.2.0",
        "playwright==1.65.0",
    ),
    "voice": (
        "torch==2.9.1+cu128", "torchaudio==2.9.1+cu128", "qwen-tts==0.1.1",
    ),
    "accelerators": ("triton-windows==3.5.1.post24", "fla-core==0.6.0"),
}


def install(profile):
    target = RUNTIME / profile
    marker = target / ".installed"
    expected = "\n".join(PROFILES[profile])
    if marker.is_file() and marker.read_text(encoding="utf-8") == expected:
        return
    subprocess.run(
        [str(RUNTIME / "uv.exe"), "--no-config", "pip", "install", "--upgrade",
         "--python", sys.executable, "--target", str(target), "--index",
         "https://download.pytorch.org/whl/cu128", "--index-strategy", "unsafe-best-match",
         *PROFILES[profile]],
        check=True,
    )
    marker.write_text(expected, encoding="utf-8")


def install_profiles():
    for profile in PROFILES:
        install(profile)
    browser_env = {
        **os.environ, "PYTHONPATH": str(RUNTIME / "brain"),
        "PLAYWRIGHT_BROWSERS_PATH": str(RUNTIME / "browsers"),
    }
    subprocess.run([sys.executable, "-m", "playwright", "install", "chromium"],
                   env=browser_env, check=True)


def digest(path, algorithm="sha256"):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def download(repo, revision, target, item):
    filename = item["rfilename"]
    path = target / filename
    size = item["size"]
    checksum = item.get("lfs", {}).get("sha256")
    if path.is_file() and path.stat().st_size == size and (
        not checksum or digest(path) == checksum
    ):
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    partial = path.with_name(path.name + ".part")
    url = f"https://huggingface.co/{repo}/resolve/{revision}/{quote(filename)}"
    print(f"Downloading {repo}/{filename} ({size:,} bytes)", flush=True)
    if partial.is_file() and partial.stat().st_size > size:
        partial.unlink()
    if not partial.is_file() or partial.stat().st_size < size:
        subprocess.run(
            ["curl.exe", "--fail", "--silent", "--show-error", "--location",
             "--retry", "4", "--retry-all-errors", "--connect-timeout", "20",
             "--continue-at", "-", "--output", str(partial), url],
            check=True,
        )
    if partial.stat().st_size != size or (checksum and digest(partial) != checksum):
        partial.unlink()
        raise ValueError(f"Corrupt model download: {filename}")
    partial.replace(path)


def model(pool, profile):
    settings = SETTINGS[profile]
    repo, revision = settings["repo"], settings["revision"]
    target = ROOT / "models" / profile
    with urlopen(f"https://huggingface.co/api/models/{repo}/revision/{revision}?blobs=true", timeout=30) as response:
        manifest = json.load(response)
    files = []
    for item in manifest["siblings"]:
        name = item["rfilename"]
        if name.startswith(("benchmarks/", "assets/")):
            continue
        if name in {"LICENSE", "README.md"} or name.endswith((".json", ".jinja", ".txt", ".safetensors")):
            files.append(item)
    if manifest["sha"] != revision or not any(item["rfilename"].endswith(".safetensors") for item in files):
        raise ValueError(f"Unexpected model manifest: {repo}")
    return [pool.submit(download, repo, revision, target, item) for item in files]


def main():
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--packages-only", action="store_true")
    mode.add_argument("--models-only", action="store_true")
    args = parser.parse_args()
    with ThreadPoolExecutor(max_workers=6) as pool:
        work = []
        if not args.models_only:
            work.append(pool.submit(install_profiles))
        if not args.packages_only:
            for profile in ("brain", "voice"):
                work.extend(model(pool, profile))
        for task in work:
            task.result()


if __name__ == "__main__":
    main()
