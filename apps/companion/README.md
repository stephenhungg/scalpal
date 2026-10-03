# Companion Application

Owner: Nathan for the newly assigned companion and realtime lane. No GitHub account mapping is assumed. Follow [Nathan's implementation plan](../../docs/nathan-plan.md).

Browser companion to show the simulated session live, exercise/organ/step and Jarvis context, recording/processing status, feedback, and completed robot replay. SpacetimeDB supplies session state; a separate media path supplies composited headset video. Wallets, receipts, Solana, and monetary completion rewards are removed. This folder is not yet an initialized web application.

Coordinate video source with Stephen, Jarvis context/action outcomes with Matthew, and replay format with Silas. The first video route to test captures the existing Quest mirror window on the Mac and streams it to an authorized browser viewer. It is not already implemented; raw RGB alone would omit the virtual organs/tools. Matthew's Jarvis remains the single voice agent.

See the [current direction](../../docs/current-direction.md), [team plan](../../docs/team-plan.md), and [contracts](../../packages/contracts/README.md). Keep provider/storage credentials outside the browser client.
