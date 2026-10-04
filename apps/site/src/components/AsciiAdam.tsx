"use client";

// From MatthewKim323/adam (src/components/AsciiAdam.tsx), unchanged apart from this header, both arms set to white, the onTouch / introSpeed / holdAfterTouch (with breathing) / shiftY props, and pointer reach turned off (glyph scramble kept).
import { useEffect, useRef } from 'react'

/*
 * Two reaching arms rendered as ASCII on a WebGL2 canvas.
 * The scene (two cutout textures + a spark) is evaluated once per cell at the
 * cell's center, its luminance picks a glyph off RAMP, its color tints it.
 */

// dim -> bright; the last entry is the inverted "?" tile
const RAMP = [' ', '.', ':', '-', '+', '*', '%', '#', '@', 'TILE']
const LOOP = 9.55 // seconds from apart to touching (and holding); then it plays back in reverse
const ASPECT = 3696 / 2304 // design frame, covered onto the canvas
const CELL = 21 / 2304 // cell size as a fraction of the covered frame's height

const vert = /* glsl */ `#version 300 es
in vec2 a_pos;
void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }
`

const frag = /* glsl */ `#version 300 es
precision highp float;
out vec4 outColor;

uniform vec2 u_res;        // canvas px
uniform float u_shift;     // Scalpal site: scene offset down, as a fraction of canvas height
uniform float u_cell;      // cell px
uniform float u_time;      // seconds into the loop
uniform float u_wall;      // free-running seconds, drives the scramble flicker
uniform float u_aspect;
uniform sampler2D u_atlas; // RAMP glyphs, one row
uniform float u_glyphs;
uniform sampler2D u_left;
uniform sampler2D u_right;
uniform vec2 u_leftSize;   // design units
uniform vec2 u_rightSize;

// layer transforms (xy = center, z = rotation in radians), solved per frame
uniform vec3 u_leftX;
uniform vec3 u_rightX;
uniform vec2 u_spark;
uniform vec2 u_pointer;    // design space
uniform float u_scramble;  // 0..1, rises while the pointer moves, decays when it rests

vec4 layer(sampler2D tex, vec2 p, vec3 xf, vec2 size) {
  vec2 d = p - xf.xy;
  float c = cos(-xf.z), s = sin(-xf.z);
  d = vec2(c * d.x - s * d.y, s * d.x + c * d.y);
  vec2 uv = d / size + 0.5;
  if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return vec4(0.0);
  return texture(tex, uv);
}

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

void main() {
  // cell center in canvas px (y down)
  vec2 frag = vec2(gl_FragCoord.x, u_res.y - gl_FragCoord.y);
  vec2 cell = floor(frag / u_cell);
  vec2 local = (frag - cell * u_cell) / u_cell;
  vec2 center = (cell + 0.5) * u_cell;

  // cover-fit the design frame: x in [0, aspect], y in [0, 1]
  float unit = max(u_res.x / u_aspect, u_res.y);
  vec2 p = (center - u_res * vec2(0.5, 0.5 + u_shift)) / unit + vec2(u_aspect * 0.5, 0.5);

  float t = u_time;
  float k = smoothstep(0.0, 6.8, t);

  vec4 L = layer(u_left, p, u_leftX, u_leftSize);
  vec4 R = layer(u_right, p, u_rightX, u_rightSize);

  // Scalpal site: both arms white
  float lg = dot(L.rgb, vec3(0.299, 0.587, 0.114));
  vec3 lcol = vec3(lg);
  float rg = dot(R.rgb, vec3(0.299, 0.587, 0.114));
  vec3 rcol = vec3(rg);

  vec3 col = rcol * R.a;
  col = mix(col, lcol, L.a);
  float a = max(L.a, R.a);

  // shading: arms fall off toward their roots so the hands carry the light
  float dist = distance(p, u_spark);
  col *= mix(0.55, 1.25, smoothstep(1.1, 0.12, dist));

  // overall exposure climbs as the hands close in
  col *= mix(0.8, 1.15, k);

  // spark where the fingers meet: hot white-yellow core, orange halo, rays
  float glow = smoothstep(5.0, 6.6, t);
  vec2 d = p - u_spark;
  float ang = atan(d.y, d.x);
  float rays = 0.6 + 0.4 * pow(abs(sin(ang * 3.0 + 0.4)), 6.0);
  float flick = 0.85 + 0.15 * sin(t * 23.0 + hash(cell) * 6.28);
  float core = exp(-dist * dist / 0.0012);
  float halo = exp(-dist / 0.075) * rays;
  float spark = glow * flick * (core * 1.4 + halo * 1.4);
  col += mix(vec3(0.95, 0.3, 0.08), vec3(1.0, 0.86, 0.22), clamp(core * 1.6, 0.0, 1.0)) * spark;
  a = max(a, clamp(spark, 0.0, 1.0));

  float lum = clamp(dot(col, vec3(0.299, 0.587, 0.114)), 0.0, 1.0) * a;
  lum = pow(lum, 1.9);
  float idx = floor(clamp(lum, 0.0, 0.999) * u_glyphs);

  // decode scramble around the pointer: glyphs flip to random characters
  // while it moves, then resolve back to the image as it settles
  float near = u_scramble * smoothstep(0.16, 0.03, distance(p, u_pointer));
  float tick = floor(u_wall * 18.0 + hash(cell) * 7.0);
  float roll = hash(cell + tick * 0.137);
  if (idx >= 1.0 && roll < near) {
    idx = 1.0 + floor(hash(cell * 1.31 + tick) * (u_glyphs - 1.0));
  } else if (idx < 1.0 && roll < near * 0.35) {
    // a little noise in the dark around it
    idx = 1.0 + floor(hash(cell + tick * 0.71) * 3.0);
    col = vec3(0.32, 0.3, 0.3);
    lum = 0.18;
  }
  if (idx < 1.0) { outColor = vec4(0.0, 0.0, 0.0, 1.0); return; }

  vec2 auv = vec2((idx + local.x) / u_glyphs, local.y);
  float ink = texture(u_atlas, auv).r;

  // glyph color: the scene color lifted so dim glyphs stay legible
  vec3 tint = col / max(max(col.r, max(col.g, col.b)), 1e-3);
  vec3 c = tint * mix(0.35, 1.0, lum);
  outColor = vec4(c * ink, 1.0);
}
`

