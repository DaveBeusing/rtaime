<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

# rtaime Brand Assets

## Purpose

The repository contains one shared visual identity for the Operator application, product documentation and external presentation material.

The canonical product name remains `rtaime`, pronounced "realtime", with the expansion **Real Time AI Media Engine** and the slogan **Production-grade real-time AI media platform.**

## Application resources

The Operator loads:

`src/Hosts/rtaime.Operator/Themes/Brand/RtaimeBrand.xaml`

Available resources include:

- `RtaimeBrandEmblemImage` — primary silver/electric-blue emblem.
- `RtaimeBrandEmblemMonochromeImage` — neutral single-colour emblem.
- `RtaimeBrandAppIconImage` — dark rounded app icon treatment.
- `RtaimeBrandCompactLockupTemplate` — compact product lockup.
- `RtaimeBrandHeroLockupTemplate` — startup/about presentation lockup.
- shared brand colours and gradient brushes.

WPF views should consume these resources through `StaticResource` instead of embedding duplicate path data.

Example:

```xml
<Image Source="{StaticResource RtaimeBrandEmblemImage}"
	Width="32"
	Height="32"
	Stretch="Uniform" />
```

The main Operator window already uses the app icon resource, the emblem in the production top bar and the hero lockup during startup.

## Vector source assets

Reusable SVG files are stored under:

`src/Hosts/rtaime.Operator/Assets/Brand/`

The current set contains:

- `RtaimeLogoHorizontal.svg`
- `RtaimeEmblem.svg`
- `RtaimeEmblemMonochrome.svg`
- `RtaimeAppIcon.svg`

These files are intended for documentation, presentations, web/marketing export, installer artwork and future raster exports.

## UI icon system

Application action and workspace icons remain native WPF geometries in:

`src/Hosts/rtaime.Operator/Themes/Controls/RtaimeIcons.xaml`

The set covers production navigation, media, timeline, compositing, routing, monitoring, performance, health, alerts, settings, account, search, transport, record, upload/download and common editing actions.

Consumers should use the geometry through existing rtaime controls, for example:

```xml
<controls:RtaimeIconButton
	IconData="{StaticResource RtaimeIconMonitoringGeometry}"
	ToolTip="Monitoring" />
```

## Brand palette

| Token | Value | Purpose |
| --- | --- | --- |
| Deep Black | `#05070B` | Primary dark background |
| Graphite | `#1A1F2B` | Raised dark surfaces |
| Silver White | `#E8ECF4` | Primary mark and high-contrast brand text |
| Electric Blue | `#00A3FF` | Brand accent and real-time highlight |
| Deep Blue | `#0059C7` | Gradient depth |

The visual treatment should remain restrained in dense production UI. Strong glow and large-format treatments are reserved for startup, presentation and marketing surfaces.
