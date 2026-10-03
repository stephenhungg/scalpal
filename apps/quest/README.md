# Quest Application

Owner: Stephen for runtime, capture, registration, bootstrap, and Unity configuration; Matthew for anatomy experience, voice client, and exercise assets. See the [team plan](../../docs/team-plan.md) for planned subfolders and scene ownership.

This is a scaffold, not an initialized Unity project. Stephen initializes one native Quest application here, pins the toolchain, and establishes the shared runtime interfaces before overlapping scene work. Keep authored assets under `Assets/Scalpal/`, commit `.meta` files, and keep Unity caches/builds out of Git.

The prior physical-headset camera/bottle experiment exists separately; see the [hardware baseline](../../docs/hardware-baseline.md). Do not treat it as a complete Scalpal app or copy a full sample project without deciding its package/version setup.

Consume the shared [contracts](../../packages/contracts/README.md). Raw camera/video input, torso registration, known virtual objects, exercise progression, and voice actions have separate responsibilities. Never use a voice command as payment authority.
