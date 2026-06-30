"""
Downloads EDMFormer model weights from smileyxo/ORBI-AI-Models (HuggingFace Space).

Uses huggingface_hub.snapshot_download — built-in resumption, integrity checks,
no custom HTTP code needed. Falls back to original upstream sources on failure.

Expected layout in ckpts/ after download:
  ckpts/
    SongFormer.safetensors
    MusicFM/
      pretrained_msd.pt
      msd_stats.json
"""

import os
import shutil
import sys
from pathlib import Path


HF_REPO_ID   = "smileyxo/ORBI-AI-Models"
HF_REPO_TYPE = "space"

# Fallback: original upstream sources if Space download fails
FALLBACK_FILES = [
    (
        "https://huggingface.co/minzwon/MusicFM/resolve/main/msd_stats.json",
        "MusicFM/msd_stats.json",
    ),
    (
        "https://huggingface.co/minzwon/MusicFM/resolve/main/pretrained_msd.pt",
        "MusicFM/pretrained_msd.pt",
    ),
    (
        "https://huggingface.co/ASLP-lab/SongFormer/resolve/main/SongFormer.safetensors",
        "SongFormer.safetensors",
    ),
]


def resolve_ckpts_dir() -> Path:
    here = Path(__file__).resolve().parent
    # Script may live in utils/ inside SongFormer, or copied elsewhere by installer
    candidates = [
        here.parent / "ckpts",
        here.parent.parent.parent / "EDMFormer" / "src" / "SongFormer" / "ckpts",
    ]
    for c in candidates:
        if c.parent.name == "SongFormer" or (c.parent / "infer.py").exists():
            return c
    return here / "ckpts"


def _place(src: Path, dst: Path):
    if src.exists() and not dst.exists():
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(src), str(dst))
        print(f"  Placed: {dst.name}")


def move_to_ckpts(staging: Path, ckpts: Path):
    musicfm = ckpts / "MusicFM"
    for fname, dest in [
        ("SongFormer.safetensors", ckpts / "SongFormer.safetensors"),
        ("SongFormer.pt",          ckpts / "SongFormer.pt"),
        ("pretrained_msd.pt",      musicfm / "pretrained_msd.pt"),
        ("msd_stats.json",         musicfm / "msd_stats.json"),
    ]:
        _place(staging / fname, dest)
        _place(staging / "MusicFM" / fname, dest)


def download_from_space(ckpts: Path) -> bool:
    try:
        from huggingface_hub import snapshot_download
    except ImportError:
        print("  huggingface_hub not available — skipping Space download")
        return False

    staging = ckpts / "_hf_staging"
    staging.mkdir(parents=True, exist_ok=True)
    print(f"  Source : huggingface.co/{HF_REPO_ID}")
    print(f"  Dest   : {ckpts}")
    print("  (Resumable — safe to interrupt and re-run)\n")

    try:
        snapshot_download(
            repo_id=HF_REPO_ID,
            repo_type=HF_REPO_TYPE,
            local_dir=str(staging),
            ignore_patterns=["*.py", "*.md", "*.txt", "requirements*", "app*",
                             ".gitattributes", "README*"],
        )
    except Exception as e:
        print(f"  Space download error: {e}")
        shutil.rmtree(staging, ignore_errors=True)
        return False

    move_to_ckpts(staging, ckpts)
    shutil.rmtree(staging, ignore_errors=True)
    return True


def download_fallback(ckpts: Path) -> bool:
    try:
        import requests
        from tqdm import tqdm
    except ImportError:
        print("  requests/tqdm not installed — cannot use fallback")
        return False

    all_ok = True
    for url, rel_path in FALLBACK_FILES:
        dest = ckpts / rel_path
        dest.parent.mkdir(parents=True, exist_ok=True)

        if dest.exists():
            print(f"  Already exists: {rel_path}")
            continue

        print(f"  Downloading (fallback): {rel_path}")
        existing = dest.stat().st_size if dest.exists() else 0
        headers = {"Range": f"bytes={existing}-"} if existing else {}

        try:
            resp = requests.get(url, stream=True, headers=headers, timeout=60)
            total = int(resp.headers.get("content-length", 0)) + existing
            mode = "ab" if existing and resp.status_code == 206 else "wb"

            with open(dest, mode) as f, tqdm(
                desc=f"    {dest.name}", initial=existing, total=total,
                unit="B", unit_scale=True, unit_divisor=1024, ncols=80
            ) as bar:
                for chunk in resp.iter_content(chunk_size=1 << 17):
                    f.write(chunk)
                    bar.update(len(chunk))
        except Exception as e:
            print(f"  FAILED: {e}")
            all_ok = False

    return all_ok


def verify(ckpts: Path) -> bool:
    required = [
        ckpts / "SongFormer.safetensors",
        ckpts / "MusicFM" / "pretrained_msd.pt",
        ckpts / "MusicFM" / "msd_stats.json",
    ]
    missing = [f for f in required if not f.exists()]
    if missing:
        for m in missing:
            print(f"  Missing: {m}")
        return False

    print("  All weights present:")
    for f in required:
        mb = f.stat().st_size / (1024 * 1024)
        print(f"    {f.name}: {mb:.0f} MB")
    return True


def main():
    ckpts = resolve_ckpts_dir()
    print(f"Target: {ckpts}\n")

    if verify(ckpts):
        print("\nWeights already downloaded — nothing to do.")
        return

    print("[1/2] Downloading from ORBIT HuggingFace Space...")
    download_from_space(ckpts)

    if not verify(ckpts):
        print("\n[2/2] Trying upstream sources directly...")
        download_fallback(ckpts)

    if not verify(ckpts):
        print("\nDownload incomplete — re-run to resume.")
        print("Manual sources:")
        for url, rel in FALLBACK_FILES:
            print(f"  {url}")
        sys.exit(1)

    print("\nAll model weights ready.")


if __name__ == "__main__":
    main()
