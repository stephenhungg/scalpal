# Doctor Office and Patient Provenance

The botanical office geometry/materials are original Scalpal art. The earlier primitive patient prototypes were replaced after user review; neither runtime model is a primitive caricature.

## MakeHuman Graphical Assets — CC0 1.0 Universal

Both seated fictional adult avatars derive from MakeHuman/MPFB graphical assets. The MakeHuman Team releases the base mesh, targets, rigs, clothes and textures under CC0. The MPFB software code has a separate GPLv3 license; it was used as an authoring-time dependency and is not copied into the native runtime.

Primary license statements:

- [MPFB license and graphical-output distinction](https://github.com/makehumancommunity/mpfb2/blob/afb9f530a7c2741dedb8df0ebae2e0b183caec21/LICENSE.md)
- [Full CC0 graphical-asset statement](https://github.com/makehumancommunity/mpfb2/blob/afb9f530a7c2741dedb8df0ebae2e0b183caec21/LICENSE.ASSETS.md)
- [Official core asset-pack inventory, with individual CC0 rows](https://static.makehumancommunity.org/assets/assetpacks/makehuman_system_assets.html)

Preserved license text: `third-party/MakeHuman/MPFB-LICENSE.md` and `LICENSE.ASSETS.md`. Individual MHCLO/MHMAT files retain the upstream copyright/release headers naming Data Collection AB, Joel Palmius and Jonas Hauquier. Credit: MakeHuman Team / MakeHuman Community. No attribution is required by CC0, but these notices preserve the source audit.

## Actual Source Snapshots and Assets

MPFB version 2.0.17, source commit `afb9f530a7c2741dedb8df0ebae2e0b183caec21`. Its `base.obj`, `rig.default.json` and `weights.default.json` are preserved here. Donor target data remain in the pinned upstream authoring dependency; the editable posed output and genuine rig are stored in both `doctor-office.blend` and `patients.blend`.

Official archive: `https://files.makehumancommunity.org/asset_packs/makehuman_system_assets/makehuman_system_assets_cc0.zip`. HTTP source metadata observed during download: 280,737,770 bytes, last modified April 14, 2024, 13:28:46 UTC, ETag `10bbb7ea-6160e7c0d7996`. Only the selected graphical subset is preserved, rather than the entire archive. `third-party/MakeHuman/source-hashes.json` records SHA-256 hashes of the actual donor files.

| Patient | Skin texture | Outfit | Hair | Shared assets |
| --- | --- | --- | --- | --- |
| Priya Ramaswamy, authored age 40 | `middleage_asian_female` | `female_casualsuit01` | `bob02` | `shoes01`, `low-poly` eyes with `brown` material, `eyebrow001`, MakeHuman default rig |
| Jonah Okoye, authored age 30 | `young_african_male` | `male_casualsuit01` | `short04` | Same shared assets |

The stock asset names describe MakeHuman's library, not additional facts from the patient records. The avatars' appearance and phenotype parameters are artistic choices. No real patient record, user portrait, participant recording or captured likeness was used. These are fictional case presentations, not identity matches.

## Modifications and Runtime Limits

The humans were parameterized as adults and posed with their actual weighted rigs. The seated pose was baked as the rest pose; the rig and genuine weighted `HeadPivot`/`JawPivot` bones remain. Clothing masks remove covered body/helper geometry. Shoes are fitted above the office floor. `NoseTip` is attached to the real head bone and supports measured native facing correction.

Diffuse maps were bounded to 1024 pixels, packed into the editable Blender sources and copied into Unity's `Art/Textures` folder. Hair is tinted dark brown; hair/brow alpha is cut out rather than rendered with blended transparency. Outfit alpha islands receive neighboring fabric colors for opaque clothing. The originals remain in the donor subset.

Priya is 27,180 triangles and six render meshes; Jonah is 36,906 triangles and six render meshes. Only one patient is active. This is visual art and bounded rig animation, not validated clinical anatomy, calibrated viseme lip sync, realistic facial performance, or measured Quest frame-time evidence. Findings and scoring remain authoritative in the encounter engine.
