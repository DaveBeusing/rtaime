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
$liveSceneCuePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/LiveSceneCueControl.xaml"
$liveControlsPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/LiveControlsControl.xaml"
$liveDocumentationPath = Join-Path $repositoryRoot "docs/LiveMultiviewAndSceneControl.md"
$compositingGraphPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/CompositingGraphControl.xaml"
$compositingGraphCodePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/CompositingGraphControl.xaml.cs"
$compositingGraphViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/CompositingGraphViewModel.cs"
$compositingGraphProjectionPath = Join-Path $repositoryRoot "src/Client/rtaime.Client/CompositingGraphProjection.cs"
$compositingGraphDocumentationPath = Join-Path $repositoryRoot "docs/CompositingNodeGraph.md"
$mediaPoolPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MediaPoolInspectorViewModel.cs"
$inspectorHostPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorInspectorControl.xaml"
$virtualizingWrapPanelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/VirtualizingWrapPanel.cs"
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
$outputHealthControlPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OutputRoutingHealthControl.xaml"
$outputHealthViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OutputRoutingHealthViewModel.cs"
$previewViewerPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/PreviewViewer.xaml"
$previewViewerCodePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/PreviewViewer.xaml.cs"
$programViewerPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ProgramViewer.xaml"
$programViewerCodePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ProgramViewer.xaml.cs"
$monitorViewPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/MonitorView.cs"
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
$customControlsPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Controls/RtaimeControls.cs"
$customInputControlsPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Controls/RtaimeInputControls.cs"
$customMediaControlsPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Controls/RtaimeMediaControls.cs"
$customControlThemePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Themes/Controls/RtaimeControls.xaml"
$customInputThemePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Themes/Controls/RtaimeInputsAndScrolling.xaml"
$customMediaThemePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Themes/Controls/RtaimeMediaLibrary.xaml"
$monitorWorkspaceThemePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Themes/Controls/RtaimeMonitorWorkspace.xaml"
$customIconThemePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Themes/Controls/RtaimeIcons.xaml"
$manifestPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/app.manifest"
$projectPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/rtaime.Operator.csproj"
$documentationPath = Join-Path $repositoryRoot "docs/OperatorUiV1.md"
$workspaceDocumentationPath = Join-Path $repositoryRoot "docs/OperatorWorkspaces.md"
$outputHealthDocumentationPath = Join-Path $repositoryRoot "docs/OutputRoutingHealth.md"
$mediaLibraryDocumentationPath = Join-Path $repositoryRoot "docs/MediaLibraryAssetBrowser.md"

