"""
Reference CUE-DETR pipeline (verbatim port of ETH-DISCO's predict.py) used to check ORBIT's C#
port (Services/AudioAnalysis/CueDetr). Needs the packages listed in export_onnx.py plus librosa,
matplotlib, scipy, pillow.

    # cue points for audio files (PyTorch model, or --onnx to run the exported model)
    python reference.py cues track1.flac track2.mp3 [--onnx ../Essentia/models/cue-detr.onnx]

    # regenerate the C# parity fixtures (Tests/SLSKDONET.Tests/TestData/CueDetr)
    python reference.py fixtures --out ../../Tests/SLSKDONET.Tests/TestData/CueDetr --track some.flac

    # the viridis byte table used by the C# port
    python reference.py lut
"""
import argparse
import json
import os
import sys

import numpy as np
import librosa
from matplotlib import cm
from PIL import Image
from scipy.signal import find_peaks

OVERLAP = 0.75
W_WIN = 355
PADDING = 266
MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)


def mel_db(y):
    M = librosa.feature.melspectrogram(y=y, sr=22050, n_fft=2048)
    return librosa.power_to_db(M, ref=np.max)


def to_image(M_db):
    arr = M_db[::-1]
    sm = cm.ScalarMappable(cmap='viridis')
    sm.set_clim(arr.min(), arr.max())
    rgba = sm.to_rgba(arr, bytes=True)
    rgb_shape = (rgba.shape[1], rgba.shape[0])
    rgba = np.require(rgba, requirements='C')
    im = Image.frombuffer("RGBA", rgb_shape, rgba, "raw", "RGBA", 0, 1)
    return np.array(im)[:, :, :3]


def windows(image):
    image_w = image.shape[1] + PADDING
    n_windows = int(np.floor(image_w / (W_WIN * (1 - OVERLAP))))
    images, borders = [], []
    for i in range(n_windows):
        l = int(np.floor(i * W_WIN * (1 - OVERLAP))) - PADDING
        r = l + W_WIN
        borders.append(l)
        if l < 0:
            segment = image[:, :r]
            segment = np.pad(segment, ((0, 0), (-l, 0), (0, 0)), mode='linear_ramp')
        elif r > image.shape[1]:
            segment = image[:, l:]
            segment = np.pad(segment, ((0, 0), (0, r - l - segment.shape[1]), (0, 0)), mode='linear_ramp')
        else:
            segment = image[:, l:r]
        images.append(segment)
    return images, borders


def normalise(images):
    # DetrImageProcessor(do_resize=False): rescale 1/255, ImageNet normalise, HWC -> CHW.
    x = np.stack(images).astype(np.float32) / 255.0
    x = (x - MEAN) / STD
    return x.transpose(0, 3, 1, 2).astype(np.float32)


def run_model(pixel_values, onnx_path=None):
    if onnx_path:
        import onnxruntime as ort
        sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
        logits, boxes = [], []
        for s in range(0, len(pixel_values), 16):
            lo, bo = sess.run(None, {"pixel_values": pixel_values[s:s + 16]})
            logits.append(lo); boxes.append(bo)
        return np.concatenate(logits), np.concatenate(boxes)
    import torch
    from transformers import DetrForObjectDetection
    model = DetrForObjectDetection.from_pretrained('disco-eth/cue-detr').eval()
    with torch.no_grad():
        out = model(torch.from_numpy(pixel_values))
    return out.logits.numpy(), out.pred_boxes.numpy()


def postprocess(logits, boxes, borders, sensitivity=0.9, radius=16):
    """Same maths as DetrImageProcessor.post_process_object_detection(outputs, 0, [(128, 355)])."""
    import torch
    prob = torch.nn.functional.softmax(torch.from_numpy(logits), -1)
    scores_t = prob[..., :-1].max(-1).values
    b = torch.from_numpy(boxes)
    cx, w = b[..., 0], b[..., 2]
    x1 = (cx - 0.5 * w) * 355
    x2 = (cx + 0.5 * w) * 355
    scores, positions = [], []
    for k, l in enumerate(borders):
        scores.extend(scores_t[k].tolist())
        pos = (x1[k] + x2[k]) // 2 + l
        positions.extend(pos.long().tolist())
    scale = lambda x: (x - np.min(x)) / (np.max(x) - np.min(x))
    positions, scores = zip(*sorted(zip(positions, scale(scores))))
    peak_idx, _ = find_peaks(scores, height=sensitivity, distance=radius)
    frames = [positions[i] for i in peak_idx]
    return frames, list(librosa.frames_to_time(frames))


