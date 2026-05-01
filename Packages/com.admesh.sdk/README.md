# AdMesh Unity SDK

Image and video placements for Unity using AdMesh public v1 scheduled DTL serving.

## What ships

- `Runtime/Core/AdMeshPlugin.cs` - initialization, session creation, selector fetch, and proof-event posting
- `Runtime/Core/AdMeshPlacement.cs` - creative rendering, lease handling, and placeholder fallback
- `Runtime/Components/AdMeshPlacementComponent.cs` - drop-in placement component
- `Runtime/Components/AdMeshPlacementManager.cs` - placement discovery and refresh helper
- `Runtime/Internal/AdMeshSignalReporter.cs` - 5-minute heartbeat batching and final partial flushes
- `Runtime/Internal/AdMeshRateLimiter.cs` - client-side throttling utility
- `Runtime/Internal/AdMeshMemoryPool.cs` - texture reuse helper
- `Runtime/Core/AdMeshLogger.cs` - runtime logging utility
- `Editor/AdMeshPlacementEditor.cs` - inspector UI
- `StreamingAssets/admesh_config.json.example` - optional config template

## Public v1 scope

- image and video placements only
- fixed developer-defined placement surfaces
- scheduled DTL serving only
- selector chooses which creative is shown for an `ad_unit_id`
- selector response includes `schedule_id`, lease window, and signed `selection_token`
- SDK creates a runtime `session_id` on initialize
- SDK posts delivery-start plus 5-minute session heartbeat proof events
- SDK may keep the active creative during short outages only until `valid_until`
- no public VPP runtime
- no remote placement movement
- no client billing logic
- no user-level personalized targeting

## Install

### Unity Package Manager

Add the package from your AdMesh repository URL in `Window > Package Manager > Add package from git URL`.

### Manual import

Copy `Runtime/`, `Editor/`, `StreamingAssets/`, `AdMesh.asmdef`, and `package.json` into a Unity package folder.

## Quick start

Initialize once at game startup:

```csharp
using AdMesh.Core;
using UnityEngine;

public sealed class GameBootstrap : MonoBehaviour
{
    private void Awake()
    {
        AdMeshPlugin.Initialize("YOUR_SDK_KEY");
    }
}
```

Add a placement to any object with a `Renderer` and configure:
- `Ad Unit Id`
- `Ad Format`
- `Use Real Ads`

Use `Use Real Ads = true` only when the app and placement are approved for live serving.

## Serving model

- new apps and placements should stay in test mode by default
- only `live` apps with `live` ad units can receive scheduled production creatives
- selector precedence is:
  1. active admin override for the booked schedule window
  2. active scheduled DTL booking
  3. no fill
- DTL charging and payout stay server-side and are derived from placement schedules, not client-side impressions

## Session and cache behavior

- the SDK generates one runtime `session_id` when initialized
- selector request includes `sdk_key`, `ad_unit_id`, `session_id`, engine/platform/version metadata, and capability hints
- selector response includes:
  - `schedule_id`
  - `override_id` when applicable
  - `valid_until`
  - `refresh_after`
  - `cache_policy = lease_until_valid_then_placeholder`
  - `max_cached_creatives = 3`
- `max_cached_creatives` is an upper-bound hint; the Unity public v1 runtime guarantees continuity for the active leased creative rather than a full offline prefetch queue
- the SDK refreshes on session start and again at `refresh_after`
- if refresh fails but the current lease is still valid, the SDK may keep the current creative as `cache_status = cached_lease`
- once `valid_until` passes, the SDK switches to placeholder content until it reconnects and gets a fresh lease

## Proof events

- `impression` = delivery start for a newly applied creative
- `view` = 5-minute session heartbeat or final partial flush
- proof payload includes:
  - `selection_token`
  - `schedule_id`
  - `session_id`
  - `creative_version`
  - `cache_status`
  - `render_status`
  - `delivery_mode`
  - `override_id`

## Optional config file

Create `StreamingAssets/admesh_config.json` from the example file:

```json
{
  "sdkKey": "YOUR_SDK_KEY",
  "adSelectorUrl": "https://select.admesh.cloud",
  "eventCollectorUrl": "https://events.admesh.cloud"
}
```

Runtime initialization parameters take precedence over the config file.
For v1, direct `AdMeshPlugin.Initialize(...)` is the recommended setup path on Android and WebGL. The automatic `StreamingAssets` config file load is intended for Editor and desktop-style file-system targets.

## Supported public media formats

- Images: standard Unity `Texture2D` download/decode path, recommended `.png` and `.jpg`
- Video: URL-based playback through Unity `VideoPlayer`, recommended H.264 `.mp4`

## Platform scope for public v1

- Windows standalone: supported
- Android: supported with direct `Initialize(...)` setup
- WebGL: defer for public v1 because remote video playback remains browser-restricted and needs separate handling

## Production boundary

- the SDK only posts coarse placement proof signals and lease-aware runtime metadata
- qualification, billability, schedule resolution, rate limiting, and payout logic stay server-side
- the public package excludes client billing logic, threshold constants, anti-tamper internals, and proprietary VPP logic

## Support

- Docs: https://dev.admesh.cloud/docs
- Developer Portal: https://dev.admesh.cloud
- Email: support@admesh.cloud
