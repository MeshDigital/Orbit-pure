"""
EDMFormer Microservice for ORBIT
=================================
FastAPI server that wraps the EDMFormer/SongFormer inference pipeline.
ORBIT C# calls POST /analyze with {"audio_path": "/abs/path/to/track.wav"}
and receives back EDM phrase segments as JSON.

Setup:
  1. Install: Tools\install_edmformer.bat  (or see manual steps below)
  2. Clone EDMFormer into Tools\EDMFormer\
  3. Run: conda activate edmformer && python Tools\edmformer_server.py

Manual setup if needed:
  conda create -n edmformer python=3.10 -y
  conda activate edmformer
  git clone https://github.com/25ohms/EDMFormer Tools\EDMFormer
  cd Tools\EDMFormer
  pip install -r requirements.txt
  pip install fastapi uvicorn[standard]
  python src/SongFormer/utils/fetch_pretrained.py
"""

import argparse
import importlib
import json
import math
import os
import sys
import time
from pathlib import Path

import numpy as np
import uvicorn
from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse
from pydantic import BaseModel

# ── Locate EDMFormer src ────────────────────────────────────────────────────
SCRIPT_DIR = Path(__file__).parent
EDMFORMER_DIR = SCRIPT_DIR / "EDMFormer" / "src" / "SongFormer"
if not EDMFORMER_DIR.exists():
    sys.exit(
        f"[edmformer_server] EDMFormer not found at {EDMFORMER_DIR}\n"
        "Clone it with:  git clone https://github.com/25ohms/EDMFormer Tools\\EDMFormer"
    )

sys.path.insert(0, str(EDMFORMER_DIR))
os.chdir(str(EDMFORMER_DIR))

# Monkey-patch scipy.inf which msaf uses (removed in newer scipy)
import scipy
import librosa
import torch
from omegaconf import OmegaConf
from ema_pytorch import EMA
from muq import MuQ
from musicfm.model.musicfm_25hz import MusicFM25Hz
from postprocessing.functional import postprocess_functional_structure
from dataset.label2id import DATASET_ID_ALLOWED_LABEL_IDS, DATASET_LABEL_TO_DATASET_ID

scipy.inf = np.inf

# ── Constants ───────────────────────────────────────────────────────────────
MUSICFM_HOME_PATH = os.path.join("ckpts", "MusicFM")
INPUT_SAMPLING_RATE = 24000
TIME_DUR = 420
AFTER_DOWNSAMPLING_FRAME_RATES = 8.333
DATASET_LABEL = "SongForm-HX-8Class"
DATASET_IDS = [5]
MODEL_CONFIG = "SongFormer.yaml"
MODEL_MODULE = "SongFormer"
CHECKPOINT = "SongFormer.safetensors"
NUM_CLASSES = 128
PORT = 7774

# ── EDM label → canonical ORBIT label ───────────────────────────────────────
LABEL_MAP = {
    "intro": "Intro",
    "buildup": "Build",
    "drop": "Drop",
    "breakdown": "Breakdown",
    "outro": "Outro",
    "silence": "Silence",
    "end": None,  # sentinel, strip from output
}

# ── Model state (loaded once at startup) ────────────────────────────────────
_muq = None
_musicfm = None
_model = None
_hp = None
_dataset_id2label_mask = {}
_device = "cuda:0" if torch.cuda.is_available() else "cpu"
_ready = False

app = FastAPI(title="EDMFormer Microservice", version="1.0")


