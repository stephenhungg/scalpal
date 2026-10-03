# Companion Application

Owner: Nakim.

Planned browser client for wallet pairing, session and processing status, robot replay presentation where supported, feedback/receipt access, and the observer workflow. This folder is not an initialized web application. Choose its stack when implementation starts.

Use `services/api/` for trusted acceptance and reward state. Coordinate replay output/display with Silas instead of assuming the robot simulator already emits a browser viewer. The existing headset mirror is a separate spectator tool; this app need not implement video mirroring.

See the [team plan](../../docs/team-plan.md), [demo flow](../../docs/demo-flow.md), and [contracts](../../packages/contracts/README.md). Keep treasury/provider keys outside the browser client.
