# Reusable Surgery Environment Research

Researched October 3, 2026. Distinguish room art, patient/anatomy meshes, simulation mechanics and native headset integration. A downloadable room supplies none of the latter automatically.

## Candidate Comparison

| Source | License evidence | What it supplies | Decision |
| --- | --- | --- | --- |
| [3DAssets.dev Operating Theatre](https://3dassets.dev/assets/hospital-wards-and-clinic-operations-operating-theatre-83f50a06) | Publisher declares CC0; [public versioned GLB](https://cdn.3dassets.dev/assets/23665/v1/model.glb) | Static stylized room/table/equipment, no patient or organs; publisher identifies AI-generated art | Acquired and converted for a preview. Direct page access returned 403; publisher search content and successful CDN download recorded. |
| [UMRAM-Bilkent supine human](https://github.com/UMRAM-Bilkent/supine-human-model) | [CC0 repository license](https://github.com/UMRAM-Bilkent/supine-human-model/blob/728f23ab5eb9d6cb2c8fb39acb3440bd81db0d3e/LICENSE); Quaternius-derived character | Static approximate human surface, no internal anatomy | Acquired at pinned revision; scaled to 1.75 m and prepared supine. Raw GLB is not metric/supine by itself. |
| [Chenchanchong hospital operating room](https://sketchfab.com/3d-models/hospital-operating-room-a27400a73f4a43fbb1dda30031cb91c2) | Creator/Sketchfab metadata lists CC BY 4.0 | Room art, roughly 823,652 triangles; patient inclusion not established | Richer alternative requiring large geometry/material reduction, attribution and download workflow; not imported. |
| [UniCAVE examples](https://github.com/widVE/UniCAVE_Examples) | Repository software MIT; separate asset provenance not established | Older Unity/CAVE operating-room projects with patient/equipment art | Reference only. Do not redistribute all artwork solely because the code has MIT. Not imported. |
| [Kitware iMSTK](https://github.com/Kitware/iMSTK) | Apache 2.0 for toolkit; [Unity integration announcement](https://www.kitware.com/imstk-is-now-available-on-the-unity-asset-store/) | Surgical simulation toolkit and examples, rather than a drop-in Quest room | Useful mechanics reference. Upstream says support/development ended May 2, 2025; no native Android/Quest integration is verified here. Not imported. |

[VR-Surgery](https://github.com/IsaacYu15/VR-Surgery) demonstrates grabbing/cutting/suturing but no explicit redistribution license was established in this research. It is not a licensed asset source for our repository.

## Recommended Demo Path

Use the lightweight room/patient art with Scalpal's own selected anatomy, tools and authored exercise. Mixed reality derives the anatomy transform from a validated real-person fit; full VR uses an authored virtual-patient fit. Reuse the same voice/action/assessment core in both. A separate rendering mode does not justify retraining a model for virtual objects whose scene IDs and transforms are already known.

Matthew's anatomy branch already contains pinned Z-Anatomy and HRA preparation. Its [upstream attribution list](https://github.com/Z-Anatomy/Models-of-human-anatomy#attributions) explicitly includes noncommercial kidney and inner-ear sources within a broader CC BY-SA atlas. Retain component-specific terms and select meshes deliberately; do not describe the whole atlas as CC0 or uniformly permissive. This research does not merge or redistribute that atlas.

## Imported Evidence

Original room: 421,936-byte GLB, eight materials, 173 rendered instances. Inspected source index counts give 12,184 triangles across instances; Blender/FBX conversion yields 12,168 triangles across eight merged meshes. Original patient: 162,136-byte GLB, 1,578 triangles, one mesh/material. The facing axis in the baked mesh required checking the upstream build script; visual inspection confirmed the prepared figure is lying on its back.

The actual Unity 6000.0.66f2 editor built the preview prefabs/scene and validated geometry counts, reclining metric placement and absence of missing scripts/materials/meshes. A Blender render was inspected. These checks establish prepared scene art, not actual Quest FPS, stereo behavior, registration or surgical correctness. Sources, hashes, original notices and modified exports are committed under [environment assets](../../assets/environments/README.md).

Latest integration checkpoint fetched Matthew's `a629fdf` coach/contact changes, the `5e01dfb` anatomy source/LFS workspace commit and Nathan's new `9bd6517` companion/realtime branch. All fifteen tool IDs now match the coach catalog. These teammate branches are not merged by the environment import. Their setup assumptions are not proof that main has a native XR rig.
