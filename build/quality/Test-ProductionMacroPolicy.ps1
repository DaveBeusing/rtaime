# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

function Assert-Condition {
	param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
	if (-not $Condition) { throw $Message }
}

$contractsPath = Join-Path $repositoryRoot "src/Contracts/rtaime.Control.Contracts/ProductionMacroContracts.cs"
$showContractsPath = Join-Path $repositoryRoot "src/Contracts/rtaime.Control.Contracts/ShowControlContracts.cs"
$adapterPath = Join-Path $repositoryRoot "src/Control/rtaime.Control/ProductionMacroShowControlAdapter.cs"
$coordinatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/ProductionMacroCoordinator.cs"
$validatorPath = Join-Path $repositoryRoot "src/Hosts/rtaime.ControlHost/GovernedProductionActionValidator.cs"
$clientPath = Join-Path $repositoryRoot "src/Client/rtaime.Client/OperatorControlClient.cs"
$integrationConfigPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/IntegrationConfiguration.cs"
$integrationGatewayPath = Join-Path $repositoryRoot "src/Hosts/rtaime.IntegrationHost/IntegrationGateway.cs"
$operatorViewModelPath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ProductionMacroViewModel.cs"
$operatorSurfacePath = Join-Path $repositoryRoot "src/Hosts/rtaime.Operator/ProductionMacroControl.xaml"
$documentationPath = Join-Path $repositoryRoot "docs/ProductionMacros.md"
$contractTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Contracts/ProductionMacroContractTests.cs"
$integrationTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Integration/ProductionMacroCoordinatorIntegrationTests.cs"
$operatorTestsPath = Join-Path $repositoryRoot "tests/rtaime.Tests.Operator/ProductionMacroOperatorTests.cs"

foreach ($path in @(
	$contractsPath, $showContractsPath, $adapterPath, $coordinatorPath, $validatorPath, $clientPath,
	$integrationConfigPath, $integrationGatewayPath, $operatorViewModelPath, $operatorSurfacePath,
	$documentationPath, $contractTestsPath, $integrationTestsPath, $operatorTestsPath
)) {
	Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Production Macro artifact is missing: '$path'."
}

$contracts = Get-Content -LiteralPath $contractsPath -Raw
$showContracts = Get-Content -LiteralPath $showContractsPath -Raw
$adapter = Get-Content -LiteralPath $adapterPath -Raw
$coordinator = Get-Content -LiteralPath $coordinatorPath -Raw
$validator = Get-Content -LiteralPath $validatorPath -Raw
$client = Get-Content -LiteralPath $clientPath -Raw
$integrationConfig = Get-Content -LiteralPath $integrationConfigPath -Raw
$integrationGateway = Get-Content -LiteralPath $integrationGatewayPath -Raw
$operatorViewModel = Get-Content -LiteralPath $operatorViewModelPath -Raw
$operatorSurface = Get-Content -LiteralPath $operatorSurfacePath -Raw
$documentation = Get-Content -LiteralPath $documentationPath -Raw
$contractTests = Get-Content -LiteralPath $contractTestsPath -Raw
$integrationTests = Get-Content -LiteralPath $integrationTestsPath -Raw
$operatorTests = Get-Content -LiteralPath $operatorTestsPath -Raw

