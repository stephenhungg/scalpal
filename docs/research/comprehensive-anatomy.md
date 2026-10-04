# Comprehensive Anatomy for Scalpal

Researched October 3, 2026. This document separates the inspected repository from recommendations. The proposed anatomy graph, expanded interactions, regional loading and progression system are **not built** by this research. No clinical procedure instructions are supplied here.

## The Direction

Build a reusable anatomy platform that supports progressively broader authored exercises. “Every organ and vessel” is a useful long-term ambition, but cannot serve as a single acceptance criterion: named anatomy, visible geometry, correct anatomical relationships, simulated behavior, learner assessment and participant registration require separate evidence. A structure can be searchable before it supports a scored interaction. An unsupported operation should remain unavailable rather than inheriting a misleading generic behavior.

The immediate milestone remains the physical Quest appendectomy rehearsal. Expand anatomy after its selection, practice, feedback and recap work on the headset. Preserve one authored case authority and Matthew's single Scalpal agent; an expanded atlas does not require another progression engine or coach.

## What Is Actually Present

The inspected [atlas manifest](../../apps/quest/Assets/Scalpal/Anatomy/Resources/anatomy-atlas.json) contains **4,031 parts** across 12 asset systems: 3,874 full-body Z-Anatomy parts, 11 supplemental exercise targets and 146 parts in three independent HRA detail models. The full-body systems include 982 skeletal, 683 muscular and 676 cardiovascular parts. These are mesh subdivisions and source identities, not a count of distinct organs or a demonstration of comprehensive anatomy. The manifest separately lists 1,400 excluded source guides.

The [asset documentation](../../assets/anatomy/README.md) now places the complete editable source/workspace and all twelve runtime FBXs on main after the team consolidation. The native scene still loads only its procedure subset. A catalog entry does not establish that its mesh is present in the APK, loaded, visible or interactive.

| Layer | Inspected evidence | Practical limit |
| --- | --- | --- |
| Prepared catalog | 4,031 independently identified parts with source/display geometry metadata | No completeness guarantee across sex, age, anatomical variation, pathology or fine vascular detail |
| Catalog mapping | 18 original targets plus 11 supplemental targets; 29 mapped canonical IDs | Most source meshes do not have an exercise-catalog mapping |
| Supplemental targets | 11 authored schematic targets with provenance and validation status | All marked unreviewed generic teaching approximations; five gallbladder tubes deliberately enlarged to 12 mm diameter |
| Coverage checks | No missing interactive/mistake target IDs for the three authored procedures | Identifier/collider coverage does not establish geometric, behavioral or clinical validity |
| Native Quest slice | Nine selected anatomy meshes, instantiated for selection and practice | Not the entire atlas; not all three procedures as physical-headset experiences |
| Current interactions | Identity, highlight, isolation, visibility, static colliders and authored event checks | No general tissue mechanics, vascular network physiology or damage model |

The nine native IDs are `appendicular_artery`, `appendix`, `cecum`, `greater_omentum`, `mesoappendix`, `right_ureter`, `small_bowel`, `terminal_ileum` and `urinary_bladder`. [NativeSessionBuild](../../apps/quest/Assets/Scalpal/Quest/Editor/NativeSessionBuild.cs) uses the procedure-specific builder; [AnatomyAtlasBuilder](../../apps/quest/Assets/Scalpal/Anatomy/Editor/AnatomyAtlasBuilder.cs) validates required interactive/mistake IDs and includes available context structures.

The manifest records identity, labels, systems, geometry and selected provenance/centerlines. It has **no anatomical relationship graph or reusable behavior graph**. Its `sourceLandmarks` support schematic geometry provenance, not validated anatomical relationships. The [TypeScript anatomy catalog](../../services/preop/src/catalog/anatomy.ts) supplies a much smaller canonical list with systems and regions. [AnatomyPart](../../apps/quest/Assets/Scalpal/Anatomy/Runtime/AnatomyPart.cs) manages presentation; it does not encode physiology. Four canonical IDs remain unmapped: `abdominal_wall`, `umbilicus`, `heart` and `lungs`. Source heart/lung subdivisions can exist without a canonical combined node.

