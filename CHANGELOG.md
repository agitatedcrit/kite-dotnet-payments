# Changes

## 0.2.0 — 2026-10-28

The JSON proxy middleware now accepts an explicit PaidPath and Method in KiteOptions. Requests for other routes continue through the ASP.NET app. The example exposes PAID_PATH and PAID_METHOD. Keep the sample URL only when intentionally using the sample. Each middleware instance protects one configured JSON upstream; it does not implement general stream/media billing or claim SIWX support. Use the operator-authenticated recovery endpoint for this release.

This evaluation update retains explicit blocked real-payment, public-hosting and official-listing gates. Prior releases remain available.
