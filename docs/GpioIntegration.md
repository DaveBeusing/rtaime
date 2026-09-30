<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# GPIO / GPI Integration Boundary

Discrete control is represented by the provider-neutral `IDiscreteIoBackend` seam. The currently implemented and qualified backend is `VirtualDiscreteIoBackend`.

Input trigger keys use `input:<channel-id>`. Per-channel active-low input polarity is applied before a trigger enters the gateway. Feedback target keys are stable output channel identities; active-low output polarity is applied after rtaime feedback is resolved.

The virtual provider supplies deterministic input injection and observable output/tally state for CI. It proves the gateway semantics, debounce/polarity behavior and hardware-provider seam; it does **not** qualify any physical GPIO/GPI interface. Future hardware providers must implement the same backend boundary and earn their own device/driver qualification evidence.
