# Human abdominal tissue material data

Research checked 2026-10-04. This is a source inventory plus a completed [force/source-summary experiment](solver-calibration-experiment.md). No runtime material has a validated measured specimen calibration, and no physical headset or clinical validation ran. Published measurements are offline references; source condition, tissue site and loading mode must travel with every parameter.

## Available numeric evidence

| Target | Best verified evidence | Accessible numeric file | Use now |
| --- | --- | --- | --- |
| Whole abdomen under indentation | Remus 2024 fitted force–displacement curves | Supplement S1 CSV downloaded with checksums | Composite response benchmark only |
| Skin without hypodermis | Human explant tensile, relaxation and frequency results | Published values/figures; raw traces not verified | Limited reference points; relaxation fit underdetermined |
| Abdominal fat | Alkhouli 2013 tensile moduli and two-exponential relaxation fits | Published tables; raw traces not verified | Site-specific stiffness/time references, not a complete passive fit |
| Peritoneum with transversalis fascia | Kriener 2023 cadaveric tension | Published table; raw data on request | Tensile reference, no measured viscosity |
| Large bowel wall | Fontanella 2026 normalized Prony fits | Published table; public raw file not identified | Directional relaxation benchmark, not appendix/ileum calibration |
| Orbital fat | Hollister et al. open shear-relaxation dataset | Zenodo XLSX + MATLAB | Fitting-method reference only; wrong anatomical site |

### Composite abdominal indentation: Remus et al. 2024

