# Quest Application

Owner: Stephen for runtime, capture, registration, bootstrap, and Unity configuration; Matthew for anatomy experience, voice client, and exercise assets. See the [team plan](../../docs/team-plan.md) for planned subfolders and scene ownership.

This is not yet an initialized native Quest project. It now contains a standalone [instrument kit](../../assets/instruments/README.md) under `Assets/Scalpal/Instruments/`, with model sources, pickup/action prefabs and a virtual practice sandbox. See its [runtime contract](../../docs/instrument-runtime.md). Stephen initializes the native Quest application here, pins the toolchain, and establishes the shared runtime interfaces before overlapping scene work. Keep authored assets under `Assets/Scalpal/`, commit `.meta` files, and keep Unity caches/builds out of Git.

The prior physical-headset camera/bottle experiment exists separately; see the [hardware baseline](../../docs/hardware-baseline.md). Do not treat it as a complete Scalpal app or copy a full sample project without deciding its package/version setup.

Consume the shared [contracts](../../packages/contracts/README.md). Raw camera/video input, torso registration, known virtual objects, exercise progression, and voice actions have separate responsibilities. Never use a voice command as payment authority.