def load_models():
    global _muq, _musicfm, _model, _hp, _dataset_id2label_mask, _ready

    print(f"[edmformer] Loading models on {_device}...")
    t0 = time.time()

    _muq = MuQ.from_pretrained("OpenMuQ/MuQ-large-msd-iter").to(_device).eval()
    print(f"[edmformer] MuQ loaded ({time.time()-t0:.1f}s)")

    _musicfm = MusicFM25Hz(
        is_flash=False,
        stat_path=os.path.join(MUSICFM_HOME_PATH, "msd_stats.json"),
        model_path=os.path.join(MUSICFM_HOME_PATH, "pretrained_msd.pt"),
    ).to(_device).eval()
    print(f"[edmformer] MusicFM loaded ({time.time()-t0:.1f}s)")

    hp = OmegaConf.load(os.path.join("configs", MODEL_CONFIG))
    module = importlib.import_module("models." + MODEL_MODULE)
    Model = getattr(module, "Model")
    model = Model(hp)

    ckpt_path = os.path.join("ckpts", CHECKPOINT)
    if ckpt_path.endswith(".safetensors"):
        from safetensors.torch import load_file
        ckpt = {"model_ema": load_file(ckpt_path, device=_device)}
    else:
        ckpt = torch.load(ckpt_path, map_location=_device)

    if ckpt.get("model_ema"):
        model_ema = EMA(model, include_online_model=False)
        model_ema.load_state_dict(ckpt["model_ema"])
        model.load_state_dict(model_ema.ema_model.state_dict())
    else:
        model.load_state_dict(ckpt["model"])

    _model = model.to(_device).eval()
    _hp = hp
    print(f"[edmformer] SongFormer loaded ({time.time()-t0:.1f}s)")

    for key, allowed_ids in DATASET_ID_ALLOWED_LABEL_IDS.items():
        mask = np.ones(NUM_CLASSES, dtype=bool)
        mask[allowed_ids] = False
        _dataset_id2label_mask[key] = mask

    _ready = True
    print(f"[edmformer] All models ready in {time.time()-t0:.1f}s on {_device}")


def rule_post_processing(msa_list):
    """Merge very short segments at track edges — same as upstream infer.py."""
    if len(msa_list) <= 2:
        return msa_list
    result = msa_list.copy()
    while len(result) > 2:
        if result[1][0] - result[0][0] < 1.0:
            result[0] = (result[0][0], result[1][1])
            result = [result[0]] + result[2:]
        else:
            break
    while len(result) > 2:
        if result[-1][0] - result[-2][0] < 1.0:
            result = result[:-2] + [result[-1]]
        else:
            break
    return result


