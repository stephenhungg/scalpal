"use client";

import { useEffect, useRef } from "react";

// Halftone "Creation of Adam" hands (public-domain fresco, cut out and grayscaled in
// public/hands). Matches aeterna's hero: 4px grid of 3px squares in a few gray levels,
// dots dissolving off the outer end of Adam's arm, and a cursor lens that pushes dots
// out into a ring (~52px radius).

const VERT = `#version 300 es
in vec2 aPos;
void main() { gl_Position = vec4(aPos, 0.0, 1.0); }`;

const FRAG = `#version 300 es
precision highp float;
uniform vec2 uRes;
uniform float uDpr;
uniform sampler2D uL;
uniform sampler2D uR;
uniform vec4 uRectL; // center x, center y, width, height (CSS px)
uniform vec4 uRectR;
uniform float uRotL; // radians, clockwise on screen
uniform float uRotR;
uniform vec2 uMouse;
uniform float uLens;
uniform float uCell;
out vec4 outColor;

float hash(vec2 p) {
  p = fract(p * vec2(123.34, 456.21));
  p += dot(p, p + 45.32);
  return fract(p.x * p.y);
}

// side -1: outer end at uv.x = 0 (Adam, arm enters from the left); +1: outer end at uv.x = 1.
float hand(sampler2D t, vec4 rect, float rot, vec2 c, float side, float h1, float h2, out float scatter) {
  vec2 d = c - rect.xy;
  float cs = cos(rot), sn = sin(rot);
  vec2 uv = vec2(cs * d.x + sn * d.y, -sn * d.x + cs * d.y) / rect.zw + 0.5;
  scatter = 0.0;
  if (uv.x < -0.15 || uv.x > 1.15 || uv.y < -0.25 || uv.y > 1.25) return 0.0;
  vec2 uvc = clamp(uv, 0.0, 1.0);
  vec4 s = texture(t, uvc);
  float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
  float dark = 1.0 - s.r;
  float v = s.a * inside * (0.08 + 1.5 * max(dark - 0.22, 0.0));
  // Stochastic presence leaves the white gaps aeterna has inside the skin.
  v *= step(h2 * 0.55, v);
  // Dissolve: Adam's forearm breaks up from the wrist (uv.x ~0.66) to the frame edge;
  // the creator's arm only frays right at its outer end.
  float w = side < 0.0 ? 1.0 - smoothstep(0.38, 0.68, uv.x) : smoothstep(0.9, 1.0, uv.x);
  v *= step(w * 0.92, h1);
  float halo = textureLod(t, uvc, 4.0).a;
  scatter = step(h2, w * halo * (side < 0.0 ? 0.32 : 0.12 * inside)) * (0.35 + 0.65 * h1);
  return v;
}

void main() {
  vec2 p = vec2(gl_FragCoord.x, uRes.y * uDpr - gl_FragCoord.y) / uDpr;
  vec2 q = p;
  bool ring = false;
  if (uLens > 0.001) {
    vec2 d = p - uMouse;
    float r = length(d);
    float R = 52.0 * uLens;
    float band = 9.0 * uLens;
    if (r < R) { outColor = vec4(0.0); return; }
    if (r < R + band) { q = uMouse + d / max(r, 1e-3) * ((r - R) / band * (R + band)); ring = true; }
  }
  float cell = uCell;
  vec2 ci = floor(q / cell);
  vec2 loc = (q - ci * cell) * (3.0 / cell);
  vec2 c = (ci + 0.5) * cell;
  float h1 = hash(ci);
  float h2 = hash(ci + 17.3);
  float sL; float sR;
  float v = max(hand(uL, uRectL, uRotL, c, -1.0, h1, h2, sL), hand(uR, uRectR, uRotR, c, 1.0, h1, h2, sR));
  v = max(v, max(sL, sR));
  float level = floor(clamp(v, 0.0, 1.0) * 8.0 + h2 * 0.5) / 8.0;
  // 2px dot in a 3px cell; dark cells sometimes bridge into the gap, which reads as the
  // short horizontal runs visible in the reference.
  bool bridge = level > 0.55 && h1 > 0.6;
  float shape = bridge
    ? max(step(loc.x, 3.0) * step(loc.y, 2.0), step(loc.x, 2.0) * step(loc.y, 3.0))
    : step(loc.x, 2.0) * step(loc.y, 2.0);
  // Dots pushed into the ring thin out, like the scattered ring on aeterna.
  if (ring && h2 > 0.55) shape = 0.0;
  outColor = vec4(0.0, 0.0, 0.0, level * shape);
}`;

// Placement fitted to aeterna's hero silhouettes at a 1200px container / 900px viewport
// (best-overlap search over scale, rotation, and position), scaled with container width
// and anchored to the viewport bottom. Angles are clockwise.
const DEG = Math.PI / 180;
function layout(w: number, h: number, aspL: number, aspR: number) {
  const k = w / 1200;
  const lw = 858 * k, rw = 825 * k;
  const lift = w < 810 ? 55 * (h / 844) : 0; // phones sit the pair a little higher
  return {
    L: [77 * k, h - 250 * k - lift, lw, lw / aspL, 14 * DEG],
    R: [1133 * k, h - 267 * k - lift, rw, rw / aspR, 1 * DEG],
  };
}

