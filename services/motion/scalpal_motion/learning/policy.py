"""State-based behavior cloning: an MLP that predicts a chunk of future actions (ACT-style chunking).

Input: the 42-dim robot/object state from `TransferEnv.obs()` (no time or phase input, so the
policy cannot replay a clock). Output: 10 future actions of [wrist delta (4), finger targets (18)].
At run time overlapping chunks are averaged with exponential weights (temporal ensembling).
"""

from __future__ import annotations

import time
from dataclasses import dataclass

import numpy as np
import torch
from torch import nn

from .demos import CHUNK
from .env import ACT_DIM, OBS_DIM


class ChunkMLP(nn.Module):
    def __init__(self, obs_dim: int = OBS_DIM, act_dim: int = ACT_DIM, chunk: int = CHUNK, hidden: int = 512, depth: int = 3,
                 dropout: float = 0.1):
        super().__init__()
        layers, d = [], obs_dim
        for _ in range(depth):
            layers += [nn.Linear(d, hidden), nn.LayerNorm(hidden), nn.GELU(), nn.Dropout(dropout)]
            d = hidden
        layers.append(nn.Linear(d, chunk * act_dim))
        self.net = nn.Sequential(*layers)
        self.chunk, self.act_dim = chunk, act_dim

    def forward(self, x):
        return self.net(x).view(-1, self.chunk, self.act_dim)


@dataclass
class Policy:
    model: ChunkMLP
    obs_mu: np.ndarray
    obs_sd: np.ndarray
    act_mu: np.ndarray
    act_sd: np.ndarray

    def predict(self, obs: np.ndarray) -> np.ndarray:
        x = torch.from_numpy(((obs - self.obs_mu) / self.obs_sd).astype(np.float32))[None]
        with torch.no_grad():
            y = self.model(x)[0].numpy()
        return y * self.act_sd + self.act_mu

    def state_dict(self) -> dict:
        return {"model": self.model.state_dict(), "obs_mu": self.obs_mu, "obs_sd": self.obs_sd, "act_mu": self.act_mu,
                "act_sd": self.act_sd}

    @classmethod
    def from_state(cls, st: dict) -> Policy:
        m = ChunkMLP()
        m.load_state_dict(st["model"])
        m.eval()
        return cls(m, st["obs_mu"], st["obs_sd"], st["act_mu"], st["act_sd"])


def train(data: dict, seed: int = 0, steps: int = 6000, batch: int = 512, lr: float = 1e-3, obs_noise: float = 0.005,
          threads: int = 2, log_every: int = 0) -> tuple[Policy, dict]:
    """L1 behavior cloning on normalized observations and action chunks (AdamW, cosine decay)."""
    torch.manual_seed(seed)
    np.random.seed(seed)
    torch.set_num_threads(threads)
    obs = data["obs"].astype(np.float32)
    act = data["act"].astype(np.float32)
    obs_mu, obs_sd = obs.mean(0), obs.std(0) + 1e-3
    flat = act.reshape(-1, ACT_DIM)
    act_mu, act_sd = flat.mean(0), flat.std(0) + 1e-3
    X = torch.from_numpy((obs - obs_mu) / obs_sd)
    Y = torch.from_numpy((act - act_mu) / act_sd)
    model = ChunkMLP()
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, steps)
    g = torch.Generator().manual_seed(seed)
    n = len(X)
    t0 = time.perf_counter()
    losses = []
    model.train()
    for it in range(steps):
        idx = torch.randint(0, n, (min(batch, n),), generator=g)
        x = X[idx] + obs_noise / torch.from_numpy(obs_sd) * torch.randn(len(idx), X.shape[1], generator=g)
        loss = (model(x) - Y[idx]).abs().mean()
        opt.zero_grad()
        loss.backward()
        opt.step()
        sched.step()
        if it % 100 == 0 or it == steps - 1:
            losses.append(float(loss.detach()))
        if log_every and it % log_every == 0:
            print(f"step {it} loss {float(loss):.4f}")
    model.eval()
    info = {"steps": steps, "samples": int(n), "final_loss": round(float(np.mean(losses[-5:])), 4),
            "train_s": round(time.perf_counter() - t0, 1), "seed": seed}
    return Policy(model, obs_mu, obs_sd, act_mu, act_sd), info


class Ensembler:
    """ACT temporal ensembling: average every chunk's prediction for the current step, w_i = exp(-m * i)."""

    def __init__(self, chunk: int = CHUNK, m: float = 0.1):
        self.chunk, self.m = chunk, m
        self.buf: list[tuple[int, np.ndarray]] = []

    def __call__(self, t: int, chunk: np.ndarray) -> np.ndarray:
        self.buf.append((t, chunk))
        self.buf = [(s, c) for s, c in self.buf if t - s < self.chunk]
        preds = np.stack([c[t - s] for s, c in self.buf])  # oldest first
        w = np.exp(-self.m * np.arange(len(preds)))
        return (w[:, None] * preds).sum(0) / w.sum()