def run_inference(audio_path: str) -> list[dict]:
    """
    Run full EDMFormer inference on a single audio file.
    Returns list of {label, start, end, duration} dicts with canonical ORBIT labels.
    """
    wav, _ = librosa.load(audio_path, sr=INPUT_SAMPLING_RATE)
    audio = torch.tensor(wav).to(_device)

    win_size = 420
    hop_size = 420
    total_len = ((audio.shape[0] // INPUT_SAMPLING_RATE) // TIME_DUR) * TIME_DUR + TIME_DUR
    total_frames = math.ceil(total_len * AFTER_DOWNSAMPLING_FRAME_RATES)

    logits = {
        "function_logits": np.zeros([total_frames, NUM_CLASSES]),
        "boundary_logits": np.zeros([total_frames]),
    }
    logits_num = {
        "function_logits": np.zeros([total_frames, NUM_CLASSES]),
        "boundary_logits": np.zeros([total_frames]),
    }

    dataset_ids = torch.Tensor(DATASET_IDS).to(_device, dtype=torch.long)
    label_mask_key = DATASET_LABEL_TO_DATASET_ID[DATASET_LABEL]
    label_mask = (
        torch.Tensor(_dataset_id2label_mask[label_mask_key])
        .to(_device, dtype=torch.bool)
        .unsqueeze(0)
        .unsqueeze(0)
    )

    i = 0
    lens = 0
    with torch.no_grad():
        while True:
            start_idx = i * INPUT_SAMPLING_RATE
            end_idx = min((i + win_size) * INPUT_SAMPLING_RATE, audio.shape[-1])
            if start_idx >= audio.shape[-1]:
                break
            if end_idx - start_idx <= 1024:
                i += hop_size
                continue

            audio_seg = audio[start_idx:end_idx]

            muq_out = _muq(audio_seg.unsqueeze(0), output_hidden_states=True)
            muq_embd_420s = muq_out["hidden_states"][10]
            del muq_out; torch.cuda.empty_cache() if _device != "cpu" else None

            _, mfm_hidden = _musicfm.get_predictions(audio_seg.unsqueeze(0))
            musicfm_embd_420s = mfm_hidden[10]
            del mfm_hidden; torch.cuda.empty_cache() if _device != "cpu" else None

            muq_30s_chunks, mfm_30s_chunks = [], []
            for idx_30s in range(i, i + hop_size, 30):
                s = idx_30s * INPUT_SAMPLING_RATE
                e = min((idx_30s + 30) * INPUT_SAMPLING_RATE, audio.shape[-1], (i + hop_size) * INPUT_SAMPLING_RATE)
                if s >= audio.shape[-1] or e - s <= 1024:
                    break
                seg = audio[s:e].unsqueeze(0)
                muq_30s_chunks.append(_muq(seg, output_hidden_states=True)["hidden_states"][10])
                torch.cuda.empty_cache() if _device != "cpu" else None
                mfm_30s_chunks.append(_musicfm.get_predictions(seg)[1][10])
                torch.cuda.empty_cache() if _device != "cpu" else None

            if not muq_30s_chunks:
                i += hop_size
                continue

            muq_30s = torch.cat(muq_30s_chunks, dim=1)
            mfm_30s = torch.cat(mfm_30s_chunks, dim=1)

            all_embds = [mfm_30s, muq_30s, musicfm_embd_420s, muq_embd_420s]
            min_len = min(x.shape[1] for x in all_embds)
            all_embds = [x[:, :min_len, :] for x in all_embds]
            embd = torch.cat(all_embds, dim=-1)

            _, chunk_logits = _model.infer(
                input_embeddings=embd,
                dataset_ids=dataset_ids,
                label_id_masks=label_mask,
                with_logits=True,
            )

            sf = int(i * AFTER_DOWNSAMPLING_FRAME_RATES)
            ef = sf + min(
                math.ceil(hop_size * AFTER_DOWNSAMPLING_FRAME_RATES),
                chunk_logits["boundary_logits"][0].shape[0],
            )
            logits["function_logits"][sf:ef] += chunk_logits["function_logits"][0].cpu().numpy()
            logits["boundary_logits"][sf:ef] = chunk_logits["boundary_logits"][0].cpu().numpy()
            logits_num["function_logits"][sf:ef] += 1
            logits_num["boundary_logits"][sf:ef] += 1
            lens += ef - sf
            i += hop_size

    # Avoid div-by-zero in unvisited frames
    logits_num["function_logits"] = np.maximum(logits_num["function_logits"], 1)
    logits["function_logits"] /= logits_num["function_logits"]
    logits_num["boundary_logits"] = np.maximum(logits_num["boundary_logits"], 1)
    logits["boundary_logits"] /= logits_num["boundary_logits"]

    logits["function_logits"] = torch.from_numpy(logits["function_logits"][:lens]).unsqueeze(0)
    logits["boundary_logits"] = torch.from_numpy(logits["boundary_logits"][:lens]).unsqueeze(0)

    msa_output = postprocess_functional_structure(logits, _hp)
    msa_output = rule_post_processing(msa_output)

    # Convert to ORBIT segment format
    segments = []
    for idx in range(len(msa_output) - 1):
        raw_label = msa_output[idx][1]
        orbit_label = LABEL_MAP.get(raw_label, raw_label.capitalize())
        if orbit_label is None:
            continue
        start = float(msa_output[idx][0])
        end = float(msa_output[idx + 1][0])
        segments.append({
            "label": orbit_label,
            "start": round(start, 3),
            "end": round(end, 3),
            "duration": round(end - start, 3),
        })

    return segments


# ── API ──────────────────────────────────────────────────────────────────────

class AnalyzeRequest(BaseModel):
    audio_path: str


@app.on_event("startup")
async def startup_event():
    load_models()


@app.get("/health")
def health():
    return {"status": "ready" if _ready else "loading", "device": _device}


@app.post("/analyze")
def analyze(req: AnalyzeRequest):
    if not _ready:
        raise HTTPException(503, "Models still loading")
    if not os.path.exists(req.audio_path):
        raise HTTPException(400, f"Audio file not found: {req.audio_path}")

    try:
        t0 = time.time()
        segments = run_inference(req.audio_path)
        elapsed = round(time.time() - t0, 2)
        return JSONResponse({"segments": segments, "elapsed_seconds": elapsed})
    except Exception as e:
        raise HTTPException(500, str(e))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=PORT)
    parser.add_argument("--host", type=str, default="127.0.0.1")
    parser.add_argument("--cpu", action="store_true", help="Force CPU mode (slow but works without GPU)")
    args = parser.parse_args()

    if args.cpu:
        _device = "cpu"

    uvicorn.run(app, host=args.host, port=args.port, log_level="info")
