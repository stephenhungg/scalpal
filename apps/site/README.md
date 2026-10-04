# Scalpal Site

Owner: Silas. Two pages: `/` (one screen, no scroll, Explore button) and `/explore` (demo video plus Quest download).

```sh
cd apps/site
npm install
npm run dev        # http://localhost:3000
npm run build
```

## Demo video and APK

Each resolves from an env URL first, then from a file in `public/`. If neither exists, the page shows "Demo video coming soon" or "Download soon".

| Asset | Env var (production) | Local file |
| --- | --- | --- |
| Demo video | `NEXT_PUBLIC_DEMO_VIDEO_URL` | `public/demo.mp4` |
| Quest APK | `NEXT_PUBLIC_APK_URL` | `public/scalpal.apk` |

Both files are gitignored. Host the APK somewhere public (a release asset on a public repo, R2, etc.) and set the env var on the deploy. Pages are static, so the file check runs at build time: rebuild after adding a file.

## Design notes

The visual system follows the aeterna Framer template, measured from the live site at 1440×900 and 390×844:
- Instrument Serif headline at 110/100 with -3px tracking (52/50 on phones)
- Geist Mono everywhere else
- `rgb(245,245,245)` background, full-bleed (no column rules)
- word-by-word blur-in entrance: 0.5s per word, 50ms stagger
- the button label roll uses a spring (stiffness 230, damping 24) fitted to the measured hover trace (within 1px at every sample)
- the button row fades in 0.4s, starting ~460ms after the first word (measured 463ms on the reference)

The hands in `src/components/HandsField.tsx` are a WebGL2 halftone of Michelangelo's *Creation of Adam*, a public-domain fresco. The arm cutouts come from Matthew's [adam](https://github.com/MatthewKim323/adam) hero (`public/hero/ascii/arm-*.webp`), grayscaled into `public/hands/`. Placement was fitted to the reference silhouettes. Dots are 2px on a 3px grid (1.5px cells on phones). Adam's forearm dissolves from the wrist, and the cursor lens pushes dots out into a 52px ring.

The landing background is ReactBits [Pattern Waves](https://reactbits.dev/backgrounds/pattern-waves) (`src/components/PatternWaves.tsx`, via `ogl`), set to faint black dots on a transparent background. A backdrop-blur layer, masked by a soft copy of the hand silhouettes, blurs the waves wherever they pass behind the arms.

No template assets or code are used. Fonts are Google Fonts (OFL).
