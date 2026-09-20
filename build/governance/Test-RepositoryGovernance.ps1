# Copyright (c) Dave Beusing <david.beusing@gmail.com>.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$policyPath = Join-Path $repositoryRoot "docs/Governance/RepositoryPolicy.json"
$rulesetPath = Join-Path $repositoryRoot ".github/rulesets/master.ruleset.json"
$workflowPath = Join-Path $repositoryRoot ".github/workflows/required-gates.yml"

function Assert-Condition {
	param(
		[Parameter(Mandatory)][bool]$Condition,
		[Parameter(Mandatory)][string]$Message
	)
	if (-not $Condition) {
		throw $Message
	}
}

function Read-JsonFile {
	param([Parameter(Mandatory)][string]$Path)
	Assert-Condition (Test-Path -LiteralPath $Path -PathType Leaf) "Required governance JSON was not found at '$Path'."
	return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

$policy = Read-JsonFile $policyPath
$ruleset = Read-JsonFile $rulesetPath
Assert-Condition (Test-Path -LiteralPath $workflowPath -PathType Leaf) "Required gates workflow was not found at '$workflowPath'."
$workflow = Get-Content -LiteralPath $workflowPath -Raw

$expectedChecks = @("CI", "Quality", "Security", "Packaged E2E", "Provider Smoke")
$policyChecks = @($policy.requiredChecks)
Assert-Condition ([string]$policy.schemaVersion -eq "1.0") "Unsupported repository governance policy schema version."
Assert-Condition ([string]$policy.primaryBranch -eq "master") "Repository governance primary branch must be master."
Assert-Condition ($policy.pullRequestRequired -eq $true) "Primary branch must require pull requests."
Assert-Condition ([int]$policy.requiredApprovingReviewCount -eq 0) "Repository governance must not invent a mandatory foreign-review count."
Assert-Condition ($policy.requireCodeOwnerReview -eq $false) "Repository governance must not require CODEOWNERS approval by default."
Assert-Condition ($policy.requireLastPushApproval -eq $false) "Repository governance must not require a different last-push approver."
Assert-Condition ($policy.requireReviewThreadResolution -eq $true) "Review thread resolution must be required."
Assert-Condition ($policy.blockForcePush -eq $true) "Force pushes to master must be blocked."
Assert-Condition ($policy.blockBranchDeletion -eq $true) "Deletion of master must be blocked."
Assert-Condition ($policy.strictRequiredStatusChecks -eq $true) "Required status checks must use strict branch freshness."
Assert-Condition ([string]$policy.administrativeEnforcement.evidenceStatus -eq "UNVERIFIED") "Repository-side policy must not claim administrative enforcement before GitHub ruleset evidence exists."

Assert-Condition ($policyChecks.Count -eq $expectedChecks.Count) "Repository policy must declare exactly the five required aggregate checks."
foreach ($expected in $expectedChecks) {
	Assert-Condition ($policyChecks -contains $expected) "Repository policy is missing required check '$expected'."
}

Assert-Condition ([string]$ruleset.name -eq "rtaime master governance") "Unexpected master ruleset name."
Assert-Condition ([string]$ruleset.target -eq "branch") "Master ruleset must target branches."
Assert-Condition ([string]$ruleset.enforcement -eq "active") "Master ruleset specification must request active enforcement."
Assert-Condition (@($ruleset.conditions.ref_name.include) -contains "refs/heads/master") "Master ruleset must include refs/heads/master."

$rulesByType = @{}
foreach ($rule in @($ruleset.rules)) {
	$type = [string]$rule.type
	Assert-Condition (-not $rulesByType.ContainsKey($type)) "Master ruleset contains duplicate rule type '$type'."
	$rulesByType[$type] = $rule
}
foreach ($requiredRuleType in @("deletion", "non_fast_forward", "pull_request", "required_status_checks")) {
	Assert-Condition ($rulesByType.ContainsKey($requiredRuleType)) "Master ruleset is missing '$requiredRuleType'."
}

$pullRequestRule = $rulesByType["pull_request"].parameters
Assert-Condition ([int]$pullRequestRule.required_approving_review_count -eq 0) "Master ruleset must not impose a mandatory approving-review count."
Assert-Condition ($pullRequestRule.require_code_owner_review -eq $false) "Master ruleset must not require CODEOWNERS review by default."
Assert-Condition ($pullRequestRule.require_last_push_approval -eq $false) "Master ruleset must not require a different last-push approver."
Assert-Condition ($pullRequestRule.required_review_thread_resolution -eq $true) "Master ruleset must require review-thread resolution."

$statusRule = $rulesByType["required_status_checks"].parameters
Assert-Condition ($statusRule.strict_required_status_checks_policy -eq $true) "Master required checks must require an up-to-date branch."
$rulesetChecks = @($statusRule.required_status_checks | ForEach-Object { [string]$_.context })
Assert-Condition ($rulesetChecks.Count -eq $expectedChecks.Count) "Master ruleset must declare exactly the five aggregate checks."
foreach ($expected in $expectedChecks) {
	Assert-Condition ($rulesetChecks -contains $expected) "Master ruleset is missing required check '$expected'."
}

Assert-Condition ($workflow -match '(?m)^\s*pull_request:\s*$') "Required gates workflow must run on pull_request."
Assert-Condition ($workflow -match '(?m)^\s*-\s+master\s*$') "Required gates workflow must target master."
Assert-Condition ($workflow -match '(?ms)^permissions:\s*\r?\n\s+contents:\s+read\s*$') "Required gates workflow must default to contents: read."
Assert-Condition ($workflow -notmatch '(?im)^\s*permissions:\s*write-all\s*$') "Required gates workflow must not use write-all permissions."

foreach ($expected in $expectedChecks) {
	$escaped = [Regex]::Escape($expected)
	Assert-Condition ($workflow -match "(?m)^\s+name:\s+$escaped\s*$") "Required gates workflow does not expose a job named '$expected'."
}

Write-Host "Repository governance intent verification PASS"
Write-Host "Primary branch: master"
Write-Host "Required checks: $($expectedChecks -join ', ')"
Write-Host "Required approving review count: 0"
Write-Host "Force push policy: blocked by desired ruleset"
Write-Host "Branch deletion policy: blocked by desired ruleset"
Write-Host "Administrative GitHub enforcement: UNVERIFIED until live ruleset evidence exists"