function buildAtlas(cellPx: number) {
  const n = RAMP.length
  const size = Math.max(8, Math.round(cellPx))
  const canvas = document.createElement('canvas')
  canvas.width = size * n
  canvas.height = size
  const ctx = canvas.getContext('2d')!
  ctx.fillStyle = '#000'
  ctx.fillRect(0, 0, canvas.width, canvas.height)
  ctx.textAlign = 'center'
  ctx.textBaseline = 'middle'
  const font = `${Math.round(size * 0.92)}px ui-monospace, SFMono-Regular, Menlo, monospace`
  RAMP.forEach((g, i) => {
    const cx = i * size + size / 2
    const cy = size / 2 + size * 0.04
    if (g === 'TILE') {
      const inset = size * 0.14
      ctx.fillStyle = '#fff'
      ctx.fillRect(i * size + inset, inset * 0.6, size - inset * 2, size - inset * 1.2)
      ctx.fillStyle = '#000'
      ctx.font = `bold ${font}`
      ctx.fillText('?', cx, cy)
      return
    }
    ctx.fillStyle = '#fff'
    ctx.font = font
    ctx.fillText(g, cx, cy)
  })
  return canvas
}

function loadImage(src: string) {
  return new Promise<HTMLImageElement>((resolve, reject) => {
    const img = new Image()
    img.onload = () => resolve(img)
    img.onerror = reject
    img.src = src
  })
}

function compile(gl: WebGL2RenderingContext, type: number, src: string) {
  const sh = gl.createShader(type)!
  gl.shaderSource(sh, src)
  gl.compileShader(sh)
  if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(sh) ?? 'shader')
  return sh
}

function texture(gl: WebGL2RenderingContext, src: TexImageSource, unit: number, linear: boolean) {
  const tex = gl.createTexture()
  gl.activeTexture(gl.TEXTURE0 + unit)
  gl.bindTexture(gl.TEXTURE_2D, tex)
  gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false)
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, src)
  const f = linear ? gl.LINEAR : gl.NEAREST
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, f)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, f)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE)
  return tex
}

