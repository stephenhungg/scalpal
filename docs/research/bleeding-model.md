# Bounded vessel bleeding model

`VesselBleeding` is a pure fluid-volume model for the shared MR/full-VR exercise. It has no renderer, collider, anatomy-registration authority or scored event path. Runtime bindings must gate injury, sealing/clipping and suction through the existing tracked tool/session path.

## Physics and evidence

[NASA Glenn's Bernoulli equation](https://www1.grc.nasa.gov/beginners-guide-to-aeronautics/bernoullis-equation-1/) gives pressure plus kinetic energy density as constant for steady, incompressible, inviscid flow with negligible height difference. Our approximation assumes negligible upstream velocity and atmospheric outflow, giving `v = sqrt(2 Δp / ρ)`. The orifice discharge is `Q = Cd A v`, from effective opening area times velocity with a configurable discharge coefficient. `Q` is m³/s, `A` is m², `Δp` is Pa, and `ρ` is kg/m³. This is a documented physical approximation; NASA does not establish our vessel parameters or a clinical bleeding model.

Defaults are **unmeasured teaching assumptions**: pressure difference 12,000 Pa, density 1,060 kg/m³, coefficient 0.6 and a finite 500 mL virtual reservoir. Default maximum injury area is 0.00001 m² (10 mm²). These are configurable numerical/demo choices, not measured appendicular artery pressure, hole geometry, discharge coefficient or available circulating blood. The source holds fixed pressure until depletion. No pulsatility, upstream vascular resistance, pressure collapse, clotting, perfusion, viscosity-dependent lumen flow or patient-specific physiology is modeled.

## API and ledger

Create one `VesselBleeding` per bound vessel injury. Constructor arguments use explicit SI names; only `sourceMilliliters` converts volume at the public boundary.

| Call | Effect |
| --- | --- |
| `OpenInjury(areaSquareMeters)` | Opens/grows one effective orifice, capped by the configured maximum. Uses max(existing, requested), so repeated collider/cut observations do not double count the same injury. Does not remove existing occlusion. Returns acceptance. |
| `Step(seconds)` | Integrates `min(Q Δt, remaining source)`, returns acceptance, exposes actual `LastStepLossMilliliters`. |
| `SetOccluded(bool)` | Seal/clamp stops new flow; reopening permits flow. Existing lost/pool volumes remain. The runtime decides whether a clip is reversible or a seal persists. |
| `RemovePool(requestedMilliliters)` | Removes at most the available pool and returns the actual removed amount. Does not reduce cumulative blood loss or restore the source. |
| `Reset()` | Clears injury, occlusion, fault, elapsed time, loss, pool and removal; restores initial virtual source/configuration for retry. |

Volumes are stored in m³. `CumulativeLossMilliliters`, `PooledMilliliters`, `RemovedMilliliters` and `RemainingSourceMilliliters` expose the two conserved balances:

```text
initial source = remaining source + cumulative loss
cumulative loss = pool + suction removed
```

`FlowMillilitersPerSecond` is the currently available discharge rate. Use actual `LastStepLossMilliliters` when rendering a frame that exhausts the source. Pool rendering reads `PooledMilliliters`; suction may clear the surface while cumulative loss persists.

## Bounds and failure behavior

Constructor bounds are numerical budgets, not physiology ranges: pressure 0–100,000 Pa; density 1–3,000 kg/m³; coefficient 0–1; source 0–10,000 mL; maximum area positive and ≤0.0001 m²; duration positive and ≤3,600 s. Invalid configuration throws before a model exists.

Steps accept 0–0.1 s, with a configured total-duration bound. No large stalled-frame catch-up is applied. Openings must be finite, positive and within the configured area limit. Suction requests must be finite and 0–10,000 mL. Invalid operations leave the volume ledger unchanged and latch `IsFaulted`; flow stays zero until `Reset()`. A pause should stop calling `Step`, preserving injury and pool. A failed registration/tracking gate must never invoke an injury/control mutation. Missing spatial overlap must not create a nominal injury.

## Synthetic verification and runtime acceptance

`NativeBleedingValidation.Run()` covers analytic flow/unit conversion, pressure/density/area scaling, time partitioning, source depletion, duplicate cuts, occlusion/reopening, pool removal, volume conservation, invalid inputs and retry reset. The root Unity gate passed221 synthetic arithmetic assertions and261 runtime geometry/contact/lifecycle assertions. These are Editor fixtures, not a physical headset test.

Synthetic runtime checks cover: finite blade sweep intersects the actual appendicular-artery target; valid tool/tracking/registration/readiness gates open the injury once; existing Seal/Clip/Suction contacts control the same model; pause/lost registration freezes effects; retry clears topology and this ledger; pooled visual amount follows the ledger. Authored case progression must continue using the single existing accepted-event path. Actual headset cutting, wound appearance, control usability and performance remain acceptance work. No physical/clinical bleeding-rate claim is made.
