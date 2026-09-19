# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) { throw $Message }
}

$appPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/App.xaml"
$appCodePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/App.xaml.cs"
$windowPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MainWindow.xaml"
$windowCodePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MainWindow.xaml.cs"
$shellPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorShellViewModel.cs"
$keyboardPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorKeyboardCommandRegistry.cs"
$quickControlsPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorQuickControlsViewModel.cs"
$multiviewPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorMultiviewControl.xaml"
$multiviewCodePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorMultiviewControl.xaml.cs"
$mediaPoolPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaPoolInspectorViewModel.cs"
$deckPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaDeckControl.xaml"
$deckViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaDeckViewModel.cs"
$timelinePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaTimelineControl.xaml"
$timelineCodePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaTimelineControl.xaml.cs"
$timelineViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaTimelineViewModel.cs"
$markerControllerPath = Join-Path $repositoryRoot "src/Client/rtaime.Client/MediaTimelineMarkerController.cs"
$timelineDocumentationPath = Join-Path $repositoryRoot "docs/LayeredTimeline.md"
$viewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorViewModel.cs"
$monitorViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorMonitoringViewModel.cs"
$programOutputControllerPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ProgramOutputController.cs"
$programOutputWindowPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ProgramOutputWindow.xaml"
$previewViewerPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/PreviewViewer.xaml"
$previewViewerCodePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/PreviewViewer.xaml.cs"
$programViewerPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ProgramViewer.xaml"
$programViewerCodePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ProgramViewer.xaml.cs"
$sourceTileViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorSourceTileViewModel.cs"
$audioInputViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorAudioInputViewModel.cs"
$graphicsLoaderPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/GraphicsOverlayAssetLoader.cs"
$demoControllerPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/DemoProductionPackageController.cs"
$demoManifestPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/DemoAssets/demo-production.package.json"
$demoProductPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/DemoAssets/ProductClip.mp4.b64"
$demoGraphicsPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/DemoAssets/LowerThird.png.b64"
$demoDocumentationPath = Join-Path $repositoryRoot "docs/DemoProductionPackage.md"
$tokensPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Themes/OperatorTokens.xaml"
$themePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Themes/OperatorTheme.xaml"
$manifestPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/app.manifest"
$projectPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/rtaime.Operator.csproj"
$documentationPath = Join-Path $repositoryRoot "docs/OperatorUiV1.md"
$workspaceDocumentationPath = Join-Path $repositoryRoot "docs/OperatorWorkspaces.md"

