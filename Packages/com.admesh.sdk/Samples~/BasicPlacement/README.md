# Unity Sample: Basic Placement

This sample shows the minimum runtime wiring for the public Unity SDK.

## Included files

- `GameBootstrap.cs` initializes the SDK once at startup.

## Scene setup

1. Create an empty GameObject named `AdMeshBootstrap`.
2. Add `GameBootstrap` to it.
3. Add a plane or quad with a `Renderer`.
4. Add `AdMeshPlacementComponent` to that object.
5. Set:
   - `Ad Unit Id`
   - `Use Real Ads = true` only after the app and placement are approved for live serving
   - optional fallback texture

## What to verify

- the placement loads the currently scheduled DTL creative successfully
- the inspector only offers supported public formats (`Image`, `Video`)
- the SDK generates one runtime `session_id` and includes it in selector/proof traffic
- proof events use the selector-issued `selection_token` and include `schedule_id`
- 5-minute heartbeat reporting runs while the placement is active
- lease expiry swaps back to fallback content instead of leaving stale paid content on-screen indefinitely
- the game continues normally if selector or collector requests fail
