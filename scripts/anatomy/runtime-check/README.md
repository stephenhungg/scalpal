# Anatomy runtime checks

Run from repository root:

```sh
dotnet run --project scripts/anatomy/runtime-check/RuntimeCheck.csproj
```

Uses .NET 10 and small UnityEngine/CoachRelay test doubles to compile the production runtime scripts and exercise visibility, registration gates, preview rotation, isolation, system filters, property-block restoration, duplicate ids, nested ownership, and relay acknowledgements.

These checks do not execute a Unity scene, render shader output, test XR interaction, or measure Quest performance. Import and headset validation remain required. The production binding still uses the existing `Scalpal.Exercises.Coach.CoachRelay`; the stub only captures that boundary for local tests.

The editor checks validate malformed manifests and shared tissue/URP/built-in material configuration. Builder-only mesh, transform, renderer, and tissue APIs are signature doubles, not a geometry importer or tissue simulation. Relay adoption/forwarding methods are signature-only network boundaries; these checks do not assert successful session adoption or delivery.
