# Patient Dossiers

Voice-agent dossiers for the diagnosis office, one per FinchNode demo patient with an authored encounter. Synthetic patients; illustrative teaching content, not clinical guidance.

Each dossier has: **Patient knowledge** (safe to give the patient agent: life story, speech style, emotional arc, lay beliefs, symptom story by topic, 25+ off-script answers, a never-say list), **Clinician key** (never given to the patient agent: diagnosis, must-not-miss differential, key findings, chart-driven risks, a model presentation, Socratic probes for Scalpal, cited sources), and **Consistency notes** against `services/preop/src/catalog/encounters.ts`, `cases.ts` and the live FinchNode chart.

`encounters.ts` stays the source of truth for scored facts. Dossiers add depth for questions outside the authored topics; any conflict is listed in the dossier's consistency notes and should be fixed in the catalog, not by the agent.

| Patient | Subject | Case |
| --- | --- | --- |
| Priya Ramaswamy, 40 | [patient-demo-multi-source](patient-demo-multi-source.md) | Acute appendicitis, urgent |
| Theo Abernathy, 9 (mother Laura speaks) | [patient-demo-pediatric-asthma](patient-demo-pediatric-asthma.md) | Acute appendicitis, urgent |
| Jonah Okoye, 30 | [patient-demo-sparse](patient-demo-sparse.md) | Perforated appendicitis, emergency |
| Harriet Lindqvist, 78 | [patient-demo-polypharmacy](patient-demo-polypharmacy.md) | Acute cholecystitis on apixaban, urgent |
| Morgan Rivera, 38 | [patient-demo-001](patient-demo-001.md) | Symptomatic cholelithiasis, elective |
| Ingrid Solano | [patient-demo-source-unavailable](patient-demo-source-unavailable.md) | Interval cholecystectomy after gallstone pancreatitis |
| Dolores Marchetti | [patient-demo-messy-coding](patient-demo-messy-coding.md) | Recurrent sigmoid diverticulitis, elective colectomy |
| Sam Ortiz | [patient-demo-consent-partial](patient-demo-consent-partial.md) | Symptomatic cholelithiasis, partial consent |

No dossier: [rate-limited](patient-demo-rate-limited.md) and [consent-revoked](patient-demo-consent-revoked.md) are explore-page states, explained in their notes.

## Catalog issues for the encounter owner

Collected from the consistency notes (details and exact values in each dossier):

- Priya: latex allergy marked high severity while the chart records low criticality (contact rash); `brief.ts` drops one source's "No known allergy" entry, hiding the conflict the scenario teaches.
- Theo: chart peanut reaction (hives, age 2) differs from the encounter (anaphylaxis, age 5); EpiPen not on the chart med list; an asthma ER visit in Nov 2024 versus "never hospitalized".
- Jonah: "doesn't go to doctors" versus an annual physical in Jan 2026; "started Wednesday" assumes today is Friday (Oct 3, 2026 is a Saturday).
- Harriet: apixaban hold of ~48 h is short at CrCl ~27 (≥72 h or a level); Tokyo 2018 would consider antibiotics plus percutaneous drainage first, but scoring accepts only cholecystectomy.
- Sam: FinchNode names the consent-partial patient Marcus Delacroix (b. 1972), the encounter says Sam Ortiz, 61; the menstrual question falls back to "don't remember" because the chart sex is blank.
- Ingrid and Sam lack authored allergy / medication answers, so critical items fall back to bare chart lines.
- Rate-limited exclusion reason is wrong: FinchNode alternates 429s every 2 s and the client's retry usually loads Samir Haddad, 47.
- Voice presets look swapped by age for the 38- and 63-year-old patients.

Prior diagnoses: elective patients (Ingrid, Dolores) were told earlier diagnoses, so the patient prompt now allows repeating a condition a tool returns from their past while still forbidding any guess about today's problem.
