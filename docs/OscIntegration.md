<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# OSC Integration

OSC is implemented in `rtaime.IntegrationHost` as an outer UDP adapter. It does not define OSC-specific Production commands.

Configure an `osc` adapter with a literal listen address/port, maximum packet size, optional source-IP allowlist and optional feedback destination. The implemented bounded OSC message subset accepts one message with type tags for int32 (`i`), float32 (`f`), string (`s`) and boolean (`T`/`F`). Bundles and unsupported tags are rejected.

A mapping's `triggerKey` is the OSC address, for example `/rtaime/cut`. Feedback mapping target keys are OSC addresses. Feedback is encoded as a string value to preserve stable, protocol-neutral state semantics.

Use source allowlists on production networks, bind only required interfaces and retain secure gRPC/TLS authentication between IntegrationHost and ControlHost. OSC itself is not treated as an authority or trusted identity boundary.
