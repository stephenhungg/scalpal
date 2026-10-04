# Robot Assets

Owner: Silas.

## `shadow_hand/` (selected for the first slice)

Shadow Dexterous Hand, right and left, from [dexsuite/dex-urdf](https://github.com/dexsuite/dex-urdf) at commit `f5e7132f22108164577fea4c25ef99b5cc0e1900` (`robots/hands/shadow_hand`). Only `shadow_hand_right.urdf`, `shadow_hand_left.urdf`, and the meshes they reference were copied (about 3.3 MB, plain files, no LFS). Both URDFs use identical link and joint names.

- License: the URDF header carries Shadow Robot Company's BSD license; the dex-urdf directory ships an Apache 2.0 `LICENSE`, included here. dex-urdf modified the original meshes and increased fingertip inertia (see their README).
- 24 revolute joints: `WRJ2 WRJ1`, `FF/MF/RF J4..J1`, `LFJ5..J1`, `THJ5..J1`. Units are radians. Joint order differs between pinocchio (dex-retargeting) and MuJoCo, so always map joints by name.
- Loaded by `services/motion` with dex-retargeting (pinocchio) for retargeting and MuJoCo 3.x for kinematic replay. MuJoCo's URDF importer puts visual meshes in geom group 1.
- Not tested in Unity, SAPIEN, or any physics task.
