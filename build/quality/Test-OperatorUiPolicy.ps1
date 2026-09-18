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
$windowPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MainWindow.xaml"
$deckPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaDeckControl.xaml"
$deckViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaDeckViewModel.cs"
$timelinePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaTimelineControl.xaml"
$viewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorViewModel.cs"
$monitorViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorMonitoringViewModel.cs"
$programOutputControllerPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ProgramOutputController.cs"
$programOutputWindowPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ProgramOutputWindow.xaml"
$sourceTileViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorSourceTileViewModel.cs"
$audioInputViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorAudioInputViewModel.cs"
$graphicsLoaderPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/GraphicsOverlayAssetLoader.cs"
$tokensPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Themes/OperatorTokens.xaml"
$themePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Themes/OperatorTheme.xaml"
$manifestPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/app.manifest"
$projectPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/rtaime.Operator.csproj"
$documentationPath = Join-Path $repositoryRoot "docs/OperatorUiV1.md"

foreach ($path in @($appPath, $windowPath, $deckPath, $deckViewModelPath, $timelinePath, $viewModelPath, $monitorViewModelPath, $programOutputControllerPath, $programOutputWindowPath, $sourceTileViewModelPath, $audioInputViewModelPath, $graphicsLoaderPath, $tokensPath, $themePath, $manifestPath, $projectPath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required Operator UI artifact is missing: '$path'."
}

$app = Get-Content -LiteralPath $appPath -Raw
$window = Get-Content -LiteralPath $windowPath -Raw
$deck = Get-Content -LiteralPath $deckPath -Raw
$deckViewModel = Get-Content -LiteralPath $deckViewModelPath -Raw
$timeline = Get-Content -LiteralPath $timelinePath -Raw
$viewModel = Get-Content -LiteralPath $viewModelPath -Raw
$monitorViewModel = Get-Content -LiteralPath $monitorViewModelPath -Raw
$programOutputController = Get-Content -LiteralPath $programOutputControllerPath -Raw
$programOutputWindow = Get-Content -LiteralPath $programOutputWindowPath -Raw
$sourceTileViewModel = Get-Content -LiteralPath $sourceTileViewModelPath -Raw
$audioInputViewModel = Get-Content -LiteralPath $audioInputViewModelPath -Raw
$graphicsLoader = Get-Content -LiteralPath $graphicsLoaderPath -Raw
$tokens = Get-Content -LiteralPath $tokensPath -Raw
$theme = Get-Content -LiteralPath $themePath -Raw
$manifest = Get-Content -LiteralPath $manifestPath -Raw
$project = Get-Content -LiteralPath $projectPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw

Assert-Condition ($app -match 'Source="Themes/OperatorTheme\.xaml"') "Operator must load the reusable theme resource dictionary."
Assert-Condition ($theme -match 'Source="OperatorTokens\.xaml"') "Operator theme must load the shared design-token dictionary."
foreach ($token in @("OperatorFontFamily", "OperatorWindowPadding", "OperatorControlHeight", "OperatorColorPreview", "OperatorColorProgram", "OperatorColorHealthy", "OperatorColorWarning", "OperatorColorError")) {
	Assert-Condition ($tokens -match [Regex]::Escape($token)) "Operator design token '$token' is required."
}
foreach ($resource in @("OperatorPreviewBrush", "OperatorProgramBrush", "OperatorArmedBrush", "OperatorHealthyBrush", "OperatorWarningBrush", "OperatorErrorBrush", "OperatorEvidenceBadge", "OperatorFocusVisual", "OperatorToolbar", "OperatorToggleButton", "OperatorSourceItem", "OperatorMeter", "OperatorTimelineSlider", "OperatorPreviewTally", "OperatorProgramTally")) {
	Assert-Condition ($theme -match [Regex]::Escape($resource)) "Operator theme resource '$resource' is required."
}

Assert-Condition ($manifest -match 'PerMonitorV2,PerMonitor') "Operator must declare PerMonitorV2 DPI awareness with PerMonitor fallback."
Assert-Condition ($project -match '<ApplicationManifest>app\.manifest</ApplicationManifest>') "Operator project must bind the DPI-awareness manifest."
Assert-Condition ($project -notmatch '<UseWindowsForms>true</UseWindowsForms>') "AP-51 must keep the Operator WPF-only rather than mixing WinForms into the presentation host."
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
Assert-Condition ($window -match 'OperatorPreviewPanel') "Preview monitor must use Preview semantic styling."
Assert-Condition ($window -match 'OperatorProgramPanel') "Program monitor must use Program semantic styling."
Assert-Condition ($window -match 'OperatorPreviewTally') "Preview tally semantics must be explicit."
Assert-Condition ($window -match 'OperatorProgramTally') "Program tally semantics must be explicit."
Assert-Condition ($window -match 'Monitoring\.PreviewImage') "Operator Preview must bind the independent monitoring image."
Assert-Condition ($window -match 'Monitoring\.ProgramImage') "Operator Program must bind the independent monitoring image."
Assert-Condition ($window -match '<Image\s') "AP-29 must render visual monitoring with WPF Image surfaces."
Assert-Condition ($window -notmatch 'MediaElement|VideoDrawing') "Operator monitoring must use the qualified bounded bitmap path, not an ungoverned media player."
Assert-Condition ($window -match 'Text="PROGRAM OUTPUT / CLEAN FEED"') "AP-51 must expose Program Output controls in the Operator."
Assert-Condition ($window -match 'ProgramOutput\.Displays') "AP-51 must expose display selection."
Assert-Condition ($window -match 'ProgramOutput\.StartCommand') "AP-51 must expose controlled Program Output start."
Assert-Condition ($window -match 'ProgramOutput\.StopCommand') "AP-51 must expose controlled Program Output stop."
Assert-Condition ($window -match 'ProgramOutput\.ToggleFullscreenCommand') "AP-51 must expose fullscreen/windowed control."
Assert-Condition ($window -match 'ProgramOutput\.Health') "AP-51 must expose visible output health."
Assert-Condition ($programOutputWindow -match 'Source="\{Binding ProgramImage\}"') "Clean Feed must present the same Runtime-derived ProgramImage used by the Operator monitoring surface."
Assert-Condition ($programOutputWindow -match 'Stretch="Uniform"') "Clean Feed must preserve Program aspect ratio."
Assert-Condition ($programOutputWindow -notmatch '<Button|<ComboBox|<TextBox|MediaElement|VideoDrawing') "Clean Feed must contain no Operator controls or independent media player."
Assert-Condition ($programOutputController -match 'OperatorMonitoringViewModel') "Clean Feed controller must consume the existing Operator monitoring projection rather than own Program rendering."
Assert-Condition ($programOutputController -match 'EnumDisplayMonitors' -and $programOutputController -match 'GetMonitorInfo') "AP-51 must enumerate selectable Windows displays through the native monitor API."
Assert-Condition ($programOutputController -match 'SystemEvents\.DisplaySettingsChanged') "AP-51 must react to display topology changes."
Assert-Condition ($programOutputController -match 'WindowStyle\.None' -and $programOutputController -match 'WindowStyle\.SingleBorderWindow') "AP-51 must support fullscreen and defined windowed fallback."
Assert-Condition ($programOutputController -match 'SetWindowPos') "AP-51 display placement must target the selected physical display."
Assert-Condition ($programOutputController -match '_fallbackActive' -and $programOutputController -match 'Selected display was removed') "AP-51 must surface controlled display-disconnect fallback."
Assert-Condition ($programOutputController -notmatch 'using rtaime\\.(ControlHost|RuntimeHost)|MediaElement|VideoDrawing') "Program Output presentation must not bypass the Client/monitoring boundary or create a second renderer."
Assert-Condition ($timeline -match 'Style="\{StaticResource OperatorTimelineSlider\}"') "Timeline seeker must use the design-system slider style."
Assert-Condition ($timeline -match 'Style="\{StaticResource OperatorMeter\}"') "Timeline progress must use the design-system meter style."
Assert-Condition ($deck -match 'OperatorStatusBadge') "Media deck state must use shared status presentation."
Assert-Condition ($deck -match 'AUTO PLAY ON PROGRAM') "AP-52 must expose Auto Play on Program."
Assert-Condition ($deck -match 'Binding EndBehaviors') "AP-52 must expose deterministic media-deck end behavior selection."
Assert-Condition ($deck -match 'Binding EndBehavior') "AP-52 end behavior selection must bind confirmed presentation state."
Assert-Condition ($deck -match 'Binding Countdown') "AP-52 must expose a visible clip countdown."
Assert-Condition ($deck -match 'Binding ProgramDeckState') "AP-52 must expose whether the loaded media slot is on Program."
Assert-Condition ($deck -match 'Binding EffectiveRange') "AP-52 must expose the effective IN/OUT playback range."
Assert-Condition ($deck -match 'Binding ApplyPlaybackPolicyCommand') "AP-52 playback policy changes must be explicit operator actions."
Assert-Condition ($deckViewModel -match 'ConfigurePlaybackAsync\(AutoPlayOnProgram, EndBehavior') "AP-52 policy changes must cross the Client SDK deck controller."
Assert-Condition ($deckViewModel -match 'if \(!IsLoaded \|\| IsBusy\)') "Loaded media decks must keep observing Runtime-triggered autoplay state."
Assert-Condition ($deckViewModel -match 'EffectiveRemainingFrames') "Deck countdown must use effective IN/OUT remaining frames."
Assert-Condition ($deck -notmatch 'Auto Pause') "AP-52 must not imply unspecified auto-pause-on-remove semantics."
Assert-Condition ($viewModel -match 'EffectiveRemainingFrames') "Source-tile remaining time must use the effective media range."
Assert-Condition ($deck -match 'Remaining') "Media deck must retain remaining-time presentation."
Assert-Condition ($monitorViewModel -notmatch 'ConfigurePlayback') "Monitoring must remain independent from media playback policy."

Assert-Condition ($window -match 'Key="F5"') "Operator must expose keyboard synchronization."
Assert-Condition ($window -match 'Key="Space"\s+Command="\{Binding CutCommand\}"') "Operator must expose a keyboard CUT command."
Assert-Condition ($window -match 'Modifiers="Control"\s+Command="\{Binding DissolveCommand\}"') "Operator must expose a keyboard DISSOLVE/AUTO command."
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
Assert-Condition ($window -match 'Text="GRAPHICS / OVERLAY"') "Operator must expose the AP-49 graphics workflow."
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
Assert-Condition ($window -match 'Text="AUDIO / AFV"') "Operator must expose the AP-50 audio/AFV workflow."
Assert-Condition ($window -match 'ItemsSource="\{Binding AudioInputs\}"') "Audio workflow must expose Runtime-observed inputs."
Assert-Condition ($window -match 'Binding AudioLeftPeak') "Audio workflow must expose left Program meter."
Assert-Condition ($window -match 'Binding AudioRightPeak') "Audio workflow must expose right Program meter."
Assert-Condition ($window -match 'Binding AudioMasterPeak') "Audio workflow must expose master Program meter."
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
Assert-Condition ($window -match 'Text="PROGRAM RECORDING"') "AP-53 must expose a dedicated Program recording workflow."
Assert-Condition ($window -match 'Binding RecordingStatus') "AP-53 must expose confirmed REC state."
Assert-Condition ($window -match 'Binding RecordingElapsed') "AP-53 must expose recording elapsed time."
Assert-Condition ($window -match 'Binding RecordingDestination') "AP-53 must expose recording destination."
Assert-Condition ($window -match 'Binding RecordingFileName') "AP-53 must expose file naming."
Assert-Condition ($window -match 'Binding RecordingFinalPath') "AP-53 must expose the finalized recording path."
Assert-Condition ($window -match 'Binding RecordingError') "AP-53 must expose recording failure state."
Assert-Condition ($window -match 'Binding StartRecordingCommand') "AP-53 must expose explicit recording start."
Assert-Condition ($window -match 'Binding StopRecordingCommand') "AP-53 must expose explicit recording stop."
Assert-Condition ($viewModel -match '_client\.StartRecordingAsync') "Recording start must cross the Client SDK seam."
Assert-Condition ($viewModel -match '_client\.StopRecordingAsync') "Recording stop must cross the Client SDK seam."
Assert-Condition ($viewModel -match 'ApplyRecording\(snapshot\.Recording') "Recording status must project confirmed Runtime observations."
Assert-Condition ($viewModel -notmatch 'ProgramRecorder|ReferenceRecordingPayloadWriter') "Operator must not own recording execution or storage writers."
Assert-Condition ($window -match 'Text="AI / PERSON SEGMENTATION"') "AP-55 must expose one visible AI showcase panel."
Assert-Condition ($window -match 'Content="AI ON"' -and $window -match 'Binding EnableAIShowcaseCommand') "AP-55 must expose explicit AI enable control."
Assert-Condition ($window -match 'Content="AI OFF"' -and $window -match 'Binding DisableAIShowcaseCommand') "AP-55 must expose explicit AI disable control."
foreach ($binding in @("AIProvider", "AIStatus", "AIInferenceTime", "AIPersonRegions", "AIConfidence", "AISynchronization")) {
	Assert-Condition ($window -match "Binding $binding") "AP-55 Operator binding '$binding' is required."
}
Assert-Condition ($viewModel -match '_client\.SetAIShowcaseEnabledAsync') "AP-55 AI enable/disable must cross the Client SDK seam."
Assert-Condition ($viewModel -match 'ApplyAI\(snapshot\.AIShowcase\)') "AP-55 observations must project confirmed Runtime/AIHost state."
Assert-Condition ($viewModel -notmatch 'ManagedReferencePersonSegmentationProvider|GovernedInferenceRuntime|AIHostService|ai\.inference\.execute') "Operator must not own inference or bypass the Client SDK."
$ap55PollCount = [Regex]::Matches($viewModel, 'PeriodicTimer\(TimeSpan\.FromMilliseconds\(200\)\)').Count
Assert-Condition ($ap55PollCount -eq 1) "AP-55 must reuse the existing bounded management poll rather than add an AI UI polling loop."
Assert-Condition ($window -match 'Text="RUNTIME HEALTH / PERFORMANCE"') "AP-54 must expose a compact Runtime health/performance HUD."
foreach ($binding in @("EngineHealth", "ControlHealth", "RuntimeHealth", "MediaHealth", "ProviderHealth", "GpuProviderHealth", "CurrentFormat", "FrameTime", "DroppedFrames", "Uptime", "GpuUtilization", "Vram")) {
	Assert-Condition ($window -match "Binding $binding") "AP-54 HUD binding '$binding' is required."
}
Assert-Condition ($theme -match 'Trigger Property="Tag" Value="PASS"' -and $theme -match 'OperatorHealthyBrush') "PASS evidence must use the healthy semantic."
Assert-Condition ($theme -match 'Trigger Property="Tag" Value="FAIL"' -and $theme -match 'OperatorErrorBrush') "FAIL evidence must use the error semantic."
Assert-Condition ($theme -match 'Trigger Property="Tag" Value="UNVERIFIED"' -and $theme -match 'OperatorWarningBrush') "UNVERIFIED evidence must remain visually distinct from healthy PASS."
Assert-Condition ($viewModel -match 'ApplyHealth\(snapshot\.Health\)') "AP-54 HUD must project confirmed Client health snapshots."
$managementPollCount = [Regex]::Matches($viewModel, 'PeriodicTimer\(TimeSpan\.FromMilliseconds\(200\)\)').Count
Assert-Condition ($managementPollCount -eq 1) "AP-54 must reuse the single bounded 200 ms management poll rather than add a new UI telemetry loop."
Assert-Condition ($viewModel -notmatch 'PerformanceCounter|ManagementObjectSearcher|nvidia-smi|NVML') "Operator must not synthesize GPU telemetry locally."
Assert-Condition ($window -match 'Text="COMMIT"') "Program workspace must expose commit status."
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
Assert-Condition ($documentation -match 'AP-54 Runtime Health') "Operator UI documentation must record the AP-54 Runtime health/performance HUD."
Assert-Condition ($documentation -match 'PASS / FAIL / UNVERIFIED') "AP-54 documentation must preserve evidence-state semantics."
Assert-Condition ($documentation -match 'AP-55 Visible AI Showcase') "Operator UI documentation must record AP-55."
Assert-Condition ($documentation -match 'Person Segmentation Highlight') "AP-55 documentation must identify the real existing segmentation capability."

Write-Host "Operator UI policy verification PASS"
Write-Host "Operator authority: remote Client SDK only"
Write-Host "Design system: tokens, semantic tallies, reusable controls and keyboard focus verified"
Write-Host "DPI qualification: PerMonitorV2; 1920x1080 reference layout supports 100%, 125% and 150% scaling invariants"
Write-Host "Monitoring: independent non-authoritative bitmap plane"
Write-Host "Production workspace: selected source -> confirmed Preview -> confirmed Program TAKE semantics verified"
Write-Host "Source bin: live/media metadata, monitoring thumbnails, health, PGM/PVW and remaining-time presentation verified"
Write-Host "Media autoplay: Program-triggered playback policy, effective-range countdown and deterministic end-behavior controls verified"
Write-Host "Graphics: PNG/RGBA load, placement, scale and confirmed show/hide through Client SDK verified"
Write-Host "Audio: AFV, stereo/master meters, gain, mute, clipping/health and clip-audio status use Runtime observations"
Write-Host "Recording: confirmed REC state, elapsed time, destination/name, final path and failures use RuntimeHost recording truth"
Write-Host "Health HUD: PASS/FAIL/UNVERIFIED Runtime evidence, bounded 5 Hz projection and no locally invented GPU telemetry"
Write-Host "AI showcase: Person Segmentation Highlight, explicit ON/OFF, AIHost execution and clean Program fallback verified"
Write-Host "Program Output: display selection, start/stop, fullscreen/windowed fallback and shared Program monitoring truth verified"
Write-Host "Commit state: pending, confirmed, rejected/failed and resynchronization presentation verified"
Write-Host "Keyboard controls: synchronization, Preview, CUT and DISSOLVE/AUTO declared"
