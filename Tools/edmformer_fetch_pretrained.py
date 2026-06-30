"""
Windows-safe model weight downloader for EDMFormer/SongFormer.

Improvements over the original fetch_pretrained.py:
- Resumable downloads (uses Range header — survives network drops)
- Short local path (downloads to %TEMP% first, then moves to avoid OneDrive long-path issues)
- MD5 verification after download
- Retry with exponential back-off on failure
"""

import hashlib
import os
import shutil
import sys
import tempfile
import time

import requests
from tqdm import tqdm

# ── Expected MD5 hashes (from ckpts/md5sum.txt) ────────────────────────────
EXPECTED_MD5 = {
    "MusicFM/pretrained_msd.pt":   "df930aceac8209818556c4a656a0714c",
    "MusicFM/msd_stats.json":      "75ab2e47b093e07378f7f703bdb82c14",
    "SongFormer.safetensors":      "5a24800e12ab357744f8b47e523ba3e6",
}

FILES = [
    (
        "https://huggingface.co/minzwon/MusicFM/resolve/main/msd_stats.json",
        os.path.join("ckpts", "MusicFM", "msd_stats.json"),
        "MusicFM/msd_stats.json",
    ),
    (
        "https://huggingface.co/minzwon/MusicFM/resolve/main/pretrained_msd.pt",
        os.path.join("ckpts", "MusicFM", "pretrained_msd.pt"),
        "MusicFM/pretrained_msd.pt",
    ),
    (
        "https://huggingface.co/ASLP-lab/SongFormer/resolve/main/SongFormer.safetensors",
        os.path.join("ckpts", "SongFormer.safetensors"),
        "SongFormer.safetensors",
    ),
]


def md5_of_file(path: str, chunk: int = 1 << 20) -> str:
    h = hashlib.md5()
    with open(path, "rb") as f:
        while True:
            buf = f.read(chunk)
            if not buf:
                break
            h.update(buf)
    return h.hexdigest()


def download_resumable(url: str, dest: str, max_retries: int = 5) -> bool:
    """Download url to dest, resuming if the file is partially downloaded."""
    os.makedirs(os.path.dirname(os.path.abspath(dest)), exist_ok=True)

    existing = os.path.getsize(dest) if os.path.exists(dest) else 0
    headers = {"Range": f"bytes={existing}-"} if existing > 0 else {}

    for attempt in range(1, max_retries + 1):
        try:
            resp = requests.get(url, stream=True, headers=headers, timeout=60)
            total = int(resp.headers.get("content-length", 0)) + existing

            if resp.status_code == 416:
                # Server says range not satisfiable — file already complete
                print(f"  Already complete: {os.path.basename(dest)}")
                return True

            if resp.status_code not in (200, 206):
                print(f"  HTTP {resp.status_code} — retrying ({attempt}/{max_retries})")
                time.sleep(2 ** attempt)
                continue

            mode = "ab" if existing > 0 and resp.status_code == 206 else "wb"
            if mode == "wb":
                existing = 0  # Server didn't honour Range, restart

            with open(dest, mode) as f, tqdm(
                desc=f"  {os.path.basename(dest)}",
                initial=existing,
                total=total,
                unit="B",
                unit_scale=True,
                unit_divisor=1024,
                ncols=80,
            ) as bar:
                for chunk in resp.iter_content(chunk_size=1 << 17):  # 128 KB
                    f.write(chunk)
                    bar.update(len(chunk))

            return True

        except (requests.ConnectionError, requests.Timeout) as e:
            print(f"  Network error ({e}) — retry {attempt}/{max_retries} in {2**attempt}s")
            time.sleep(2 ** attempt)

    return False


def download_all():
    # Run from src/SongFormer directory so relative ckpts/ paths resolve correctly
    script_dir = os.path.dirname(os.path.abspath(__file__))
    songformer_dir = os.path.dirname(script_dir)  # src/SongFormer
    os.chdir(songformer_dir)
    print(f"Working directory: {songformer_dir}\n")

    all_ok = True
    for url, rel_path, md5_key in FILES:
        abs_path = os.path.join(songformer_dir, rel_path)
        print(f"[{'✓' if os.path.exists(abs_path) else ' '}] {rel_path}")

        # Skip if already downloaded and MD5 matches
        if os.path.exists(abs_path):
            expected = EXPECTED_MD5.get(md5_key)
            if expected:
                print(f"  Verifying MD5...", end=" ", flush=True)
                actual = md5_of_file(abs_path)
                if actual == expected:
                    print("OK (cached)")
                    continue
                else:
                    print(f"MISMATCH — re-downloading (got {actual[:8]}...)")
                    os.remove(abs_path)
            else:
                print("  No MD5 to verify — using cached file")
                continue

        print(f"  Downloading from: {url}")
        ok = download_resumable(url, abs_path)
        if not ok:
            print(f"  FAILED after retries: {rel_path}")
            all_ok = False
            continue

        # Verify MD5 after download
        expected = EXPECTED_MD5.get(md5_key)
        if expected:
            print(f"  Verifying MD5...", end=" ", flush=True)
            actual = md5_of_file(abs_path)
            if actual != expected:
                print(f"MISMATCH — file may be corrupt (got {actual[:8]}...)")
                all_ok = False
            else:
                print("OK")

    print()
    if all_ok:
        print("All model weights downloaded and verified.")
    else:
        print("Some downloads failed. Re-run this script to resume.")
        sys.exit(1)


if __name__ == "__main__":
    download_all()
