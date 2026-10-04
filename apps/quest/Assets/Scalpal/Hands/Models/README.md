# Hand models

MediaPipe Hands (Google, Apache-2.0), converted to ONNX for Unity's Inference Engine by Unity
([unity/inference-engine-blaze-hand](https://huggingface.co/unity/inference-engine-blaze-hand)).

| File | Input | Outputs |
| --- | --- | --- |
| `hand_detector.onnx` | 1x192x192x3 RGB 0..1 | `Identity` 1x2016x18 box regressors, `Identity_1` 1x2016x1 score logits |
| `hand_landmarks_detector.onnx` | 1x224x224x3 rotated hand crop | `Identity` 21x3 crop-space joints, `Identity_1` presence, `Identity_2` handedness, `Identity_3` 21x3 metric world joints |
| `hand_anchors.csv` | 2016 detector anchors | |

Stored with Git LFS. `services/hands/reference.py` runs the same files on a Mac and checks them against Google's MediaPipe.