Assert-Condition ($contracts -match 'ProductionMacroContractVersion' -and $contracts -match 'ProductionMacroId' -and $contracts -match 'ProductionMacroActionId') "Production Macro contracts must be versioned and retain stable identities."
Assert-Condition ($contracts -match 'MaximumActions = ShowControlCue\.MaximumActions' -and $contracts -match 'MaximumMacros = 128') "Production Macro definitions and libraries must remain explicitly bounded."
Assert-Condition ($contracts -match 'ShowControlAction' -and $adapter -match 'action => action\.Command' -and $adapter -match 'ShowControlCueList') "Production Macros must reuse the governed Show Control action union."
Assert-Condition ($showContracts -match 'RouteOutputRole = 15' -and $validator -match 'ShowControlActionKind\.RouteOutputRole') "Governed output-role routing must remain a closed Show Control action."
Assert-Condition ($coordinator -match 'ShowControlExecutionMachine' -and $coordinator -match 'ShowControlActionExecutor' -and $coordinator -match 'ShowControlFrameObserver') "Macro execution must reuse Show Control execution and Runtime-frame timing seams."
Assert-Condition ($coordinator -match 'ProductionMacroExecutionState\.RecoveryRequired' -or $coordinator -match 'RecoveryRequired') "Macro execution must preserve explicit ambiguous-restart recovery."
Assert-Condition ($coordinator -notmatch 'RuntimeHost|rtaime\.Provider|Process\.Start|System\.Reflection|HttpClient|PowerShell|CSharpScript') "Production Macro coordinator must not bypass ControlHost, launch code, or add generic external execution."
Assert-Condition ($contracts -notmatch 'NestedMacro|LoopCount|ConditionExpression|ScriptText|ExecutableCode') "Production Macro contracts must not expose nesting, loops, conditions, or executable code."
Assert-Condition ($client -match 'GetProductionMacroAsync' -and $client -match 'ExecuteProductionMacroAsync' -and $client -match 'ValidateProductionMacroAsync') "Production Macro list/get/validate/execute operations must cross rtaime.Client."
Assert-Condition ($integrationConfig -match 'ProductionMacroExecute' -and $integrationGateway -match 'ExecuteProductionMacroAsync\(action\.TargetId') "IntegrationHost must invoke Macros by stable identity through rtaime.Client."
Assert-Condition ($integrationGateway -notmatch 'ProductionMacroAction|ShowControlActionKind') "IntegrationHost must not duplicate Macro action chains."

Assert-Condition ($operatorSurface -match 'controls:RtaimeButton' -and $operatorSurface -match 'controls:RtaimeComboBox' -and $operatorSurface -match 'controls:RtaimeTextBox' -and $operatorSurface -match 'controls:RtaimeListBox') "Production Macro UI must use rtaime custom controls."
Assert-Condition ($operatorSurface -notmatch '<(Button|ToggleButton|CheckBox|RadioButton|TextBox|ComboBox|ListBox|ListView|DataGrid|Slider|ScrollBar|ScrollViewer)(\s|/|>)') "Production Macro UI must not reintroduce directly visible stock WPF controls."
Assert-Condition ($operatorViewModel -notmatch 'RuntimeHost|Provider\.' -and $operatorViewModel -match '_client\.ExecuteProductionMacroAsync') "Production Macro Operator must remain a Client-only presentation surface."

Assert-Condition ($documentation -match 'ControlHost remains Production Authority' -and $documentation -match 'does not provide arbitrary executable code') "Production Macro documentation must preserve authority and no-code boundaries."
Assert-Condition ($documentation -match 'Runtime production frames' -and $documentation -match 'RecoveryRequired') "Production Macro documentation must record frame-domain timing and recovery semantics."

foreach ($test in @(
	"Macro_library_round_trips_stable_identity_order_and_governed_payloads",
	"Macro_failure_stops_before_later_actions",
	"Macro_wait_uses_runtime_frame_target_before_continuing",
	"Cancelled_wait_cannot_fire_later_actions",
	"Restart_during_wait_requires_explicit_recovery_without_replay",
	"Macro_surface_uses_only_custom_interaction_controls"
)) {
	$allTests = $contractTests + $integrationTests + $operatorTests
	Assert-Condition ($allTests -match [Regex]::Escape($test)) "Production Macro regression coverage is missing '$test'."
}

Write-Host "Production Macro policy PASS"
Write-Host "Authority: ControlHost"
Write-Host "Action model: closed governed Show Control union"
Write-Host "Timing: Runtime production frames"
Write-Host "Generic scripting/nesting/unbounded execution: prohibited"
