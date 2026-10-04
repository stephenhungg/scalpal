"""Short OWLv2 head fine-tune on the synthetic instrument records, with before/after metrics.

    uv run python scripts/prepare_finetune.py --dataset data/synthetic
    uv run python scripts/finetune_owlv2.py --data data/synthetic/finetune --epochs 6 --out runs/owlv2-synth

What it trains: only the class head (the patch-embedding projection and per-patch logit
shift/scale that score patches against prompt embeddings). The vision and text towers and
the box head stay frozen: zero-shot OWLv2 already localizes these renders (mean IoU 0.68
on held-out views); what it lacks is telling 15 similar instruments apart.

Targets, per image: every patch whose frozen predicted box has IoU >= 0.5 with a
ground-truth box is a positive for that box's prompt (the single best patch if none
reaches 0.5); every other patch x prompt pair is a negative. Loss is sigmoid focal loss
normalized by the number of positives.

An earlier variant that also trained the box head with DETR-style one-to-one Hungarian
matching made localization worse (held-out mean IoU 0.68 -> 0.26) and is not kept.

Boxes are in OWLv2's space: normalized cx, cy, w, h relative to the image padded to a
square at the bottom/right (divide pixels by the long side).

Metric (test split, held-out views): for each image, the single highest-scoring detection
over all 15 prompts. "top1_class" is the fraction whose class is correct; "top1_hit@0.5"
also requires IoU >= 0.5 with the ground truth box; "gt_iou" is the mean IoU of the best
box for the correct prompt. This measures synthetic-render recognition only, never
real-camera accuracy.

`--save` writes a Hugging Face checkpoint usable with `scalpal-vision serve --weights`.
"""

from __future__ import annotations

import argparse
import json
import random
import time
from pathlib import Path

import torch
import torch.nn.functional as F
from PIL import Image
from transformers import Owlv2ForObjectDetection, Owlv2Processor

from scalpal_vision.detector import OWLV2_NATIVE, OWLV2_SIZE, owlv2_pixels

REPO = "google/owlv2-base-patch16-ensemble"


def load(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]


def to_cxcywh(xyxy, side):
    x0, y0, x1, y1 = (v / side for v in xyxy)
    return [(x0 + x1) / 2, (y0 + y1) / 2, x1 - x0, y1 - y0]


def cxcywh_to_xyxy(b: torch.Tensor) -> torch.Tensor:
    cx, cy, w, h = b.unbind(-1)
    return torch.stack([cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2], -1)


def giou(a: torch.Tensor, b: torch.Tensor) -> torch.Tensor:
    """Pairwise generalized IoU for xyxy boxes, a: (N, 4), b: (M, 4) -> (N, M)."""
    area_a = (a[:, 2] - a[:, 0]).clamp(min=0) * (a[:, 3] - a[:, 1]).clamp(min=0)
    area_b = (b[:, 2] - b[:, 0]).clamp(min=0) * (b[:, 3] - b[:, 1]).clamp(min=0)
    lt = torch.max(a[:, None, :2], b[None, :, :2])
    rb = torch.min(a[:, None, 2:], b[None, :, 2:])
    inter = (rb - lt).clamp(min=0).prod(-1)
    union = area_a[:, None] + area_b[None, :] - inter
    iou = inter / union.clamp(min=1e-9)
    lt_c = torch.min(a[:, None, :2], b[None, :, :2])
    rb_c = torch.max(a[:, None, 2:], b[None, :, 2:])
    hull = (rb_c - lt_c).clamp(min=0).prod(-1)
    return iou - (hull - union) / hull.clamp(min=1e-9), iou


def focal(logits: torch.Tensor, targets: torch.Tensor, alpha=0.25, gamma=2.0) -> torch.Tensor:
    prob = logits.sigmoid()
    ce = F.binary_cross_entropy_with_logits(logits, targets, reduction="none")
    p_t = prob * targets + (1 - prob) * (1 - targets)
    loss = ce * (1 - p_t) ** gamma
    loss = (alpha * targets + (1 - alpha) * (1 - targets)) * loss
    return loss.sum()


