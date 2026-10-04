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

The reaching arms are Matthew's ASCII Adam (`src/components/AsciiAdam.tsx` plus `public/hero/ascii/`, from [MatthewKim323/adam](https://github.com/MatthewKim323/adam)), unchanged. It was built for a black page, so `AdamLayer` inverts it with a hue turn and multiplies it onto the light background. It's placed below the button.

No template assets or code are used. Fonts are Google Fonts (OFL).
