<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Companion / Stream Deck-friendly Integration

The Companion adapter exposes a small authenticated surface intended for Bitfocus Companion generic HTTP requests, Stream Deck middleware and similar bounded control systems.

Base path:

```text
/rtaime/integration/v1
```

Endpoints:

- `GET /health` — adapter health;
- `GET /state` — current configured feedback projection;
- `GET /actions` — configured action keys and bounded action kinds;
- `GET|POST /actions/{key}` — enqueue a configured trigger only;
- `WebSocket /feedback` — bounded current-state feedback stream.

Every endpoint requires `Authorization: Bearer <token>`. The token is loaded from the environment variable named by `bearerTokenEnvironmentVariable`. Comparisons use fixed-time equality. Request bodies are bounded and action admission returns HTTP 429 when the gateway cannot accept more input.

This surface never exposes RuntimeHost objects or arbitrary command payloads. An HTTP key can invoke only a mapping already present in validated gateway configuration. For remote deployments, place the adapter behind an appropriate trusted network/TLS termination boundary; the upstream IntegrationHost-to-ControlHost connection remains HTTPS using the secure external-control client.
