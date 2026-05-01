# Changelog

## 0.2.4

- Added runtime `session_id` creation and aligned selector requests with the public v1 DTL session-serving contract.
- Added lease-aware refresh and bounded cached continuity via `valid_until`, `refresh_after`, and placeholder fallback behavior.
- Standardized proof metadata to include `schedule_id`, `creative_version`, `cache_status`, `render_status`, and `delivery_mode`.
- Clarified public v1 platform scope as Windows and Android, with WebGL deferred.

## 0.2.3

- Reduced public Unity signal collection to coarse renderer-visible timing only.
- Removed public frustum/angle/occlusion-style calculations from the Unity release surface.

## 0.2.2

- Locked the public Unity release scope to image and video placements only.
- Added selector request capability metadata for safer server-side compatibility handling.
- Improved inspector setup by constraining `Ad Format` to supported public options.

## 0.2.1

- Forwarded selector-issued `selection_token` values from fetched creatives into event collector view reports.
- Clarified public release docs around token forwarding and staged strict-mode rollout.
- Added Unity sample package scaffolding for package-manager consumers.

## 0.2.0

- Added sanitized placement runtime, manager, signal reporter, logger, rate limiter, and memory pool.
- Added package metadata via `AdMesh.asmdef` and `StreamingAssets/admesh_config.json.example`.
- Removed all client-side billing logic and excluded internal anti-tamper/security code from the public package.
