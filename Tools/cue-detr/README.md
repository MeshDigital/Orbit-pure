# CUE-DETR tooling

These are developer scripts behind `Tools/Essentia/models/cue-detr.onnx`. They are not part of the
app build: `SLSKDONET.csproj` excludes this folder from Content.

## Setup

Python 3.11:

```
pip install torch --index-url https://download.pytorch.org/whl/cpu
pip install "transformers<5" timm onnx onnxruntime "numpy<2.3" librosa matplotlib scipy pillow
```

## Scripts

- **`export_onnx.py --out ../Essentia/models/cue-detr.onnx`** exports `disco-eth/cue-detr` to ONNX.
  - Opset 17, dynamic batch.
  - Input: `pixel_values [N,3,128,355]`.
  - Outputs: `logits [N,100,2]` and `pred_boxes [N,100,4]`.
  - Before exiting, it checks the ONNX output against PyTorch (logit difference under 1e-3).
- **`reference.py cues <files…> [--onnx model]`** runs the reference pipeline and prints cue
  times. It is a verbatim port of ETH-DISCO's `predict.py`. Checked on a real track: the pixel
  values are bit-identical to `DetrImageProcessor`, and the cue frames match the original script.
- **`reference.py fixtures --out ../../Tests/SLSKDONET.Tests/TestData/CueDetr --track <file> --onnx <model>`**
  regenerates the C# parity fixtures used by `CueDetrParityTests`.
- **`reference.py lut`** prints the viridis byte table embedded in `CueDetrFrontEnd`.

## Parity between the C# port and Python

- **dB mel:** matches librosa to within 0.006 dB.
- **Images:** bit-exact when built from the librosa mel. From ORBIT's own mel, 11 of 198,528 bytes
  differ.
- **Windows, ramp padding and post-processing:** bit-exact, including scipy `find_peaks`.
- **End to end:** the same cue frames on a real FLAC.

The one intended difference is resampling. ORBIT uses its own Kaiser-sinc resampler at about
libsoxr-HQ quality; librosa uses soxr.

## Cost

Inference is about 90 ms per 355-frame window, roughly 10 s for a 4-minute track, on both CPU and
DirectML on the dev machine.
