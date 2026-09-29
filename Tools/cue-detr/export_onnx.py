"""
Export CUE-DETR (disco-eth/cue-detr, MIT) to ONNX for ORBIT's in-process cue detection.

    pip install torch transformers<5 timm onnx onnxruntime numpy<2.3
    python export_onnx.py --out ../models/cue-detr.onnx

Input : pixel_values float32 [N, 3, 128, 355]  (viridis mel image window, ImageNet-normalised)
Output: logits [N, 100, C+1], pred_boxes [N, 100, 4] (cx, cy, w, h normalised)

The export is verified against PyTorch on random input before the script exits.
"""
import argparse
import numpy as np
import torch
from transformers import DetrForObjectDetection


class Wrapper(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, pixel_values):
        mask = torch.ones((pixel_values.shape[0], pixel_values.shape[2], pixel_values.shape[3]),
                          dtype=torch.long, device=pixel_values.device)
        out = self.model(pixel_values=pixel_values, pixel_mask=mask)
        return out.logits, out.pred_boxes


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="disco-eth/cue-detr")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    model = DetrForObjectDetection.from_pretrained(args.model).eval()
    print("labels:", model.config.id2label)
    wrapper = Wrapper(model).eval()
    dummy = torch.randn(2, 3, 128, 355)

    torch.onnx.export(
        wrapper, (dummy,), args.out,
        input_names=["pixel_values"], output_names=["logits", "pred_boxes"],
        dynamic_axes={"pixel_values": {0: "batch"}, "logits": {0: "batch"}, "pred_boxes": {0: "batch"}},
        opset_version=17, do_constant_folding=True, dynamo=False)

    import onnxruntime as ort
    sess = ort.InferenceSession(args.out, providers=["CPUExecutionProvider"])
    x = torch.randn(3, 3, 128, 355)
    with torch.no_grad():
        ref_logits, ref_boxes = wrapper(x)
    logits, boxes = sess.run(None, {"pixel_values": x.numpy()})
    dl = np.abs(logits - ref_logits.numpy()).max()
    db = np.abs(boxes - ref_boxes.numpy()).max()
    print(f"max |diff| logits={dl:.2e} boxes={db:.2e}")
    assert dl < 1e-3 and db < 1e-4, "ONNX output diverges from PyTorch"
    print("ok", args.out)


if __name__ == "__main__":
    main()