foreach ($path in @($appPath, $appCodePath, $windowPath, $windowCodePath, $shellPath, $keyboardPath, $quickControlsPath, $multiviewPath, $multiviewCodePath, $mediaPoolPath, $deckPath, $deckViewModelPath, $timelinePath, $timelineCodePath, $timelineViewModelPath, $markerControllerPath, $timelineDocumentationPath, $viewModelPath, $monitorViewModelPath, $programOutputControllerPath, $programOutputWindowPath, $previewViewerPath, $previewViewerCodePath, $programViewerPath, $programViewerCodePath, $sourceTileViewModelPath, $audioInputViewModelPath, $graphicsLoaderPath, $demoControllerPath, $demoManifestPath, $demoProductPath, $demoGraphicsPath, $demoDocumentationPath, $tokensPath, $themePath, $manifestPath, $projectPath, $documentationPath, $workspaceDocumentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required Operator UI artifact is missing: '$path'."
}

$app = Get-Content -LiteralPath $appPath -Raw
$appCode = Get-Content -LiteralPath $appCodePath -Raw
$window = Get-Content -LiteralPath $windowPath -Raw
$windowCode = Get-Content -LiteralPath $windowCodePath -Raw
$shell = Get-Content -LiteralPath $shellPath -Raw
$keyboard = Get-Content -LiteralPath $keyboardPath -Raw
$quickControls = Get-Content -LiteralPath $quickControlsPath -Raw
$multiview = Get-Content -LiteralPath $multiviewPath -Raw
$multiviewCode = Get-Content -LiteralPath $multiviewCodePath -Raw
$mediaPool = Get-Content -LiteralPath $mediaPoolPath -Raw
$deck = Get-Content -LiteralPath $deckPath -Raw
$deckViewModel = Get-Content -LiteralPath $deckViewModelPath -Raw
$timeline = Get-Content -LiteralPath $timelinePath -Raw
$timelineCode = Get-Content -LiteralPath $timelineCodePath -Raw
$timelineViewModel = Get-Content -LiteralPath $timelineViewModelPath -Raw
$markerController = Get-Content -LiteralPath $markerControllerPath -Raw
$timelineDocumentation = Get-Content -LiteralPath $timelineDocumentationPath -Raw
$viewModel = Get-Content -LiteralPath $viewModelPath -Raw
$monitorViewModel = Get-Content -LiteralPath $monitorViewModelPath -Raw
$programOutputController = Get-Content -LiteralPath $programOutputControllerPath -Raw
$programOutputWindow = Get-Content -LiteralPath $programOutputWindowPath -Raw
$previewViewer = Get-Content -LiteralPath $previewViewerPath -Raw
$previewViewerCode = Get-Content -LiteralPath $previewViewerCodePath -Raw
$programViewer = Get-Content -LiteralPath $programViewerPath -Raw
$programViewerCode = Get-Content -LiteralPath $programViewerCodePath -Raw
$sourceTileViewModel = Get-Content -LiteralPath $sourceTileViewModelPath -Raw
$audioInputViewModel = Get-Content -LiteralPath $audioInputViewModelPath -Raw
$graphicsLoader = Get-Content -LiteralPath $graphicsLoaderPath -Raw
$demoController = Get-Content -LiteralPath $demoControllerPath -Raw
$demoManifest = Get-Content -LiteralPath $demoManifestPath -Raw
$demoDocumentation = Get-Content -LiteralPath $demoDocumentationPath -Raw
$tokens = Get-Content -LiteralPath $tokensPath -Raw
$theme = Get-Content -LiteralPath $themePath -Raw
$manifest = Get-Content -LiteralPath $manifestPath -Raw
$project = Get-Content -LiteralPath $projectPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$workspaceDocumentation = Get-Content -LiteralPath $workspaceDocumentationPath -Raw

Assert-Condition ($app -match 'Source="Themes/OperatorTheme\.xaml"') "Operator must load the reusable theme resource dictionary."
Assert-Condition ($theme -match 'Source="OperatorTokens\.xaml"') "Operator theme must load the shared design-token dictionary."
foreach ($token in @("OperatorFontFamily", "OperatorWindowPadding", "OperatorControlHeight", "OperatorColorPreview", "OperatorColorProgram", "OperatorColorHealthy", "OperatorColorWarning", "OperatorColorError")) {
	Assert-Condition ($tokens -match [Regex]::Escape($token)) "Operator design token '$token' is required."
}
foreach ($resource in @("OperatorPreviewBrush", "OperatorProgramBrush", "OperatorArmedBrush", "OperatorHealthyBrush", "OperatorWarningBrush", "OperatorErrorBrush", "OperatorEvidenceBadge", "OperatorFocusVisual", "OperatorToolbar", "OperatorToggleButton", "OperatorSourceItem", "OperatorMeter", "OperatorTimelineSlider", "OperatorPreviewTally", "OperatorProgramTally", "OperatorTopBar", "OperatorShellRegion", "OperatorTransportBar")) {
	Assert-Condition ($theme -match [Regex]::Escape($resource)) "Operator theme resource '$resource' is required."
}

Assert-Condition ($manifest -match 'PerMonitorV2,PerMonitor') "Operator must declare PerMonitorV2 DPI awareness with PerMonitor fallback."
Assert-Condition ($project -match '<ApplicationManifest>app\.manifest</ApplicationManifest>') "Operator project must bind the DPI-awareness manifest."
Assert-Condition ($project -notmatch '<UseWindowsForms>true</UseWindowsForms>') "Program Output / Clean Feed must keep the Operator WPF-only rather than mixing WinForms into the presentation host."
Assert-Condition ($window -match 'UseLayoutRounding="True"') "Operator window must use layout rounding."
Assert-Condition ($window -match 'SnapsToDevicePixels="True"') "Operator window must snap layout edges to device pixels."
Assert-Condition ($window -match 'TextOptions\.TextFormattingMode="Display"') "Operator must use display text formatting for production-console legibility."
Assert-Condition ($window -match 'FontFamily="\{StaticResource OperatorFontFamily\}"') "Operator typography must use the shared font-family token."

$width = [int]([Regex]::Match($window, 'Width="(?<value>\d+)"').Groups["value"].Value)
$height = [int]([Regex]::Match($window, 'Height="(?<value>\d+)"').Groups["value"].Value)
$minWidth = [int]([Regex]::Match($window, 'MinWidth="(?<value>\d+)"').Groups["value"].Value)
$minHeight = [int]([Regex]::Match($window, 'MinHeight="(?<value>\d+)"').Groups["value"].Value)
Assert-Condition ($width -le 1920 -and $height -le 1080) "Operator reference window must fit within the 1920x1080 qualification surface."
Assert-Condition ($minWidth -le [Math]::Floor(1920 / 1.5)) "Operator minimum width must remain usable at 150% scaling on 1920x1080."
Assert-Condition ($minHeight -le [Math]::Floor(1080 / 1.5)) "Operator minimum height must remain usable at 150% scaling on 1920x1080."
Assert-Condition ($window -match '<ScrollViewer[^>]+VerticalScrollBarVisibility="Auto"') "Operator must preserve vertical access when DPI scaling reduces logical workspace height."

Assert-Condition ($window -match 'ItemsSource="\{Binding Sources\}"') "Operator must expose the source bank as a bound collection."
Assert-Condition ($window -match 'Style="\{StaticResource OperatorSourceBank\}"') "Source bank must use the shared design-system collection style."
Assert-Condition ($window -match 'ItemContainerStyle="\{StaticResource OperatorSourceItem\}"') "Source tiles must use the shared tile style."
Assert-Condition ($window -match 'Text="SOURCE BIN"') "Operator must present the source collection as a production source bin."
Assert-Condition ($window -match 'Binding Thumbnail') "Source tiles must render live monitoring thumbnails."
Assert-Condition ($window -match 'Binding Type') "Source tiles must expose LIVE/MEDIA source type."
Assert-Condition ($window -match 'Binding Format') "Source tiles must expose source format."
Assert-Condition ($window -match 'Binding Health') "Source tiles must expose source health."
Assert-Condition ($window -match 'Binding IsPreview') "Source tiles must expose Preview tally state."
Assert-Condition ($window -match 'Binding IsProgram') "Source tiles must expose Program tally state."
Assert-Condition ($window -match 'Binding StateDetail') "Source tiles must expose media state and remaining time."
Assert-Condition ($sourceTileViewModel -match 'ApplyRouting') "Source tile presentation must derive PGM/PVW state from authoritative routing."
Assert-Condition ($sourceTileViewModel -match 'ApplyMediaDeck') "Source tile presentation must project Media Deck state without taking media authority."
Assert-Condition ($monitorViewModel -match 'ApplySourceThumbnail') "Source thumbnails must derive from the independent monitoring plane."
Assert-Condition ($window -match '<local:PreviewViewer' -and $window -match '<local:ProgramViewer') "Operator must compose dedicated reusable Preview and Program viewers."
Assert-Condition ($previewViewer -match 'OperatorPreviewPanel') "Preview viewer must use Preview semantic styling."
Assert-Condition ($programViewer -match 'OperatorProgramPanel') "Program viewer must use Program semantic styling."
Assert-Condition ($previewViewer -match 'OperatorPreviewTally') "Preview tally semantics must be explicit."
Assert-Condition ($programViewer -match 'OperatorProgramTally') "Program tally semantics must be explicit."
Assert-Condition ($window -match 'Monitoring\.PreviewImage') "Operator Preview must bind the independent monitoring image."
Assert-Condition ($window -match 'Monitoring\.ProgramImage') "Operator Program must bind the independent monitoring image."
Assert-Condition ($previewViewer -match '<Image\s' -and $programViewer -match '<Image\s') "Preview and Program viewers must render monitoring with WPF Image surfaces."
Assert-Condition ($previewViewer -match 'Stretch="Uniform"' -and $programViewer -match 'Stretch="Uniform"') "Production viewers must preserve aspect ratio without stretching."
Assert-Condition (($window + $previewViewer + $programViewer) -notmatch 'MediaElement|VideoDrawing') "Operator monitoring must use the qualified bounded bitmap path, not an ungoverned media player."
Assert-Condition ($window -match 'Text="CLEAN PROGRAM MONITOR"') "Clean Program monitoring controls must remain visible in the Operator SYSTEM workspace."
Assert-Condition ($window -match 'ProgramOutput\.Displays') "Program Output / Clean Feed must expose display selection."
Assert-Condition ($window -match 'ProgramOutput\.StartCommand') "Program Output / Clean Feed must expose controlled Program Output start."
Assert-Condition ($window -match 'ProgramOutput\.StopCommand') "Program Output / Clean Feed must expose controlled Program Output stop."
Assert-Condition ($window -match 'ProgramOutput\.ToggleFullscreenCommand') "Program Output / Clean Feed must expose fullscreen/windowed control."
Assert-Condition ($window -match 'ProgramOutput\.Health') "Program Output / Clean Feed must expose visible output health."
Assert-Condition ($programOutputWindow -match 'Source="\{Binding ProgramImage\}"') "Clean Feed must present the same Runtime-derived ProgramImage used by the Operator monitoring surface."
Assert-Condition ($programOutputWindow -match 'Stretch="Uniform"') "Clean Feed must preserve Program aspect ratio."
Assert-Condition ($programOutputWindow -notmatch '<Button|<ComboBox|<TextBox|MediaElement|VideoDrawing') "Clean Feed must contain no Operator controls or independent media player."
Assert-Condition ($programOutputController -match 'OperatorMonitoringViewModel') "Clean Feed controller must consume the existing Operator monitoring projection rather than own Program rendering."
Assert-Condition ($programOutputController -match 'EnumDisplayMonitors' -and $programOutputController -match 'GetMonitorInfo') "Program Output / Clean Feed must enumerate selectable Windows displays through the native monitor API."
Assert-Condition ($programOutputController -match 'SystemEvents\.DisplaySettingsChanged') "Program Output / Clean Feed must react to display topology changes."
Assert-Condition ($programOutputController -match 'WindowStyle\.None' -and $programOutputController -match 'WindowStyle\.SingleBorderWindow') "Program Output / Clean Feed must support fullscreen and defined windowed fallback."
Assert-Condition ($programOutputController -match 'SetWindowPos') "Program Output / Clean Feed display placement must target the selected physical display."
Assert-Condition ($programOutputController -match '_fallbackActive' -and $programOutputController -match 'Selected display was removed') "Program Output / Clean Feed must surface controlled display-disconnect fallback."
Assert-Condition ($programOutputController -notmatch 'using rtaime\\.(ControlHost|RuntimeHost)|MediaElement|VideoDrawing') "Program Output presentation must not bypass the Client/monitoring boundary or create a second renderer."
Assert-Condition ($timeline -match 'Text="LAYERED TIMELINE"' -and $timeline -match 'ItemsSource="\{Binding Tracks\}"') "Timeline must render a first-class layered production workspace."
foreach ($trackCategory in @("Video", "Graphics", "Overlay", "Audio", "AI", "Control", "Cue")) {
	Assert-Condition ($timelineViewModel -match [Regex]::Escape("TimelineTrackCategory.$trackCategory")) "Layered timeline semantic track '$trackCategory' is required."
}
Assert-Condition ($timeline -match 'ItemsSource="\{Binding RulerTicks\}"' -and $timelineViewModel -match 'TimelineRulerTickViewModel') "Timeline must expose a frame-derived time ruler."
Assert-Condition ($timeline -match 'Binding DisplayFrame' -and $timeline -match 'OperatorProgramBrush') "Timeline playhead must remain visually explicit."
Assert-Condition ($timeline -match 'Binding ScrollValue' -and $timeline -match 'Binding ZoomInCommand' -and $timeline -match 'Binding ZoomOutCommand' -and $timeline -match 'Binding FitCommand') "Timeline must expose horizontal scrolling, zoom and fit controls."
Assert-Condition ($timelineViewModel -match 'MediaTimelineVisibleRange' -and $timelineViewModel -match 'FrameFromVisiblePosition' -and $timelineViewModel -match 'SnapFrame') "Timeline viewport and snapping must remain frame-based."
Assert-Condition ($timeline -match 'Binding HasInPoint' -and $timeline -match 'Binding HasOutPoint' -and $timeline -match 'Tag="IN"' -and $timeline -match 'Tag="OUT"') "Timeline must present IN/OUT markers as bounded trim handles."
Assert-Condition ($timelineCode -match 'TrimInAsync' -and $timelineCode -match 'TrimOutAsync' -and $timelineViewModel -match '_markers\.SetInAtFrameAsync' -and $timelineViewModel -match '_markers\.SetOutAtFrameAsync') "Timeline trim handles must cross explicit marker command paths."
Assert-Condition ($timeline -match 'PreviousCueCommand' -and $timeline -match 'NextCueCommand' -and $timelineCode -match 'Key\.PageUp' -and $timelineCode -match 'Key\.PageDown') "Cue navigation must be visible and keyboard-accessible."
Assert-Condition ($timeline -match 'MediaDeck\.CueName' -and $timeline -match 'MediaDeck\.AddCueCommand') "Timeline must expose named cue creation through the existing Media Deck marker command path."
Assert-Condition ($markerController -match 'JumpToPreviousCueAsync' -and $markerController -match 'JumpToNextCueAsync' -and $markerController -match '_timeline\.SeekToFrameAsync') "Cue navigation must seek through the existing timeline controller."
Assert-Condition ($timelineViewModel -match '_projectionInitialized && hash == _projectionHash') "Playback updates must not rebuild stable timeline track/cue projection objects per refresh."
Assert-Condition ($deckViewModel -match 'PeriodicTimer\(TimeSpan\.FromMilliseconds\(100\)\)') "Timeline/media observation cadence must remain bounded rather than frame-driven."
Assert-Condition ($timelineCode -match 'catch \(OperationCanceledException\)') "Timeline pointer and trim interaction must treat lifecycle/IPC cancellation as non-fatal."
Assert-Condition ($demoController -match 'catch \(OperationCanceledException\)') "Demo Production startup must surface IPC cancellation without crashing Operator."
Assert-Condition ($viewModel -match 'internal sealed class AsyncRelayCommand[\s\S]+catch \(OperationCanceledException\)') "Async Operator commands must not let cancellation escape async-void ICommand execution."
Assert-Condition ($deck -match 'OperatorStatusBadge') "Media deck state must use shared status presentation."
Assert-Condition ($deck -match 'AUTO PLAY ON PROGRAM') "Media Autoplay & End Behavior must expose Auto Play on Program."
Assert-Condition ($deck -match 'Binding EndBehaviors') "Media Autoplay & End Behavior must expose deterministic media-deck end behavior selection."
Assert-Condition ($deck -match 'Binding EndBehavior') "Media Autoplay & End Behavior end behavior selection must bind confirmed presentation state."
Assert-Condition ($deck -match 'Binding Countdown') "Media Autoplay & End Behavior must expose a visible clip countdown."
Assert-Condition ($deck -match 'Binding ProgramDeckState') "Media Autoplay & End Behavior must expose whether the loaded media slot is on Program."
Assert-Condition ($deck -match 'Binding EffectiveRange') "Media Autoplay & End Behavior must expose the effective IN/OUT playback range."
Assert-Condition ($deck -match 'Binding ApplyPlaybackPolicyCommand') "Media Autoplay & End Behavior playback policy changes must be explicit operator actions."
Assert-Condition ($deckViewModel -match 'ConfigurePlaybackAsync\(AutoPlayOnProgram, EndBehavior') "Media Autoplay & End Behavior policy changes must cross the Client SDK deck controller."
Assert-Condition ($deckViewModel -match 'if \(!IsLoaded \|\| IsBusy\)') "Loaded media decks must keep observing Runtime-triggered autoplay state."
Assert-Condition ($deckViewModel -match 'EffectiveRemainingFrames') "Deck countdown must use effective IN/OUT remaining frames."
Assert-Condition ($deck -notmatch 'Auto Pause') "Media Autoplay & End Behavior must not imply unspecified auto-pause-on-remove semantics."
Assert-Condition ($viewModel -match 'EffectiveRemainingFrames') "Source-tile remaining time must use the effective media range."
Assert-Condition ($deck -match 'Remaining') "Media deck must retain remaining-time presentation."
Assert-Condition ($monitorViewModel -notmatch 'ConfigurePlayback') "Monitoring must remain independent from media playback policy."

Assert-Condition ($window -notmatch '<Window.InputBindings>') "Window-level shortcuts must be centralized rather than duplicated in XAML."
Assert-Condition ($keyboard -match 'new\("sync".+Key\.F5' -and $keyboard -match 'new\("play-pause".+Key\.Space') "Central keyboard registry must expose synchronization and media Play/Pause."
Assert-Condition ($keyboard -match 'new\("auto".+Key\.Enter, ModifierKeys\.None, @operator\.DissolveCommand' -and $keyboard -match 'new\("cut".+Key\.Enter, ModifierKeys\.Control, @operator\.CutCommand') "Central keyboard registry must expose Enter=AUTO and Ctrl+Enter=CUT."
Assert-Condition ($keyboard -match 'FindConflicts' -and $keyboard -match 'Operator keyboard shortcut conflict') "Central keyboard registry must reject conflicting bindings."
Assert-Condition ($keyboard -match 'IsTextEntryContext' -and $keyboard -match 'TextBoxBase' -and $keyboard -match 'PasswordBox') "Production shortcuts must be suppressed while the operator is typing."
Assert-Condition ($window -match 'KeyboardNavigation.TabNavigation="Cycle"') "Showcase keyboard navigation must stay inside the Operator workspace."
Assert-Condition ($window -match 'x:Name="SynchronizeButton"' -and $windowCode -match 'SynchronizeButton\.Focus\(\)') "Initial keyboard focus must land on the synchronization action."
Assert-Condition ($window -match 'ToolTip="Prepare the deterministic showcase state' -and $window -match 'ToolTip="CUT the confirmed Preview source to Program') "Primary showcase actions must expose consistent explanatory tooltips."
Assert-Condition ($theme -match '<Style TargetType="ToolTip">' -and $theme -match 'ToolTipService\.InitialShowDelay') "Showcase tooltips must use the shared theme and bounded presentation timing."
Assert-Condition ($window -match 'No production sources available' -and $window -match 'Binding Sources\.Count') "Source Bin must expose an explicit empty state instead of a blank panel."
Assert-Condition ($window -match 'Text="\{Binding LastEvent\}"' -and $window -match 'OperatorErrorBadge' -and $window -match 'Binding LastError') "Footer must separate normal operator status from active error presentation."
Assert-Condition ($windowCode -match 'Closing \+= OnClosingAsync' -and $windowCode -match '_shutdownComplete' -and $windowCode -match 'await Monitoring\.DisposeAsync') "Operator shutdown must await owned monitoring and view-model resources before final close."
Assert-Condition ($appCode -match 'DispatcherUnhandledException \+=' -and $appCode -match 'TryWriteCrashReport' -and $appCode -match 'LocalApplicationData') "Unexpected UI failures must produce controlled user-visible presentation and a local diagnostic report."
Assert-Condition ($theme -match 'IsKeyboardFocused') "Primary controls must expose visible keyboard-focus state."
Assert-Condition ($theme -match 'IsKeyboardFocusWithin') "Selectable source tiles must expose visible keyboard-focus state."
Assert-Condition ($window -notmatch 'Width="1100"\s*\r?\n\s*Height="680"') "Operator must not retain the fixed bootstrap 1100x680 layout."

foreach ($propertyName in @("IsBusy", "IsConnected", "IsStale", "ConnectionState", "CommandStatus", "CommitStatus", "TransitionStatus", "LastEvent")) {
	Assert-Condition ($viewModel -match "public\s+[^\r\n]+\s+$propertyName\b") "Operator presentation state '$propertyName' is required."
}
Assert-Condition ($viewModel -match 'RemoteHostSessionChangedException') "Operator must retain explicit ControlHost-session resynchronization handling."
Assert-Condition ($viewModel -match 'CanControl\(\)') "Operator mutations must be guarded by shared presentation readiness."
Assert-Condition ($viewModel -match 'CanSetPreview\(\)') "Preview selection must have an explicit readiness predicate."
Assert-Condition ($viewModel -match 'CanTakePreview\(\)') "Program TAKE must have an explicit confirmed-Preview readiness predicate."
Assert-Condition ($viewModel -match 'CutPreviewAsync\(\)') "CUT must take the confirmed authoritative Preview source rather than the local UI selection."
Assert-Condition ($viewModel -match 'DissolvePreviewAsync\(durationFrames\)') "AUTO/DISSOLVE must take the confirmed authoritative Preview source."
Assert-Condition ($viewModel -notmatch 'CutAsync\(source\.Id\)') "Operator CUT must not route the local selected-source id directly to Program."
Assert-Condition ($viewModel -notmatch 'DissolveAsync\(source\.Id') "Operator DISSOLVE must not route the local selected-source id directly to Program."
Assert-Condition ($viewModel -match 'if \(IsBusy\) return;') "Operator command execution must reject rapid re-entry while an authoritative command is in flight."
Assert-Condition ($window -match 'Text="GRAPHICS / OVERLAY"') "Operator must expose the Graphics & Overlay Operator Workflow graphics workflow."
Assert-Condition ($window -match 'Binding LoadGraphicsCommand') "Graphics workflow must expose PNG asset loading."
Assert-Condition ($window -match 'Binding ApplyGraphicsCommand') "Graphics workflow must expose explicit placement apply."
Assert-Condition ($window -match 'Binding ToggleGraphicsCommand') "Graphics workflow must expose confirmed show/hide control."
Assert-Condition ($window -match 'Binding GraphicsPositionX') "Graphics workflow must expose X placement."
Assert-Condition ($window -match 'Binding GraphicsPositionY') "Graphics workflow must expose Y placement."
Assert-Condition ($window -match 'Binding GraphicsScale') "Graphics workflow must expose scale."
Assert-Condition ($viewModel -match 'LoadGraphicsOverlayAsync') "Operator graphics load must cross the Client SDK seam."
Assert-Condition ($viewModel -match 'SetGraphicsOverlayAsync') "Operator graphics state changes must cross the Client SDK seam."
Assert-Condition ($viewModel -match 'ClearGraphicsOverlayAsync') "Operator graphics clear must cross the Client SDK seam."
Assert-Condition ($graphicsLoader -match 'PngBitmapDecoder') "Graphics asset loader must use the bounded PNG decode path."
Assert-Condition ($graphicsLoader -match 'PixelFormats\.Bgra32') "Graphics asset loader must normalize PNG pixels before RGBA conversion."
Assert-Condition ($graphicsLoader -match 'rgba\[offset\] = bgra\[offset \+ 2\]') "Graphics asset loader must preserve RGBA channel order for RuntimeHost."
Assert-Condition ($window -match 'Text="AUDIO / AFV"') "Operator must expose the Audio Operator Workflow audio/AFV workflow."
Assert-Condition ($window -match 'ItemsSource="\{Binding AudioInputs\}"') "Audio workflow must expose Runtime-observed inputs."
Assert-Condition ($window -match 'Binding AudioLeftPeak') "Audio workflow must expose left Program meter."
Assert-Condition ($window -match 'Binding AudioRightPeak') "Audio workflow must expose right Program meter."
Assert-Condition ($window -match 'Binding AudioMasterPeak') "Audio workflow must expose master Program meter."
foreach ($readOnlyMeter in @("AudioLeftPeak", "AudioRightPeak", "AudioMasterPeak", "LeftPeak", "RightPeak")) {
	$pattern = 'Value="\{Binding ' + [Regex]::Escape($readOnlyMeter) + ', Mode=OneWay\}"'
	Assert-Condition ($window -match $pattern) "Read-only audio meter '$readOnlyMeter' must bind OneWay to avoid WPF source-write failures."
}
Assert-Condition ($window -match 'Binding AudioAfvSourceName') "Audio workflow must identify the Program-followed AFV source."
Assert-Condition ($window -match 'Binding AudioHealth') "Audio workflow must expose audio health/clipping state."
Assert-Condition ($window -match 'Binding ClipAudioStatus') "Audio workflow must expose local clip audio metadata/state."
Assert-Condition ($window -match 'Binding ApplyAudioGainCommand') "Audio workflow must expose gain control."
Assert-Condition ($window -match 'Binding ToggleAudioMuteCommand') "Audio workflow must expose mute control."
Assert-Condition ($viewModel -match 'SetAudioInputStateAsync') "Audio mutations must cross the Client SDK seam."
Assert-Condition ($viewModel -match 'PeriodicTimer\(TimeSpan\.FromMilliseconds\(200\)\)') "Audio meters must poll confirmed Runtime observations on a bounded management cadence."
Assert-Condition ($viewModel -notmatch 'DispatcherTimer|DoubleAnimation') "Audio meters must not be locally animated or synthesized by WPF."
Assert-Condition ($audioInputViewModel -match 'OperatorAudioInputDescriptor') "Audio input presentation must project Client SDK descriptors."
Assert-Condition ($audioInputViewModel -match 'LeftPeak' -and $audioInputViewModel -match 'RightPeak') "Audio input presentation must retain stereo meter values."
Assert-Condition ($window -match 'Text="PROGRAM RECORDING"') "Program Recording Workflow must expose a dedicated Program recording workflow."
Assert-Condition ($window -match 'Binding RecordingStatus') "Program Recording Workflow must expose confirmed REC state."
Assert-Condition ($window -match 'Binding RecordingElapsed') "Program Recording Workflow must expose recording elapsed time."
Assert-Condition ($window -match 'Binding RecordingDestination') "Program Recording Workflow must expose recording destination."
Assert-Condition ($window -match 'Binding RecordingFileName') "Program Recording Workflow must expose file naming."
Assert-Condition ($window -match 'Binding RecordingFinalPath') "Program Recording Workflow must expose the finalized recording path."
Assert-Condition ($window -match 'Binding RecordingError') "Program Recording Workflow must expose recording failure state."
Assert-Condition ($window -match 'Binding StartRecordingCommand') "Program Recording Workflow must expose explicit recording start."
Assert-Condition ($window -match 'Binding StopRecordingCommand') "Program Recording Workflow must expose explicit recording stop."
Assert-Condition ($viewModel -match '_client\.StartRecordingAsync') "Recording start must cross the Client SDK seam."
Assert-Condition ($viewModel -match '_client\.StopRecordingAsync') "Recording stop must cross the Client SDK seam."
Assert-Condition ($viewModel -match 'ApplyRecording\(snapshot\.Recording') "Recording status must project confirmed Runtime observations."
Assert-Condition ($viewModel -notmatch 'ProgramRecorder|ReferenceRecordingPayloadWriter') "Operator must not own recording execution or storage writers."
Assert-Condition ($window -match 'Text="AI / PERSON SEGMENTATION"') "Visible AI Showcase must expose one visible AI showcase panel."
Assert-Condition ($window -match 'Content="AI ON"' -and $window -match 'Binding EnableAIShowcaseCommand') "Visible AI Showcase must expose explicit AI enable control."
Assert-Condition ($window -match 'Content="AI OFF"' -and $window -match 'Binding DisableAIShowcaseCommand') "Visible AI Showcase must expose explicit AI disable control."
foreach ($binding in @("AIProvider", "AIStatus", "AIInferenceTime", "AIPersonRegions", "AIConfidence", "AISynchronization")) {
	Assert-Condition ($window -match "Binding $binding") "Visible AI Showcase Operator binding '$binding' is required."
}
Assert-Condition ($viewModel -match '_client\.SetAIShowcaseEnabledAsync') "Visible AI Showcase AI enable/disable must cross the Client SDK seam."
Assert-Condition ($viewModel -match 'ApplyAI\(snapshot\.AIShowcase\)') "Visible AI Showcase observations must project confirmed Runtime/AIHost state."
Assert-Condition ($viewModel -notmatch 'ManagedReferencePersonSegmentationProvider|GovernedInferenceRuntime|AIHostService|ai\.inference\.execute') "Operator must not own inference or bypass the Client SDK."
$aiShowcasePollCount = [Regex]::Matches($viewModel, 'PeriodicTimer\(TimeSpan\.FromMilliseconds\(200\)\)').Count
Assert-Condition ($aiShowcasePollCount -eq 1) "Visible AI Showcase must reuse the existing bounded management poll rather than add an AI UI polling loop."
Assert-Condition ($window -match 'Text="SYSTEM STATUS"') "Operator must expose a compact System Status surface that includes Runtime health/performance evidence."
foreach ($binding in @("EngineHealth", "ControlHealth", "RuntimeHealth", "MediaHealth", "ProviderHealth", "GpuProviderHealth", "CurrentFormat", "FrameTime", "DroppedFrames", "Uptime", "GpuUtilization", "Vram")) {
	Assert-Condition ($window -match "Binding $binding") "Runtime Health & Performance HUD HUD binding '$binding' is required."
}
Assert-Condition ($theme -match 'Trigger Property="Tag" Value="PASS"' -and $theme -match 'OperatorHealthyBrush') "PASS evidence must use the healthy semantic."
Assert-Condition ($theme -match 'Trigger Property="Tag" Value="FAIL"' -and $theme -match 'OperatorErrorBrush') "FAIL evidence must use the error semantic."
Assert-Condition ($theme -match 'Trigger Property="Tag" Value="UNVERIFIED"' -and $theme -match 'OperatorWarningBrush') "UNVERIFIED evidence must remain visually distinct from healthy PASS."
Assert-Condition ($viewModel -match 'ApplyHealth\(snapshot\.Health\)') "Runtime Health & Performance HUD HUD must project confirmed Client health snapshots."
$managementPollCount = [Regex]::Matches($viewModel, 'PeriodicTimer\(TimeSpan\.FromMilliseconds\(200\)\)').Count
Assert-Condition ($managementPollCount -eq 1) "Runtime Health & Performance HUD must reuse the single bounded 200 ms management poll rather than add a new UI telemetry loop."
Assert-Condition ($viewModel -notmatch 'PerformanceCounter|ManagementObjectSearcher|nvidia-smi|NVML') "Operator must not synthesize GPU telemetry locally."
Assert-Condition ($programViewer -match 'Text="COMMIT"') "Program viewer must expose authoritative commit status."
Assert-Condition ($window -match 'Binding CommitStatus') "Program workspace must bind authoritative commit status."
Assert-Condition ($window -match 'Binding TransitionStatus') "Program workspace must expose transition state."
Assert-Condition ($window -match 'CONFIRMED PREVIEW / NEXT TAKE') "Transition workspace must make the authoritative Preview take target explicit."
Assert-Condition ($window -match 'CUT PREVIEW → PROGRAM') "CUT control must explicitly describe Preview-to-Program semantics."
Assert-Condition ($window -match 'AUTO PREVIEW → PROGRAM') "AUTO control must explicitly describe Preview-to-Program semantics."
Assert-Condition ($monitorViewModel -match 'NamedPipeOperatorMonitoringTransport') "Visual monitoring must use the independent monitoring transport."
Assert-Condition ($monitorViewModel -match 'MonitoringStreamKind\.Program') "Operator monitoring must distinguish actual Program frames from source frames."
Assert-Condition ($monitorViewModel -match 'PreviewSourceId') "Preview monitoring must follow authoritative Preview routing rather than own routing state."

$projectReferenceCount = [Regex]::Matches($project, '<ProjectReference\s+Include=').Count
Assert-Condition ($projectReferenceCount -eq 1) "Operator must retain exactly one project dependency."
Assert-Condition ($project -match 'Client\\rtaime\.Client\\rtaime\.Client\.csproj') "Operator may depend only on the Client SDK seam."
Assert-Condition ($project -notmatch 'ControlHost|RuntimeHost|AIHost') "Operator must not reference production host implementations."

Assert-Condition ($documentation -match '1920.?x.?1080') "Operator UI documentation must record the reference resolution."
Assert-Condition ($documentation -match '125%') "Operator UI documentation must record 125% DPI qualification."
Assert-Condition ($documentation -match '150%') "Operator UI documentation must record 150% DPI qualification."
Assert-Condition ($documentation -match 'Runtime Health & Performance HUD') "Operator UI documentation must record the Runtime Health & Performance HUD."
Assert-Condition ($documentation -match 'PASS / FAIL / UNVERIFIED') "Runtime Health & Performance HUD documentation must preserve evidence-state semantics."
Assert-Condition ($documentation -match 'Visible AI Showcase') "Operator UI documentation must record the Visible AI Showcase."
Assert-Condition ($documentation -match 'Person Segmentation Highlight') "Visible AI Showcase documentation must identify the real existing segmentation capability."
Assert-Condition ($window -match 'Content="Open Demo Production"') "Demo Production Package must expose a one-click Open Demo Production action."
Assert-Condition ($window -match 'DemoProduction\.OpenCommand') "Demo Production Package one-click action must bind the Demo Production controller."
Assert-Condition ($window -match 'DemoProduction\.State') "Demo Production Package must expose visible package state."
Assert-Condition ($demoController -match 'OperatorControlClient' -and $demoController -match 'MediaDeckViewModel') "Demo Production Package orchestration must stay on existing Client/Media Deck seams."
Assert-Condition ($demoController -match 'SynchronizeAsync' -and $demoController -match 'SelectPreviewAsync' -and $demoController -match 'SetAudioInputStateAsync' -and $demoController -match 'LoadGraphicsOverlayAsync' -and $demoController -match 'SetAIShowcaseEnabledAsync') "Demo Production Package must compose existing authoritative feature seams rather than bypass them."
Assert-Condition ($demoController -match 'SHA256\.HashData' -and $demoController -match 'LocalApplicationData') "Demo Production Package bundled assets must be integrity-checked before user-local materialization."
Assert-Condition ($demoController -notmatch 'using rtaime\.(ControlHost|RuntimeHost|AIHost|Media);|GovernedInferenceRuntime|ProgramRecorder|Process\.Start') "Demo Production Package Operator orchestration must not own production hosts, inference, recording or host lifecycle."
Assert-Condition ($project -match 'DemoAssets\\\*\*\\\*' -and $project -match 'CopyToPublishDirectory') "Demo Production Package DemoAssets must be included in build/publish output."
foreach ($required in @('"schema": "rtaime.demo.production-package/1"', '"program": "Input A"', '"productClip": "Input B"', '"Product Intro"', '"Product End"', '"dissolveFrames": 12', '"source": "Input B"', '"feature": "Person Segmentation Highlight"')) {
	Assert-Condition ($demoManifest -match [Regex]::Escape($required)) "Demo Production Package Demo Production manifest is missing '$required'."
}
Assert-Condition ($demoManifest -match '"initialVisible": false') "Demo Production Package lower third must start hidden while the AI highlight is enabled."
Assert-Condition ($demoDocumentation -match 'pre-rendered' -and $demoDocumentation -match 'does not automatically TAKE') "Demo Production Package documentation must preserve the CG boundary and operator TAKE authority."
Assert-Condition ($documentation -match 'Demo Production Package') "Operator UI documentation must record the Demo Production Package."


Assert-Condition ($window -match 'Binding StartupComplete') "Startup presentation must remain visible until the first qualified authoritative synchronization completes."
Assert-Condition ($window -match 'Binding EngineLifecycleState' -and $window -match 'ENGINE ') "Engine lifecycle state must stay persistently visible with text, not color alone."
Assert-Condition ($window -match 'Binding ProgramSafety') "System Status must expose whether Program mutations are currently safe."
Assert-Condition ($window -match 'Binding RecoveryAction') "System Status must provide a concise recovery/operator action."
Assert-Condition ($window -match 'Binding AIStatus' -and $window -match 'Binding RecordingStatus') "System Status must distinguish AI and recording state from core Control/Runtime state."
Assert-Condition ($theme -match 'OperatorLifecycleBadge' -and $theme -match 'Value="RECOVERING"' -and $theme -match 'Value="FAILED"') "Lifecycle presentation must provide explicit semantic states for recovery and terminal failure."
Assert-Condition ($windowCode -match 'SynchronizeCommand\.Execute\(null\)') "Operator startup must initiate authoritative synchronization automatically."
Assert-Condition ($viewModel -match 'Automatic full-snapshot recovery is active') "Control transport loss must enter visible automatic recovery."
Assert-Condition ($viewModel -match 'Apply\(snapshot\)' -and $viewModel -match 'Automatic recovery restored a full authoritative Control snapshot') "Automatic recovery must restore a complete authoritative snapshot before normal operation resumes."
Assert-Condition ($viewModel -notmatch '_client is null \|\| !IsConnected \|\| IsStale \|\| IsBusy') "The bounded management refresh must continue attempting synchronization while stale/disconnected."
Assert-Condition ($viewModel -match 'ProgramSafety != OperatorProgramSafetyStates\.Blocked') "Unsafe lifecycle states must participate in the shared mutation gate."
Assert-Condition ($viewModel -match 'if \(!StartupComplete && projection\.MainUiReady\)') "Startup completion must latch after initial readiness so later recovery remains visible in the main Operator."


# Media Pool and context-sensitive Inspector.
Assert-Condition ($window -match 'Text="MEDIA"' -and $window -match 'MediaPool\.SearchText' -and $window -match 'MediaPool\.SelectedCategory' -and $window -match 'MediaPool\.SelectedFilter') "Media Pool must expose persistent category, search and filter controls."
Assert-Condition ($window -match 'MediaPool\.GridViewCommand' -and $window -match 'MediaPool\.ListViewCommand') "Media Pool must support Grid and List presentation."
Assert-Condition ($mediaPool -match 'ImageSource\? Thumbnail' -and $mediaPool -match 'source\.Thumbnail' -and $window -match 'Source="\{Binding Thumbnail\}"') "Media Pool must reuse existing verified source thumbnails without adding metadata extraction."
Assert-Condition ($window -match 'MediaPool\.FilteredItems' -and $window -match 'MediaPool\.SelectedItem') "Media Pool presentation must bind the bounded selection projection."
Assert-Condition ($mediaPool -match 'MaxVisibleItems = 256' -and $mediaPool -match 'Take\(MaxVisibleItems\)') "Media Pool collections must remain explicitly bounded."
Assert-Condition ($mediaPool -match 'StringComparison\.OrdinalIgnoreCase' -and $mediaPool -match 'OrderBy\(item => item\.Category, StringComparer\.Ordinal\)') "Media Pool search/filter ordering must be deterministic."
Assert-Condition ($mediaPool -match 'ImportCommand => _mediaDeck\.OpenCommand') "Media Pool import must reuse the existing Media Deck open path."
Assert-Condition ($mediaPool -match 'DropToPreviewAsync' -and $mediaPool -match '_operator\.SetPreviewCommand\.Execute\(null\)') "Media Pool Preview drag/drop must reuse the existing authoritative Preview command path."
Assert-Condition ($windowCode -match 'DragDropEffects\.None' -and $windowCode -match 'CanDropToPreview') "Invalid Media Pool Preview drops must be rejected safely."
Assert-Condition ($window -match 'OnTimelineDrop' -and $windowCode -match 'MediaDeck\.RefreshCommand\.Execute\(null\)') "Loaded Clip Timeline drop must reuse the existing Media Deck context rather than create another timeline."
Assert-Condition ($timelineViewModel -match 'MediaPoolItemKind\.Clip' -and $timelineViewModel -match 'MediaPoolItemKind\.Audio' -and $timelineViewModel -match 'MediaPoolItemKind\.Graphics' -and $timelineViewModel -match 'TimelineTrackCategory\.Overlay') "Timeline drag/drop must enforce semantic Clip/Audio/Graphics track compatibility."
Assert-Condition ($timelineViewModel -match 'PROJECTED ·' -and $timelineViewModel -match 'CanTrim,?\s*false|false\)') "Unsupported timed Audio/Graphics semantics must remain explicitly projected and non-trimmable."
Assert-Condition ($timelineViewModel -match '_activeTimelineContextReference' -and $timelineViewModel -match 'snapshot\.Probe\?\.AssetId') "Timeline-only resource projections must reset when the loaded media asset or source context changes."
Assert-Condition ($windowCode -match 'Timeline\.SelectionChanged \+= OnTimelineSelectionChanged' -and $windowCode -match 'MediaPool\.SelectTimelineItem' -and $windowCode -match 'MediaPool\.SelectTimelineCue') "Timeline selection must project into the existing context Inspector."
Assert-Condition ($windowCode -match 'MediaDeck\.SelectedCue = MediaDeck\.Cues\.FirstOrDefault' -and $window -match 'MediaDeck\.RenameCueCommand' -and $window -match 'MediaDeck\.DeleteCueCommand') "Selected timeline cues must reuse existing Media Deck rename/delete commands in the shared Inspector."
foreach ($timelinePropertyId in @("timeline.item.label", "timeline.item.start", "timeline.item.in", "timeline.item.out", "timeline.cue.name", "timeline.cue.time")) {
	Assert-Condition ($mediaPool -match [Regex]::Escape($timelinePropertyId)) "Timeline Inspector stable property id '$timelinePropertyId' is required."
}
Assert-Condition ($timelineDocumentation -match 'Runtime remains the execution authority' -and $timelineDocumentation -match 'No timed Graphics, Audio, AI or Control mutation is invented') "Layered timeline documentation must preserve the production authority boundary."
foreach ($propertyId in @("source.name", "clip.duration", "clip.playback.autoplay", "audio.gain", "graphics.position.x", "ai.provider", "ai.fallback")) {
	Assert-Condition ($mediaPool -match [Regex]::Escape($propertyId)) "Inspector stable property id '$propertyId' is required."
}
Assert-Condition ($window -match 'METADATA is read-only' -and $window -match 'DESIRED edits' -and $window -match 'COMMITTED') "Inspector must visually distinguish metadata, desired configuration and committed state."
Assert-Condition ($window -match 'MediaDeck\.ApplyPlaybackPolicyCommand' -and $window -match 'Binding ApplyAudioGainCommand' -and $window -match 'Binding ApplyGraphicsCommand') "Inspector edits must reuse existing product command paths."
Assert-Condition ($mediaPool -notmatch 'OperatorControlClient|NamedPipe|RuntimeHost|ControlHost|AIHost') "Media Pool selection/Inspector projection must not acquire production host or transport authority."
Assert-Condition ($documentation -match 'Media Pool & Context Inspector' -and $documentation -match 'METADATA' -and $documentation -match 'DESIRED' -and $documentation -match 'COMMITTED') "Operator documentation must record Media Pool and Inspector state semantics."

Write-Host "Operator UI policy verification PASS"
Write-Host "Operator authority: remote Client SDK only"
Write-Host "Design system: tokens, semantic tallies, reusable controls and keyboard focus verified"
Write-Host "Showcase UX: keyboard cycle, action tooltips, empty/error states, graceful shutdown and crash presentation verified"
Write-Host "DPI qualification: PerMonitorV2; 1920x1080 reference layout supports 100%, 125% and 150% scaling invariants"
Write-Host "Monitoring: independent non-authoritative bitmap plane"
Write-Host "Production workspace: selected source -> confirmed Preview -> confirmed Program TAKE semantics verified"
Write-Host "Source bin: live/media metadata, monitoring thumbnails, health, PGM/PVW and remaining-time presentation verified"
Write-Host "Media autoplay: Program-triggered playback policy, effective-range countdown and deterministic end-behavior controls verified"
Write-Host "Graphics: PNG/RGBA load, placement, scale and confirmed show/hide through Client SDK verified"
Write-Host "Audio: AFV, stereo/master meters, gain, mute, clipping/health and clip-audio status use Runtime observations"
Write-Host "Recording: confirmed REC state, elapsed time, destination/name, final path and failures use RuntimeHost recording truth"
Write-Host "System status: lifecycle, Program safety, PASS/FAIL/UNVERIFIED evidence, AI/recording state and bounded 5 Hz projection verified"
Write-Host "AI showcase: Person Segmentation Highlight, explicit ON/OFF, AIHost execution and clean Program fallback verified"
Write-Host "Demo Production: one-click integrity-checked Product Clip, cues, audio, lower third, transition and AI preparation verified"
Write-Host "Program Output: display selection, start/stop, fullscreen/windowed fallback and shared Program monitoring truth verified"
Write-Host "Commit state: pending, confirmed, rejected/failed and resynchronization presentation verified"
Write-Host "Keyboard controls: centralized deterministic bindings, conflict detection and text-entry safety verified"


# Fullscreen production shell and layout persistence.
foreach ($region in @("TopBar", "LeftToolRegion", "CenterWorkspace", "RightInspectorRegion", "LowerTimelineRegion", "BottomTransportRegion")) {
	Assert-Condition ($window -match ('x:Name="' + [Regex]::Escape($region) + '"')) "Production shell region '$region' must remain explicit and addressable."
}
Assert-Condition ($keyboard -match 'new\("preview-view".+Key\.D1.+shell\.MaximizePreviewCommand') "Preview maximize must be keyboard-accessible through Ctrl+1."
Assert-Condition ($keyboard -match 'new\("program-view".+Key\.D2.+shell\.MaximizeProgramCommand') "Program maximize must be keyboard-accessible through Ctrl+2."
Assert-Condition ($keyboard -match 'new\("dual-view".+Key\.D0.+shell\.RestoreViewersCommand') "Dual-view restore must be keyboard-accessible through Ctrl+0."
Assert-Condition ($shell -match 'PreviewViewerWidth' -and $shell -match 'new GridLength\(0\.85, GridUnitType\.Star\)') "Preview must use the smaller default production-view allocation."
Assert-Condition ($shell -match 'ProgramViewerWidth' -and $shell -match 'new GridLength\(1\.15, GridUnitType\.Star\)') "Program must be visually dominant by default."
Assert-Condition ($shell -match 'MaximizePreviewCommand' -and $shell -match 'MaximizeProgramCommand' -and $shell -match 'RestoreViewersCommand') "Viewer maximize/restore must remain local presentation commands."
Assert-Condition ($shell -match 'PreviewViewerVisibility' -and $shell -match 'ProgramViewerVisibility' -and $window -match 'Shell\.PreviewViewerVisibility' -and $window -match 'Shell\.ProgramViewerVisibility') "Maximized production viewers must collapse the inactive viewer and restore it in dual mode."
Assert-Condition ($window -match 'Text="PRODUCTION CONTROLS"' -and $window -match 'CUT PREVIEW → PROGRAM' -and $window -match 'AUTO PREVIEW → PROGRAM') "CUT/AUTO controls must sit in the central production workspace."
Assert-Condition ($window -match 'Text="TRANSITION TYPE"' -and $window -match 'Text="CUT / DISSOLVE"' -and $window -match 'Binding TransitionFrames') "Production controls must expose the current transition types and duration."
Assert-Condition ($window -match 'HOLD / FTB · NOT AVAILABLE IN V1') "Unsupported HOLD/FTB controls must remain an explicit extension surface rather than invented commands."
Assert-Condition ($programViewer -match 'Binding AudioLeftPeak' -and $programViewer -match 'Binding AudioRightPeak' -and $programViewer -match 'dBFS' -and $programViewer -match 'Text="-60"' -and $programViewer -match 'Text="-12"' -and $programViewer -match 'Text="0"') "Program viewer must keep bounded stereo metering with a visible dBFS reference scale."
Assert-Condition ($programViewer -match 'CLIP' -and $programViewer -match 'Clipping') "Program viewer must expose clipping with text as well as styling."
Assert-Condition ($programOutputController -match 'ViewerOutputState => IsRunning \? "OUTPUT LIVE" : "OUTPUT DISABLED"') "Program viewer output tally must distinguish live output from disabled output."
foreach ($viewerState in @("NO SIGNAL", "DISCONNECTED", "RECOVERING", "SOURCE OFFLINE")) {
	Assert-Condition ($viewModel -match [Regex]::Escape($viewerState)) "Production viewer state '$viewerState' must be explicit."
}
Assert-Condition ($viewModel -match 'ProgramViewerState = MapViewerSourceState' -and $viewModel -notmatch 'ProgramViewerState\s*=\s*.*AIStatus') "Program availability must derive from Runtime/source state rather than AI-only degradation."
Assert-Condition ($window -match 'Monitoring\.PreviewState' -and $window -match 'Monitoring\.ProgramState') "Production viewers must use monitoring-aware presentation state."
Assert-Condition ($monitorViewModel -match 'PreviewState => ResolveViewerState' -and $monitorViewModel -match 'ProgramState => ResolveViewerState') "Monitoring availability must be combined with confirmed production state for viewer presentation."
Assert-Condition ($monitorViewModel -match 'PreviewImage = null' -and $monitorViewModel -match 'No Preview monitor frame received for current source') "Changing Preview to a source without a monitor frame must clear the previous source image."
Assert-Condition ($monitorViewModel -match 'Value="STALE"|State, "STALE"|State\), "STALE"|string\.Equals\(State, "STALE"') "Stale monitoring must remain explicitly detectable without affecting Program authority."

Assert-Condition ($keyboard -match 'new\("fullscreen".+Key\.F11.+shell\.ToggleFullscreenCommand') "Production fullscreen must be keyboard-accessible through F11."
Assert-Condition ($keyboard -match 'new\("exit-fullscreen".+Key\.Escape.+shell\.ExitFullscreenCommand') "Production fullscreen must provide an Escape path back to windowed operation."
Assert-Condition ($windowCode -match 'WindowStyle = WindowStyle\.None' -and $windowCode -match 'ResizeMode = ResizeMode\.NoResize' -and $windowCode -match 'WindowStyle = _windowedStyle') "Fullscreen must enter borderless mode and restore windowed chrome."
Assert-Condition ($window -match 'ResizeDirection="Columns"' -and $window -match 'ResizeDirection="Rows"') "Production shell side panels and lower workspace must be resizable."
Assert-Condition ($window -match 'Shell\.ToggleLeftPanelCommand' -and $window -match 'Shell\.ToggleRightPanelCommand' -and $window -match 'Shell\.ToggleCenterMaximizeCommand') "Production shell must expose collapse and center-maximize controls."
Assert-Condition ($window -match 'DataContext="\{Binding Timeline, RelativeSource=\{RelativeSource AncestorType=\{x:Type Window\}\}\}"') "The timeline must remain available in the persistent lower workspace."
Assert-Condition ($deck -notmatch '<local:MediaTimelineControl') "Media Deck must not duplicate the shell-hosted timeline."
Assert-Condition ($shell -match 'record OperatorLayoutSettings' -and $shell -match 'Normalize\(\)' -and $shell -match 'Math\.Clamp') "Persisted layout dimensions must be normalized and safely clamped."
Assert-Condition ($shell -match 'LocalApplicationData' -and $shell -match 'operator-layout\.json') "Operator layout persistence must use local user UI configuration storage."
foreach ($layoutProperty in @("LeftPanelWidth", "RightPanelWidth", "LowerPanelHeight", "IsLeftCollapsed", "IsRightCollapsed", "IsFullscreen", "SelectedWorkspace", "ViewerMode", "Workspaces", "CurrentVersion")) {
	Assert-Condition ($shell -match [Regex]::Escape($layoutProperty)) "Operator layout persistence must retain '$layoutProperty'."
}
Assert-Condition ($shell -notmatch 'using rtaime\.(Client|Control|Runtime|Media|AI|Recording)') "Production shell layout state must remain presentation-only and must not acquire production authority dependencies."
Assert-Condition ($windowCode -match 'OnLayoutSplitterDragCompleted' -and $windowCode -match 'Shell\.Save\(\)') "Resizable shell geometry must be persisted after operator layout changes."

# Workspaces, Quick Controls, multiview and Clean Program.
foreach ($workspace in @("LIVE", "EDIT", "MEDIA", "GRAPHICS", "SYSTEM")) {
	Assert-Condition ($shell -match ('const string [A-Za-z]+ = "' + $workspace + '"')) "Canonical workspace '$workspace' must be defined."
	Assert-Condition ($window -match ('CommandParameter="' + $workspace + '"')) "Canonical workspace '$workspace' must be selectable from the Operator."
}
Assert-Condition ($shell -match 'CurrentVersion = 2' -and $shell -match 'Dictionary<string, OperatorWorkspaceLayoutSettings>') "Workspace layout persistence must be versioned and per-workspace."
Assert-Condition ($shell -match 'CaptureCurrentWorkspace\(\)' -and $shell -match 'ApplyWorkspaceLayout' -and $shell -match 'SelectWorkspace') "Workspace switching must preserve independent presentation layouts."
Assert-Condition ($shell -notmatch 'OperatorControlClient|NamedPipe|RuntimeHost|ControlHost|AIHost') "Workspace switching must remain presentation-only."
Assert-Condition ($window -match 'Content="SAVE LAYOUT"' -and $window -match 'Shell\.SaveLayoutCommand' -and $window -match 'Content="LAYOUT RESET"') "Operator must expose Save Layout and Reset Layout actions."
Assert-Condition ($window -match 'Shell\.ProductionControlsVisibility' -and $window -match 'Shell\.MediaDeckVisibility' -and $window -match 'Shell\.GraphicsVisibility' -and $window -match 'Shell\.SystemWorkspaceVisibility') "Workspaces must configure presentation without duplicating product state."

Assert-Condition ($quickControls -match 'MaximumPinnedControls = 8' -and $quickControls -match 'operator-quick-controls\.json') "Quick Controls must remain bounded and persist only local presentation preferences."
Assert-Condition ($quickControls -match 'SupportedPropertyIds' -and $quickControls -match 'propertyIds' -and $quickControls -notmatch 'OperatorControlClient|NamedPipe|RuntimeHost|ControlHost|AIHost') "Quick Controls must reference stable Inspector properties without owning production state."
foreach ($commandName in @("ApplyPlaybackPolicyCommand", "ApplyAudioGainCommand", "ToggleAudioMuteCommand", "ApplyGraphicsCommand", "ToggleGraphicsCommand", "EnableAIShowcaseCommand", "DisableAIShowcaseCommand")) {
	Assert-Condition ($quickControls -match [Regex]::Escape($commandName)) "Quick Controls must reuse existing command '$commandName'."
}
Assert-Condition ($window -match 'QuickControls\.TogglePinCommand' -and $window -match 'CommandParameter="\{Binding\}"') "Pinnable Inspector values must be able to add/remove Quick Controls."
Assert-Condition ($mediaPool -match '"production\.transition\.frames"' -and $mediaPool -match '"ai\.enabled"') "Inspector must expose stable pinnable production and AI identifiers."

Assert-Condition ($window -match '<local:OperatorMultiviewControl' -and $window -match 'Monitoring\.PreviewImage' -and $window -match 'Monitoring\.ProgramImage') "LIVE multiview must consume the existing Preview/Program monitoring projection."
Assert-Condition ($multiview -match 'PreviewImage' -and $multiview -match 'ProgramImage' -and $multiview -match 'Sources') "Reusable multiview must project supported monitoring feeds and source thumbnails."
Assert-Condition ($multiviewCode -notmatch 'NamedPipe|MediaElement|VideoDrawing|OperatorControlClient') "Multiview must not create another transport, player or authority path."
Assert-Condition ($window -match 'CLEAN PROGRAM MONITOR' -and $window -match 'not the physical Program output path') "Clean Program must be identified as monitoring rather than physical Program output."
Assert-Condition ($programOutputController -match 'OperatorMonitoringViewModel' -and $programOutputController -notmatch 'MediaElement|VideoDrawing') "Clean Program must reuse the existing monitoring projection."
Assert-Condition ($window -match 'Text="SYSTEM WORKSPACE"' -and $window -match 'Text="Engine"' -and $window -match 'Text="Control"' -and $window -match 'Text="Runtime"' -and $window -match 'Text="Outputs"' -and $window -match 'Text="Diagnostics"') "SYSTEM workspace must consolidate existing operational evidence."
Assert-Condition ($workspaceDocumentation -match 'five task-oriented workspaces' -and $workspaceDocumentation -match 'Quick Controls' -and $workspaceDocumentation -match 'Keyboard-first operation' -and $workspaceDocumentation -match 'Clean Program monitoring') "Operator workspace documentation must cover the implemented UX model."
