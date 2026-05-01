# AdMesh Unity Demo Game

Public Unity sample project for evaluating AdMesh in a multiplayer game scene.

## What this repo is

- a playable Unity demo project with AdMesh placements already wired in
- a public evaluation sample for developers reviewing the AdMesh Unity SDK
- a sanitized project: live SDK keys and live ad unit IDs are not included

## What this repo is not

- not a production-ready game template
- not a grant of rights to third-party assets beyond their original licenses
- not legal advice about how you may reuse third-party content in this repo

## Engine and structure

- Unity `2022.3`
- embedded SDK path: `Packages/com.admesh.sdk`
- scene assets and gameplay content live under `Assets/`

## Running the demo

1. Open the project in Unity.
2. Open the demo scene you want to test.
3. Add your AdMesh SDK key.
4. Add your AdMesh ad unit IDs.
5. Leave test mode enabled until your live setup is approved.

## Network endpoints

The embedded SDK defaults to:

- selector: `https://select.admesh.cloud`
- collector: `https://events.admesh.cloud`

## Credentials and telemetry

- this public repo does not ship live AdMesh credentials
- real ad serving requires manual SDK key and ad unit configuration
- when configured for live use, the demo can contact AdMesh services to request ads and submit event data

## Legal

- repo-level demo content is covered by the top-level `LICENSE`
- the embedded AdMesh SDK is called out separately in `THIRD_PARTY_NOTICES.md`
- Privacy Policy: [admesh.cloud/privacy](https://admesh.cloud/privacy)
- Terms of Service: [admesh.cloud/terms](https://admesh.cloud/terms)

## Support

- Website: [admesh.cloud](https://admesh.cloud)
- Developer Portal: [dev.admesh.cloud](https://dev.admesh.cloud)
- Support: `support@admesh.cloud`
