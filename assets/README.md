# Source Assets

Proposed ownership: Matthew for `anatomy/`; Silas for `robots/`. Create those subfolders when choosing the first actual assets.

Keep anatomy Blender source, selected exports, attribution, and licenses under `anatomy/`. Matthew imports runtime models into the Quest project with stable Unity metadata. Robot descriptions/models and their licensing belong under `robots/` with the simulator/version conventions documented.

Choose one permitted model of each type for the first slice. Geometry does not imply anatomy accuracy, tissue physics, or robot-controller compatibility. Configure Git LFS for actual large source binaries when adding them; do not commit large generated exports or third-party datasets by default.

No participant imagery belongs here. See [research](../docs/research/README.md) and the [team plan](../docs/team-plan.md).

The [instrument kit](instruments/README.md) supplies the fourteen existing case tool IDs plus a scalpel, with editable Blender sources, optimized FBX exports, renders and MIT-licensed source geometry attribution. These are generic simulator assets with an authored virtual practice runtime, not patient-specific anatomy or a completed Quest app.