// scene layout in design units (x 0..ASPECT, y 0..1 top-down)
const SCENE = {
  leftWidth: 1.15,
  rightWidth: 1.4,
  leftA: [0.052, 0.634, -0.18],
  leftB: [0.212, 0.544, -0.18],
  rightA: [1.505, 0.172, -0.2],
  rightB: [1.375, 0.207, -0.2],
  spark: [0.775, 0.41],
  // fingertip of each cutout, in its own uv space
  leftTip: [0.978, 0.45],
  rightTip: [0.07, 0.62],
}

// how the arms answer the pointer
const REACH = {
  lift: 0.03, // max vertical drift toward the pointer, design units
  near: 0.12, // full pull inside this distance from a fingertip
  far: 0.6, // no pull beyond this
  gap: 0.22, // pointer this close to the meeting point speeds the approach
  rush: 2, // extra timeline speed with the pointer right in the gap
  stiffness: 2.2, // spring rate, 1/s
}

type Vec = [number, number]
type Xf = [number, number, number]

const lerp = (a: number, b: number, k: number) => a + (b - a) * k
const smooth = (e0: number, e1: number, x: number) => {
  const k = Math.min(1, Math.max(0, (x - e0) / (e1 - e0)))
  return k * k * (3 - 2 * k)
}
const rotate = ([x, y]: Vec, a: number): Vec => [x * Math.cos(a) - y * Math.sin(a), x * Math.sin(a) + y * Math.cos(a)]
const dist = (a: Vec, b: Vec) => Math.hypot(a[0] - b[0], a[1] - b[1])

// a point of a layer (given in its uv space) in design space
function pointOf(xf: Xf, size: Vec, uv: number[]): Vec {
  const [ox, oy] = rotate([(uv[0] - 0.5) * size[0], (uv[1] - 0.5) * size[1]], xf[2])
  return [xf[0] + ox, xf[1] + oy]
}

type Arm = { size: Vec; tip: number[]; lift: number }

// drift an arm up or down toward the pointer's height (never sideways)
function aim(arm: Arm, base: Xf, pointer: Vec | null, dt: number): Xf {
  const tip = pointOf(base, arm.size, arm.tip)
  let lift = 0
  if (pointer) {
    const pull = smooth(REACH.far, REACH.near, dist(pointer, tip))
    lift = Math.max(-REACH.lift, Math.min(REACH.lift, pointer[1] - tip[1])) * pull
  }
  arm.lift = lerp(arm.lift, lift, 1 - Math.exp(-dt * REACH.stiffness))
  return [base[0], base[1] + arm.lift, base[2]]
}

// Scalpal site additions: onTouch fires once when the fingertips first meet (spark fully lit),
// introSpeed speeds up only that first approach so it can work as a loader, and holdAfterTouch
// keeps the hands together once they meet instead of looping.
const TOUCH = 6.6
const CLOSED = 6.8 // arms fully at their touching pose
// once held, the arms breathe: a slow bob (out of phase) and a slight ease apart and back
const BREATH = { period: 3.4, bob: 0.006, part: 0.007, easeIn: 1.2 }

type Props = { playing?: boolean; time?: number; onTouch?: () => void; introSpeed?: number; holdAfterTouch?: boolean; shiftY?: number }