[Primary article, DOI 10.3389/fbioe.2024.1384062](https://www.frontiersin.org/journals/bioengineering-and-biotechnology/articles/10.3389/fbioe.2024.1384062/full). CC BY 4.0. Ten healthy males, ages 25–37, BMI 19.5–24.6; six regions, relaxed/activated musculature. Hemisphere radius 10 mm; feed 5 ± 1 mm/s; indentation up to 30 mm. The reference ring slightly compresses skin when defining zero. Holds over 1 s were excluded. These are composite skin/fat/muscle/deeper-structure measurements, not isolated layers or stress-relaxation traces.

Downloaded unchanged [S1 CSV](../../assets/anatomy/material-data/remus2024/DataSheet1.CSV), 19,505 bytes, 154 rows; [provenance](../../assets/anatomy/material-data/remus2024/provenance.json). [Supplement retrieval](https://www.ebi.ac.uk/europepmc/webservices/rest/PMC11157078/supplementaryFiles). MD5 `8f7b1a1351368d3a0b0213bdd01c96a4` matches the publisher entry. SHA256 `ce9e4039ea5f7d3141c3158c0653dcfb98646cf5b00842fa02357cfa79907e17`.

`fitMaxDispl` is mm; force is N; coefficients have descending polynomial powers. Ignore unused `NaN` fields. Example `R3/rangeMean`: `F(δ) = 0.00860378238220968 δ² + 0.125999776463138 δ`, valid 0–30 mm. Convert Unity meters to mm. Do not extrapolate. Individual CSV domains reach 37.26 mm; preserve row-specific limits rather than imposing the paper's protocol summary globally.

Source discrepancy: CSV `A2/rangeMean` is degree 4; main Table 4 labels degree 5 and contains an extra coefficient. `A4` coefficients also differ. Pin the exact CSV row; do not mix table coefficients. Surface deformation and contact-force fits both matter for inverse calibration.

### Skin explant: 2026 instrument study

[Primary article, DOI 10.1038/s41598-026-42371-9](https://www.nature.com/articles/s41598-026-42371-9), [PMC full text](https://pmc.ncbi.nlm.nih.gov/articles/PMC13096531/). CC BY 4.0. Explants mainly from abdominal surgery; hypodermis removed, epidermis/dermis retained. All mechanical testing around 21°C; tensile speed 10 mm/min.

At held 18% strain for approximately two minutes, relaxation modulus decreases from 36.5 ± 0.5 kPa at the start to 25.0 ± 0.5 kPa at 30 s. Total reported relaxation is 39 ± 3%. Tensile initial modulus is 28.0 ± 0.5 kPa; reported tangent modulus at maximum deformation is 88.4 ± 0.5 kPa (the corresponding tensile strain is not assigned from the separate 18% relaxation protocol). At 10% strain, dynamic moduli are `E′ = 11.4 ± 0.5, E″ = 2.2 ± 0.5 kPa` at 0.01 Hz and `E′ = 14.7 ± 0.5, E″ = 3 ± 2 kPa` at 1 Hz.

No numeric time-series file verified. Two relaxation points do not uniquely determine an equilibrium modulus and relaxation time. These are an explant's test-condition results, not a universal skin constant or a fat-inclusive indentation modulus.

### Abdominal adipose: Alkhouli et al. 2013

[Primary article, DOI 10.1152/ajpendo.00111.2013](https://journals.physiology.org/doi/full/10.1152/ajpendo.00111.2013). Raw files/license unverified. PBS, pH 7.4, room temperature; tension 5 µm/s; relaxation 26 µm/s to 30% strain held ~50 min. Paired tension n=19:

| Tissue | Initial tangent modulus, kPa | Tangent modulus at 30%, kPa |
| --- | ---: | ---: |
| Subcutaneous | 1.6 ± 0.8 | 11.7 ± 6.4 |
| Omental | 2.9 ± 1.5 | 32 ± 15.6 |

Table 3: `F(t)=A1 exp(−k1 t)+A2+A3 exp(−k2 t)`; mean ± SD, n=6/depot:

| Tissue | k1, s⁻¹ | k2, s⁻¹ | Reported t1, min | Reported t2, min |
| --- | ---: | ---: | ---: | ---: |
| Omental | 0.008 ± 0.005 | 0.001 ± 0.0003 | 2.1 ± 3.3 | 16.7 ± 55.6 |
| Subcutaneous | 0.004 ± 0.003 | 0.0004 ± 0.001 | 4.2 ± 5.6 | 41.7 ± 16.7 |

Amplitudes are forces, not moduli. Subcutaneous mean A3 = −0.4 ± 3.2 mN prevents passive Prony conversion. Reciprocating averaged rates does not recover reported time distributions.

### Peritoneum/fascia: Kriener et al. 2023

[Primary article: Mechanical Characterization of the Human Abdominal Wall Using Uniaxial Tensile Testing](https://pmc.ncbi.nlm.nih.gov/articles/PMC10604332/). CC BY 4.0. “Peritoneum” specimens include transversalis fascia. Room temperature 18–21°C, hydration maintained; no preconditioning; 0.2 N preload; crosshead speed 5 mm/min. Engineering stress uses original area; elastic modulus is the selected post-toe linear tensile slope.

Table 5 median (IQR):

| Preservation | Tensile modulus, MPa | Ultimate tensile strength, MPa | Failure strain, % |
| --- | ---: | ---: | ---: |
| Fresh-never-frozen | 6.79 (6.09) | 1.80 (3.99) | 145.54 (118.00) |
| Fresh-frozen | 4.42 (14.15) | 2.24 (3.66) | 135.71 (108.00) |

Raw data are available on request, not an identified open download. The 6.79 MPa result is a tensile modulus, not viscosity, compressive stiffness or a pure isolated peritoneum material. Failure stress/strain do not identify blade fracture energy. Gauge dimensions vary, so 5 mm/min is not one constant strain rate across all specimens.

### Integrated colon wall: Fontanella et al. 2026

[Primary article, DOI 10.1016/j.actbio.2026.09.033](https://www.sciencedirect.com/science/article/pii/S1742706126006343), [date record](https://pubmed.ncbi.nlm.nih.gov/42767542/): September 21, 2026. Twenty-one donors; healthy descending/sigmoid specimens frozen −20°C, intact wall layers. Six tensile increments: 10% longitudinal or 30% circumferential strain, 100% strain/s, 600 s holds. Previous equilibrium stress subtracted before peak normalization. Table 5:

| Direction | γ1, dimensionless | τ1, s | γ2, dimensionless | τ2, s |
| --- | ---: | ---: | ---: | ---: |
| Longitudinal | 0.23 | 126.80 | 0.62 | 1.91 |
| Longitudinal with taeniae | 0.24 | 134.00 | 0.61 | 1.55 |
| Circumferential | 0.25 | 109.43 | 0.59 | 1.81 |
| Radial | 0.25 | 100.16 | 0.70 | 0.62 |
| Radial with taeniae | 0.30 | 91.34 | 0.64 | 0.94 |

`G(t)=1−γ1−γ2+γ1 exp(−t/τ1)+γ2 exp(−t/τ2)`. Radial groups use compression, not the tensile protocol. Raw files, radial ramps, temperature and reuse license unverified; [authors' repository](https://research.unipd.it/handle/11577/3616821) has no files. Not appendix/ileum or separated-layer calibration; normalized fits supply no absolute modulus.

### Open dataset for a fitting-method reference

[Hollister et al., Shear Viscoelastic Properties of Human Orbital Fat, Zenodo DOI 10.5281/zenodo.10836108](https://zenodo.org/records/10836108). Repository metadata specifies CC BY 4.0, raw stress relaxation at 37°C, `Stress relaxation.xlsx` (413,205 bytes) and MATLAB analysis `prony_st_rel.m` (5,369 bytes). [Numeric-file endpoint](https://zenodo.org/api/records/10836108/files/Stress%20relaxation.xlsx/content). Not downloaded or assigned to an abdominal material: orbital fat in shear is a distinct tissue/site/loading mode.

Further primary lead: [Fontanella et al. 2022, abdominal SAT/VAT in severe obesity, DOI 10.3390/pr10091798](https://www.mdpi.com/2227-9717/10/9/1798). Its inverse model separates equilibrium Ogden fitting from two-branch relaxation fitting. The protocol models a 20 mm diameter, 9.1 mm high specimen and a 10 mm diameter indenter, up to 50% indentation strain, 3000%/s ramp, 300 s hold. Numeric raw data are on request; complete parameter-table extraction and units/support audit are recorded in [the source audit](abdominal-fat-calibration-source.md). Avoid transferring the model directly into a St Venant–Kirchhoff solver.

## Proposed solver calibration

These are engineering recommendations, not completed experiments:

1. Reproduce the source coupon/indenter geometry, grip/contact conditions, preload, preservation, temperature and strain history. Fit force and stress only using the same definitions as the measurement. Use meters, seconds, kilograms, newtons and pascals internally; record every conversion.
2. Treat the current `VolumeMaterial` St Venant–Kirchhoff law as a constitutive approximation. A reported tensile slope can seed a small-strain coupon; it cannot reproduce a full nonlinear anisotropic tissue curve automatically. Fit the implemented energy law through the actual discretized coupon. Check both deformation and measured reaction force. Do not distribute a composite abdomen modulus to each skin/fat/peritoneum layer.
3. Add stress-history/internal-variable relaxation separately from numerical motion damping. A fixed-strain coupon must lose reaction force while its boundary stays fixed. Velocity damping alone does not demonstrate this material behavior. Fit `E(t)=E∞+Σ Ei exp(−t/τi)` with `Ei≥0`, `E∞≥0`, `τi>0`; include the real loading ramp rather than assuming an instantaneous step. Only with matching material/test modes does a Maxwell branch permit `ηi=Ei τi` in Pa·s. Normalized γ/τ tables alone do not identify η.
4. Keep individual layers/sites and loading directions separate. Verify force, relaxation residuals, volume preservation and inversion protection at multiple time steps and mesh resolutions. Constraint compliance must follow the energy, geometry and units; a visually useful cage parameter is not a measured viscosity.
5. Calibrate fracture and contact separately. Blade cutting needs measured fracture energy/force versus blade geometry, speed and hydration; ultimate tensile strength is insufficient. Organ contact friction/adhesion and pressure-driven bleeding need their own measurements. A visual blood effect or disconnected tetrahedral face is implementation evidence, not a calibrated biological response.

## Remaining evidence gaps

No verified open raw time-series set here covers all abdominal layers, appendix, mesoappendix and appendicular artery. Missing values include isolated peritoneum relaxation, specimen-level abdominal fat fits with positive amplitudes, patient/site-specific nonlinear skin curves, vessel pressure/flow/compliance, blade fracture energy, organ contact friction and layer adhesion. Do not label runtime materials “measured viscosities” or the assembled abdomen “realistic calibrated physics” until the corresponding coupon validation and boundary checks pass.
