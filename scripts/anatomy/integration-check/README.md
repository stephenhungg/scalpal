# Anatomy and exercise integration check

From the repo root:

```sh
dotnet run --project scripts/anatomy/integration-check/IntegrationCheck.csproj
```

Compiles the actual AnatomyController, AnatomyPart, AnatomyExerciseBinding, AnatomyInstrumentTip, CaseRunner, and case DTOs. Loads the committed bundle and completes the adult appendectomy case through the same gated input boundary used by the scene. Builds test identities from the actual atlas manifest and completes all three procedures through that boundary; absent optional abdominal wall and umbilicus context do not block port placement or confirmation. Checks missing cecum/terminal ileum, review acknowledgement, registration loss, preview gating, hidden/foreign colliders, instrument selection, coach identity, session replacement, completion, and forwarding.

Unity renderers and physics are test doubles. These checks do not run Unity physics, import assets, validate registration, exercise real networking, or measure headset performance. `relay-check` separately exercises the actual CoachRelay against transport doubles.

## Scene integration

Assign the atlas controller to `AnatomyExerciseBinding.anatomy`. Load `Resources/scalpal_bundle.json` through `JsonUtility.FromJson<ScalpalBundle>` and call `SelectCase(bundle, caseId, acknowledgeReview, out reason)`. Subscribe to `StepStarted`, `MistakeMade`, `CaseCompleted`, or `EventHandled` before selecting the case. Selection reports missing required AnatomyParts or colliders; it never silently substitutes anatomy.

The participant tracker must call `AnatomyController.SetRegistrationValid`. All scoring goes through `Submit` or `TouchCollider`, including port placement, UI confirm, and voice identification. Never bypass this gate by creating an unrelated CaseRunner in the scene. A visible selection preview cannot score. This does not perform anatomical fitting or transform registration.

Call `SelectInstrument` from the tool selector. Put `AnatomyInstrumentTip` on each tip with its exact instrument ID, a trigger collider, and an appropriate kinematic Rigidbody. Assign `exercise`. Trigger entry dispatches one contact, never a per-frame Stay loop. For a controller button action, disable `activateOnEntry` and invoke `ActivateContact` once for the selected collider per deliberate activation. Unity physics/layer configuration and hardware button mapping remain scene work.

Offline mode is the default and does not send exercise progress to Jarvis. For live mode, assign `coach`, set `requireCoachSynchronization` before selecting, and attach AnatomyCoachBinding for tracking/highlight commands. Selection adopts a fresh matching Jarvis session; an unavailable, progressed, mismatched, failed, or replaced session pauses inputs. Create a new Jarvis session and reselect the case to restart; this does not reconstruct a partially completed remote attempt. Do not subscribe EventHandled to another Forward call, since the binding already forwards accepted live events once.

## Service-loaded cases

Attach AnatomyCaseSource, assign `ScalpalPreopService` and the exercise binding, and route patient selection through `LoadPatient(patientId)`. It serializes case requests, rejects mismatched patient responses, and clears the previous runner while loading. Ready cases select automatically; `needs_review` cases raise `CasePending` and wait for `AcknowledgeReviewAndStart`. Bind `StatusChanged` to feedback and `CaseStarted` to case presentation. Registration must be re-established after selection. Request failures and blocked cases leave no stale runner to resume.

ScalpalPreopService failure events lack request correlation, so any failure on the assigned service conservatively stops this attempt. Use a dedicated service instance if unrelated UI requests must not interrupt it. This harness exercises the event boundary with a service double; service HTTP and offline fallback are not exercised here.