def cues_for(path, onnx_path=None):
    y, _ = librosa.load(path)
    image = to_image(mel_db(y))
    images, borders = windows(image)
    logits, boxes = run_model(normalise(images), onnx_path)
    return postprocess(logits, boxes, borders)


def cmd_cues(args):
    out = {}
    for p in args.files:
        frames, times = cues_for(p, args.onnx)
        out[os.path.basename(p)] = [round(float(t), 3) for t in times]
        print(os.path.basename(p), out[os.path.basename(p)], flush=True)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(out, f, indent=1)


def synthetic_clip(seconds=12.0, sr=22050):
    """Deterministic test signal: kick every beat at 174 BPM, a rising chirp, a hi-hat noise burst."""
    t = np.arange(int(seconds * sr)) / sr
    y = 0.3 * np.sin(2 * np.pi * (200 + 300 * t) * t)
    beat = 60 / 174
    phase = np.mod(t, beat)
    y += 0.6 * np.sin(2 * np.pi * 55 * phase) * np.exp(-phase * 30)
    rng = np.random.default_rng(1234)
    y += 0.05 * rng.standard_normal(len(t)) * (np.mod(t, 2.0) < 0.5)
    return (np.clip(y, -1, 1) * 32767).astype(np.int16)


def cmd_fixtures(args):
    os.makedirs(args.out, exist_ok=True)
    # 1) front end: 16-bit clip -> dB mel -> RGB image -> windows (exact, no model needed)
    pcm = synthetic_clip()
    pcm.tofile(os.path.join(args.out, "clip_22050_s16.raw"))
    y = pcm.astype(np.float32) / 32768.0
    M_db = mel_db(y).astype(np.float32)
    M_db.tofile(os.path.join(args.out, "clip_mel_db_f32.bin"))
    image = to_image(M_db)
    image.astype(np.uint8).tofile(os.path.join(args.out, "clip_rgb_u8.bin"))
    images, borders = windows(image)
    np.stack(images).astype(np.uint8).tofile(os.path.join(args.out, "clip_windows_u8.bin"))
    meta = {"mel_shape": list(M_db.shape), "windows": len(images), "borders": borders}

    # 2) back end: real model outputs for a real track -> cue frames (exact post-processing)
    if args.track:
        y, _ = librosa.load(args.track)
        image = to_image(mel_db(y))
        images, borders = windows(image)
        logits, boxes = run_model(normalise(images), args.onnx)
        logits.astype(np.float32).tofile(os.path.join(args.out, "track_logits_f32.bin"))
        boxes.astype(np.float32).tofile(os.path.join(args.out, "track_boxes_f32.bin"))
        frames, _ = postprocess(logits, boxes, borders)
        meta["track"] = {"windows": len(borders), "borders": borders,
                         "logits_shape": list(logits.shape), "cue_frames": [int(f) for f in frames]}
    with open(os.path.join(args.out, "meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f)
    print("fixtures written to", args.out, meta.get("track", {}).get("cue_frames"))


def cmd_lut(_):
    import matplotlib
    lut = (matplotlib.colormaps['viridis'](np.arange(256))[:, :3] * 255).astype(np.uint8)
    rows = [", ".join(f"0x{v:02X}" for v in lut[i:i + 8].reshape(-1)) for i in range(0, 256, 8)]
    print(",\n".join(rows))


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("cues"); c.add_argument("files", nargs="+"); c.add_argument("--onnx"); c.add_argument("--json")
    f = sub.add_parser("fixtures"); f.add_argument("--out", required=True); f.add_argument("--track"); f.add_argument("--onnx")
    sub.add_parser("lut")
    a = ap.parse_args()
    {"cues": cmd_cues, "fixtures": cmd_fixtures, "lut": cmd_lut}[a.cmd](a)
