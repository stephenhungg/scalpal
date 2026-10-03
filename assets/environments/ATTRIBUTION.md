# Environment Asset Provenance

## Operating Theatre

Publisher: 3D Assets / 3DAssets.dev. [Publisher page](https://3dassets.dev/assets/hospital-wards-and-clinic-operations-operating-theatre-83f50a06); [versioned GLB](https://cdn.3dassets.dev/assets/23665/v1/model.glb). The publisher declares CC0 1.0 Universal and identifies the art as AI-generated. The CC0 legal text is retained in `operating-theatre/LICENSE`; this is a copy of the standard dedication, while the publisher page supplies the asset-specific declaration. The page was available through search results; direct page retrieval returned HTTP 403. The public CDN asset downloaded successfully.

The original GLB is unmodified and hashed in `sources.json`. Prepared runtime geometry bakes transforms and consolidates static pieces by material. The room supplies scenery, not an anatomically or clinically validated simulation.

## Supine Patient

Credit Quaternius for the original CC0 character, and UMRAM / Bilkent University for the prepared mesh. [Repository](https://github.com/UMRAM-Bilkent/supine-human-model), pinned revision `728f23ab5eb9d6cb2c8fb39acb3440bd81db0d3e`. The repository distributes the mesh, scripts and documentation under CC0 1.0; original license and README are retained in `supine-patient/`.

The original GLB is unmodified and hashed. Our preparation bakes the source transforms, scales body length to 1.75 m, lays it on its back, aligns the long axis to the table and changes the palette to a matte training mannequin. We inspected the source build script to resolve its baked facing axis. This static, stylized mesh contains no internal organs, rig or tissue mechanics.

The generated Blender library, Unity FBX conversions, prefabs and preview retain these source notices. No real participant imagery or anatomy derived from an individual is included.
