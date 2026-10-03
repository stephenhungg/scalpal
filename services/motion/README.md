# Motion Reconstruction and Robot Replay

Owner: Silas.

Planned offline pipeline on the Mac: decode a short permitted passthrough clip, infer hand landmarks with a pretrained model, retarget valid movement to one articulated robot hand, and emit a simulated replay plus quality/status information. This folder is not an initialized Python project.

Agree capture metadata with Stephen and result/job contracts with Nakim. Check the selected simulator's runtime compatibility and robot-joint conventions before committing to it. Use synthetic motion to isolate mapping errors; the final replay must be derived from the submitted clip.

Policy training, real robot control, precise forces, and exact world-space wrist/arm reconstruction are outside the first slice. Report confidence gaps rather than concealing missing observations. Keep input clips and derived private datasets outside Git.

See the [research](../../docs/research/README.md), [contracts](../../packages/contracts/README.md), and [team plan](../../docs/team-plan.md).
