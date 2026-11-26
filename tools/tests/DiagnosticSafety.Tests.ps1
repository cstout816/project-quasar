[CmdletBinding()]
param([switch]$KeepFixtures)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $projectRoot 'tools\DiagnosticSafety.ps1')
$template=Get-Content -LiteralPath (Join-Path $projectRoot 'analysis\diagnostic-experiment.json') -Raw
$fixtureRoot=Join-Path $env:TEMP ('sb-diagnostic-safety-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$script:passed=0
function New-Fixture([string]$Name){
    $root=Join-Path $fixtureRoot $Name
    New-Item -ItemType Directory -Path (Join-Path $root 'analysis') -Force|Out-Null
    $plan=$template|ConvertFrom-Json
    $plan.PSObject.Properties.Remove('campaign')
    $plan.status='approved';$plan.experimentId='test-experiment';$plan.userApprovalReference='ISOLATED TEST FIXTURE; not approval for any real diagnostic'
    $plan.hypothesis='A test allowance is consumed once';$plan.newEvidence='Fixture evidence only';$plan.expectedObservation='A second claim is refused'
    $plan.expiresAtUtc=[DateTimeOffset]::UtcNow.AddHours(1).ToString('o')
    $plan.remainingBuilds=1;$plan.remainingRuns=1;$plan.history=@()
    $plan|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $root 'analysis\diagnostic-experiment.json') -Encoding UTF8
    return $root
}
function Read-Plan([string]$Root){Get-Content -LiteralPath (Join-Path $Root 'analysis\diagnostic-experiment.json') -Raw|ConvertFrom-Json}
function Write-Plan([string]$Root,$Plan){$Plan|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $Root 'analysis\diagnostic-experiment.json') -Encoding UTF8}
function Assert-True([bool]$Value,[string]$Message){if(-not $Value){throw "FAIL: $Message"};$script:passed++;Write-Output "PASS: $Message"}
function Assert-Refused([scriptblock]$Code,[string]$Message,[string]$Text=''){
    $caught=$false
    try{& $Code}catch{$caught=$true;if($Text -and $_.Exception.Message -notlike "*$Text*"){throw "Wrong refusal for $Message : $($_.Exception.Message)"}}
    Assert-True $caught $Message
}
function New-CampaignFixture([string]$Name, [int]$BuildLimit = 2, [int]$RunLimit = 3) {
    $root = New-Fixture $Name
    $plan = Read-Plan $root
    $plan.status = 'awaiting_review'
    $plan.remainingBuilds = 0
    $plan.remainingRuns = 0
    $plan.history = @([pscustomobject]@{
        experimentId = 'earlier-single-approval'; action = 'Run'
        userApprovalReference = 'LEGACY FIXTURE APPROVAL'; fingerprint = 'legacy-fingerprint'
    })
    $now = [DateTimeOffset]::UtcNow
    $plan | Add-Member -NotePropertyName campaign -NotePropertyValue ([pscustomobject]@{
        id = 'test-campaign'; status = 'active'; userApprovalReference = 'ISOLATED CAMPAIGN FIXTURE'
        activatedAtUtc = $now.AddHours(-1).ToString('o'); expiresAtUtc = $now.AddDays(5).ToString('o')
        buildLimit = $BuildLimit; runLimit = $RunLimit; buildsClaimed = 0; runsClaimed = 0
        experiment = $null
    })
    Write-Plan $root $plan
    $pause = Join-Path $root 'CLEANUP-2026-11-06'
    New-Item -ItemType Directory -Path $pause | Out-Null
    Set-Content -LiteralPath (Join-Path $pause 'DIAGNOSTICS-PAUSED.txt') -Value 'paused'
    return $root
}
function Select-FixtureRun([string]$Root, [string]$Id, [string]$Evidence = 'first observation', [hashtable]$Repeat = @{}) {
    Set-DiagnosticCampaignExperiment -WorkspaceRoot $Root -ExperimentId $Id -Action Run `
        -Hypothesis 'Test completion publication' -NewEvidence $Evidence `
        -ExpectedObservation 'A waiter observes the publication' -StopCondition 'Stop at end of fixture' @Repeat
}
function Claim-FixtureRun([string]$Root, [string]$Id) {
    Assert-DiagnosticExperiment -WorkspaceRoot $Root -Action Run -CampaignExperimentId $Id -RunSeconds 900 -RunStorageMiB 64
}
function Complete-FixtureClaim([string]$Root, [string]$Id) {
    Complete-DiagnosticCampaignExperiment -WorkspaceRoot $Root -ExperimentId $Id -Result Verified `
        -Observation 'Fixture claim finished' -NextDecision 'Record the next distinct test'
}
try {
    $root=New-Fixture 'paused';$p=Read-Plan $root;$p.status='paused';Write-Plan $root $p
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'paused plan blocks without any cleanup flag' 'paused'

    $root=Join-Path $fixtureRoot 'missing';New-Item -ItemType Directory -Path $root|Out-Null
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'missing plan fails closed' 'missing'
    Assert-True (-not(Test-Path (Join-Path $root 'analysis'))) 'denial creates no analysis output'

    $root=New-Fixture 'invalid';$path=Join-Path $root 'analysis\diagnostic-experiment.json';Set-Content -LiteralPath $path -Value '{'
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'invalid JSON fails closed'

    $root=New-Fixture 'legacy-pause';$pause=Join-Path $root 'CLEANUP-2026-11-06';New-Item -ItemType Directory -Path $pause|Out-Null;Set-Content -LiteralPath (Join-Path $pause 'DIAGNOSTICS-PAUSED.txt') -Value 'paused'
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'legacy pause still blocks approved plans' 'remain paused'

    foreach($field in 'hypothesis','newEvidence','userApprovalReference','expectedObservation','stopCondition'){
        $root=New-Fixture ('missing-'+$field);$p=Read-Plan $root;$p.$field='';Write-Plan $root $p
        Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} ("required field $field") $field
    }

    $root=New-Fixture 'expired';$p=Read-Plan $root;$p.expiresAtUtc=[DateTimeOffset]::UtcNow.AddSeconds(-10).ToString('o');Write-Plan $root $p
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'expired approval refused' 'within the next 24 hours'
    $root=New-Fixture 'long-expiry';$p=Read-Plan $root;$p.expiresAtUtc=[DateTimeOffset]::UtcNow.AddDays(3).ToString('o');Write-Plan $root $p
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'unbounded approval refused' 'within the next 24 hours'

    $root=New-Fixture 'multiple-builds';$p=Read-Plan $root;$p.remainingBuilds=2;Write-Plan $root $p
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'multiple build allowances refused' 'at most one'
    $root=New-Fixture 'runtime-budget'
    Assert-Refused {Assert-DiagnosticExperiment -Action Run -WorkspaceRoot $root -RunSeconds 901 -RunStorageMiB 64} 'time limit enforced' 'budget'
    Assert-True ((Read-Plan $root).remainingRuns -eq 1) 'budget rejection does not consume a valid allowance'
    Assert-Refused {Assert-DiagnosticExperiment -Action Run -WorkspaceRoot $root -RunSeconds 900 -RunStorageMiB 65} 'per-run storage cap enforced' 'budget'

    $root=New-Fixture 'storage-quota';$p=Read-Plan $root;$p.limits.maxDiagnosticAllocatedMiB=1;Write-Plan $root $p
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'aggregate storage reserve enforced' 'storage quota'
    $root=New-Fixture 'free-disk';$p=Read-Plan $root;$p.limits.minimumFreeDiskMiB=1000000000000;Write-Plan $root $p
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'minimum free-space guard enforced' 'free disk'

    $root=New-Fixture 'claims'
    Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root
    Assert-True ((Read-Plan $root).remainingBuilds -eq 0) 'build allowance is consumed before any build'
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'automatic build retry refused' 'consumed'
    Assert-DiagnosticExperiment -Action Run -WorkspaceRoot $root -RunSeconds 900 -RunStorageMiB 64
    $p=Read-Plan $root
    Assert-True ($p.remainingRuns -eq 0 -and $p.status -eq 'awaiting_review' -and $p.history.Count -eq 2) 'one build and run require review afterward'
    Assert-Refused {Assert-DiagnosticExperiment -Action Run -WorkspaceRoot $root -RunSeconds 900 -RunStorageMiB 64} 'automatic replay refused after consumption' 'paused'
    $p.status='approved';$p.remainingBuilds=1;Write-Plan $root $p
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'resetting counter cannot repeat an attempted action' 'already attempted'
    $p.experimentId='renamed-test';Write-Plan $root $p
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'renaming experiment cannot reuse approval' 'same approval'
    $p.userApprovalReference='A different TEST approval string';Write-Plan $root $p
    Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'renaming plan does not disguise unchanged evidence' 'unchanged hypothesis'

    $root=New-Fixture 'concurrent';$lease=[IO.File]::Open((Join-Path $root 'analysis\diagnostic-experiment.json'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{Assert-Refused {Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root} 'concurrent claim cannot enter locked plan'}finally{$lease.Dispose()}

    $root=New-Fixture 'logical-quota';$p=Read-Plan $root;$p.limits.maxDiagnosticLogicalMiB=1;Write-Plan $root $p
    Assert-Refused {Assert-DiagnosticExperiment -Action Run -WorkspaceRoot $root -RunSeconds 900 -RunStorageMiB 64} 'logical-size reserve also enforced' 'storage quota'

    $root = New-CampaignFixture 'campaign-follow-up' -BuildLimit 1 -RunLimit 2
    Assert-Refused { Claim-FixtureRun $root 'first-run' } 'campaign refuses unselected attempts' 'selected'
    Select-FixtureRun $root 'first-run'
    Assert-Refused { Assert-DiagnosticExperiment -Action Run -WorkspaceRoot $root -RunSeconds 900 -RunStorageMiB 64 } 'legacy entry cannot consume campaign allowance' 'paused'
    Assert-Refused { Claim-FixtureRun $root 'different-run' } 'campaign requires the explicit selected ID' 'selected'
    Assert-Refused { Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root -CampaignExperimentId 'first-run' } 'campaign requires the selected action' 'selected'
    Claim-FixtureRun $root 'first-run'
    $p = Read-Plan $root
    Assert-True ($p.campaign.runsClaimed -eq 1 -and $p.campaign.buildsClaimed -eq 0 -and
        $null -eq $p.campaign.experiment -and $p.history.Count -eq 2 -and
        $p.history[0].experimentId -eq 'earlier-single-approval' -and $p.history[1].experimentId -eq 'first-run') 'campaign claim consumes one run and preserves legacy history'
    Assert-Refused { Select-FixtureRun $root 'second-run' 'new measurement' } 'unreviewed campaign claim prevents overlapping attempts' 'prior campaign claim'
    Assert-Refused { Claim-FixtureRun $root 'first-run' } 'consumed campaign claim cannot replay' 'selected'
    Complete-FixtureClaim $root 'first-run'
    Assert-Refused { Complete-FixtureClaim $root 'first-run' } 'campaign outcome is single-write' 'no unique pending claim'
    Assert-Refused { Select-FixtureRun $root 'renamed-run' } 'campaign rejects unchanged evidence under a new name' 'unchanged hypothesis'
    Select-FixtureRun $root 'second-run' 'new measurement'
    Claim-FixtureRun $root 'second-run'
    Complete-FixtureClaim $root 'second-run'
    Assert-True ((Read-Plan $root).campaign.runsClaimed -eq 2) 'authorized distinct follow-up claims cumulatively'
    Assert-Refused { Select-FixtureRun $root 'third-run' 'another measurement' } 'campaign run exhaustion enforced' 'allowance exhausted'
    Set-DiagnosticCampaignExperiment -WorkspaceRoot $root -ExperimentId 'build' -Action Build `
        -Hypothesis 'One separate build' -NewEvidence 'Code change' -ExpectedObservation 'New candidate' -StopCondition 'Build exits'
    Assert-DiagnosticExperiment -Action Build -WorkspaceRoot $root -CampaignExperimentId 'build'
    Complete-FixtureClaim $root 'build'
    Assert-True ((Read-Plan $root).campaign.buildsClaimed -eq 1) 'campaign build counter is independent of run counter'
    Assert-Refused {
        Set-DiagnosticCampaignExperiment -WorkspaceRoot $root -ExperimentId 'build2' -Action Build `
            -Hypothesis 'Second separate build' -NewEvidence 'Code change' -ExpectedObservation 'New candidate' -StopCondition 'Build exits'
    } 'campaign build exhaustion enforced' 'allowance exhausted'
    $p = Read-Plan $root
    $p.campaign.runsClaimed = 0
    Write-Plan $root $p
    Assert-Refused { Select-FixtureRun $root 'reset-run' 'new measurement' } 'campaign cannot reset counters against its retained ledger' 'ledger mismatch'

    $root = New-CampaignFixture 'campaign-repeat'
    Select-FixtureRun $root 'original'
    Claim-FixtureRun $root 'original'
    Complete-FixtureClaim $root 'original'
    $repeat = @{ Kind = 'Intermittent'; RepeatOf = 'original'; RepeatPurpose = 'Measure observed intermittent transition' }
    Select-FixtureRun $root 'intermittent' 'first observation' $repeat
    Claim-FixtureRun $root 'intermittent'
    Complete-FixtureClaim $root 'intermittent'
    Assert-Refused { Select-FixtureRun $root 'repeat-again' 'first observation' $repeat } 'same repeat purpose cannot run repeatedly' 'already attempted'
    $verification = @{ Kind = 'Reproducibility'; RepeatOf = 'original'; RepeatPurpose = 'Verify the second independent launch' }
    Select-FixtureRun $root 'verification' 'first observation' $verification
    Claim-FixtureRun $root 'verification'
    Complete-FixtureClaim $root 'verification'
    Assert-True ((Read-Plan $root).campaign.runsClaimed -eq 3) 'explicit intermittent and reproducibility repeats each consume a run'

    $root = New-CampaignFixture 'campaign-expiry'
    Select-FixtureRun $root 'expires-before-launch'
    $p = Read-Plan $root
    $p.campaign.expiresAtUtc = [DateTimeOffset]::UtcNow.AddSeconds(-10).ToString('o')
    Write-Plan $root $p
    Assert-Refused { Claim-FixtureRun $root 'expires-before-launch' } 'expired campaign refuses selected launch' 'campaign expired'
    $p.campaign.expiresAtUtc = [DateTimeOffset]::UtcNow.AddDays(8).ToString('o')
    Write-Plan $root $p
    Assert-Refused { Claim-FixtureRun $root 'expires-before-launch' } 'campaign duration cannot exceed seven days' 'fixed expiry'
    $p.campaign.expiresAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(10).ToString('o')
    Write-Plan $root $p
    Assert-Refused { Claim-FixtureRun $root 'expires-before-launch' } 'run must fit remaining campaign time' 'remaining campaign time'
    Assert-True ((Read-Plan $root).campaign.runsClaimed -eq 0) 'expiry rejections consume nothing'

    $root = New-CampaignFixture 'campaign-fixed-expiry'
    Select-FixtureRun $root 'first'
    Claim-FixtureRun $root 'first'
    Complete-FixtureClaim $root 'first'
    $p = Read-Plan $root
    $p.campaign.expiresAtUtc = [DateTimeOffset]::UtcNow.AddDays(6).ToString('o')
    Write-Plan $root $p
    Assert-Refused { Select-FixtureRun $root 'expiry-extension' 'new measurement' } 'existing claim prevents extending campaign expiry' 'ceilings changed'

    $root = New-CampaignFixture 'campaign-concurrent'
    Select-FixtureRun $root 'locked'
    $lease = [IO.File]::Open((Join-Path $root 'analysis\diagnostic-experiment.json'), [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Assert-Refused { Claim-FixtureRun $root 'locked' } 'locked campaign plan rejects concurrent claim' } finally { $lease.Dispose() }
    Assert-True ((Read-Plan $root).campaign.runsClaimed -eq 0) 'exclusive claim denial does not alter counters'
    Claim-FixtureRun $root 'locked'
    Assert-True ((Read-Plan $root).campaign.runsClaimed -eq 1) 'claim succeeds once after lock releases'

    $root = New-CampaignFixture 'campaign-budget'
    Select-FixtureRun $root 'budget'
    Assert-Refused {
        Assert-DiagnosticExperiment -Action Run -WorkspaceRoot $root -CampaignExperimentId 'budget' -RunSeconds 900 -RunStorageMiB 65
    } 'campaign run storage cap enforced' 'budget'
    $p = Read-Plan $root
    $p.limits.maxDiagnosticAllocatedMiB = 8192
    Write-Plan $root $p
    Assert-Refused { Claim-FixtureRun $root 'budget' } 'campaign cannot raise aggregate storage ceiling' 'limits were relaxed'
    Assert-True ((Read-Plan $root).campaign.runsClaimed -eq 0) 'campaign budget rejection consumes nothing'
    $root = New-CampaignFixture 'supplemental-build' 1 2
    Set-DiagnosticCampaignExperiment -WorkspaceRoot $root -ExperimentId 'original-build' -Action Build -Hypothesis 'Fixture original build' -NewEvidence 'Fixture source' -ExpectedObservation 'Fixture output' -StopCondition 'One claim'
    Assert-DiagnosticExperiment -WorkspaceRoot $root -Action Build -CampaignExperimentId 'original-build'
    Complete-FixtureClaim $root 'original-build'
    $p = Read-Plan $root
    $p | Add-Member -NotePropertyName supplementalBuild -NotePropertyValue ([pscustomobject]@{
        experimentId='extra-build'; campaignId=$p.campaign.id; status='approved'; remainingBuilds=1
        userApprovalReference='ISOLATED EXPLICIT EXTRA BUILD APPROVAL'
        expiresAtUtc=[DateTimeOffset]::UtcNow.AddHours(1).ToString('o')
        hypothesis='Compile fixture correction'; newEvidence='Original build exhausted'
        expectedObservation='One new fixture build'; stopCondition='One claim'
    })
    Write-Plan $root $p
    Assert-DiagnosticExperiment -WorkspaceRoot $root -Action Build -CampaignExperimentId 'extra-build'
    $p = Read-Plan $root
    Assert-True ($p.campaign.buildsClaimed -eq 1 -and $p.campaign.buildLimit -eq 1) 'supplemental build preserves original counters and ceiling'
    Assert-True ($p.supplementalBuild.remainingBuilds -eq 0) 'supplemental build consumes one explicit approval'
    Assert-Refused { Assert-DiagnosticExperiment -WorkspaceRoot $root -Action Build -CampaignExperimentId 'extra-build' } 'supplemental build cannot repeat'
    Assert-Refused { Select-FixtureRun $root 'overlapping-extra' } 'pending supplemental build blocks concurrent campaign run'
    Complete-FixtureClaim $root 'extra-build'
    Select-FixtureRun $root 'after-extra'
    Claim-FixtureRun $root 'after-extra'
    Assert-True ((Read-Plan $root).campaign.runsClaimed -eq 1) 'original run allowance remains usable after supplemental outcome'
    Complete-FixtureClaim $root 'after-extra'
    $p = Read-Plan $root
    $p.supplementalBuild.remainingBuilds=1; $p.supplementalBuild.status='approved'
    Write-Plan $root $p
    Assert-Refused { Assert-DiagnosticExperiment -WorkspaceRoot $root -Action Build -CampaignExperimentId 'extra-build' } 'supplemental counter reset cannot reuse approval'
    Write-Output "All $script:passed diagnostic safety checks passed. No game or compiler was launched."
} finally {
    if($KeepFixtures){Write-Output "Fixtures retained in $fixtureRoot"}
    else {
        $resolved=[IO.Path]::GetFullPath($fixtureRoot)
        $tempPrefix=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\'
        if(-not $resolved.StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'sb-diagnostic-safety-tests-*'){throw 'Refusing unexpected fixture cleanup target'}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