See [anatomy integration](../anatomy-integration.md) for the supplemental targets and [native session](../native-session.md) for current runtime verification. Older anatomy-branch documentation contains scaffold statements superseded by the newer native-session map; retain its asset facts without treating those old integration statements as current.

## Primary Sources and Their Roles

| Source | Useful contribution | Does not establish |
| --- | --- | --- |
| [Z-Anatomy upstream](https://github.com/Z-Anatomy/Models-of-human-anatomy) | Editable atlas geometry, labels and source attribution; includes BodyParts3D-derived models | Uniform component licensing, reviewed procedure content or simulated tissue behavior |
| [BodyParts3D description](https://dbarchive.biosciencedbc.jp/en/bodyparts3d/desc.html) | Anatomical concepts represented as segments of an adult male whole-body model; references FMA | A participant-specific body or every possible anatomical variant |
| [University of Washington FMA](https://si.washington.edu/projects/fma/) and [developers' reference-ontology paper](https://www.sciencedirect.com/science/article/pii/S1532046403001278) | Machine-readable anatomical concepts and structural relationships | A physics solver, operational procedure rubric or an individual person's geometry |
| [IFAA/FIPAT terminology status](https://ifaa.net/committees/anatomical-terminology-fipat/fipat-ifaa-terminologies/) and [TA2Viewer](https://ta2viewer.openanatomy.org/) | Standard Latin/English anatomical terminology and a browsing/cross-reference route | A complete executable connectivity graph or mesh correspondence |
| [SOFA features](https://www.sofa-framework.org/about/features/) | Separate mechanical, collision and rendering representations; elastic mechanics and interaction infrastructure | A ready-made validated procedure, tissue parameters or a demonstrated native Quest integration |

Use TA2 for terminology and reviewed FMA cross-references for semantics. Do not infer equivalence from matching labels alone. The FMA maintainers' current page says there have been no new releases since 2019, although the project remains listed active; pin the actual imported release rather than depending on a moving URL. This research did not import an ontology or verify every relationship it contains. Some direct FMA/FIPAT page requests failed; official indexed text, the primary ontology paper and the officially partnered viewer supported the limited claims above. No TA2 content-redistribution license was inferred from the viewer's accessibility.

Licensing requires component provenance. Z-Anatomy declares CC BY-SA 4.0 but credits noncommercial kidney and inner-ear components and older BodyParts3D terms. The [BodyParts3D archive's license page](https://dbarchive.biosciencedbc.jp/en/bodyparts3d/lic.html), updated February 27, 2025, now states CC BY 4.0. That does not automatically relicense the pinned adapted Z-Anatomy assets or third-party additions. Keep exact asset version, source notice and modifications with every export; review rights before redistribution. This is source evidence, not a legal determination.

## Proposed Reusable Layers

### Identity and Representation

Preserve existing stable IDs and authored case references. Add reviewed external mappings rather than renaming every mesh. Distinguish an anatomical concept from a concrete model instance: several meshes may represent one organ, while one grouped mesh may represent several structures. Keep left/right identity and source-specific variants explicit.

A future anatomy record should include the existing ID and aliases, optional verified FMA/TA2 identifiers, system/region, laterality, source version, component license, geometry quality and review status. A representation record should reference the mesh, coordinate frame, scale, collision model, available detail levels and interaction support. Source-prefixed IDs remain valid when no canonical mapping is reviewed. Ambiguous mappings should not become scoring targets.

### Verified Relationships

Start with the small regional subset required by the next case. Proposed typed edges include `part_of`, `contained_in`, `adjacent_to`, `connected_to`, and appropriately reviewed vessel/nerve relations. These are application-schema proposals; they are not a claim that every predicate or edge can be copied directly from FMA. Keep structural relations separate from functional or simulated-flow relations. A mesh tree, shared system label, nearest neighbor or touching collider is not sufficient evidence for a connection.

Each edge needs source/release, subject and object mappings, reviewer, status, and variant scope. Preserve missing/unknown relationships rather than filling them with generated text. Require sensible laterality and node existence; check acyclicity for the chosen strict containment hierarchy, not for every relation. A learner-facing explanation can cite verified graph facts, while Scalpal continues to request bounded actions through the existing tool interface.

### Interaction Profiles

Reuse explicit capability profiles, then attach reviewed per-structure parameters. The first profile supports selection, identification, highlighting and isolation. Later profiles can represent controlled grasp/displacement, lumen or vessel continuity, symbolic damage state and feedback, followed by narrowly validated deformation/topology changes. These are software capability examples, not instructions for operating on a person.

Define what each action actually changes and which feedback it can justify. A contact counter should be described as a contact counter; a visual effect should not imply measured force, realistic bleeding or tissue response. Separate the render mesh, interaction/collision representation, anatomical graph and simulation state. Preserve deliberate activation, held/tracked-tool checks, registration validity and the single case event path. Do not automatically make every artery behave identically because its label contains “artery.”

SOFA is a candidate for a later mechanics proof of concept. Its official supported-platform list is Windows/macOS/Linux; no Android/Quest validation was established here. Before adopting it, test one interaction, native deployment, solver stability, tissue parameter provenance, frame time and any bridge/plugin licensing. Do not replace the working Unity renderer or runtime solely to import a simulation framework.

### Region Bundles and Detail Levels

Keep the full semantic catalog available for search while loading geometry for the active region/case and essential context. Build bundles from verified IDs and dependency closure. HRA detail models have independent source frames; use them for separate inspection until their alignment is deliberately validated.

Use coarse context geometry and higher detail around active targets, with stable interaction IDs across visual detail changes. Small vessels need preservation rules so simplification does not remove a required target. Measure renderer count, draw calls, transparent overdraw, collision cost, memory, loading time and frame time on the Quest with coach/camera workloads. Triangle counts alone do not establish performance. Numerical budgets should follow measurements; none are set by this proposal.

## Authored Cases and Progression

“Every structure” should mean transparent coverage across an explicit target inventory, not one giant procedure. Proposed learning tiers are:

1. **Explore and identify:** reviewed labels, regions, laterality and visual selection.
2. **Understand relationships:** reviewed containment, continuity and spatial context; feedback grounded in mapped graph facts.
3. **Practice bounded tool skills:** an authored goal and observable input criteria using supported capability profiles.
4. **Rehearse one case:** reviewed sequence, mistakes, uncertainty gates and recap using the existing case runner.
5. **Study variations and complications:** separately authored scenarios with reviewed geometry, behaviors and assessment validity.

These are proposed product tiers, not a recognized clinical credential framework. Advancing in this app does not establish surgical competence. Each supported case needs a defined learner group, prerequisite knowledge, learning objective, content review and evidence that its feedback measures the intended skill. Patient health-record context can inform a synthetic scenario; it does not supply internal 3D anatomy. Require clinical/anatomy reviewers for educational claims and separate testing of visual usability, learning outcomes and transfer before positioning the app as surgeon development.

Publish a coverage matrix per case: required concepts, representation availability, relationship review, interaction capability, authored input/feedback, domain review and physical-headset verification. Existing identifier coverage is the first gate. Missing reviewed semantics or mechanics should block the corresponding capability, not make the whole catalog disappear. A new case is accepted only when its actual producer/consumer path works through selection, input, assessment, recap and retry.

## Generic Anatomy Versus a Real Person

The current native mannequin fit is authored teaching geometry. The atlas manifest explicitly records an unverified source origin and independent detail frames. For MR, surface landmarks and tracking can estimate a participant-relative teaching transform; this does not reveal internal organ locations, vessel variants or pathology. Generic anatomy should remain labeled as such. Participant movement, occlusion and uncertain registration must hide misleading overlays and stop assessment as required by the existing registration gate.

Patient-specific internal anatomy would be a separate imaging/segmentation/registration project with appropriate permissions, domain review and measured error. No such imaging input or validation is present. A point tracker can help preserve visible correspondences; it cannot fill that missing internal evidence. Full VR uses authored scene coordinates and should not require CV to rediscover virtual anatomy whose identity and transform are already known.

## Recommended Next Slice

Finish the current physical native session, then add a reviewed anatomy-exploration layer around its existing regional bundle. Verify a small set of external concept mappings and relationships, reuse current selection/highlight tools, and make the coverage matrix explicit. Expand one region and one capability at a time. This creates reusable structure for the broad atlas without pretending that thousands of meshes already form a complete surgical simulator.
