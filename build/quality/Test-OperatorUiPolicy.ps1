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
$viewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/OperatorViewModel.cs"
$themePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/Themes/OperatorTheme.xaml"
$projectPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/rtaime.Operator.csproj"
$documentationPath = Join-Path $repositoryRoot "docs/OperatorUiV1.md"

foreach ($path in @($appPath, $windowPath, $viewModelPath, $themePath, $projectPath, $documentationPath)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required Operator UI artifact is missing: '$path'."
}

$app = Get-Content -LiteralPath $appPath -Raw
$window = Get-Content -LiteralPath $windowPath -Raw
$viewModel = Get-Content -LiteralPath $viewModelPath -Raw
$theme = Get-Content -LiteralPath $themePath -Raw
$project = Get-Content -LiteralPath $projectPath -Raw

Assert-Condition ($app -match 'Source="Themes/OperatorTheme\.xaml"') "Operator must load the reusable theme resource dictionary."
Assert-Condition ($theme -match 'OperatorPreviewBrush') "Operator theme must define a Preview semantic brush."
Assert-Condition ($theme -match 'OperatorProgramBrush') "Operator theme must define a Program semantic brush."
Assert-Condition ($theme -match 'OperatorErrorBrush') "Operator theme must define an error semantic brush."

Assert-Condition ($window -match 'ItemsSource="\{Binding Sources\}"') "Operator must expose the source bank as a bound collection."
Assert-Condition ($window -match 'Text="\{Binding MonitoringStatus\}"') "Operator must bind the explicit monitoring boundary into Preview and Program presentation."
Assert-Condition ($viewModel -match 'Monitoring unavailable until AP-29') "Operator must explicitly identify the monitoring boundary before AP-29."
Assert-Condition ($window -match 'Key="F5"') "Operator must expose keyboard synchronization."
Assert-Condition ($window -match 'Key="Space"\s+Command="\{Binding CutCommand\}"') "Operator must expose a keyboard CUT command."
Assert-Condition ($window -match 'Modifiers="Control"\s+Command="\{Binding DissolveCommand\}"') "Operator must expose a keyboard DISSOLVE/AUTO command."
Assert-Condition ($window -notmatch 'Width="1100"|Height="680"') "Operator must not retain the fixed bootstrap 1100x680 layout."
Assert-Condition ($window -notmatch '<\s*(Image|MediaElement|VideoDrawing)\b') "AP-28 must not introduce fake or management-IPC video monitoring."

foreach ($propertyName in @("IsBusy", "IsConnected", "IsStale", "ConnectionState", "CommandStatus", "LastEvent")) {
	Assert-Condition ($viewModel -match "public\s+[^\r\n]+\s+$propertyName\b") "Operator presentation state '$propertyName' is required."
}
Assert-Condition ($viewModel -match 'RemoteHostSessionChangedException') "Operator must retain explicit ControlHost-session resynchronization handling."
Assert-Condition ($viewModel -match 'CanMutate\(\)') "Operator mutations must be guarded by shared presentation readiness."

$projectReferenceCount = [Regex]::Matches($project, '<ProjectReference\s+Include=').Count
Assert-Condition ($projectReferenceCount -eq 1) "Operator must retain exactly one project dependency."
Assert-Condition ($project -match 'Client\\rtaime\.Client\\rtaime\.Client\.csproj') "Operator may depend only on the Client SDK seam."
Assert-Condition ($project -notmatch 'ControlHost|RuntimeHost|AIHost') "Operator must not reference production host implementations."

Write-Host "Operator UI policy verification PASS"
Write-Host "Operator authority: remote Client SDK only"
Write-Host "Monitoring: explicitly unavailable until AP-29"
Write-Host "Keyboard controls: synchronization, Preview, CUT and DISSOLVE/AUTO declared"