class Runner:
    def __init__(self, device: str, prompts: list[str], weights: str | None = None, size: int = OWLV2_SIZE):
        self.device = device
        self.size = size
        self.processor = Owlv2Processor.from_pretrained(REPO)
        self.model = Owlv2ForObjectDetection.from_pretrained(weights or REPO).to(device)
        tokens = self.processor.tokenizer(prompts, padding="max_length", max_length=16, truncation=True, return_tensors="pt")
        self.text = {k: v.to(device) for k, v in tokens.items()}

    def pixels(self, record: dict) -> torch.Tensor:
        image = Image.open(record["image"]).convert("RGB")
        return owlv2_pixels(image, self.size).to(self.device)

    def forward(self, pixel_values: torch.Tensor):
        return self.model(pixel_values=pixel_values, **self.text, interpolate_pos_encoding=self.size != OWLV2_NATIVE)

    @torch.inference_mode()
    def evaluate(self, records: list[dict]) -> dict:
        self.model.eval()
        class_hits = hit50 = 0
        gt_ious = []
        for record in records:
            out = self.forward(self.pixels(record))
            side = max(record["width"], record["height"])
            logits = out.logits[0].float().cpu()
            boxes = cxcywh_to_xyxy(out.pred_boxes[0].float().cpu())
            gt = record["boxes"][0]
            gt_box = torch.tensor([[v / side for v in gt["xyxy"]]])
            flat = logits.flatten().argmax().item()
            patch, cls = divmod(flat, logits.shape[1])
            _, iou_top = giou(boxes[patch : patch + 1], gt_box)
            class_hits += cls == gt["class"]
            hit50 += cls == gt["class"] and iou_top.item() >= 0.5
            best_patch = logits[:, gt["class"]].argmax().item()
            _, iou_gt = giou(boxes[best_patch : best_patch + 1], gt_box)
            gt_ious.append(iou_gt.item())
        n = len(records)
        return {
            "images": n,
            "top1_class": round(class_hits / n, 3),
            "top1_hit@0.5": round(hit50 / n, 3),
            "gt_iou": round(sum(gt_ious) / n, 3),
        }

    def train(self, records: list[dict], epochs: int, lr: float, log_every: int = 25) -> list[float]:
        model = self.model
        for name, param in model.named_parameters():
            param.requires_grad = name.startswith("class_head.")
        trainable = [p for p in model.parameters() if p.requires_grad]
        print(f"trainable params: {sum(p.numel() for p in trainable):,}", flush=True)
        optimizer = torch.optim.AdamW(trainable, lr=lr, weight_decay=1e-4)
        cache = {r["image"]: self.pixels(r).cpu() for r in records}
        losses = []
        step = 0
        for epoch in range(epochs):
            model.train()
            order = records[:]
            random.shuffle(order)
            for record in order:
                pixel = cache[record["image"]].to(self.device)
                flipped = random.random() < 0.5
                if flipped:  # horizontal flip; the padded square is 1.0 wide in box space
                    pixel = pixel.flip(-1)
                out = self.forward(pixel)
                logits = out.logits[0]
                side = max(record["width"], record["height"])
                target = torch.zeros_like(logits)
                with torch.no_grad():
                    pred = cxcywh_to_xyxy(out.pred_boxes[0].float())
                    for box in record["boxes"]:
                        cx, cy, w, h = to_cxcywh(box["xyxy"], side)
                        if flipped:
                            cx = 1.0 - cx
                        gt = cxcywh_to_xyxy(torch.tensor([[cx, cy, w, h]], device=self.device))
                        _, overlap = giou(pred, gt)
                        overlap = overlap[:, 0]
                        positive = overlap >= 0.5
                        if not positive.any():
                            positive = overlap == overlap.max()
                        target[positive, box["class"]] = 1.0
                loss = focal(logits, target) / target.sum().clamp(min=1)
                optimizer.zero_grad()
                loss.backward()
                optimizer.step()
                losses.append(loss.item())
                step += 1
                if step % log_every == 0:
                    recent = losses[-log_every:]
                    print(f"epoch {epoch} step {step} loss {sum(recent) / len(recent):.4f}", flush=True)
        model.eval()
        return losses


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", type=Path, required=True, help="folder with train.jsonl, test.jsonl, prompts.json")
    parser.add_argument("--epochs", type=int, default=8)
    parser.add_argument("--lr", type=float, default=1e-4)
    parser.add_argument("--device", default="mps" if torch.backends.mps.is_available() else "cpu")
    parser.add_argument("--out", type=Path, default=Path("runs/owlv2-synth"))
    parser.add_argument("--save", action="store_true", help="also save the fine-tuned weights")
    parser.add_argument("--seed", type=int, default=817)
    parser.add_argument("--size", type=int, default=OWLV2_SIZE, help="input size; match the service's --size")
    args = parser.parse_args()
    random.seed(args.seed)
    torch.manual_seed(args.seed)
    prompts = json.loads((args.data / "prompts.json").read_text())["prompts"]
    train, test = load(args.data / "train.jsonl"), load(args.data / "test.jsonl")
    runner = Runner(args.device, prompts, size=args.size)
    report = {"device": args.device, "size": args.size, "train_images": len(train), "test_images": len(test), "epochs": args.epochs, "lr": args.lr}
    report["zero_shot_test"] = runner.evaluate(test)
    print("zero-shot test:", report["zero_shot_test"], flush=True)
    start = time.perf_counter()
    losses = runner.train(train, args.epochs, args.lr)
    report["train_seconds"] = round(time.perf_counter() - start, 1)
    report["loss_first_epoch"] = round(sum(losses[: len(train)]) / len(train), 3)
    report["loss_last_epoch"] = round(sum(losses[-len(train) :]) / len(train), 3)
    report["finetuned_test"] = runner.evaluate(test)
    report["finetuned_train"] = runner.evaluate(train)
    print("fine-tuned test:", report["finetuned_test"], flush=True)
    print("fine-tuned train:", report["finetuned_train"], flush=True)
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    if args.save:
        runner.model.save_pretrained(args.out / "weights")
        runner.processor.save_pretrained(args.out / "weights")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
