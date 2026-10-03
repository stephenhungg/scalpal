# Anatomy atlas assets

Full-body Z-Anatomy layers and separate high-detail Human Reference Atlas organs for Scalpal's Unity experience. These are generic anatomical models, not participant-specific anatomy or tissue simulation.

## Reproduce sources

Run `python3 scripts/anatomy/fetch.py` from the repository. The downloader validates the pinned source files against `sources.json` and caches originals in ignored `assets/anatomy/cache/`. Original full-resolution geometry stays intact. Do not commit the cache.

Sources:

- Z-Anatomy Unity FBX exports: https://github.com/LluisV/Z-Anatomy, revision `6c7f9016bd5899ac8edafd31b9900c151df42ed6`. Credit Lluis Vinent, Z-Anatomy, and BodyParts3D / DBCLS. Source project declares CC BY-SA 4.0; upstream model credits include component-specific terms. Preserved notices are in `attribution/`.
- Human Reference Atlas male heart, liver, and blood vasculature, release 2.5 high-resolution GLBs: https://github.com/cns-iu/hra-organ-gallery-in-vr, revision `92dc604271f1a3e92cc2ce598405ada2daeb9460`. Credit Human Reference Atlas / HuBMAP, the contributing model authors, and the NLM Visible Human Project. Source catalog: https://humanatlas.io/3d-reference-library. Keep embedded glTF asset attribution in the generated inventory.

Different atlas sources are not spatially interchangeable. Full-body layers share the Z-Anatomy source frame. HRA detail models are separate inspection assets and must not be automatically attached to the participant torso.

## Status

Asset preparation and local geometry inspection are not physical Quest validation. Unity scene composition, registration fitting, actual stereo rendering, and headset frame time must be verified by the Quest integrator. Rendering anatomy does not implement cutting, deformation, bleeding, or physiological flow.
