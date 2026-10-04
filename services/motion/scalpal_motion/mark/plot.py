"""Presentation-only plot of saved mark-incision evaluations; never trains a policy.

Usage: uv run python -m scalpal_motion.mark.plot INPUT.json OUTPUT.png
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np


def _read_curve(path: Path) -> dict:
    curve = json.loads(path.read_text())
    if curve.get("schema") != "scalpal.robot_mark_curve.v1" or curve.get("step") != "mark_incision":
        raise ValueError("Expected a saved mark-incision learning curve")
    rows = curve.get("rows", [])
    seeds = curve.get("seeds", 0)
    trials = curve.get("rolloutsPerSeed", 0)
    if not rows or seeds < 2 or trials < 1:
        raise ValueError("Curve requires evaluation rows and at least two training seeds")
    for row in rows:
        if row.get("humanDemos") != 0 or row.get("syntheticDemos") != row.get("demos"):
            raise ValueError("This presentation is specifically for synthetic-only demonstrations")
        rates = row.get("perSeed", [])
        if len(rates) != seeds or row.get("rollouts") != seeds * trials:
            raise ValueError("Seed counts and evaluation rollout counts must agree")
        if not all(0 <= v <= 1 for v in [row["successRate"], *rates]):
            raise ValueError("Success rates must be between zero and one")
        if not np.isclose(np.mean(rates), row["successRate"], atol=0.001):
            raise ValueError("Combined success rate must agree with the training seeds")
    if any(a["demos"] >= b["demos"] for a, b in zip(rows, rows[1:])) or rows[0]["demos"] <= 0:
        raise ValueError("Demonstration counts must be positive and strictly increasing")
    return curve


def plot_learning_curve(source: str | Path, output: str | Path) -> Path:
    """Write a 1920x1080 evidence card, showing seed range (not an IID interval).

    The aggregate points are the saved evaluation values, not fitted or smoothed.
    The stored binomial interval is deliberately omitted: each training seed uses
    the same held-out scenarios, so the pooled rollouts are not distinct patients.
    """
    from matplotlib.backends.backend_agg import FigureCanvasAgg
    from matplotlib.figure import Figure
    from matplotlib.lines import Line2D
    from matplotlib.patches import Patch
    from matplotlib.ticker import NullLocator

    curve = _read_curve(Path(source))
    rows = curve["rows"]
    x = np.array([r["demos"] for r in rows])
    y = np.array([r["successRate"] for r in rows]) * 100
    per_seed = np.array([r["perSeed"] for r in rows]) * 100
    bg, white, gray = "#111b27", "#edf3f6", "#b8c6d2"
    mint, purple = "#73e0c0", "#b09aff"

    fig = Figure(figsize=(16, 9), dpi=120, facecolor=bg)
    FigureCanvasAgg(fig)
    fig.text(.058, .945, "SCALPAL  /  SIMULATED POLICY EVALUATION", color=mint, fontsize=13, weight="bold")
    fig.text(.058, .884, "More demonstrations improve simulated marking.", color=white, fontsize=27, weight="bold")
    fig.text(.058, .832, "Synthetic demonstrations · 0 headset demonstrations", color=mint, fontsize=21)

    ax = fig.add_axes((.09, .255, .86, .475), facecolor=bg)
    ax.set_xscale("log")
    ax.set_xlim(x[0] / 1.1, x[-1] * 1.09)
    ax.set_ylim(0, 114)
    ax.set_axisbelow(True)
    ax.grid(axis="y", color="#334252", linewidth=.8)
    ax.fill_between(x, per_seed.min(axis=1), per_seed.max(axis=1), color=mint, alpha=.12, zorder=1)
    ax.vlines(x, per_seed.min(axis=1), per_seed.max(axis=1), color=mint, alpha=.75, linewidth=2, zorder=2)
    ax.scatter(np.repeat(x, per_seed.shape[1]), per_seed.ravel(), color=mint, marker="_", s=180, linewidth=2, zorder=3)
    ax.plot(x, y, color=purple, linewidth=3.2, marker="o", markersize=9, markeredgecolor=bg, markeredgewidth=2, zorder=4)
    for count, rate in zip(x, y):
        ax.annotate(f"{rate:.0f}%", (count, rate), xytext=(0, 16), textcoords="offset points", ha="center", color=white, fontsize=20, weight="bold")

    endpoint = rows[-1]
    total = endpoint["rollouts"]
    successes = round(endpoint["successRate"] * total)
    ax.annotate(f"{successes}/{total} evaluation rollouts", (x[-1], y[-1]),
                xytext=(-8, -39), textcoords="offset points", ha="right", color=white, fontsize=19, weight="bold")
    ax.set_xticks(x, [str(n) for n in x])
    ax.xaxis.set_minor_locator(NullLocator())
    ax.set_yticks([0, 25, 50, 75, 100], ["0%", "25%", "50%", "75%", "100%"])
    ax.tick_params(axis="both", colors=gray, labelsize=17, length=0, pad=12)
    for spine in ax.spines.values():
        spine.set_visible(False)
    ax.set_xlabel("Passing synthetic demonstrations used for training (log scale)", color=white, fontsize=18, labelpad=16)
    ax.text(0, 1.075, "Shared milestone checks passed", transform=ax.transAxes, color=gray, fontsize=16)
    ax.legend(handles=[Line2D([0], [0], color=purple, linewidth=3, label="Combined success"),
                       Patch(facecolor=mint, alpha=.35, label=f"Range across {curve['seeds']} training seeds")],
              loc="lower right", bbox_to_anchor=(1, 1.045), ncol=2, frameon=False, labelcolor=gray, fontsize=14, handlelength=1.5)

    fig.text(.058, .101, f"{curve['rolloutsPerSeed']} held-out scenarios reused by {curve['seeds']} training seeds · {total} rollouts per point", color=white, fontsize=17)
    fig.text(.058, .061, "Seed range shows training variability, not a confidence interval or distinct patients.", color=gray, fontsize=16)
    target = Path(output)
    target.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(target, dpi=120, facecolor=bg, metadata={
        "Title": "More demonstrations improve simulated marking.",
        "Description": "Synthetic demonstrations · 0 headset demonstrations; seed range, not confidence interval. "
                       + "; ".join(f"{r['demos']} demos: {r['successRate']:.3f}" for r in rows),
    })
    return target


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    print(plot_learning_curve(args.source, args.output))