foreach ($path in @($appPath, $appCodePath, $windowPath, $windowCodePath, $shellPath, $keyboardPath, $quickControlsPath, $multiviewPath, $multiviewCodePath, $liveSceneCuePath, $liveControlsPath, $liveDocumentationPath, $compositingGraphPath, $compositingGraphCodePath, $compositingGraphViewModelPath, $compositingGraphProjectionPath, $compositingGraphDocumentationPath, $mediaPoolPath, $inspectorHostPath, $virtualizingWrapPanelPath, $deckPath, $deckViewModelPath, $timelinePath, $timelineCodePath, $timelineViewModelPath, $markerControllerPath, $timelineDocumentationPath, $viewModelPath, $monitorViewModelPath, $programOutputControllerPath, $programOutputWindowPath, $outputHealthControlPath, $outputHealthViewModelPath, $previewViewerPath, $previewViewerCodePath, $programViewerPath, $programViewerCodePath, $monitorViewPath, $sourceTileViewModelPath, $audioInputViewModelPath, $graphicsLoaderPath, $demoControllerPath, $demoManifestPath, $demoProductPath, $demoGraphicsPath, $demoDocumentationPath, $tokensPath, $themePath, $customControlsPath, $customInputControlsPath, $customMediaControlsPath, $customControlThemePath, $customInputThemePath, $customMediaThemePath, $monitorWorkspaceThemePath, $customIconThemePath, $manifestPath, $projectPath, $documentationPath, $workspaceDocumentationPath, $outputHealthDocumentationPath, $mediaLibraryDocumentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required Operator UI artifact is missing: '$path'."
}

$app = Get-Content -LiteralPath $appPath -Raw
$appCode = Get-Content -LiteralPath $appCodePath -Raw
$window = Get-Content -LiteralPath $windowPath -Raw
$windowCode = Get-Content -LiteralPath $windowCodePath -Raw
$outputHealthControl = Get-Content -LiteralPath $outputHealthControlPath -Raw
$outputHealthViewModel = Get-Content -LiteralPath $outputHealthViewModelPath -Raw
$outputHealthDocumentation = Get-Content -LiteralPath $outputHealthDocumentationPath -Raw
$shell = Get-Content -LiteralPath $shellPath -Raw
$keyboard = Get-Content -LiteralPath $keyboardPath -Raw
$quickControls = Get-Content -LiteralPath $quickControlsPath -Raw
$multiview = Get-Content -LiteralPath $multiviewPath -Raw
$multiviewCode = Get-Content -LiteralPath $multiviewCodePath -Raw
$liveSceneCue = Get-Content -LiteralPath $liveSceneCuePath -Raw
$liveControls = Get-Content -LiteralPath $liveControlsPath -Raw
$liveDocumentation = Get-Content -LiteralPath $liveDocumentationPath -Raw
$compositingGraph = Get-Content -LiteralPath $compositingGraphPath -Raw
$compositingGraphCode = Get-Content -LiteralPath $compositingGraphCodePath -Raw
$compositingGraphViewModel = Get-Content -LiteralPath $compositingGraphViewModelPath -Raw
$compositingGraphProjection = Get-Content -LiteralPath $compositingGraphProjectionPath -Raw
$compositingGraphDocumentation = Get-Content -LiteralPath $compositingGraphDocumentationPath -Raw
$mediaPool = Get-Content -LiteralPath $mediaPoolPath -Raw
$inspectorHost = Get-Content -LiteralPath $inspectorHostPath -Raw
$inspectorSurface = "$window`n$inspectorHost"
$virtualizingWrapPanel = Get-Content -LiteralPath $virtualizingWrapPanelPath -Raw
$deck = Get-Content -LiteralPath $deckPath -Raw
$deckViewModel = Get-Content -LiteralPath $deckViewModelPath -Raw
$timeline = Get-Content -LiteralPath $timelinePath -Raw
$inputSurface = "$window`n$inspectorHost`n$deck`n$timeline`n$liveControls`n$liveSceneCue`n$multiview`n$outputHealthControl"
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
$monitorView = Get-Content -LiteralPath $monitorViewPath -Raw
$sourceTileViewModel = Get-Content -LiteralPath $sourceTileViewModelPath -Raw
$audioInputViewModel = Get-Content -LiteralPath $audioInputViewModelPath -Raw
$graphicsLoader = Get-Content -LiteralPath $graphicsLoaderPath -Raw
$demoController = Get-Content -LiteralPath $demoControllerPath -Raw
$demoManifest = Get-Content -LiteralPath $demoManifestPath -Raw
$demoDocumentation = Get-Content -LiteralPath $demoDocumentationPath -Raw
$tokens = Get-Content -LiteralPath $tokensPath -Raw
$theme = Get-Content -LiteralPath $themePath -Raw
$customControls = Get-Content -LiteralPath $customControlsPath -Raw
$customInputControls = Get-Content -LiteralPath $customInputControlsPath -Raw
$customMediaControls = Get-Content -LiteralPath $customMediaControlsPath -Raw
$customControlTheme = Get-Content -LiteralPath $customControlThemePath -Raw
$customInputTheme = Get-Content -LiteralPath $customInputThemePath -Raw
$customMediaTheme = Get-Content -LiteralPath $customMediaThemePath -Raw
$monitorWorkspaceTheme = Get-Content -LiteralPath $monitorWorkspaceThemePath -Raw
$customIconTheme = Get-Content -LiteralPath $customIconThemePath -Raw
$manifest = Get-Content -LiteralPath $manifestPath -Raw
$project = Get-Content -LiteralPath $projectPath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$workspaceDocumentation = Get-Content -LiteralPath $workspaceDocumentationPath -Raw
$mediaLibraryDocumentation = Get-Content -LiteralPath $mediaLibraryDocumentationPath -Raw
$operatorXaml = (Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src/Hosts/rtaime.Operator") -Filter "*.xaml" -File -Recurse |
	ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"

$topBarStart = $window.IndexOf('x:Name="TopBar"', [StringComparison]::Ordinal)
$bodyStart = $window.IndexOf('<Grid Grid.Row="1" ClipToBounds="True">', [StringComparison]::Ordinal)
$navigationStart = $window.IndexOf('x:Name="WorkspaceNavigation"', [StringComparison]::Ordinal)
$leftRegionStart = $window.IndexOf('x:Name="LeftToolRegion"', [StringComparison]::Ordinal)
Assert-Condition ($topBarStart -ge 0 -and $bodyStart -gt $topBarStart) "Operator top bar must precede the production body."
Assert-Condition ($navigationStart -ge 0 -and $leftRegionStart -gt $navigationStart) "Operator navigation must precede the left tool region."
$topBarSurface = $window.Substring($topBarStart, $bodyStart - $topBarStart)
$navigationSurface = $window.Substring($navigationStart, $leftRegionStart - $navigationStart)
$mediaLibraryEnd = $window.IndexOf('<controls:RtaimeSplitter Grid.RowSpan="2" Grid.Column="5"', $leftRegionStart, [StringComparison]::Ordinal)
Assert-Condition ($mediaLibraryEnd -gt $leftRegionStart) "Media Library surface must end before the right workspace splitter."
$mediaLibrarySurface = $window.Substring($leftRegionStart, $mediaLibraryEnd - $leftRegionStart)
$productionWorkspaceStart = $window.IndexOf('x:Name="PreviewProgramProductionWorkspace"', [StringComparison]::Ordinal)
$productionWorkspaceEnd = $window.IndexOf('<local:MediaDeckControl', $productionWorkspaceStart, [StringComparison]::Ordinal)
Assert-Condition ($productionWorkspaceStart -ge 0 -and $productionWorkspaceEnd -gt $productionWorkspaceStart) "Edit production workspace must remain an explicit bounded surface."
$productionWorkspaceSurface = $window.Substring($productionWorkspaceStart, $productionWorkspaceEnd - $productionWorkspaceStart)

Assert-Condition ($app -match 'Source="Themes/OperatorTheme\.xaml"') "Operator must load the reusable theme resource dictionary."
Assert-Condition ($app -match 'Source="Themes/Controls/RtaimeIcons\.xaml"') "Operator must load the custom icon geometry dictionary."
Assert-Condition ($app -match 'Source="Themes/Controls/RtaimeControls\.xaml"') "Operator must load the custom control chrome dictionary."
Assert-Condition ($app -match 'Source="Themes/Controls/RtaimeInputsAndScrolling\.xaml"') "Operator must load the custom input and scrolling dictionary."
Assert-Condition ($app -match 'Source="Themes/Controls/RtaimeMediaLibrary\.xaml"') "Operator must load the custom Media Library dictionary."
Assert-Condition ($app -match 'Source="Themes/Controls/RtaimeMonitorWorkspace\.xaml"') "Operator must load the custom monitor workspace dictionary."
$iconThemeIndex = $app.IndexOf('Source="Themes/Controls/RtaimeIcons.xaml"', [StringComparison]::Ordinal)
$controlThemeIndex = $app.IndexOf('Source="Themes/Controls/RtaimeControls.xaml"', [StringComparison]::Ordinal)
Assert-Condition ($iconThemeIndex -ge 0 -and $controlThemeIndex -gt $iconThemeIndex) "Custom icon resources must load before custom control templates."

foreach ($controlName in @("RtaimeButton", "RtaimeIconButton", "RtaimeToggleButton", "RtaimeTransportButton", "RtaimeNavigationItem", "RtaimePanelHeader", "RtaimeStatusBadge", "RtaimeMetricBar", "RtaimeTimecode", "RtaimeIcon")) {
	Assert-Condition ($customControls -match ("class " + $controlName + "\b")) "Custom Operator control '$controlName' must exist."
	Assert-Condition ($customControls -match ("OverrideMetadata\(typeof\(" + $controlName + "\)")) "Custom Operator control '$controlName' must own its default style key."
	Assert-Condition ($customControlTheme -match ('ControlTemplate TargetType="\{x:Type controls:' + $controlName + '\}"')) "Custom Operator control '$controlName' must have a complete own template."
}

Assert-Condition ($customControls -match 'enum RtaimeStatusKind') "Custom status visuals must use explicit semantic states."
foreach ($statusKind in @("Selection", "Action", "Healthy", "Ready", "Warning", "Armed", "Preview", "Program", "OnAir")) {
	Assert-Condition ($customControlTheme -match ('RtaimeStatusKind\.' + $statusKind)) "Custom status badge must style semantic state '$statusKind'."
}
Assert-Condition ($customControlTheme -match 'RtaimeStatusKind\.Program[\s\S]+OperatorProgramBrush' -and $customControlTheme -match 'RtaimeStatusKind\.OnAir[\s\S]+OperatorProgramBrush') "Program and On-Air status must use the red Program semantic brush."
Assert-Condition ($customControlTheme -match 'RtaimeStatusKind\.Healthy[\s\S]+OperatorHealthyBrush' -and $customControlTheme -match 'RtaimeStatusKind\.Ready[\s\S]+OperatorHealthyBrush') "Healthy and Ready status must use the green health semantic brush."
Assert-Condition ($customControlTheme -match 'RtaimeStatusKind\.Warning[\s\S]+OperatorWarningBrush' -and $customControlTheme -match 'RtaimeStatusKind\.Armed[\s\S]+OperatorArmedBrush') "Warning and Armed status must use the amber semantic brushes."
Assert-Condition ($customControlTheme -match 'RtaimeStatusKind\.Selection[\s\S]+OperatorAccentBrush' -and $customControlTheme -match 'RtaimeStatusKind\.Action[\s\S]+OperatorAccentBrush') "Selection and action status must use the cyan accent brush."
Assert-Condition ($customControlTheme -match '<Setter Property="Width" Value="28" />' -and $customControlTheme -match '<Setter Property="Height" Value="28" />') "Custom icon buttons must default to 28x28."
Assert-Condition ($customControlTheme -match 'Property="Width" Value="\{DynamicResource OperatorNavigationWidth\}"' -and $customControlTheme -match 'Property="MinHeight" Value="72"') "Custom navigation items must use the 92px rail token and approximately 72px item height."
Assert-Condition ($customControlTheme -match 'Property="MaxHeight" Value="26"' -and $customControlTheme -match 'Property="Height" Value="4"') "Status badges and metric bars must retain compact mockup metrics."
Assert-Condition ($customControlTheme -match 'FocusVisualStyle" Value="\{x:Null\}"' -and $customControlTheme -match 'Property="IsKeyboardFocused"' -and $customControlTheme -match 'OperatorFocusBrush') "Interactive custom controls must suppress stock focus visuals and provide the cyan focus treatment."
Assert-Condition ($customControlTheme -match 'Property="IsMouseOver"' -and $customControlTheme -match 'Property="IsPressed"' -and $customControlTheme -match 'Property="IsEnabled" Value="False"') "Custom action controls must define hover, pressed and disabled states."
foreach ($iconSize in @(14, 16, 18, 20)) {
	Assert-Condition ($customControlTheme -match ('x:Key="RtaimeIcon' + $iconSize + '"')) "Custom icon size '$iconSize' must be available."
}
foreach ($iconName in @("Play", "Pause", "Stop", "Previous", "Next", "Media", "Live", "Timeline", "Output", "Compositing", "Settings", "Check", "Warning", "Close", "ChevronLeft", "ChevronRight", "Search", "Add", "Grid", "List", "Fit", "Zoom", "Maximize", "Fullscreen", "MarkIn", "MarkOut", "More")) {
	Assert-Condition ($customIconTheme -match ('x:Key="RtaimeIcon' + $iconName + 'Geometry"')) "Custom icon geometry '$iconName' must exist."
}
Assert-Condition ($customIconTheme -notmatch '[\uD800-\uDFFF]') "Custom icon resources must use geometry rather than emoji glyphs."

foreach ($controlName in @("RtaimeTextBox", "RtaimeComboBoxItem", "RtaimeComboBox", "RtaimeCheckBox", "RtaimeRadioButton", "RtaimeSlider", "RtaimeScrollBar", "RtaimeScrollViewer", "RtaimeSplitter", "RtaimeListBoxItem", "RtaimeListBox", "RtaimeTabItem", "RtaimeTabControl")) {
	Assert-Condition ($customInputControls -match ("class " + $controlName + "\b")) "Custom Operator input control '$controlName' must exist."
	Assert-Condition ($customInputControls -match ("OverrideMetadata\(typeof\(" + $controlName + "\)")) "Custom Operator input control '$controlName' must own its default style key."
	Assert-Condition ($customInputTheme -match ('TargetType="\{x:Type controls:' + $controlName + '\}"')) "Custom Operator input control '$controlName' must have own chrome."
}
Assert-Condition ($customInputControls -match 'GetContainerForItemOverride\(\)' -and $customInputControls -match 'RtaimeComboBoxItem' -and $customInputControls -match 'RtaimeListBoxItem' -and $customInputControls -match 'RtaimeTabItem') "Selection controls must generate custom item containers rather than stock WPF containers."
Assert-Condition ($customInputTheme -match 'RtaimeVerticalScrollBarTemplate' -and $customInputTheme -match 'RtaimeHorizontalScrollBarTemplate' -and $customInputTheme -match 'RtaimeScrollThumbTemplate') "Scrolling must use complete custom vertical, horizontal and thumb templates."
Assert-Condition ($customInputTheme -match 'RtaimeHorizontalSliderTemplate' -and $customInputTheme -match 'RtaimeVerticalSliderTemplate' -and $customInputTheme -match 'RtaimeSliderThumbTemplate') "Sliders must use complete custom tracks and thumbs."
Assert-Condition ($customInputTheme -match 'PART_ContentHost' -and $customInputTheme -match 'CaretBrush' -and $customInputTheme -match 'SelectionBrush') "Custom text input must preserve WPF text editing semantics with explicit visual chrome."
Assert-Condition ($customInputTheme -match 'PART_Popup' -and $customInputTheme -match 'RtaimeScrollViewer' -and $customInputTheme -match 'RtaimeComboBoxItem') "Custom combo boxes must use the custom popup, scrolling and item chrome."
Assert-Condition ($customInputTheme -match 'FocusVisualStyle" Value="\{x:Null\}"' -and $customInputTheme -match 'OperatorFocusBrush') "Input controls must suppress stock focus visuals and render the shared cyan focus treatment."
Assert-Condition ($customInputTheme -match 'x:Key="RtaimeVerticalSplitter"' -and $customInputTheme -match 'x:Key="RtaimeHorizontalSplitter"' -and $customInputTheme -match 'ResizeDirection" Value="Columns"' -and $customInputTheme -match 'ResizeDirection" Value="Rows"') "Custom splitters must preserve keyboard-resizable column and row behavior."
Assert-Condition ($inputSurface -notmatch '<(TextBox|ComboBox|CheckBox|RadioButton|Slider|ScrollViewer|ScrollBar|GridSplitter|ListBox|TabControl|TabItem)(\s|/|>)') "Feature XAML must use rtaime input, selection and scrolling controls instead of direct visible WPF defaults."
foreach ($customTag in @("RtaimeTextBox", "RtaimeComboBox", "RtaimeCheckBox", "RtaimeSlider", "RtaimeScrollViewer", "RtaimeScrollBar", "RtaimeSplitter", "RtaimeListBox", "RtaimeTabControl", "RtaimeTabItem")) {
	Assert-Condition ($inputSurface -match ("controls:" + $customTag)) "Migrated Operator surfaces must exercise custom control '$customTag'."
}
foreach ($controlName in @("RtaimeSearchBox", "RtaimeMediaTile", "RtaimeContextMenu", "RtaimeMenuItem", "RtaimeMenuSeparator")) {
	Assert-Condition ($customMediaControls -match ("class " + $controlName + "\b")) "Custom Media Library control '$controlName' must exist."
	Assert-Condition ($customMediaControls -match ("OverrideMetadata\(typeof\(" + $controlName + "\)")) "Custom Media Library control '$controlName' must own its default style key."
	Assert-Condition ($customMediaTheme -match ('TargetType="\{x:Type controls:' + $controlName + '\}"')) "Custom Media Library control '$controlName' must have own chrome."
}
Assert-Condition ($customMediaTheme -match 'RtaimeMediaOverlayScrollViewer' -and $customMediaTheme -match 'PART_VerticalScrollBar' -and $customMediaTheme -match 'controls:RtaimeScrollBar') "Media Library scrolling must use the custom overlay scrollbar."
Assert-Condition ($customMediaTheme -match 'Property="IsSelected" Value="True"' -and $customMediaTheme -match 'OperatorAccentBrush' -and $customMediaTheme -match 'SelectionAccent') "Media tile selection must use the cyan border/accent treatment."
Assert-Condition ($customMediaTheme -match 'Property="IsMouseOver" Value="True"' -and $customMediaTheme -match 'OperatorRaisedHoverBrush') "Media tile hover must remain dark and distinct from cyan selection."
Assert-Condition ($customMediaTheme -match '<RowDefinition Height="68" />' -and $customMediaTheme -match 'FontSize="11"' -and $customMediaTheme -match 'OperatorTimecodeFontFamily') "Media tiles must retain the approximately 120x68 thumbnail, 11px filename and compact mono duration treatment."
Assert-Condition ($customMediaTheme -match 'OfflineOverlay' -and $customMediaTheme -match 'Text="OFFLINE"' -and $customMediaTheme -match 'IsOnline') "Offline/missing presentation must overlay the thumbnail without changing tile geometry."

Assert-Condition ($monitorWorkspaceTheme -match 'x:Key="RtaimeMonitorPanel"' -and $monitorWorkspaceTheme -match 'x:Key="RtaimeMonitorHeader"' -and $monitorWorkspaceTheme -match 'x:Key="RtaimeMonitorTransport"') "Preview and Program must share the custom monitor chrome."
Assert-Condition ($monitorWorkspaceTheme -match 'Height" Value="40"' -and $monitorWorkspaceTheme -match 'Height" Value="42"') "Shared monitor chrome must retain the 40px header and 42px transport metrics."
Assert-Condition ($monitorWorkspaceTheme -match 'x:Key="RtaimeProductionLowerPanel"' -and $monitorWorkspaceTheme -match 'x:Key="RtaimeProductionSceneItem"' -and $monitorWorkspaceTheme -match 'x:Key="RtaimeProductionOutputItem"' -and $monitorWorkspaceTheme -match 'RtaimeProductionStatusDot') "Lower production panels must use the shared compact mockup chrome."

Assert-Condition ($theme -match 'Source="OperatorTokens\.xaml"') "Operator theme must load the shared design-token dictionary."
foreach ($token in @("OperatorFontFamily", "OperatorTimecodeFontFamily", "OperatorTopBarHeight", "OperatorNavigationWidth", "OperatorMediaPanelWidth", "OperatorInspectorWidth", "OperatorTimelineHeight", "OperatorRegionGap", "OperatorControlHeight", "OperatorCompactControlHeight", "OperatorRadiusPanel", "OperatorRadiusControl", "OperatorColorTopBar", "OperatorColorNavigation", "OperatorColorSurface", "OperatorColorAlternateSurface", "OperatorColorRaisedSurface", "OperatorColorRaisedHover", "OperatorColorBorder", "OperatorColorBorderStrong", "OperatorColorText", "OperatorColorSecondaryText", "OperatorColorMutedText", "OperatorColorAccent", "OperatorColorPreview", "OperatorColorProgram", "OperatorColorHealthy", "OperatorColorWarning", "OperatorColorError", "OperatorColorTimeline", "OperatorColorGraphics", "OperatorColorAudio")) {
	Assert-Condition ($tokens -match [Regex]::Escape($token)) "Operator design token '$token' is required."
}

foreach ($contractValue in @(
	'<sys:Double x:Key="OperatorTopBarHeight">60</sys:Double>',
	'<sys:Double x:Key="OperatorNavigationWidth">92</sys:Double>',
	'<sys:Double x:Key="OperatorMediaPanelWidth">400</sys:Double>',
	'<sys:Double x:Key="OperatorInspectorWidth">340</sys:Double>',
	'<sys:Double x:Key="OperatorTimelineHeight">320</sys:Double>',
	'<sys:Double x:Key="OperatorRegionGap">6</sys:Double>',
	'<sys:Double x:Key="OperatorControlHeight">30</sys:Double>',
	'<sys:Double x:Key="OperatorCompactControlHeight">26</sys:Double>',
	'<CornerRadius x:Key="OperatorRadiusPanel">4</CornerRadius>',
	'<CornerRadius x:Key="OperatorRadiusControl">3</CornerRadius>',
	'<Color x:Key="OperatorColorBackground">#071017</Color>',
	'<Color x:Key="OperatorColorTopBar">#09131B</Color>',
	'<Color x:Key="OperatorColorNavigation">#0B151E</Color>',
	'<Color x:Key="OperatorColorSurface">#101B24</Color>',
	'<Color x:Key="OperatorColorAlternateSurface">#121F29</Color>',
	'<Color x:Key="OperatorColorRaisedSurface">#16242F</Color>',
	'<Color x:Key="OperatorColorRaisedHover">#1C2D38</Color>',
	'<Color x:Key="OperatorColorBorder">#25343F</Color>',
	'<Color x:Key="OperatorColorBorderStrong">#314553</Color>',
	'<Color x:Key="OperatorColorText">#EEF3F6</Color>',
	'<Color x:Key="OperatorColorSecondaryText">#9AA8B4</Color>',
	'<Color x:Key="OperatorColorMutedText">#6F7D89</Color>',
	'<Color x:Key="OperatorColorAccent">#16CDD3</Color>',
	'<Color x:Key="OperatorColorHealthy">#29D38B</Color>',
	'<Color x:Key="OperatorColorProgram">#FF4D52</Color>',
	'<Color x:Key="OperatorColorWarning">#F6B84A</Color>',
	'<Color x:Key="OperatorColorTimeline">#3478D4</Color>',
	'<Color x:Key="OperatorColorGraphics">#7259D7</Color>',
	'<Color x:Key="OperatorColorAudio">#22A977</Color>'
)) {
	Assert-Condition ($tokens -match [Regex]::Escape($contractValue)) "Operator mockup contract value is required: $contractValue"
}
foreach ($resource in @("OperatorPreviewBrush", "OperatorProgramBrush", "OperatorArmedBrush", "OperatorHealthyBrush", "OperatorWarningBrush", "OperatorErrorBrush", "OperatorEvidenceBadge", "OperatorFocusVisual", "OperatorToolbar", "OperatorToggleButton", "OperatorSourceItem", "OperatorMeter", "OperatorTimelineSlider", "OperatorPreviewTally", "OperatorProgramTally", "OperatorTopBar", "OperatorShellRegion", "OperatorTransportBar", "StatusPill", "MetricMeter", "WorkspaceNavItem", "PanelHeader", "SectionDivider", "OperatorVerticalSplitter", "OperatorHorizontalSplitter", "OperatorLoadingState", "OperatorErrorState")) {
	Assert-Condition ($theme -match [Regex]::Escape($resource)) "Operator theme resource '$resource' is required."
}

$operatorShellRegionIndex = $theme.IndexOf('x:Key="OperatorShellRegion"', [StringComparison]::Ordinal)
$operatorNavigationRailIndex = $theme.IndexOf('x:Key="OperatorNavigationRail"', [StringComparison]::Ordinal)
Assert-Condition ($operatorShellRegionIndex -ge 0 -and $operatorNavigationRailIndex -gt $operatorShellRegionIndex) "OperatorShellRegion must be declared before OperatorNavigationRail because its StaticResource BasedOn is resolved during WPF startup."

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
Assert-Condition ($minWidth -le [Math]::Floor(1920 / 2.0)) "Operator minimum width must remain usable at 200% scaling on 1920x1080."
Assert-Condition ($minHeight -le [Math]::Floor(1080 / 2.0)) "Operator minimum height must remain usable at 200% scaling on 1920x1080."
Assert-Condition ($shell -match 'CompactViewportWidth' -and $shell -match 'IsCompactViewport' -and $shell -match 'CompactLowerPanelHeight') "Operator shell must provide a presentation-only compact workspace mode for constrained high-DPI viewports."
Assert-Condition ($window -match '<controls:RtaimeScrollViewer[^>]+VerticalScrollBarVisibility="Auto"') "Operator must preserve vertical access through the custom scroll surface when DPI scaling reduces logical workspace height."

Assert-Condition ($window -match 'ItemsSource="\{Binding Sources\}"') "Operator must expose the source bank as a bound collection."
Assert-Condition ($window -match 'Style="\{StaticResource RtaimeSourceBank\}"' -and $customInputTheme -match 'x:Key="RtaimeSourceBank"') "Source bank must use the shared custom rtaime collection style."
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
Assert-Condition ($previewViewer -match 'RtaimeMonitorPanel' -and $previewViewer -match 'RtaimeStatusKind\.Preview') "Preview viewer must use shared monitor chrome and Preview semantics."
Assert-Condition ($programViewer -match 'RtaimeMonitorPanel' -and $programViewer -match 'RtaimeStatusKind\.Program') "Program viewer must use shared monitor chrome and Program semantics."
Assert-Condition ($previewViewer -match 'Grid\.Row="0".+RtaimeMonitorHeader' -and $previewViewer -match 'Grid\.Row="2".+RtaimeMonitorTransport') "Preview viewer must use the 40px header and 42px custom transport."
Assert-Condition ($programViewer -match 'Grid\.Row="0".+RtaimeMonitorHeader' -and $programViewer -match 'Grid\.Row="2".+RtaimeMonitorTransport') "Program viewer must use the 40px header and 42px custom transport."
Assert-Condition ($window -match 'Monitoring\.PreviewImage') "Operator Preview must bind the independent monitoring image."
Assert-Condition ($window -match 'Monitoring\.ProgramImage') "Operator Program must bind the independent monitoring image."
Assert-Condition ($previewViewer -match '<Image\s' -and $programViewer -match '<Image\s') "Preview and Program viewers must render monitoring with WPF Image surfaces."
Assert-Condition ($previewViewerCode -match 'PreviewViewer : MonitorView' -and $programViewerCode -match 'ProgramViewer : MonitorView') "Preview and Program must reuse the shared monitor presentation component."
Assert-Condition ($monitorView -match 'Stretch\.Uniform' -and $monitorView -match '"FIT"' -and $monitorView -match '"50%"' -and $monitorView -match '"100%"') "Production monitors must provide aspect-safe Fit, 50 percent and 100 percent presentation modes."
Assert-Condition ($previewViewer -match 'ShowSafeArea' -and $previewViewer -match 'ShowCenterMark' -and $previewViewer -match 'ShowGrid' -and $programViewer -match 'ShowSafeArea' -and $programViewer -match 'ShowCenterMark' -and $programViewer -match 'ShowGrid') "Production monitors must expose independent Safe Area, Center Mark and Grid overlays."
Assert-Condition ($previewViewer -match 'DisplayTimecode' -and $programViewer -match 'DisplayTimecode' -and $monitorView -match 'IsTransportSource') "Production monitor timecode must be shown only when the loaded transport source matches the viewed source."
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
Assert-Condition ($timeline -match 'ItemsSource="\{Binding VisibleItems\}"' -and $timeline -match 'DataContext\.VisibleCues' -and $timelineViewModel -match 'RefreshVisibleItems' -and $timelineViewModel -match 'RefreshViewportProjection') "Timeline rendering must project only viewport-visible clips and cues."
Assert-Condition ($timelineViewModel -match 'SelectedItems' -and $timelineViewModel -match 'HasMultipleItemSelection' -and $timelineCode -match 'ModifierKeys\.Control' -and $timelineCode -match 'ModifierKeys\.Shift') "Timeline clip selection must support explicit multi-selection without a second state-management layer."
Assert-Condition ($timelineViewModel -match 'TimelineTrackViewModel : INotifyPropertyChanged' -and $timelineViewModel -match 'TimelineCueViewModel[\s\S]+INotifyPropertyChanged' -and $timeline -match 'Binding IsActive' -and $timeline -match 'Binding IsSelected') "Timeline must visually distinguish the active track, selected items and focused cue."
Assert-Condition ($timelineViewModel -match 'RestoreSelection' -and $timelineViewModel -match 'selectedItemIds' -and $timelineViewModel -match 'selectedCueId') "Timeline selection/focus must survive projection rebuilds when stable identities remain available."
Assert-Condition ($windowCode -match 'MediaPool\.SelectTimelineItems\(selection\.Items, selection\.Item\)' -and $mediaPool -match 'BuildTimelineMultiSelectionInspector' -and $mediaPool -match 'timeline\.selection\.' -and $mediaPool -match 'MIXED') "Timeline multi-selection must project into the existing shared Inspector with common/MIXED semantics."
Assert-Condition ($timelineViewModel -match 'BeginTrimPreview' -and $timelineViewModel -match 'PreviewTrim' -and $timelineViewModel -match 'CancelTrimPreview' -and $timeline -match 'EffectiveInPointFrame' -and $timeline -match 'EffectiveOutPointFrame' -and $timeline -match 'HasTrimPreview') "Timeline trim drag must render a local preview before committing through the existing marker commands."
Assert-Condition ($timelineCode -match 'LostMouseCapture' -and $timelineCode -match 'Key\.Escape' -and $timelineCode -match 'CancelTrimPreview') "Timeline trim preview must cancel safely on capture loss or Escape."
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
Assert-Condition ($keyboard -match 'new\("sync".+Key\.F5' -and $keyboard -match 'new\("play-pause".+Key\.Space') "Central keyboard registry must expose synchronization and Preview Play/Pause."
Assert-Condition ($keyboard -match 'previewTransportContext' -and $keyboard -match '@operator\.PreviewSourceId' -and $keyboard -match 'mediaDeck\.SourceId' -and $keyboard -match 'ContextGuardCommand') "Media transport shortcuts must be gated to the loaded source only while that source is confirmed Preview."
Assert-Condition ($keyboard -match 'new\("auto".+Key\.Return, ModifierKeys\.None, @operator\.DissolveCommand' -and $keyboard -match 'new\("cut".+Key\.Return, ModifierKeys\.Control, @operator\.CutCommand') "Central keyboard registry must expose Enter=AUTO and Ctrl+Enter=CUT."
Assert-Condition ($keyboard -match 'new\("delete-cue".+Key\.Delete' -and $deck -notmatch '<KeyBinding Key="Delete"') "Delete must route through the central shortcut registry rather than a Media Deck-local binding."
Assert-Condition ($keyboard -match 'FindConflicts' -and $keyboard -match 'Operator keyboard shortcut conflict') "Central keyboard registry must reject conflicting bindings."
Assert-Condition ($keyboard -match 'TryHandle\(KeyEventArgs args\)' -and $windowCode -match 'OnPreviewKeyDown\(KeyEventArgs e\)' -and $windowCode -match 'Shortcuts\.TryHandle\(e\)') "Window-level shortcuts must route through PreviewKeyDown so unmodified production keys do not depend on WPF KeyGesture validation."
Assert-Condition ($keyboard -notmatch 'new KeyBinding\(' -and $keyboard -notmatch 'KeyGesture' -and $windowCode -notmatch 'Shortcuts\.Apply\(InputBindings\)') "Central shortcut routing must not construct unsupported WPF KeyGesture bindings."
Assert-Condition ($keyboard -match 'args\.IsRepeat') "Production shortcut routing must suppress key-repeat command storms."
Assert-Condition ($keyboard -match 'IsTextEntryContext' -and $keyboard -match 'TextBoxBase' -and $keyboard -match 'PasswordBox') "Production shortcuts must be suppressed while the operator is typing."
Assert-Condition ($deck -notmatch '<KeyBinding Key="P"' -and $deck -notmatch '<KeyBinding Key="S"' -and $deck -notmatch '<KeyBinding Key="I"' -and $deck -notmatch '<KeyBinding Key="O"' -and $deck -notmatch '<KeyBinding Key="M"') "Media Deck must not duplicate centralized production shortcuts."
Assert-Condition ($window -match 'KeyboardNavigation.TabNavigation="Cycle"') "Showcase keyboard navigation must stay inside the Operator workspace."
Assert-Condition ($customInputTheme -match 'RtaimeVerticalSplitter' -and $customInputTheme -match 'RtaimeHorizontalSplitter' -and $customInputTheme -match 'KeyboardNavigation.IsTabStop' -and $window -match 'Use arrow keys while focused') "Workspace splitters must expose custom visible keyboard focus and keyboard resize discoverability."
Assert-Condition ($customInputTheme -match 'controls:RtaimeComboBox' -and $customInputTheme -match 'controls:RtaimeCheckBox' -and $customInputTheme -match 'controls:RtaimeTabItem') "Custom interactive controls must own shared keyboard-focus styling."
Assert-Condition ($operatorXaml -notmatch '<Storyboard|<DoubleAnimation|<ColorAnimation|<ThicknessAnimation') "Production Operator XAML must not introduce decorative animation."
Assert-Condition ($window -match 'x:Name="SynchronizeButton"' -and $windowCode -match 'SynchronizeButton\.Focus\(\)') "Initial keyboard focus must land on the synchronization action."
Assert-Condition ($window -match 'ToolTip="Prepare the deterministic showcase state' -and $window -match 'ToolTip="Take confirmed Preview to Program\."') "Primary showcase actions must expose consistent explanatory tooltips."
Assert-Condition ($theme -match '<Style TargetType="ToolTip">' -and $theme -match 'ToolTipService\.InitialShowDelay') "Showcase tooltips must use the shared theme and bounded presentation timing."
Assert-Condition ($window -match 'No production sources available' -and $window -match 'Binding Sources\.Count') "Source Bin must expose an explicit empty state instead of a blank panel."
Assert-Condition ($window -match 'Text="\{Binding LastEvent\}"' -and $window -match 'OperatorErrorBadge' -and $window -match 'Binding LastError') "Footer must separate normal operator status from active error presentation."
Assert-Condition ($shell -match '\.tmp' -and $shell -match 'File\.Move\(temporaryPath, _path, overwrite: true\)') "Operator layout persistence must use temporary-file replacement to avoid partial JSON state."
Assert-Condition ($window -match 'OperatorLoadingState' -and $window -match 'OperatorErrorState') "Loading and error presentation must use shared Operator state styles."
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
Assert-Condition ($programViewer -match 'Text="COMMIT"' -and $programViewer -match 'Binding CommitStatus') "Program viewer must expose authoritative commit status."
Assert-Condition ($window -match 'Binding CommitStatus') "Program workspace must bind authoritative commit status."
Assert-Condition ($programViewer -match 'Binding TransitionStatus' -and $window -match 'Binding TransitionStatus') "Program workspace must expose transition state."
Assert-Condition ($productionWorkspaceSurface -match 'RtaimeStatusKind\.Preview' -and $productionWorkspaceSurface -match 'Binding IsPreview' -and $productionWorkspaceSurface -match 'Content="PVW"') "Transition workspace must make the authoritative Preview take target explicit."
Assert-Condition ($productionWorkspaceSurface -match 'Command="\{Binding CutCommand\}"' -and $productionWorkspaceSurface -match 'ToolTip="Take confirmed Preview to Program\."') "CUT control must explicitly describe Preview-to-Program semantics."
Assert-Condition ($productionWorkspaceSurface -match 'Command="\{Binding DissolveCommand\}"' -and $productionWorkspaceSurface -match 'ToolTip="Dissolve confirmed Preview to Program\."') "AUTO control must explicitly describe Preview-to-Program semantics."
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
Assert-Condition ($documentation -match '200%') "Operator UI documentation must record 200% DPI qualification."
Assert-Condition ($documentation -match 'Runtime Health & Performance HUD') "Operator UI documentation must record the Runtime Health & Performance HUD."
Assert-Condition ($documentation -match 'PASS / FAIL / UNVERIFIED') "Runtime Health & Performance HUD documentation must preserve evidence-state semantics."
Assert-Condition ($documentation -match 'Visible AI Showcase') "Operator UI documentation must record the Visible AI Showcase."
Assert-Condition ($documentation -match 'Person Segmentation Highlight') "Visible AI Showcase documentation must identify the real existing segmentation capability."
Assert-Condition ($window -match 'AutomationProperties\.Name="Open Demo Production"' -and $window -match 'DemoProduction\.OpenCommand' -and $window -match 'Text="V1 PRODUCTION"') "Demo Production Package must expose a one-click custom-control Open Demo Production action."
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
$mediaReferenceWidth = 400
$mediaBorderWidth = 2
$mediaPadding = 11 * 2
$mediaInnerWidth = $mediaReferenceWidth - $mediaBorderWidth - $mediaPadding
$threeTileWidth = (3 * 120) + (2 * 8)
Assert-Condition ($mediaInnerWidth -eq 376 -and $threeTileWidth -eq $mediaInnerWidth) "Reference Media Library geometry must fit exactly three 120px tiles with two 8px gaps."
Assert-Condition ($mediaLibrarySurface -match 'Padding="11"' -and $theme -match 'x:Key="OperatorPanel"[\s\S]+BorderThickness" Value="1"') "Media Library must preserve an effective 12px inset from the 400px region edge."
Assert-Condition (($mediaLibrarySurface | Select-String -Pattern '<RowDefinition Height="34" />' -AllMatches).Matches.Count -ge 2 -and $mediaLibrarySurface -match '<RowDefinition Height="32" />') "Media Library header/search/filter rows must be 34/34/32px."
Assert-Condition ($mediaLibrarySurface -match 'ItemWidth="120"' -and $mediaLibrarySurface -match 'ItemHeight="104"' -and $mediaLibrarySurface -match 'HorizontalSpacing="8"' -and $mediaLibrarySurface -match 'VerticalSpacing="10"') "Media Library grid must retain 120px tiles with 8px horizontal and 10px vertical spacing."
Assert-Condition ($virtualizingWrapPanel -match 'HorizontalSpacingProperty' -and $virtualizingWrapPanel -match 'VerticalSpacingProperty' -and $virtualizingWrapPanel -match '\(viewportWidth \+ horizontalSpacing\) / columnStride' -and $virtualizingWrapPanel -match 'rowStride') "VirtualizingWrapPanel must include tile spacing in row/column realization math."
Assert-Condition ($mediaLibrarySurface -match 'controls:RtaimeSearchBox' -and $mediaLibrarySurface -match 'controls:RtaimeMediaTile' -and $mediaLibrarySurface -match 'controls:RtaimeContextMenu') "Media Library must use custom search, tile and context-menu controls."
Assert-Condition ($mediaLibrarySurface -notmatch '<(Button|ToggleButton|CheckBox|RadioButton|TextBox|ComboBox|TabControl|TabItem|ListBox|ListView|TreeView|DataGrid|Slider|ProgressBar|ScrollBar|ScrollViewer|GridSplitter|Menu|MenuItem|ContextMenu|ToolBar)(\s|/|>)') "Media Library feature XAML must expose no directly visible stock WPF interactive chrome."
Assert-Condition ($mediaLibrarySurface -match 'Text="MEDIA LIBRARY"' -and $mediaLibrarySurface -match 'MediaPool\.SearchText' -and $mediaLibrarySurface -match 'MediaPool\.SelectedCategory' -and $mediaLibrarySurface -match 'MediaPool\.SelectedFilter') "Media Pool must expose the mockup header, category, search and filter controls."
Assert-Condition ($window -match 'MediaPool\.GridViewCommand' -and $window -match 'MediaPool\.ListViewCommand') "Media Pool must support Grid and List presentation."
Assert-Condition ($mediaPool -match 'ImageSource\? Thumbnail' -and $mediaPool -match 'source\.Thumbnail' -and $mediaLibrarySurface -match 'Thumbnail="\{Binding Thumbnail\}"') "Media Pool must reuse existing verified source thumbnails without adding metadata extraction."
Assert-Condition ($window -match 'MediaPool\.FilteredItems' -and $window -match 'MediaPool\.SelectedItem') "Media Pool presentation must bind the bounded selection projection."
Assert-Condition ($window -match 'SelectionMode="Extended"' -and $windowCode -match 'OnMediaPoolSelectionChanged' -and $mediaPool -match 'UpdateSelection' -and $mediaPool -match 'SelectedItems') "Media Library must support explicit multi-selection without adding production authority."
Assert-Condition ($window -match 'VirtualizingWrapPanel' -and $window -match 'VirtualizingPanel\.VirtualizationMode="Recycling"' -and $virtualizingWrapPanel -match 'VirtualizingPanel' -and $virtualizingWrapPanel -match 'CleanUpItems') "Media Library Grid/List surfaces must virtualize and recycle asset containers."
Assert-Condition ($virtualizingWrapPanel -match 'ResolveGenerator\(\)' -and $virtualizingWrapPanel -match 'if \(generator is null\)' -and $virtualizingWrapPanel -match 'QueueGeneratorRetry\(\)' -and $virtualizingWrapPanel -match 'DispatcherPriority\.Loaded' -and $virtualizingWrapPanel -notmatch 'ItemsControl\.GetItemsOwner\(this\)\?\.ItemContainerGenerator') "VirtualizingWrapPanel must defer realization until its panel-owned generator is ready."
Assert-Condition ($virtualizingWrapPanel -match 'if \(itemIndex < 0\)' -and $virtualizingWrapPanel -match 'IRecyclingItemContainerGenerator' -and $virtualizingWrapPanel -match '\.Recycle\(position, 1\)') "VirtualizingWrapPanel cleanup must avoid generator removal for unmapped children and recycle mapped containers when available."
Assert-Condition ($mediaLibrarySurface -match 'DurationLabel' -and $mediaLibrarySurface -match 'FileTypeLabel' -and $customMediaTheme -match 'Text="OFFLINE"') "Media Library asset cards must expose duration/type and a textual offline state."
Assert-Condition ($window -match 'LOADING ASSETS' -and $window -match 'MediaPool\.ErrorState' -and $mediaPool -match 'IsLoading' -and $mediaPool -match 'HasError') "Media Library must expose loading and error states."
Assert-Condition ($mediaPool -match 'MaxProjectedItems = 4096' -and $mediaPool -match 'Take\(MaxProjectedItems\)') "Media Library projection must remain explicitly bounded while supporting large asset sets."
Assert-Condition ($mediaPool -match 'StringComparison\.OrdinalIgnoreCase' -and $mediaPool -match 'OrderBy\(item => item\.Category, StringComparer\.Ordinal\)') "Media Pool search/filter ordering must be deterministic."
Assert-Condition ($mediaPool -match 'ImportCommand => _mediaDeck\.OpenCommand') "Media Pool import must reuse the existing Media Deck open path."
Assert-Condition ($mediaPool -match 'MediaAssetDragPayload' -and $windowCode -match 'typeof\(MediaAssetDragPayload\)') "Media Library drag/drop must use a typed asset payload that preserves multi-selection context."
Assert-Condition ($windowCode -match 'OnMediaPoolPreviewActionClick' -and $windowCode -match 'OnMediaPoolTimelineActionClick' -and $windowCode -match 'OnMediaPoolCueActionClick' -and $windowCode -match 'OnMediaPoolRevealActionClick' -and $windowCode -match 'OnMediaPoolPropertiesActionClick') "Media Library context actions must be wired through the existing Operator paths."
Assert-Condition ($keyboard -match '"media-search"' -and $keyboard -match 'Key\.F, ModifierKeys\.Control' -and $windowCode -match 'FocusMediaSearchAsync' -and $window -match 'x:Name="MediaSearchBox"') "Ctrl+F must focus Media Library search through the central Operator shortcut registry."
Assert-Condition ($mediaPool -notmatch 'FileSystemWatcher|Directory\.EnumerateFiles|Directory\.GetFiles|Mp4LocalMediaMetadataReader') "Media Library must not introduce a second media indexer, directory crawler or metadata probe."
Assert-Condition ($mediaPool -match 'DropToPreviewAsync' -and $mediaPool -match '_operator\.SetPreviewCommand\.Execute\(null\)') "Media Pool Preview drag/drop must reuse the existing authoritative Preview command path."
Assert-Condition ($windowCode -match 'DragDropEffects\.None' -and $windowCode -match 'CanDropToPreview') "Invalid Media Pool Preview drops must be rejected safely."
Assert-Condition ($window -match 'OnTimelineDrop' -and $windowCode -match 'MediaDeck\.RefreshCommand\.Execute\(null\)') "Loaded Clip Timeline drop must reuse the existing Media Deck context rather than create another timeline."
Assert-Condition ($timelineViewModel -match 'MediaPoolItemKind\.Clip' -and $timelineViewModel -match 'MediaPoolItemKind\.Audio' -and $timelineViewModel -match 'MediaPoolItemKind\.Graphics' -and $timelineViewModel -match 'TimelineTrackCategory\.Overlay') "Timeline drag/drop must enforce semantic Clip/Audio/Graphics track compatibility."
Assert-Condition ($timelineViewModel -match 'PROJECTED ·' -and $timelineViewModel -match 'CanTrim,?\s*false|false\)') "Unsupported timed Audio/Graphics semantics must remain explicitly projected and non-trimmable."
Assert-Condition ($timelineViewModel -match '_activeTimelineContextReference' -and $timelineViewModel -match 'snapshot\.Probe\?\.AssetId') "Timeline-only resource projections must reset when the loaded media asset or source context changes."
Assert-Condition ($windowCode -match 'Timeline\.SelectionChanged \+= OnTimelineSelectionChanged' -and $windowCode -match 'MediaPool\.SelectTimelineItem' -and $windowCode -match 'MediaPool\.SelectTimelineCue') "Timeline selection must project into the existing context Inspector."
Assert-Condition ($windowCode -match 'MediaDeck\.SelectedCue = MediaDeck\.Cues\.FirstOrDefault' -and $inspectorSurface -match 'MediaDeck\.RenameCueCommand' -and $inspectorSurface -match 'MediaDeck\.DeleteCueCommand') "Selected timeline cues must reuse existing Media Deck rename/delete commands in the shared Inspector."
foreach ($timelinePropertyId in @("timeline.item.label", "timeline.item.start", "timeline.item.in", "timeline.item.out", "timeline.cue.name", "timeline.cue.time")) {
	Assert-Condition ($mediaPool -match [Regex]::Escape($timelinePropertyId)) "Timeline Inspector stable property id '$timelinePropertyId' is required."
}
Assert-Condition ($timelineDocumentation -match 'Runtime remains the execution authority' -and $timelineDocumentation -match 'No timed Graphics, Audio, AI or Control mutation is invented') "Layered timeline documentation must preserve the production authority boundary."
foreach ($propertyId in @("source.name", "clip.duration", "clip.playback.autoplay", "audio.gain", "graphics.position.x", "ai.provider", "ai.fallback")) {
	Assert-Condition ($mediaPool -match [Regex]::Escape($propertyId)) "Inspector stable property id '$propertyId' is required."
}
Assert-Condition ($inspectorSurface -match 'METADATA is read-only' -and $inspectorSurface -match 'DESIRED edits' -and $inspectorSurface -match 'COMMITTED') "Inspector must visually distinguish metadata, desired configuration and committed state."
Assert-Condition ($inspectorHost -match 'MediaDeck\.ApplyPlaybackPolicyCommand' -and $inspectorHost -match 'ApplyAudioGainCommand' -and $inspectorHost -match 'ApplyGraphicsCommand') "Inspector edits must reuse existing product command paths."
Assert-Condition ($mediaPool -notmatch 'OperatorControlClient|NamedPipe|RuntimeHost|ControlHost|AIHost') "Media Pool selection/Inspector projection must not acquire production host or transport authority."
Assert-Condition ($window -match '<local:OperatorInspectorControl' -and $inspectorHost -match 'Header="Inspector"' -and $inspectorHost -match 'Header="Processing"' -and $inspectorHost -match 'Header="Metadata"') "Inspector must retain the mockup top-level Inspector, Processing and Metadata tabs."
Assert-Condition (($inspectorHost | Select-String -Pattern 'Height" Value="40"' -AllMatches).Matches.Count -ge 1 -and $inspectorHost -match 'InspectorTopTab') "Inspector top tab row must retain the 40px mockup height."
foreach ($category in @("Video", "Audio", "Transform", "FX")) {
	Assert-Condition ($inspectorHost -match ('Header="' + $category + '"')) "Inspector property category '$category' is required."
}
Assert-Condition ($inspectorHost -match 'InspectorCategoryTab' -and $inspectorHost -match 'Height" Value="34"') "Inspector property-category tabs must retain the compact 34px height."
Assert-Condition ($inspectorHost -match 'Width="72"' -and $inspectorHost -match 'Height="42"' -and $inspectorHost -match 'InspectorTitle' -and $inspectorHost -match 'InspectorDetail' -and $inspectorHost -match 'RtaimeIconMoreGeometry') "Inspector selection header must expose the 72x42 thumbnail, identity/technical lines and overflow affordance."
Assert-Condition ($inspectorHost -match '<ColumnDefinition Width="96" />' -and $inspectorHost -match 'MinHeight="32"') "Inspector property rows must retain the approximately 96px label column and 30-34px row density."
Assert-Condition ($inspectorHost -match 'Content="BASIC PROPERTIES"' -and $inspectorHost -match 'Content="EFFECTS"' -and $inspectorHost -match 'TRANSPORT &amp; CONTROLS') "Inspector must expose flat Basic Properties, Effects and Transport & Controls sections."
Assert-Condition ($inspectorHost -match 'InspectorEffectTemplate' -and $inspectorHost -match 'RtaimeIconCompositingGeometry' -and $inspectorHost -match 'Content="\+ Add Effect"' -and $inspectorHost -match 'RtaimeIconMoreGeometry') "Inspector Effects rows must expose icon, state/context affordance and a compact Add Effect extension surface."
Assert-Condition ($inspectorHost -match 'BorderThickness="1,0,0,0"' -and $inspectorHost -notmatch 'OperatorShellRegion') "Inspector must be a continuous flat panel with only the 1px left divider and no nested shell card."
Assert-Condition ($window -match 'Grid\.RowSpan="2" Grid\.Column="6"' -and $shell -match 'DefaultRightPanelWidth = 340') "Inspector must remain full-height beside the timeline with the exact 340px default shell width."
Assert-Condition ($inspectorHost -match 'ContentTemplate="\{StaticResource InspectorSelectionHeaderTemplate\}"' -and $inspectorHost -match 'VerticalScrollBarVisibility="Auto"' -and $inspectorHost -match '<RowDefinition Height="72" />') "Inspector context changes must remain inside a fixed header plus own scroll region without changing macro shell geometry."
Assert-Condition ($customInputTheme -match 'x:Name="SelectionBar"' -and $customInputTheme -match 'Height="2"' -and $customInputTheme -match 'OperatorAccentBrush') "Inspector custom tabs must inherit the 2px cyan active underline from the rtaime tab control."
Assert-Condition ($inspectorHost -match 'Binding IsMixed' -and $inspectorHost -match 'Text="N/A"' -and $inspectorHost -match 'METADATA is read-only' -and $inspectorHost -match 'COMMITTED') "Inspector must render mixed, unavailable, read-only and committed values explicitly."
Assert-Condition ($inspectorHost -notmatch '<(Button|ToggleButton|CheckBox|RadioButton|TextBox|ComboBox|TabControl|TabItem|ListBox|ListView|TreeView|DataGrid|Slider|ProgressBar|ScrollBar|ScrollViewer|GridSplitter|Menu|MenuItem|ContextMenu|ToolBar)(\s|/|>)') "Inspector feature XAML must expose no directly visible stock WPF tab, input or scrolling controls."
Assert-Condition ($mediaPool -match 'BuildMultiSelectionInspector' -and $mediaPool -match '"MIXED"' -and $mediaPool -match 'HasMultipleSelection') "Inspector multi-selection must project common values and explicit mixed state."
Assert-Condition ($inspectorHost -match 'ValidatesOnExceptions=True' -and $inspectorHost -match 'Validation.ErrorTemplate') "Inspector numeric editors must present inline validation failures."
foreach ($resetCommand in @("ResetAutoPlayCommand", "ResetEndBehaviorCommand", "ResetAudioGainCommand", "ResetGraphicsPositionXCommand", "ResetGraphicsPositionYCommand", "ResetGraphicsScaleCommand")) {
	Assert-Condition ($mediaPool -match [Regex]::Escape($resetCommand) -and $inspectorHost -match [Regex]::Escape($resetCommand)) "Inspector reset command '$resetCommand' must be implemented and bound."
}
Assert-Condition ($mediaPool -match 'SupportsTransformRotation => false' -and $mediaPool -match 'SupportsTransformAnchor => false' -and $mediaPool -match 'SupportsTransformCrop => false' -and $inspectorHost -match 'Rotation' -and $inspectorHost -match 'Anchor' -and $inspectorHost -match 'Crop') "Unsupported transform capabilities must remain explicit and non-invented."
Assert-Condition ($mediaPool -match 'SupportsEffectOrdering => false' -and $inspectorHost -match 'UnsupportedEffectOrderingText') "Effect ordering must remain capability-gated when unavailable."
Assert-Condition ($mediaPool -match '_groupExpansion' -and $mediaPool -match 'SelectionContextKey' -and $inspectorHost -match 'IsTransformExpanded' -and $inspectorHost -match 'IsMetadataExpanded') "Inspector collapse state must be retained per selection context."
Assert-Condition ($documentation -match 'Media Pool & Context Inspector' -and $documentation -match 'METADATA' -and $documentation -match 'DESIRED' -and $documentation -match 'COMMITTED') "Operator documentation must record Media Pool and Inspector state semantics."
Assert-Condition ($mediaLibraryDocumentation -match 'virtualiz' -and $mediaLibraryDocumentation -match 'multi-select' -and $mediaLibraryDocumentation -match 'Ctrl\+F' -and $mediaLibraryDocumentation -match 'no second media index') "Media Library documentation must record scalability, selection, shortcut and indexing boundaries."

Write-Host "Operator UI policy verification PASS"
Write-Host "Operator authority: remote Client SDK only"
Write-Host "Design system: tokens, semantic tallies, reusable controls and keyboard focus verified"
Write-Host "Showcase UX: keyboard cycle, action tooltips, empty/error states, graceful shutdown and crash presentation verified"
Write-Host "DPI qualification: PerMonitorV2; 1920x1080 reference layout supports 100%, 125%, 150% and 200% scaling invariants"
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
foreach ($region in @("TopBar", "WorkspaceNavigation", "LeftToolRegion", "CenterWorkspace", "RightInspectorRegion", "LowerTimelineRegion", "BottomTransportRegion")) {
	Assert-Condition ($window -match ('x:Name="' + [Regex]::Escape($region) + '"')) "Production shell region '$region' must remain explicit and addressable."
}

Assert-Condition ($window -match '<RowDefinition Height="\{StaticResource OperatorTopBarHeight\}" />' -and $tokens -match '<sys:Double x:Key="OperatorTopBarHeight">60</sys:Double>') "Mockup shell top bar must be exactly 60px at the reference viewport."
Assert-Condition ($shell -match 'DefaultLeftPanelWidth = 400' -and $shell -match 'DefaultRightPanelWidth = 340' -and $shell -match 'DefaultLowerPanelHeight = 320') "Mockup shell reset geometry must restore 400px media, 340px inspector and 320px timeline dimensions."
Assert-Condition ($shell -match 'NavigationRailWidth => 92' -and $shell -match 'LeftSplitterWidth.+: 6' -and $shell -match 'RightSplitterWidth.+: 6') "Mockup shell must use the 92px navigation rail and 6px horizontal gutters."
Assert-Condition ($window -match 'Grid\.RowSpan="2" Grid\.Column="0".+OperatorNavigationRail' -and $window -match '<Grid Grid\.RowSpan="2" Grid\.Column="6">') "Navigation and inspector columns must continue through the lower workspace."
Assert-Condition ($window -match 'x:Name="LowerTimelineRegion" Grid\.Row="1" Grid\.Column="2" Grid\.ColumnSpan="3"' -and $window -match 'x:Name="BottomTransportRegion" Grid\.Row="1" Grid\.Column="2" Grid\.ColumnSpan="3"') "Timeline and transport must occupy only the media-through-center span and stop before the inspector."
Assert-Condition ($window -notmatch '<Grid Margin="\{StaticResource OperatorWindowPadding\}">') "The fixed mockup shell must not introduce outer padding that shifts reference boundaries."
Assert-Condition ($window -match 'x:Name="LeftToolRegion"[\s\S]{0,220}Margin="0"' -and $window -match 'x:Name="RightInspectorRegion"') "Reference media and inspector boundaries must not include legacy shell margins."
Assert-Condition ($topBarSurface -match 'controls:RtaimeButton' -and $topBarSurface -match 'controls:RtaimeIconButton' -and $topBarSurface -match 'controls:RtaimeTimecode' -and $topBarSurface -match 'controls:RtaimeStatusBadge') "Top bar must use the own rtaime action, icon, timecode and status controls."
Assert-Condition ($topBarSurface -notmatch '<(Button|ToggleButton|CheckBox|RadioButton|TextBox|ComboBox|TabControl|TabItem|ListBox|ListView|TreeView|DataGrid|Slider|ProgressBar|ScrollBar|ScrollViewer|GridSplitter|Menu|MenuItem|ContextMenu|ToolBar)(\s|/|>)') "Top bar must expose no directly visible stock WPF interactive controls."
Assert-Condition ($navigationSurface -match 'controls:RtaimeNavigationItem' -and $navigationSurface -notmatch '<(Button|ToggleButton|CheckBox|RadioButton|TextBox|ComboBox|TabControl|TabItem|ListBox|ListView|TreeView|DataGrid|Slider|ProgressBar|ScrollBar|ScrollViewer|GridSplitter|Menu|MenuItem|ContextMenu|ToolBar)(\s|/|>)') "Workspace navigation must use only custom rtaime navigation controls."
foreach ($workspace in @("MEDIA", "EDIT", "LIVE", "SCENES", "COMPOSITING", "OUTPUTS", "SETTINGS")) {
	Assert-Condition ($navigationSurface -match ('CommandParameter="' + $workspace + '"')) "Mockup navigation must retain workspace '$workspace'."
}
Assert-Condition ($window -match 'Grid\.RowSpan="2" Panel\.ZIndex="100"' -and $window -match 'Binding StartupComplete') "Startup/recovery presentation must overlay the fixed shell rather than reflow its geometry."
Assert-Condition ($shell -match 'public bool IsFullscreen \{ get; init; \} = true;' -and $windowCode -match 'WindowStyle = WindowStyle\.None') "Fresh layouts must prefer production fullscreen while preserving the existing borderless/windowed implementation."
Assert-Condition ($keyboard -match 'new\("preview-view".+Key\.D1.+shell\.MaximizePreviewCommand') "Preview maximize must be keyboard-accessible through Ctrl+1."
Assert-Condition ($keyboard -match 'new\("program-view".+Key\.D2.+shell\.MaximizeProgramCommand') "Program maximize must be keyboard-accessible through Ctrl+2."
Assert-Condition ($keyboard -match 'new\("dual-view".+Key\.D0.+shell\.RestoreViewersCommand') "Dual-view restore must be keyboard-accessible through Ctrl+0."
Assert-Condition ($shell -match 'PreviewViewerWidth' -and $shell -match 'ProgramViewerWidth' -and (($shell | Select-String -Pattern 'new GridLength\(1, GridUnitType\.Star\)' -AllMatches).Matches.Count -ge 2) -and $shell -match 'ViewerGapWidth.+\? 6 : 0') "Dual Preview/Program mode must use the equal 532/6/532 reference split."
Assert-Condition ($shell -match 'MaximizePreviewCommand' -and $shell -match 'MaximizeProgramCommand' -and $shell -match 'RestoreViewersCommand') "Viewer maximize/restore must remain local presentation commands."
Assert-Condition ($shell -match 'FullscreenPreviewCommand' -and $shell -match 'FullscreenProgramCommand' -and $shell -match '_monitorFullscreenRestoreViewerMode' -and $window -match 'Shell\.FullscreenPreviewCommand' -and $window -match 'Shell\.FullscreenProgramCommand') "Monitor fullscreen must remain transient shell presentation state and restore the prior viewer layout."
Assert-Condition ($shell -match 'PreviewViewerVisibility' -and $shell -match 'ProgramViewerVisibility' -and $productionWorkspaceSurface -match 'Shell\.PreviewViewerVisibility' -and $productionWorkspaceSurface -match 'Shell\.ProgramViewerVisibility') "Maximized production viewers must collapse the inactive viewer and restore it in dual mode."
Assert-Condition ($productionWorkspaceSurface -match 'Height="700"' -and $productionWorkspaceSurface -match '<RowDefinition Height="390" />' -and $productionWorkspaceSurface -match '<RowDefinition Height="304" />') "Edit production workspace must retain the 700px reference height with 390px monitor and 304px lower rows."
Assert-Condition ($productionWorkspaceSurface -match '<ColumnDefinition Width="320" />' -and $productionWorkspaceSurface -match '<ColumnDefinition Width="462" />' -and $productionWorkspaceSurface -match '<ColumnDefinition Width="276" />') "Lower production row must retain the 320/6/462/6/276 mockup split."
Assert-Condition ($productionWorkspaceSurface -match 'Text="SCENE STACK"' -and $productionWorkspaceSurface -match 'Text="OUTPUT ROUTING"' -and $productionWorkspaceSurface -match 'Text="SYSTEM STATUS"') "Lower production row must expose Scene Stack, Output Routing and System Status."
Assert-Condition ($productionWorkspaceSurface -match 'Width="62" Height="36"' -and $productionWorkspaceSurface -match 'Binding DisplayIndex' -and $productionWorkspaceSurface -match 'Binding Remaining') "Scene Stack must retain the compact 62x36 thumbnail, stable index and duration projection."
Assert-Condition ($monitorWorkspaceTheme -match 'x:Key="RtaimeProductionSceneItem"[\s\S]+Property="Height" Value="50"' -and $productionWorkspaceSurface -match 'ItemsSource="{Binding Sources}"') "Scene Stack must fit four compact source-projection rows without adding scene authority."
Assert-Condition ($productionWorkspaceSurface -match 'Command="\{Binding SetPreviewCommand\}"' -and $productionWorkspaceSurface -match 'Command="\{Binding CutCommand\}"' -and $productionWorkspaceSurface -match 'Command="\{Binding DissolveCommand\}"') "Scene Stack actions must reuse the existing Preview, CUT and AUTO command paths."
Assert-Condition ($productionWorkspaceSurface -notmatch '<(Button|ToggleButton|CheckBox|RadioButton|TextBox|ComboBox|TabControl|TabItem|ListBox|ListView|TreeView|DataGrid|Slider|ProgressBar|ScrollBar|ScrollViewer|GridSplitter|Menu|MenuItem|ContextMenu|ToolBar)(\s|/|>)') "Edit production workspace must expose no directly visible stock WPF interactive chrome."
Assert-Condition ($previewViewer -notmatch '<(Button|ToggleButton|ProgressBar|Slider|ScrollViewer)(\s|/|>)' -and $programViewer -notmatch '<(Button|ToggleButton|ProgressBar|Slider|ScrollViewer)(\s|/|>)') "Preview and Program viewer chrome must use only own rtaime interactive controls."
Assert-Condition ($programViewer -match 'Content="ON AIR"' -and $programViewer -match 'Value="LIVE"' -and $programViewer -match 'RtaimeStatusKind\.Program') "Program ON AIR must appear only from the observed LIVE Program state."
Assert-Condition ($previewViewer -match 'PlayPauseCommand' -and $previewViewer -match 'SetInCommand' -and $previewViewer -match 'SetOutCommand' -and $previewViewer -match 'PreviousCueCommand' -and $previewViewer -match 'NextCueCommand') "Preview viewer must expose existing transport, mark and cue commands."
Assert-Condition ($programViewer -notmatch 'PlayPauseCommand|SetInCommand|SetOutCommand|PreviousCueCommand|NextCueCommand') "Program viewer must not acquire Preview transport commands."
Assert-Condition ($window -match 'ProductionFormat="{Binding CurrentFormat}"' -and $window -match 'ColorSpace="N/A"') "Monitor headers must show authoritative runtime format while unavailable color-space telemetry remains explicitly N/A."
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
Assert-Condition ($window -match 'RtaimeVerticalSplitter' -and $window -match 'RtaimeHorizontalSplitter' -and $customInputTheme -match 'Property="ResizeDirection" Value="Columns"' -and $customInputTheme -match 'Property="ResizeDirection" Value="Rows"') "Production shell side panels and lower workspace must remain resizable through custom splitters."
Assert-Condition ($window -match 'Shell\.ToggleLeftPanelCommand' -and $window -match 'Shell\.ToggleRightPanelCommand' -and $window -match 'Shell\.ToggleCenterMaximizeCommand') "Production shell must expose collapse and center-maximize controls."
Assert-Condition ($window -match 'DataContext="\{Binding Timeline, RelativeSource=\{RelativeSource AncestorType=\{x:Type Window\}\}\}"') "The timeline must remain available in the persistent lower workspace."
Assert-Condition ($deck -notmatch '<local:MediaTimelineControl') "Media Deck must not duplicate the shell-hosted timeline."
Assert-Condition ($shell -match 'record OperatorLayoutSettings' -and $shell -match 'Normalize\(\)' -and $shell -match 'Math\.Clamp') "Persisted layout dimensions must be normalized and safely clamped."
Assert-Condition ($shell -match 'ResetLayout\(\)' -and $shell -match 'CreateCanonicalLayouts\(\)\[SelectedWorkspace\]' -and $shell -match 'DefaultLeftPanelWidth = 400' -and $shell -match 'DefaultRightPanelWidth = 340' -and $shell -match 'DefaultLowerPanelHeight = 320') "Reset Layout must always restore the binding mockup reference dimensions."
Assert-Condition ($shell -match 'LocalApplicationData' -and $shell -match 'operator-layout\.json') "Operator layout persistence must use local user UI configuration storage."
Assert-Condition ($shell -match 'Task SaveAsync\(' -and $shell -match 'TaskScheduler\.Default' -and $shell -match 'File\.WriteAllTextAsync') "Operator layout persistence must execute file I/O away from the UI thread."
Assert-Condition ($shell -notmatch 'File\.WriteAllText\(') "Operator layout persistence must not perform synchronous file writes."
Assert-Condition ($windowCode -match 'await Shell\.SaveAsync\(\)') "Operator shutdown must asynchronously flush the latest layout state before closing."
foreach ($layoutProperty in @("LeftPanelWidth", "RightPanelWidth", "LowerPanelHeight", "IsLeftCollapsed", "IsRightCollapsed", "IsFullscreen", "SelectedWorkspace", "WindowPlacement", "ViewerMode", "Workspaces", "CurrentVersion")) {
	Assert-Condition ($shell -match [Regex]::Escape($layoutProperty)) "Operator layout persistence must retain '$layoutProperty'."
}
Assert-Condition ($shell -notmatch 'using rtaime\.(Client|Control|Runtime|Media|AI|Recording)') "Production shell layout state must remain presentation-only and must not acquire production authority dependencies."
Assert-Condition ($windowCode -match 'OnLayoutSplitterDragCompleted' -and $windowCode -match 'Shell\.Save\(\)') "Resizable shell geometry must be persisted after operator layout changes."
Assert-Condition ($shell -match 'record OperatorWindowPlacementSettings' -and $shell -match 'MinimumWidth = 960' -and $shell -match 'MinimumHeight = 500') "Window placement persistence must normalize windowed geometry against the Operator minimum size."
Assert-Condition ($windowCode -match 'ApplyWindowPlacement' -and $windowCode -match 'CaptureWindowPlacement' -and $windowCode -match 'IsWindowPlacementVisible') "Window placement must restore safely and reject off-screen geometry."
Assert-Condition ($shell -match 'NavigationRailWidth => 92' -and $shell -match 'NavigationLabelVisibility => Visibility\.Visible' -and $shell -match 'SecondaryMetricVisibility' -and $shell -match '_viewportWidth < 1480') "Production shell must retain the fixed 92px navigation rail while optional top-bar metrics may compact in smaller windowed viewports."
Assert-Condition ($window -match 'Text="LIVE"' -and $window -match 'Text="UNVERIFIED"' -and $window -match 'External transmission/on-air state is not authoritative') "LIVE/ON AIR presentation must fail closed while no authoritative external transmission feed exists."
Assert-Condition ($topBarSurface -match 'Text="CPU"' -and $topBarSurface -match 'Text="N/A"' -and $topBarSurface -match 'Text="GPU"' -and $topBarSurface -match 'Binding GpuUtilization' -and $topBarSurface -match 'Text="MEMORY"' -and $topBarSurface -match 'Binding Vram' -and $topBarSurface -match 'Text="LATENCY"' -and $topBarSurface -match 'Binding FrameTime') "Mockup top-bar metrics must reuse available health evidence and explicitly avoid synthesized CPU values."

# Workspaces, Quick Controls, multiview and Clean Program.
foreach ($workspace in @("MEDIA", "EDIT", "LIVE", "SCENES", "COMPOSITING", "OUTPUTS", "SETTINGS")) {
	Assert-Condition ($shell -match ('const string [A-Za-z]+ = "' + $workspace + '"')) "Canonical workspace '$workspace' must be defined."
	Assert-Condition ($window -match ('CommandParameter="' + $workspace + '"')) "Canonical workspace '$workspace' must be selectable from the Operator."
}
Assert-Condition ($shell -match 'CurrentVersion = 4' -and $shell -match 'Dictionary<string, OperatorWorkspaceLayoutSettings>' -and $shell -match 'Version < CurrentVersion') "Workspace layout persistence must be versioned and migrate pre-mockup geometry to reference defaults."
Assert-Condition ($shell -match '"GRAPHICS".+Compositing' -and $shell -match '"SYSTEM".+Settings') "Legacy workspace names must migrate to the canonical seven-workspace shell."
Assert-Condition ($shell -match 'stableLeftPanelWidth' -and $shell -match 'stableRightPanelWidth' -and $shell -match 'stableLowerPanelHeight' -and $shell -match 'ApplyWorkspaceLayout') "Workspace switching must preserve macro shell geometry while changing presentation content."
Assert-Condition ($shell -notmatch 'OperatorControlClient|NamedPipe|RuntimeHost|ControlHost|AIHost') "Workspace switching must remain presentation-only."
Assert-Condition ($window -match 'Content="SAVE LAYOUT"' -and $window -match 'Shell\.SaveLayoutCommand' -and $window -match 'Content="RESET LAYOUT"' -and $window -match 'Shell\.ResetLayoutCommand') "SETTINGS must expose custom-control Save Layout and Reset Layout actions."
Assert-Condition ($window -match 'Shell\.ProductionControlsVisibility' -and $window -match 'Shell\.MediaDeckVisibility' -and $window -match 'Shell\.GraphicsVisibility' -and $window -match 'Shell\.SystemWorkspaceVisibility') "Workspaces must configure presentation without duplicating product state."

# Output routing, health and performance.
Assert-Condition ($shell -match 'OutputRoutingVisibility => IsOutputsWorkspace' -and $shell -match 'SystemStatusVisibility => IsSettingsWorkspace') "OUTPUTS must own its dedicated output-health presentation while generic diagnostics remain in SETTINGS."
Assert-Condition ($window -match '<local:OutputRoutingHealthControl' -and $window -match 'DataContext="\{Binding OutputHealth') "OUTPUTS must render the dedicated output-routing health control through the existing Operator shell."
Assert-Condition ($windowCode -match 'OutputRoutingHealthViewModel' -and $windowCode -match 'OutputHealth\.Dispose\(\)') "The Operator window must own and dispose the output-health presentation lifecycle."
Assert-Condition ($outputHealthControl -match 'Text="OUTPUT ROUTING"' -and $outputHealthControl -match 'Text="SYSTEM HEALTH"' -and $outputHealthControl -match 'Text="SELECTED OUTPUT"' -and $outputHealthControl -match 'Text="PERFORMANCE"') "Output health must expose routing, system health, output detail and performance surfaces."
Assert-Condition ($outputHealthControl -match 'ItemsSource="\{Binding Outputs\}"' -and $outputHealthControl -match 'ItemsSource="\{Binding SystemHealth\}"' -and $outputHealthControl -match 'ItemsSource="\{Binding Metrics\}"' -and $outputHealthControl -match 'Binding SelectedOutput') "Output health must bind the output list, system health, selected detail and metrics projection."
Assert-Condition ($outputHealthControl -match '<Run Text="\{Binding Resolution, Mode=OneWay\}"' -and $outputHealthControl -match '<Run Text="\{Binding FrameRate, Mode=OneWay\}"') "Read-only output format properties rendered through Run.Text must use explicit OneWay bindings."
Assert-Condition ($outputHealthViewModel -match 'RoutePreviewToProgramCommand => _control\.CutCommand') "Output routing must reuse the existing authoritative Preview-to-Program CUT command."
Assert-Condition ($outputHealthViewModel -match 'SAFE READ-ONLY' -and $outputHealthViewModel -match 'UNAVAILABLE') "Output health must fail closed for routing and represent missing telemetry explicitly."
Assert-Condition ($outputHealthViewModel -match 'new OutputStatusViewModel\("program", "PROGRAM"\)' -and $outputHealthViewModel -match 'new OutputStatusViewModel\("preview", "PREVIEW"\)' -and $outputHealthViewModel -match 'new OutputStatusViewModel\("aux", "AUX"\)' -and $outputHealthViewModel -match 'new OutputStatusViewModel\("clean-program", "CLEAN FEED"\)') "Production output projection must expose exactly the four mockup roles without inventing Aux authority."
Assert-Condition ($outputHealthViewModel -match 'No governed Aux output role is exposed by the current V1 contract' -and $outputHealthViewModel -match 'evidenceState: "UNVERIFIED"') "Aux output must remain explicitly fail-closed."
Assert-Condition ($productionWorkspaceSurface -match 'OutputHealth\.Outputs' -and $productionWorkspaceSurface -match 'Monitoring\.ProgramImage' -and $productionWorkspaceSurface -match 'Monitoring\.PreviewImage') "Output Routing rows must reuse the existing output projection and monitoring thumbnails."
Assert-Condition ($productionWorkspaceSurface -match 'Binding Resolution' -and $productionWorkspaceSurface -match 'Binding FrameRate' -and $productionWorkspaceSurface -match 'Binding ColorSpace' -and $productionWorkspaceSurface -match 'Binding Name') "Output Routing rows must expose resolution, frame rate, color-space evidence and the right-side role label."
Assert-Condition ($productionWorkspaceSurface -match 'OutputHealth\.DiskMetric' -and $productionWorkspaceSurface -match 'OutputHealth\.NetworkMetric' -and $productionWorkspaceSurface -match 'OutputHealth\.TemperatureMetric' -and $productionWorkspaceSurface -match 'controls:RtaimeMetricBar') "System Status must render Disk, Network and Temperature through own thin metric bars."
Assert-Condition ($outputHealthViewModel -match 'HistoryLimit = 48' -and $outputHealthViewModel -match 'HealthObserved') "Performance histories must be bounded and sample the existing health observation stream."
Assert-Condition ($outputHealthViewModel -notmatch 'PeriodicTimer|Task\.Delay|PerformanceCounter|ManagementObjectSearcher|nvidia-smi|NVML') "Output health must not add telemetry polling or local hardware probes."
Assert-Condition ($outputHealthViewModel -notmatch 'RuntimeHost|ControlHost') "Output health must remain a presentation adapter and must not depend directly on host implementations."
foreach ($metric in @("CPU", "GPU", "MEMORY", "VRAM", "RENDER TIME", "DROPPED FRAMES", "OUTPUT FPS", "DISK", "NETWORK", "TEMPERATURE")) {
	Assert-Condition ($outputHealthViewModel -match [Regex]::Escape($metric)) "Output health metric '$metric' is required."
}
Assert-Condition ($outputHealthDocumentation -match 'existing .*CutCommand|existing `OperatorViewModel\.CutCommand`' -and $outputHealthDocumentation -match 'UNAVAILABLE' -and $outputHealthDocumentation -match 'maximum of 48 samples') "Output health documentation must describe authoritative routing, unavailable telemetry and bounded history."

# Compositing node graph.
Assert-Condition ($shell -match 'CompositingGraphVisibility' -and $shell -match 'StandardViewerVisibility => IsLiveWorkspace \|\| IsCompositingWorkspace \|\| IsEditWorkspace') "COMPOSITING and EDIT must use their dedicated graph/mockup surfaces instead of duplicating the standard monitor surface."
Assert-Condition ($window -match '<local:CompositingGraphControl' -and $window -match 'DataContext="\{Binding CompositingGraph') "COMPOSITING must render the dedicated node graph through the existing Operator shell."
Assert-Condition ($windowCode -match 'CompositingGraphViewModel' -and $windowCode -match 'CompositingGraph\.Dispose\(\)') "The Operator window must own and dispose the graph presentation lifecycle."
Assert-Condition ($compositingGraph -match 'ItemsSource="\{Binding Connections\}"' -and $compositingGraph -match 'ItemsSource="\{Binding Nodes\}"') "Graph connections and node controls must render in separate presentation layers."
Assert-Condition ($compositingGraph -match 'MouseWheel="OnGraphMouseWheel"' -and $compositingGraph -match 'Command="\{Binding FitCommand\}"' -and $compositingGraph -match 'Command="\{Binding AutoLayoutCommand\}"') "Compositing graph must expose bounded pan/zoom, Fit and Auto Layout interaction."
Assert-Condition ($compositingGraph -match 'Content="REWIRE" IsEnabled="False"') "Unsupported Runtime rewiring must remain visibly disabled."
Assert-Condition ($compositingGraphViewModel -match 'CompositingGraphProjector\.Project' -and $compositingGraphViewModel -match 'node\.Apply\(projection\)' -and $compositingGraphViewModel -match 'UpdateConnections\(\)') "Live graph refresh must update stable node projections incrementally rather than replace the graph on every status change."
Assert-Condition ($compositingGraphViewModel -notmatch 'using rtaime\.(RuntimeHost|ControlHost|Provider\.Gpu|Recording)') "Compositing presentation must not depend directly on Runtime, ControlHost, GPU provider or recording implementations."
Assert-Condition ($compositingGraphProjection -match 'CompositingGraphNodeKind' -and $compositingGraphProjection -match 'CanRewire = false' -and $compositingGraphProjection -match '"program-output"' -and $compositingGraphProjection -match '"recorder"') "Client graph projection must expose stable read-only source/routing/composite/output/recorder semantics."
Assert-Condition ($mediaPool -match 'SelectCompositingNode' -and $mediaPool -match '"graph\.node\.status"' -and $mediaPool -match '"graph\.node\.rewire"') "Graph node selection must project into the existing shared Inspector."
Assert-Condition ($compositingGraphDocumentation -match 'read-only-first' -and $compositingGraphDocumentation -match 'not a second execution or routing authority' -and $compositingGraphDocumentation -match 'never performs Set Preview, CUT, AUTO') "Compositing documentation must preserve the presentation-only authority boundary."

Assert-Condition ($quickControls -match 'MaximumPinnedControls = 8' -and $quickControls -match 'operator-quick-controls\.json') "Quick Controls must remain bounded and persist only local presentation preferences."
Assert-Condition ($quickControls -match 'SupportedPropertyIds' -and $quickControls -match 'propertyIds' -and $quickControls -notmatch 'OperatorControlClient|NamedPipe|RuntimeHost|ControlHost|AIHost') "Quick Controls must reference stable Inspector properties without owning production state."
foreach ($commandName in @("ApplyPlaybackPolicyCommand", "ApplyAudioGainCommand", "ToggleAudioMuteCommand", "ApplyGraphicsCommand", "ToggleGraphicsCommand", "EnableAIShowcaseCommand", "DisableAIShowcaseCommand")) {
	Assert-Condition ($quickControls -match [Regex]::Escape($commandName)) "Quick Controls must reuse existing command '$commandName'."
}
Assert-Condition ($inspectorHost -match 'QuickControls\.TogglePinCommand' -and $inspectorHost -match 'CommandParameter="\{Binding\}"') "Pinnable Inspector values must be able to add/remove Quick Controls."
Assert-Condition ($mediaPool -match '"production\.transition\.frames"' -and $mediaPool -match '"ai\.enabled"') "Inspector must expose stable pinnable production and AI identifiers."

Assert-Condition ($window -match '<local:OperatorMultiviewControl' -and $window -match 'Monitoring\.PreviewImage' -and $window -match 'Monitoring\.ProgramImage') "LIVE multiview must consume the existing Preview/Program monitoring projection."
Assert-Condition ($multiview -match 'PreviewImage' -and $multiview -match 'ProgramImage' -and $multiview -match 'Sources') "Reusable multiview must project supported monitoring feeds and source thumbnails."
Assert-Condition ($multiviewCode -notmatch 'NamedPipe|MediaElement|VideoDrawing|OperatorControlClient') "Multiview must not create another transport, player or authority path."
Assert-Condition ($multiviewCode -match 'MaximumDisplayedSources = 16' -and $multiviewCode -match '<= 4 => 2|<= 4' -and $multiviewCode -match '<= 9 => 3|<= 9' -and $multiviewCode -match '_ => 4') "LIVE multiview must adapt through 2/3/4-column layouts and remain bounded to 16 source tiles."
Assert-Condition ($multiview -match 'Binding IsPreview' -and $multiview -match 'Binding IsProgram' -and $multiview -match 'Binding Health' -and $multiview -match 'Binding Format') "LIVE source tiles must expose confirmed PGM/PVW tally, health and format."
Assert-Condition ($sourceTileViewModel -match 'AudioLeftPeak' -and $sourceTileViewModel -match 'AudioRightPeak' -and $sourceTileViewModel -match 'AudioClipping' -and $viewModel -match 'source\.ApplyAudioMeter') "LIVE source tiles must reuse existing Runtime audio observations."
foreach ($failedSourceState in @("LOST", "ERROR", "FAILED", "OFFLINE")) {
	Assert-Condition ($multiview -match ('Value="' + $failedSourceState + '"')) "LIVE multiview must keep failed source state '$failedSourceState' visible."
}
Assert-Condition ($multiview -match 'SelectedItem="\{Binding SelectedSource' -and $window -match 'SelectedSource="\{Binding SelectedSource, Mode=TwoWay\}"') "Multiview selection must project into existing SelectedSource state without routing."
Assert-Condition ($multiviewCode -match 'ShowExpanded' -and $multiviewCode -notmatch 'SetPreviewCommand|CutCommand|DissolveCommand') "Multiview double-click expansion must remain presentation-only and must not route or take."
Assert-Condition ($multiviewCode -match '!IsVisible' -and $multiviewCode -match 'OnVisibilityChanged' -and $multiviewCode -notmatch 'DispatcherTimer|PeriodicTimer') "Multiview presentation updates must be visibility-aware without adding another polling loop."
Assert-Condition ($liveSceneCue -match 'SelectedItem="\{Binding SelectedSource, Mode=TwoWay\}"' -and $liveSceneCue -match 'Command="\{Binding SetPreviewCommand\}"' -and $liveSceneCue -match 'Command="\{Binding CutCommand\}"') "LIVE source selection must remain separate from explicit Preview/Take commands."
Assert-Condition ($liveSceneCue -match 'MediaDeck\.SelectedCue' -and $liveSceneCue -match 'MediaDeck\.JumpCueCommand' -and $liveSceneCue -notmatch 'SelectionChanged=') "LIVE cue selection must not auto-execute cue activation."
Assert-Condition ($liveSceneCue -match 'NOT AVAILABLE IN V1 CONTRACT') "LIVE scene controls must fail closed while no governed scene activation contract exists."
Assert-Condition ($liveControls -match 'Binding SetPreviewCommand' -and $liveControls -match 'Binding CutCommand' -and $liveControls -match 'Binding DissolveCommand' -and $liveControls -match 'Binding TransitionFrames') "LIVE controls must reuse existing routing and transition commands."
Assert-Condition ($liveControls -match 'Binding ToggleGraphicsCommand' -and $liveControls -match 'Binding StartRecordingCommand' -and $liveControls -match 'Binding StopRecordingCommand' -and $liveControls -match 'ProgramOutput\.StartCommand' -and $liveControls -match 'ProgramOutput\.StopCommand') "LIVE controls must reuse existing layer, recording and Clean Program command paths."
Assert-Condition ($liveControls -match 'STREAM / EXTERNAL ON AIR · UNVERIFIED') "LIVE controls must not infer external stream/on-air state."
Assert-Condition ($liveControls -match 'EngineLifecycleState' -and $liveControls -match 'EngineHealth' -and $liveControls -match 'RuntimeHealth' -and $liveControls -match 'MediaHealth' -and $liveControls -match 'LastError') "LIVE alerts must project existing lifecycle, health and error evidence."
Assert-Condition ($shell -match 'MediaLibraryLeftVisibility' -and $shell -match 'LiveSceneCueVisibility' -and $shell -match 'InspectorVisibility' -and $shell -match 'LiveControlsVisibility') "LIVE workspace must isolate dedicated left/right surfaces from Media Library and Inspector presentation."
Assert-Condition ($shell -match 'ProductionControlsVisibility => IsEditWorkspace' -and $shell -match 'SourceBinVisibility => IsMediaWorkspace' -and $shell -match 'AudioVisibility => IsOutputsWorkspace' -and $shell -match 'RecordingVisibility => IsOutputsWorkspace') "LIVE workspace must not render duplicate production, source, audio or recording panels."
Assert-Condition ($shell -match 'HasTimelineRegion => IsEditWorkspace \|\| IsMediaWorkspace \|\| IsScenesWorkspace \|\| IsCompositingWorkspace') "LIVE workspace must dedicate the lower area to live operation rather than duplicating the edit timeline."
Assert-Condition ($liveDocumentation -match 'Selection is deliberately non-destructive' -and $liveDocumentation -match 'another monitoring transport' -and $liveDocumentation -match 'UNVERIFIED') "Live multiview documentation must record selection safety, monitoring authority and external-output uncertainty."
Assert-Condition ($window -match 'CLEAN PROGRAM MONITOR' -and $window -match 'not the physical Program output path') "Clean Program must be identified as monitoring rather than physical Program output."
Assert-Condition ($programOutputController -match 'OperatorMonitoringViewModel' -and $programOutputController -notmatch 'MediaElement|VideoDrawing') "Clean Program must reuse the existing monitoring projection."
Assert-Condition ($window -match 'Text="SYSTEM WORKSPACE"' -and $window -match 'Text="Engine"' -and $window -match 'Text="Control"' -and $window -match 'Text="Runtime"' -and $window -match 'Text="Outputs"' -and $window -match 'Text="Diagnostics"') "OUTPUTS/SETTINGS must reuse the existing operational evidence projection."
Assert-Condition ($workspaceDocumentation -match 'seven task-oriented workspaces' -and $workspaceDocumentation -match 'SCENES' -and $workspaceDocumentation -match 'COMPOSITING' -and $workspaceDocumentation -match 'OUTPUTS' -and $workspaceDocumentation -match 'SETTINGS' -and $workspaceDocumentation -match 'Quick Controls' -and $workspaceDocumentation -match 'Keyboard-first operation' -and $workspaceDocumentation -match 'Clean Program monitoring') "Operator workspace documentation must cover the implemented UX model."
