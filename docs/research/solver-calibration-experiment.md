# Solver Force and Source-Summary Experiment

The actual `TissueVolume` now supports prescribed original-material-node boundary targets and read-only accepted internal forces in newtons. This is a calibration interface, not a validated clinical material. Default skin/fat/peritoneum settings remain uncalibrated; no new candidate is imported into the runtime wall.

## Force Measurement

`SetBoundaryTarget(originalNode, meters)` accepts finite targets only for constructor-pinned nodes. Targets apply during the next accepted timestep and follow material-root identity through cut-node fan duplication. A rejected inverted/nonfinite step retains the previous geometry, force cache and committed material history; pending grip targets remain until changed or reset. `Reset` restores initial boundaries/topology and clears history/forces. `Freeze` preserves accepted state.

`MeasureNodalForces` copies the accepted material internal force cache without advancing time or evaluating a future trial. A grip reaction is the negative sum of these forces on the grip. Per cell, `P = F S`, and nodal forces are minus the reference-volume-weighted energy gradient `V0 P Dm^-T`. This is not controller haptic force. Inertia, contact and numerical velocity damping are excluded. For a free-node coupon, equilibrium residuals and acceleration must be checked before interpreting material reactions as applied laboratory load.

`NativeCouponValidation` passed5,468 synthetic assertions using independently generated tetrahedral coupons: analytical traction `Pxx * A0`, force/moment balance, rotation covariance, area/density/mesh/timestep scaling, fixed-strain stress relaxation, read-only measurements, inversion rollback, and cut/reset boundary/history behavior. Affine all-node constraints isolate constitutive response; they do not verify free-node equilibrium or actual specimen grips/contact.

## Published Skin Summary Comparison

[Blanchard et al. (2026)](https://www.nature.com/articles/s41598-026-42371-9) reports representative ex vivo skin relaxation values. The [numeric summary](../../assets/anatomy/material-data/blanchard2026/summary.json) records source conditions and uncertainties; it is not raw specimen data. The tensile maximum tangent is not assigned to the separate18% relaxation strain.

`NativeSkinCalibrationBenchmark` fits the initial and30-second values under an explicitly assumed1/60-second linear Green-strain ramp and nominal-stress convention. It drives the actual volume/history/force path in an authored20×10×2 mm affine coupon. Poisson ratio0.3, isotropy, specimen dimensions and transverse strain are assumptions. Transverse stretch produces zero transverse second-Piola stress for this implemented StVK/common-spectrum law. The approximate120-second total-relaxation interval is reserved as an end-summary check; it is not an independent raw-trace holdout or equilibrium value.

The [generated result](../../assets/anatomy/material-data/blanchard2026/solver-summary-comparison.json) records six distinct passive candidates. All fit both points to relative error below0.003%; three also satisfy the published end interval. Those three equivalent uniaxial branch viscosities span approximately165,010–238,496 Pa·s under these assumptions. This spread demonstrates nonuniqueness, rather than establishing a measured tissue viscosity. Refining6 to48 tetrahedra and halving the timestep without refitting changes the30-second response by0.000617%. Because every node is prescribed, this is constitutive/discretization verification, not free-surface indentation convergence.

Reproduce with `python3 scripts/quest/verify_session.py --suite unity`, or run the Unity Editor menu `Scalpal/Quest/Benchmark Published Skin Summaries`. `SCALPAL_CALIBRATION_REPORT` can redirect output. A full measured calibration still requires the loading ramp, raw trace, stress/area convention, specimen/grip geometry and independent validation. No runtime profile is marked calibrated.

## Abdominal Fat Follow-Up

The [Fontanella2022 source audit](abdominal-fat-calibration-source.md) now pins SAT/VAT normalized spectra and the equilibrium Ogden law. Printed bulk-compliance units imply negative Poisson ratios incompatible with the current material class; supports and raw indentation data are incomplete. Do not silently reinterpret units or substitute that equilibrium shear modulus as an instantaneous Young modulus. A future source-informed surrogate must be labeled and checked against its normalized spectrum; it does not close the measured full-indentation calibration requirement.