function loadImage(src: string) {
  return new Promise<HTMLImageElement>((res, rej) => {
    const im = new Image();
    im.onload = () => res(im);
    im.onerror = rej;
    im.src = src;
  });
}

export function HandsField({ className = "" }: { className?: string }) {
  const ref = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = ref.current;
    if (!canvas) return;
    const gl = canvas.getContext("webgl2", { premultipliedAlpha: true, antialias: false });
    if (!gl) return;

    const compile = (type: number, src: string) => {
      const s = gl.createShader(type)!;
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s) ?? "shader");
      return s;
    };
    const prog = gl.createProgram()!;
    gl.attachShader(prog, compile(gl.VERTEX_SHADER, VERT));
    gl.attachShader(prog, compile(gl.FRAGMENT_SHADER, FRAG));
    gl.linkProgram(prog);
    gl.useProgram(prog);
    const buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    const aPos = gl.getAttribLocation(prog, "aPos");
    gl.enableVertexAttribArray(aPos);
    gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 0, 0);
    const u = (n: string) => gl.getUniformLocation(prog, n);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);

    let disposed = false;
    let raf = 0;
    let aspL = 2.16, aspR = 1.545;
    const mouse = { x: -9999, y: -9999, tx: -9999, ty: -9999 };
    let lens = 0, lensTarget = 0, ready = false;

    const texture = (im: HTMLImageElement, unit: number) => {
      const t = gl.createTexture();
      gl.activeTexture(gl.TEXTURE0 + unit);
      gl.bindTexture(gl.TEXTURE_2D, t);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, im);
      gl.generateMipmap(gl.TEXTURE_2D);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    };

    const draw = () => {
      raf = 0;
      if (!ready || disposed) return;
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      const w = canvas.clientWidth, h = canvas.clientHeight;
      if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
        canvas.width = Math.round(w * dpr);
        canvas.height = Math.round(h * dpr);
      }
      gl.viewport(0, 0, canvas.width, canvas.height);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      const { L, R } = layout(w, h, aspL, aspR);
      mouse.x += (mouse.tx - mouse.x) * 0.25;
      mouse.y += (mouse.ty - mouse.y) * 0.25;
      lens += (lensTarget - lens) * 0.15;
      gl.uniform2f(u("uRes"), w, h);
      gl.uniform1f(u("uDpr"), dpr);
      gl.uniform1i(u("uL"), 0);
      gl.uniform1i(u("uR"), 1);
      gl.uniform4f(u("uRectL"), L[0], L[1], L[2], L[3]);
      gl.uniform4f(u("uRectR"), R[0], R[1], R[2], R[3]);
      gl.uniform1f(u("uRotL"), L[4]);
      gl.uniform1f(u("uRotR"), R[4]);
      gl.uniform2f(u("uMouse"), mouse.x, mouse.y);
      gl.uniform1f(u("uLens"), lens);
      // Phones get finer dots (the reference swaps in a fine still image below 810px).
      gl.uniform1f(u("uCell"), w >= 810 ? 3 : 1.5);
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      const moving = Math.abs(mouse.tx - mouse.x) > 0.3 || Math.abs(mouse.ty - mouse.y) > 0.3 || Math.abs(lensTarget - lens) > 0.002;
      if (moving) raf = requestAnimationFrame(draw);
    };
    const request = () => { if (!raf) raf = requestAnimationFrame(draw); };

    Promise.all([loadImage("/hands/adam.webp"), loadImage("/hands/creator.webp")]).then(([a, c]) => {
      if (disposed) return;
      aspL = a.width / a.height;
      aspR = c.width / c.height;
      texture(a, 0);
      texture(c, 1);
      ready = true;
      request();
    });

    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    const onMove = (e: PointerEvent) => {
      if (reduce || e.pointerType === "touch") return;
      const r = canvas.getBoundingClientRect();
      const x = e.clientX - r.left, y = e.clientY - r.top;
      const inside = x >= 0 && y >= 0 && x <= r.width && y <= r.height;
      if (inside && lensTarget === 0) { mouse.x = x; mouse.y = y; }
      mouse.tx = x; mouse.ty = y;
      lensTarget = inside ? 1 : 0;
      request();
    };
    const onLeave = () => { lensTarget = 0; request(); };
    const ro = new ResizeObserver(request);
    ro.observe(canvas);
    window.addEventListener("pointermove", onMove);
    document.addEventListener("pointerleave", onLeave);
    return () => {
      disposed = true;
      cancelAnimationFrame(raf);
      ro.disconnect();
      window.removeEventListener("pointermove", onMove);
      document.removeEventListener("pointerleave", onLeave);
    };
  }, []);

  return <canvas ref={ref} aria-hidden className={`pointer-events-none block h-full w-full ${className}`} />;
}
