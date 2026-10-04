"""Python mirror of apps/quest/Assets/Scalpal/Hands/Runtime/BlazeHandMath.cs.

Runs the same ONNX models Unity runs on the headset (MediaPipe's palm detector and hand landmarker,
converted by Unity), with the same crop math as the C# port, and checks the joints against Google's
MediaPipe HandLandmarker on one image. With --golden it writes the values the C# unit test checks.

  uv run python reference.py IMAGE [--golden out.json]
"""

import argparse
import json
import math
import sys

import cv2
import numpy as np
import onnxruntime as ort

from models import ensure_models

DET, LMK = 192, 224


def affine(a, b, c, d, e, f):
    return np.array([[a, b, c], [d, e, f]], np.float64)


def translation(x, y):
    return affine(1, 0, x, 0, 1, y)


def scale(x, y):
    return affine(x, 0, 0, 0, y, 0)


def rotation(t):
    return affine(math.cos(t), -math.sin(t), 0, math.sin(t), math.cos(t), 0)


def mul(p, q):
    return (np.vstack([p, [0, 0, 1]]) @ np.vstack([q, [0, 0, 1]]))[:2]


def apply(m, x, y):
    return m[0, 0] * x + m[0, 1] * y + m[0, 2], m[1, 0] * x + m[1, 1] * y + m[1, 2]


def sigmoid(x):
    return 1 / (1 + math.exp(-max(-100.0, min(100.0, x))))


def detector_to_image(w, h):
    size = max(w, h)
    s = size / DET
    return mul(translation(0.5 * (w - size), 0.5 * (h + size)), scale(s, -s))


def detection_to_roi(box, anchor):
    ax, ay = DET * anchor[0], DET * anchor[1]
    cx, cy = ax + box[0], ay + box[1]
    size = max(box[2], box[3])
    dx, dy = box[8] - box[4], box[9] - box[5]
    length = max(math.hypot(dx, dy), 1e-6)
    cx += 0.5 * size * dx / length
    cy += 0.5 * size * dy / length
    return dict(centerX=cx, centerY=cy, size=size * 2.6, rotation=0.5 * math.pi - math.atan2(dy, dx))


def landmarker_to_image(roi_space_to_image, roi):
    s = roi["size"] / LMK
    half = 0.5 * LMK
    m = mul(roi_space_to_image, translation(roi["centerX"], roi["centerY"]))
    m = mul(m, scale(s, -s))
    m = mul(m, rotation(roi["rotation"]))
    return mul(m, translation(-half, -half))


def top_left_to_image(h):
    return mul(translation(0, h), scale(1, -1))


def roi_from_landmarks(xs, ys, scale_factor=1.8, shift=0.0):
    dx, dy = xs[9] - xs[0], ys[9] - ys[0]
    rot = 0.5 * math.pi - math.atan2(dy, dx)
    cos, sin = math.cos(-rot), math.sin(-rot)
    u = cos * xs - sin * ys
    v = sin * xs + cos * ys
    cu, cv = 0.5 * (u.min() + u.max()), 0.5 * (v.min() + v.max())
    size = scale_factor * max(u.max() - u.min(), v.max() - v.min())
    cx, cy = cos * cu + sin * cv, -sin * cu + cos * cv
    length = math.hypot(dx, dy)
    if length > 1e-6:
        cx += shift * size * dx / length
        cy += shift * size * dy / length
    return dict(centerX=cx, centerY=cy, size=size, rotation=rot)


