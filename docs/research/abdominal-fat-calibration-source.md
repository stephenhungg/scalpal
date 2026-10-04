# Abdominal Fat: Verified Source and Calibration Limits

Checked 2026-10-04 UTC. Fontanella et al. provide abdominal SAT/VAT indentation fits, but the paper does **not** identify a ready-to-use St Venant–Kirchhoff material. A normalized relaxation coupon is feasible; reproducing its finite-strain indentation fit remains blocked by an ambiguous bulk-compliance unit and incomplete support/contact conditions. No solver calibration, Unity test, headset test or participant capture ran for this source audit.

Primary source: [Fontanella et al., Processes 2022, 10, 1798](https://www.mdpi.com/2227-9717/10/9/1798), DOI `10.3390/pr10091798`. Publisher retrieval returned 429/403; the [University of Padova author-hosted article PDF](https://www.research.unipd.it/retrieve/4f7b6a4d-b1ac-4e3b-8c1d-3fd78606a8f3/Mechanical%20Behavior%20of%20Subcutaneous%20and%20Visceral%20Abdominal%20Adipose%20Tissue%20in%20Patients%20with%20Obesity%20_%20Enhanced%20Reader.pdf) was downloaded and pages 1–11 visually inspected. It is image-only, so empty text extraction was not accepted as evidence. Page 1 licenses the article CC BY 4.0; page 11 makes raw data available on request. No open raw force/time/displacement file, FE input deck or code was verified. Article reuse permission does not establish a license for unpublished raw data.

## Conditions and Definitions

The following facts are from sections 2.1–2.4, pp. 3–5:

| Item | Published definition |
| --- | --- |
| Tissue/population | Abdominal SAT and VAT; 19 severe-obesity sleeve-gastrectomy patients, 5 male/14 female; age 46 ± 11 years; BMI 46 ± 5 kg/m² |
| Preparation | Frozen −20°C; tested within 3 h after room-temperature thaw; physiological-solution hydration at room temperature |
| Specimen | 20 mm diameter punch; thickness 9.1 ± 2.3 mm; cylindrical container |
| Probe | Spherical, 10 mm diameter |
| Strain | `ε = indenter displacement / initial specimen thickness`; dimensionless, not local tissue strain |
| Preconditioning | 10 cycles, 0–20% indentation strain, 10%/s |
| Experimental loading | Five consecutive 15% increments, 3000%/s, each followed by 300 s hold |
| FE loading | Nominal 20 mm diameter × 9.1 mm high cylinder; 50% indentation at 3000%/s, 300 s hold |
| FE contact | Rigid spherical indenter; frictionless indenter/tissue contact; 8-node hexahedra, average seed 0.8 mm, approximately 28,000 nodes |
| Force processing | 0.1 N threshold for surface/indenter adjustment; equilibrium force at hold ends; each hold normalized by its initial peak force |

The experimental sequence reaches 75% (Figure 2), while the FE model and plotted calibration domain end at 50%. Do not conflate them. Container clearance, side/bottom friction, bottom fixation, preload procedure and sample-specific displacement/time files are not specified sufficiently to reproduce the FE boundary conditions. Figure 1 shows a container; it does not prove frictionless free-side compression or fully confined tissue.

For a nominal 9.1 mm specimen, 3000%/s means `dε/dt = 30 s⁻¹`, or `dδ/dt = 0.273 m/s`. The 15% ramp takes 0.005 s; the 50% FE ramp takes 1/60 s. These derived durations are shorter than, or comparable to, the runtime 90 Hz timestep: use substeps or an explicitly integrated loading history for the coupon. A 50% indentation gives 4.55 mm probe travel.

## Published Laws and Numbers

Equation (1) fits **structural force**, `F_eq(ε) = (c/α)[exp(αε)−1]`. Here `c` has force units and is not a Young modulus. Equation (2) is:

`R(t) = F(t)/F_peak = 1−γ1−γ2 + γ1 exp(−t/τ1) + γ2 exp(−t/τ2)`.

No subtraction of a previous stage's equilibrium force is described. Do not import the separate bowel paper's increment-normalization procedure.

Equation (4) specifies the **equilibrium**, one-term compressible Ogden energy:

`W∞ = (2μ/α²)(λ̄1^α + λ̄2^α + λ̄3^α − 3) + (J−1)²/D`, with `λ̄i = J^(−1/3) λi`.

Equation (3) displays `ψ(C,q) = W⁰(C) − Σ∫₀ᵗ (qᵢ(s):Ċ(s))/2 ds`; its following prose instead refers to `W∞`. Equation (5) displays the history law

`qᵢ(t) = γᵢ/(γ∞ τᵢ) ∫₀ᵗ exp[−(t−s)/τᵢ] [2∂W∞(C(s))/∂C] ds`.

Preserve that instantaneous/equilibrium distinction; the notation discrepancy is not permission to substitute StVK. Under the usual normalized spectrum interpretation, `γ∞ = 1−Σγᵢ` and instantaneous moduli are equilibrium moduli divided by `γ∞`.

Table 3, p. 8, transcribed exactly:

| Group | μ, kPa | α | D, kPa⁻¹ **as printed** | γ1 | γ2 | τ1, s | τ2, s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| VAT | 12.55 | 7.23 | 64.66 | 0.63 | 0.27 | 1.21 | 47.48 |
| SAT | 17.50 | 10.11 | 65.66 | 0.60 | 0.26 | 1.41 | 52.63 |

[Machine-readable transcription and analytic reference samples](../../assets/anatomy/material-data/fontanella2022/reference.json), with [retrieval/provenance](../../assets/anatomy/material-data/fontanella2022/provenance.json). These are published fit parameters and equation evaluations, **not experimental raw traces**. VAT must not be relabeled as mesoappendix, and SAT must not be assigned to skin/peritoneum.

## Compatibility with the Current Solver

This analysis follows the actual `VolumeMaterial` and `MaxwellHistory` source at audited commit `4ca4e31d960367e949f9a3044dbaeba1cf68c35f`:

- `VolumeMaterial` uses quadratic Green-strain StVK energy. It has no Ogden exponent and cannot generally reproduce the published strain stiffening. Force slopes in N per dimensionless indentation strain are structural stiffnesses; dividing by an arbitrary area does not produce a transferable Young modulus.
- `MaxwellHistory` interprets `youngPascals` as instantaneous stiffness and applies a shared positive spectrum to both shear and bulk. The normalized fractions/times are admissible (`γ∞`: VAT 0.10, SAT 0.14), but direct insertion only supplies a source-informed approximate spectrum. Matching a homogeneous held-strain stress ratio does not validate indenter reaction force or the nonlinear Ogden history law.
- Literal unit conversion gives `D_SAT = 0.06566 Pa⁻¹` and `D_VAT = 0.06466 Pa⁻¹`. Expanding the energy about `J=1` gives `K∞ = 2/D`: approximately 30.46 Pa (SAT), 30.93 Pa (VAT). With the printed shear moduli, `ν = (3K−2μ)/(2(3K+μ))` is approximately −0.9922 and −0.9890, respectively. The runtime requires `ν ≥ 0`; it cannot represent this printed combination. This is a documented inconsistency, not a corrected unit. Do not silently change the D unit, impose incompressibility or call the result the paper's calibrated material.
- Even if D were clarified, matching small-strain shear stiffness would require `G0 = μ/γ∞`, rather than treating the equilibrium μ as instantaneous. For a declared surrogate with chosen `ν`, `E0 = 2(1+ν)μ/γ∞`; that chosen compressibility is an assumption. `ηi = γi E0 τi` is the surrogate's uniaxial Maxwell-arm viscosity, not a directly measured tissue viscosity. Shear-arm viscosity instead uses `G0`.

## Reproducible Next Experiment

First run a material-point coupon against the analytic `R(t)` samples with fixed strain and zero numerical node damping. Check reaction-stress relaxation, all time units, branch positivity and timestep convergence. Label it **verification of the implemented approximation using published SAT/VAT normalized coefficients**.

A measured indentation calibration then needs the author's raw forces/displacements, per-specimen heights, threshold/zero correction, container/support contact and clarification of D and instantaneous/equilibrium notation. Until available, a sphere/cylinder exercise may be a disclosed assumed-boundary sensitivity study. Keep inferred supports and surrogate elastic parameters explicit; report force/deformation residuals and mesh/timestep sensitivity. No arbitrary equilibrium modulus or visually selected viscosity fills this evidence gap.