export function AsciiAdam({ playing = true, time, onTouch, introSpeed = 1, holdAfterTouch = false, shiftY = 0 }: Props) {
  const ref = useRef<HTMLCanvasElement>(null)
  const playingRef = useRef(playing)
  const onTouchRef = useRef(onTouch)
  const introSpeedRef = useRef(introSpeed)
  const holdRef = useRef(holdAfterTouch)
  const shiftRef = useRef(shiftY)
  shiftRef.current = shiftY
  useEffect(() => {
    onTouchRef.current = onTouch
    introSpeedRef.current = introSpeed
    holdRef.current = holdAfterTouch
  }, [onTouch, introSpeed, holdAfterTouch])
  useEffect(() => {
    playingRef.current = playing
  }, [playing])

  useEffect(() => {
    const canvas = ref.current
    if (!canvas) return
    const gl = canvas.getContext('webgl2', { antialias: false, premultipliedAlpha: false })
    if (!gl) return

    let raf = 0
    let disposed = false
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches

    Promise.all([loadImage('/hero/ascii/arm-left.webp'), loadImage('/hero/ascii/arm-right.webp')]).then(([left, right]) => {
      if (disposed) return
      const prog = gl.createProgram()!
      gl.attachShader(prog, compile(gl, gl.VERTEX_SHADER, vert))
      gl.attachShader(prog, compile(gl, gl.FRAGMENT_SHADER, frag))
      gl.linkProgram(prog)
      gl.useProgram(prog)

      const buf = gl.createBuffer()
      gl.bindBuffer(gl.ARRAY_BUFFER, buf)
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW)
      const loc = gl.getAttribLocation(prog, 'a_pos')
      gl.enableVertexAttribArray(loc)
      gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0)

      const u = (n: string) => gl.getUniformLocation(prog, n)
      texture(gl, left, 1, true)
      texture(gl, right, 2, true)
      gl.uniform1i(u('u_left'), 1)
      gl.uniform1i(u('u_right'), 2)
      gl.uniform1i(u('u_atlas'), 0)
      gl.uniform1f(u('u_glyphs'), RAMP.length)
      gl.uniform1f(u('u_aspect'), ASPECT)
      const leftArm: Arm = {
        size: [SCENE.leftWidth, (SCENE.leftWidth * left.height) / left.width],
        tip: SCENE.leftTip,
        lift: 0,
      }
      const rightArm: Arm = {
        size: [SCENE.rightWidth, (SCENE.rightWidth * right.height) / right.width],
        tip: SCENE.rightTip,
        lift: 0,
      }
      gl.uniform2fv(u('u_leftSize'), leftArm.size)
      gl.uniform2fv(u('u_rightSize'), rightArm.size)
      const uLeft = u('u_leftX')
      const uRight = u('u_rightX')
      const uSpark = u('u_spark')
      const uTime = u('u_time')
      const uPointer = u('u_pointer')
      const uScramble = u('u_scramble')
      const uWall = u('u_wall')
      let scramble = 0
      let lastPointer: Vec | null = null

      // the spark sits between the fingertips; this keeps it on its tuned spot at rest
      const restMid = (() => {
        const a = pointOf(SCENE.leftB as Xf, leftArm.size, SCENE.leftTip)
        const b = pointOf(SCENE.rightB as Xf, rightArm.size, SCENE.rightTip)
        return [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2]
      })()
      const sparkNudge: Vec = [SCENE.spark[0] - restMid[0], SCENE.spark[1] - restMid[1]]

      // pointer in design space, or null when it is off the art
      let pointer: Vec | null = null
      const interactive = time === undefined && !reduced
      const onMove = (e: PointerEvent) => {
        const r = canvas.getBoundingClientRect()
        const x = e.clientX - r.left
        const y = e.clientY - r.top
        if (x < 0 || y < 0 || x > r.width || y > r.height) {
          pointer = null
          return
        }
        const unit = Math.max(r.width / ASPECT, r.height)
        pointer = [(x - r.width / 2) / unit + ASPECT / 2, (y - r.height * (0.5 + shiftRef.current)) / unit + 0.5]
      }
      const onLeave = () => {
        pointer = null
      }
      if (interactive) {
        window.addEventListener('pointermove', onMove, { passive: true })
        document.documentElement.addEventListener('pointerleave', onLeave)
        window.addEventListener('blur', onLeave)
      }

      let atlasCell = 0
      const resize = () => {
        const dpr = Math.min(window.devicePixelRatio || 1, 2)
        const w = Math.max(1, Math.round(canvas.clientWidth * dpr))
        const h = Math.max(1, Math.round(canvas.clientHeight * dpr))
        if (canvas.width !== w || canvas.height !== h) {
          canvas.width = w
          canvas.height = h
        }
        gl.viewport(0, 0, w, h)
        // cells scale with the covered frame so the grid density never changes
        const cell = Math.max(w / ASPECT, h) * CELL
        gl.uniform2f(u('u_res'), w, h)
        gl.uniform1f(u('u_shift'), shiftRef.current)
        gl.uniform1f(u('u_cell'), cell)
        if (Math.round(cell) !== atlasCell) {
          atlasCell = Math.round(cell)
          texture(gl, buildAtlas(cell), 0, true)
        }
      }
      resize()
      const ro = new ResizeObserver(resize)
      ro.observe(canvas)

      let clock = 0
      let touched = false
      let held = 0 // seconds since the hands settled together
      let last = performance.now()
      let drawn = false
      const frame = (now: number) => {
        const dt = Math.min(0.1, (now - last) / 1000)
        last = now
        const live = interactive && playingRef.current
        // off screen: hold the last frame and skip the GPU work
        if (interactive && !live && drawn) {
          raf = requestAnimationFrame(frame)
          return
        }
        drawn = true

        // ping-pong: touch, hold, then ease back apart instead of restarting
        const t0 = time ?? (reduced ? LOOP - 0.5 : clock < LOOP ? clock : 2 * LOOP - clock)
        if (!touched && t0 >= TOUCH) {
          touched = true
          onTouchRef.current?.()
        }
        const k = smooth(0, 6.8, t0)
        const baseL = SCENE.leftA.map((v, i) => lerp(v, SCENE.leftB[i], k)) as Xf
        const baseR = SCENE.rightA.map((v, i) => lerp(v, SCENE.rightB[i], k)) as Xf

        // pointer in the gap between the fingers hurries them together
        // Scalpal site: the pointer only scrambles glyphs; it doesn't move or hurry the arms
        const rush = 0
        // pointer in the gap: hurry them together, and hold them there on the way back
        const speed = clock < LOOP ? 1 + REACH.rush * rush : 1 - rush
        const intro = touched ? 1 : introSpeedRef.current
        if (live) {
          clock = (clock + dt * speed * intro) % (2 * LOOP)
          // finish closing, then stay touching
          if (touched && holdRef.current) clock = Math.min(clock, CLOSED)
        }

        const lx = aim(leftArm, baseL, null, dt)
        const rx = aim(rightArm, baseR, null, dt)
        if (touched && holdRef.current && clock >= CLOSED) {
          if (live) held += dt
          const w = Math.min(1, held / BREATH.easeIn) // fade the breathing in
          const ph = (held / BREATH.period) * Math.PI * 2
          const apart = BREATH.part * (0.5 - 0.5 * Math.cos(ph)) * w
          lx[0] -= apart
          rx[0] += apart
          lx[1] += BREATH.bob * Math.sin(ph) * w
          rx[1] += BREATH.bob * Math.sin(ph + 0.9) * w
        }
        const a = pointOf(lx, leftArm.size, SCENE.leftTip)
        const b = pointOf(rx, rightArm.size, SCENE.rightTip)

        gl.uniform3fv(uLeft, lx)
        gl.uniform3fv(uRight, rx)
        gl.uniform2f(uSpark, (a[0] + b[0]) / 2 + sparkNudge[0], (a[1] + b[1]) / 2 + sparkNudge[1])
        // scramble follows pointer speed: quick to rise, slow to settle
        let target = 0
        if (live && pointer && lastPointer && dt > 0) target = Math.min(1, dist(pointer, lastPointer) / dt / 0.5)
        lastPointer = live && pointer ? [pointer[0], pointer[1]] : null
        scramble = lerp(scramble, target, 1 - Math.exp(-dt * (target > scramble ? 14 : 2.2)))
        gl.uniform2fv(uPointer, pointer ?? [-10, -10])
        gl.uniform1f(uScramble, interactive ? scramble : 0)

        gl.uniform1f(uTime, t0)
        gl.uniform1f(uWall, now / 1000)
        gl.drawArrays(gl.TRIANGLES, 0, 3)
        if (interactive) raf = requestAnimationFrame(frame)
      }
      raf = requestAnimationFrame(frame)

      cleanup = () => {
        ro.disconnect()
        window.removeEventListener('pointermove', onMove)
        document.documentElement.removeEventListener('pointerleave', onLeave)
        window.removeEventListener('blur', onLeave)
      }
    })

    let cleanup = () => {}
    return () => {
      disposed = true
      cancelAnimationFrame(raf)
      cleanup()
    }
  }, [time])

  return <canvas ref={ref} className="ascii-adam" aria-hidden="true" />
}