def sample(img_up, m, n):
    """ImageTransform.compute: bilinear sample of a bottom-left-origin image at m * (col, row)."""
    ys, xs = np.mgrid[0:n, 0:n].astype(np.float32)
    px = (m[0, 0] * xs + m[0, 1] * ys + m[0, 2]).astype(np.float32)
    py = (m[1, 0] * xs + m[1, 1] * ys + m[1, 2]).astype(np.float32)
    h, w = img_up.shape[:2]
    inside = (px >= 0) & (px <= w) & (py >= 0) & (py <= h)
    out = cv2.remap(img_up, px - 0.5, py - 0.5, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
    return (out * inside[..., None])[None].astype(np.float32)


class BlazeHand:
    def __init__(self):
        paths = ensure_models()
        self.det = ort.InferenceSession(str(paths["detector"]))
        self.lmk = ort.InferenceSession(str(paths["landmarker"]))
        self.anchors = np.loadtxt(paths["anchors"], delimiter=",")[:2016]

    def detect(self, img_up):
        h, w = img_up.shape[:2]
        m = detector_to_image(w, h)
        boxes, scores = self.det.run(None, {"input_1": sample(img_up, m, DET)})
        idx = int(np.argmax(scores[0, :, 0]))
        roi = detection_to_roi(boxes[0, idx], self.anchors[idx])
        return m, roi, idx, boxes[0, idx], sigmoid(float(scores[0, idx, 0]))

    def landmarks(self, img_up, roi_space_to_image, roi):
        m2 = landmarker_to_image(roi_space_to_image, roi)
        screen, presence, handedness, world = self.lmk.run(None, {"input_1": sample(img_up, m2, LMK)})
        lm = screen.reshape(21, 3)
        pts = np.array([apply(m2, lm[i, 0], lm[i, 1]) for i in range(21)])  # Unity texture pixels (y up)
        return pts, float(presence.ravel()[0]), float(handedness.ravel()[0]), world.reshape(21, 3), m2


def mediapipe_reference(img_rgb):
    import mediapipe as mp
    from mediapipe.tasks.python import BaseOptions, vision

    task = ensure_models()["task"]
    lm = vision.HandLandmarker.create_from_options(
        vision.HandLandmarkerOptions(base_options=BaseOptions(model_asset_path=str(task)), num_hands=2)
    )
    res = lm.detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=img_rgb))
    h, w = img_rgb.shape[:2]
    return [(np.array([[p.x * w, p.y * h] for p in hand]), hd[0].category_name) for hand, hd in zip(res.hand_landmarks, res.handedness)]


def compare(ours_top_left, refs):
    best = None
    for ref, label in refs:
        err = np.linalg.norm(ref - ours_top_left, axis=1)
        diag = float(np.hypot(*(ref.max(0) - ref.min(0))))
        if best is None or err.mean() < best["mean_px"]:
            best = dict(label=label, mean_px=float(err.mean()), max_px=float(err.max()), percent_of_hand=100 * float(err.mean()) / diag)
    return best


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("image")
    ap.add_argument("--golden")
    args = ap.parse_args()
    img = cv2.cvtColor(cv2.imread(args.image), cv2.COLOR_BGR2RGB)
    h, w = img.shape[:2]
    img_up = img[::-1].astype(np.float32) / 255.0
    bh = BlazeHand()
    refs = mediapipe_reference(img)
    if not refs:
        sys.exit("MediaPipe found no hand in this image; use a photo with a clearly visible hand")

    m, roi, idx, box, det_score = bh.detect(img_up)
    pts, presence, handedness, world, m2 = bh.landmarks(img_up, m, roi)
    tl = np.stack([pts[:, 0], h - pts[:, 1]], 1)
    detect_cmp = compare(tl, refs)

    track_roi = roi_from_landmarks(tl[:, 0], tl[:, 1])
    pts2, presence2, _, _, m3 = bh.landmarks(img_up, top_left_to_image(h), track_roi)
    tl2 = np.stack([pts2[:, 0], h - pts2[:, 1]], 1)
    track_cmp = compare(tl2, refs)

    report = dict(
        detector_score=det_score,
        presence=presence,
        handedness_raw=handedness,
        mediapipe_label=detect_cmp["label"],
        world_wrist_to_middle_mcp_m=float(np.linalg.norm(world[9] - world[0])),
        detection_vs_mediapipe=detect_cmp,
        tracking_vs_mediapipe=dict(**track_cmp, presence=presence2),
    )
    print(json.dumps(report, indent=1))
    ok = detect_cmp["percent_of_hand"] < 5 and track_cmp["percent_of_hand"] < 5
    if args.golden:
        golden = dict(
            image=dict(width=w, height=h),
            detection=dict(index=idx, box=[float(v) for v in box], anchor=[float(v) for v in bh.anchors[idx]], roi=roi),
            detectorToImage=m.ravel().tolist(),
            landmarkerToImage=m2.ravel().tolist(),
            tracking=dict(xTopLeft=tl[:, 0].tolist(), yTopLeft=tl[:, 1].tolist(), roi=track_roi, landmarkerToImage=m3.ravel().tolist()),
        )
        with open(args.golden, "w") as f:
            json.dump(golden, f, indent=1)
        print(f"wrote {args.golden}")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
