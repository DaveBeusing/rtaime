# rtaime

Production-grade real-time AI media platform.

This repository is the controlled development source for rtaime V1.

## Bootstrap

The primary managed solution entry point is `rtaime.slnx`.

```powershell
dotnet restore rtaime.slnx
dotnet build rtaime.slnx --configuration Release --no-restore
dotnet test rtaime.slnx --configuration Release --no-build
```

The repository now contains the V1 production-shaped architecture, Operator workflow, local media deck, graphics, audio, recording, governed AI showcase, managed host lifecycle and release/qualification pipeline. Hardware and production-readiness claims remain limited to the evidence explicitly recorded by the qualification system.

## Funding showcase

A verified installed Windows release exposes a one-click showcase entry point at:

`Start-rtaime-Showcase.cmd`

The launcher starts the existing managed ControlHost lifecycle, opens the Operator against the qualified endpoints, and stops only the lifecycle it started when the Operator closes. The deterministic demonstration flow and acceptance boundaries are documented in `docs/InvestorDemoScenario.md`.
