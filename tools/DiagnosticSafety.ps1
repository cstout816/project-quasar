# Definitions only. Call Assert-DiagnosticExperiment before any build/run side effects.
function Get-DiagnosticStorageUsage {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$WorkspaceRoot)
    $relativeRoots=@('iso\local-analysis\logs','local-tools\steel-battalion-game\build-xpp-seeded\ProducerTrace','local-tools\steel-battalion-game\build-xpp-seeded\ProducerTrace-Candidates','local-tools\steel-battalion-game\build-xpp-seeded\diagnostic-bundles','local-tools\steel-battalion-game\build-xpp-seeded\diagnostic-backups')
    $roots = @($relativeRoots | ForEach-Object { Join-Path $WorkspaceRoot $_ } | Where-Object { Test-Path -LiteralPath $_ })
    if ($roots.Count -eq 0) {
        return [pscustomobject]@{AllocatedBytes=0L;LogicalBytes=0L}
    }
    if (-not ('SBDiagnosticStorage' -as [type])) {
        Add-Type -TypeDefinition @'
using System;using System.IO;using System.Runtime.InteropServices;
public static class SBDiagnosticStorage {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetCompressedFileSizeW(string p,out uint hi);
 public static long Allocated(string p){uint hi;uint lo=GetCompressedFileSizeW(@"\\?\"+Path.GetFullPath(p),out hi);if(lo==UInt32.MaxValue&&Marshal.GetLastWin32Error()!=0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return ((long)hi<<32)|lo;}
}
'@
    }
    [long]$allocated=0;[long]$logical=0
    foreach($path in $roots){
        $stack=New-Object 'Collections.Generic.Stack[string]';$stack.Push($path)
        while($stack.Count -gt 0){
            $directory=$stack.Pop()
            if([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint){throw "Diagnostic budget cannot follow linked directory: $directory"}
            foreach($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)){
                $attributes=[IO.File]::GetAttributes($entry)
                if($attributes -band [IO.FileAttributes]::ReparsePoint){throw "Diagnostic budget cannot follow linked path: $entry"}
                if($attributes -band [IO.FileAttributes]::Directory){$stack.Push($entry)}
                else{$logical+=([IO.FileInfo]$entry).Length;$allocated+=[SBDiagnosticStorage]::Allocated($entry)}
            }
        }
    }
    [pscustomobject]@{AllocatedBytes=$allocated;LogicalBytes=$logical}
}

function Invoke-DiagnosticPlanTransaction {
    param([string]$WorkspaceRoot, [scriptblock]$Change)

    $root = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\')
    $planPath = Join-Path $root 'analysis\diagnostic-experiment.json'
    if (-not (Test-Path -LiteralPath $planPath -PathType Leaf)) {
        throw 'Diagnostics blocked: durable experiment plan is missing.'
    }
    $ancestor = $planPath
    while ($ancestor) {
        if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Diagnostics blocked: linked experiment plan or ancestor.'
        }
        if ($ancestor -eq $root) { break }
        $ancestor = Split-Path -Parent $ancestor
    }

    # All claims, selections, and outcome writes use the same exclusive handle.
    $stream = [IO.File]::Open($planPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        if ($stream.Length -gt 1MB) { throw 'Diagnostics blocked: experiment plan exceeds its 1 MiB size limit.' }
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 4096, $true)
        try { $raw = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $jsonOptions = @{ InputObject = $raw; ErrorAction = 'Stop' }
        if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) {
            $jsonOptions.DateKind = 'String'
        }
        $plan = ConvertFrom-Json @jsonOptions
        if ($plan.schemaVersion -ne 1 -or $plan.PSObject.Properties.Name -notcontains 'history' -or $null -eq $plan.history) {
            throw 'Diagnostics blocked: invalid plan schema or missing approval history.'
        }

        & $Change $plan $root

        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($plan | ConvertTo-Json -Depth 12))
        if ($bytes.Length -gt 1MB) { throw 'Diagnostics blocked: updated experiment plan would exceed 1 MiB.' }
        $stream.Position = 0
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.SetLength($bytes.Length)
        $stream.Flush($true)
    } finally {
        $stream.Dispose()
    }
}

function Get-DiagnosticFingerprint($Experiment) {
    $material = [string]$Experiment.hypothesis + [char]0 + [string]$Experiment.newEvidence +
        [char]0 + [string]$Experiment.expectedObservation
    $digest = [Security.Cryptography.SHA256]::Create()
    try {
        [BitConverter]::ToString($digest.ComputeHash([Text.Encoding]::UTF8.GetBytes($material))).Replace('-', '')
    } finally {
        $digest.Dispose()
    }
}

function Assert-DiagnosticEvidence($Experiment) {
    foreach ($field in 'hypothesis', 'newEvidence', 'expectedObservation', 'stopCondition') {
        if ([string]::IsNullOrWhiteSpace([string]$Experiment.$field)) {
            throw "Diagnostics blocked: missing $field."
        }
    }
}

function Assert-DiagnosticBudget($Plan, [string]$Root, [string]$Action, [int]$RunSeconds, [int]$RunStorageMiB) {
    foreach ($field in 'maxRunSeconds', 'maxRunStorageMiB', 'maxDiagnosticAllocatedMiB',
            'maxDiagnosticLogicalMiB', 'minimumFreeDiskMiB', 'buildReservationMiB') {
        if ($null -eq $Plan.limits -or $Plan.limits.PSObject.Properties.Name -notcontains $field -or
            [double]$Plan.limits.$field -le 0) {
            throw "Diagnostics blocked: invalid limit $field."
        }
    }
    if ($Action -eq 'Run' -and ($RunSeconds -le 0 -or $RunSeconds -gt [int]$Plan.limits.maxRunSeconds -or
            $RunStorageMiB -le 0 -or $RunStorageMiB -gt [int]$Plan.limits.maxRunStorageMiB)) {
        throw 'Diagnostics blocked: requested time or evidence budget exceeds the approved limit.'
    }
    $usage = Get-DiagnosticStorageUsage -WorkspaceRoot $Root
    $reserve = if ($Action -eq 'Build') { [double]$Plan.limits.buildReservationMiB * 1MB } else { [double]$RunStorageMiB * 1MB }
    if ($usage.AllocatedBytes + $reserve -gt [double]$Plan.limits.maxDiagnosticAllocatedMiB * 1MB -or
        $usage.LogicalBytes + $reserve -gt [double]$Plan.limits.maxDiagnosticLogicalMiB * 1MB) {
        throw 'Diagnostics blocked: accumulated output exceeds its storage quota.'
    }
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($Root))
    if ($drive.AvailableFreeSpace - $reserve -lt [double]$Plan.limits.minimumFreeDiskMiB * 1MB) {
        throw 'Diagnostics blocked: insufficient free disk space for the approved experiment.'
    }
}

function Get-DiagnosticCampaignState($Plan, [switch]$ForOutcome) {
    $campaign = $Plan.campaign
    if ($null -eq $campaign -or $campaign.status -ne 'active' -or
        $campaign.id -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or
        [string]::IsNullOrWhiteSpace([string]$campaign.userApprovalReference)) {
        throw 'Diagnostics blocked: an explicit active campaign is required.'
    }
    $activated = [DateTimeOffset]::Parse([string]$campaign.activatedAtUtc)
    $expiry = [DateTimeOffset]::Parse([string]$campaign.expiresAtUtc)
    $now = [DateTimeOffset]::UtcNow
    if ($activated -gt $now -or $expiry -le $activated -or $expiry -gt $activated.AddDays(7)) {
        throw 'Diagnostics blocked: invalid campaign activation or fixed expiry.'
    }
    if (-not $ForOutcome -and $now -ge $expiry) {
        throw 'Diagnostics blocked: campaign expired.'
    }

    $entries = @($Plan.history | Where-Object campaignId -eq $campaign.id)
    foreach ($entry in $entries) {
        if ($entry.campaignActivatedAtUtc -ne $campaign.activatedAtUtc -or
            $entry.campaignExpiresAtUtc -ne $campaign.expiresAtUtc -or
            $entry.userApprovalReference -ne $campaign.userApprovalReference -or
            $entry.campaignBuildLimit -ne $campaign.buildLimit -or $entry.campaignRunLimit -ne $campaign.runLimit) {
            throw 'Diagnostics blocked: campaign authorization, expiry, or ceilings changed after a claim.'
        }
    }
    if (@($Plan.history | Where-Object {
        $_.userApprovalReference -eq $campaign.userApprovalReference -and $_.campaignId -ne $campaign.id
    }).Count) {
        throw 'Diagnostics blocked: the same approval cannot start a renamed campaign.'
    }
    $builds = @($entries | Where-Object action -eq 'Build').Count
    $runs = @($entries | Where-Object action -eq 'Run').Count
    if ($campaign.buildLimit -notmatch '^\d+$' -or $campaign.runLimit -notmatch '^\d+$' -or
        $campaign.buildLimit -gt 32 -or $campaign.runLimit -gt 64 -or
        $campaign.buildsClaimed -ne $builds -or $campaign.runsClaimed -ne $runs -or
        $builds -gt $campaign.buildLimit -or $runs -gt $campaign.runLimit) {
        throw 'Diagnostics blocked: invalid campaign counters or ledger mismatch.'
    }
    if ($Plan.limits.maxRunSeconds -gt 900 -or $Plan.limits.maxRunStorageMiB -gt 64 -or
        $Plan.limits.maxDiagnosticAllocatedMiB -gt 4096 -or $Plan.limits.maxDiagnosticLogicalMiB -gt 16384 -or
        $Plan.limits.minimumFreeDiskMiB -lt 10240 -or $Plan.limits.buildReservationMiB -lt 512) {
        throw 'Diagnostics blocked: campaign storage or runtime limits were relaxed.'
    }
    [pscustomobject]@{ Campaign = $campaign; Entries = $entries; Expiry = $expiry }
}

function Assert-DiagnosticCampaignSelection($Plan, $State, $Experiment) {
    Assert-DiagnosticEvidence $Experiment
    if ($Experiment.action -notin 'Build', 'Run' -or $Experiment.kind -notin 'New', 'Intermittent', 'Reproducibility' -or
        $Experiment.id -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
        throw 'Diagnostics blocked: invalid campaign experiment selection.'
    }
    if (@($Plan.history | Where-Object {
        ($_.campaignId -eq $State.Campaign.id -or
         $_.supplementalCampaignId -eq $State.Campaign.id) -and $null -eq $_.outcome
    }).Count) {
        throw 'Diagnostics blocked: record the prior campaign claim outcome after it exits.'
    }
    if (@($Plan.history | Where-Object experimentId -eq $Experiment.id).Count) {
        throw 'Diagnostics blocked: campaign experiment ID was already attempted.'
    }
    if (($Experiment.action -eq 'Build' -and $State.Campaign.buildsClaimed -ge $State.Campaign.buildLimit) -or
        ($Experiment.action -eq 'Run' -and $State.Campaign.runsClaimed -ge $State.Campaign.runLimit)) {
        throw "Diagnostics blocked: campaign $($Experiment.action) allowance exhausted."
    }
    $fingerprint = Get-DiagnosticFingerprint $Experiment
    $equivalent = @($Plan.history | Where-Object {
        $_.action -eq $Experiment.action -and $_.fingerprint -eq $fingerprint
    })
    if ($Experiment.kind -eq 'New') {
        if ($equivalent.Count) { throw 'Diagnostics blocked: unchanged hypothesis/evidence was already tested.' }
        if ($Experiment.repeatOf) { throw 'Diagnostics blocked: a new experiment cannot reference a repeat.' }
    } else {
        $original = @($equivalent | Where-Object {
            $_.experimentId -eq $Experiment.repeatOf -and $_.campaignId -eq $State.Campaign.id -and
            $_.kind -eq 'New' -and $null -ne $_.outcome
        })
        if ($Experiment.action -ne 'Run' -or $original.Count -ne 1 -or
            [string]::IsNullOrWhiteSpace([string]$Experiment.repeatPurpose)) {
            throw 'Diagnostics blocked: repeat requires a completed original run with identical evidence and an explicit repeat purpose.'
        }
        if (@($equivalent | Where-Object {
            $_.campaignId -eq $State.Campaign.id -and $_.kind -eq $Experiment.kind
        }).Count) {
            throw 'Diagnostics blocked: this repeat purpose was already attempted for the same experiment.'
        }
    }
}

function Set-DiagnosticCampaignExperiment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$WorkspaceRoot,
        [Parameter(Mandatory)][string]$ExperimentId,
        [Parameter(Mandatory)][ValidateSet('Build', 'Run')][string]$Action,
        [Parameter(Mandatory)][string]$Hypothesis,
        [Parameter(Mandatory)][string]$NewEvidence,
        [Parameter(Mandatory)][string]$ExpectedObservation,
        [Parameter(Mandatory)][string]$StopCondition,
        [ValidateSet('New', 'Intermittent', 'Reproducibility')][string]$Kind = 'New',
        [string]$RepeatOf,
        [string]$RepeatPurpose
    )
    Invoke-DiagnosticPlanTransaction $WorkspaceRoot {
        param($plan, $root)
        $state = Get-DiagnosticCampaignState $plan
        if ($null -ne $state.Campaign.experiment) {
            throw 'Diagnostics blocked: an unclaimed campaign experiment is already selected.'
        }
        $experiment = [pscustomobject]@{
            id = $ExperimentId; action = $Action; kind = $Kind
            hypothesis = $Hypothesis; newEvidence = $NewEvidence
            expectedObservation = $ExpectedObservation; stopCondition = $StopCondition
            repeatOf = $RepeatOf; repeatPurpose = $RepeatPurpose
        }
        Assert-DiagnosticCampaignSelection $plan $state $experiment
        $state.Campaign.experiment = $experiment
    }
}

function Complete-DiagnosticCampaignExperiment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$WorkspaceRoot,
        [Parameter(Mandatory)][string]$ExperimentId,
        [Parameter(Mandatory)][ValidateSet('Confirmed', 'Rejected', 'Inconclusive', 'Failed', 'Verified')][string]$Result,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Observation,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$NextDecision
    )
    Invoke-DiagnosticPlanTransaction $WorkspaceRoot {
        param($plan, $root)
        $state = Get-DiagnosticCampaignState $plan -ForOutcome
        $entry = @($plan.history | Where-Object {
            $_.experimentId -eq $ExperimentId -and
            ($_.campaignId -eq $state.Campaign.id -or
             $_.supplementalCampaignId -eq $state.Campaign.id)
        })
        if ($entry.Count -ne 1 -or $null -ne $entry[0].outcome) {
            throw 'Diagnostics blocked: no unique pending claim to record.'
        }
        $entry[0].outcome = [pscustomobject]@{
            recordedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            result = $Result; observation = $Observation; nextDecision = $NextDecision
        }
    }
}

function Claim-DiagnosticSupplementalBuild($Plan, [string]$Root, [string]$Id) {
    $approval = $Plan.supplementalBuild
    if ($null -eq $approval -or $approval.experimentId -ne $Id) { return $false }
    $state = Get-DiagnosticCampaignState $Plan
    Assert-DiagnosticEvidence $approval
    if ($approval.status -ne 'approved' -or $approval.remainingBuilds -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$approval.userApprovalReference) -or
        $approval.campaignId -ne $state.Campaign.id -or
        $state.Campaign.buildsClaimed -ne $state.Campaign.buildLimit) {
        throw 'Diagnostics blocked: invalid supplemental build approval.'
    }
    $expiry = [DateTimeOffset]::Parse([string]$approval.expiresAtUtc)
    $now = [DateTimeOffset]::UtcNow
    if ($expiry -le $now -or $expiry -gt $now.AddHours(24) -or $expiry -gt $state.Expiry) {
        throw 'Diagnostics blocked: invalid supplemental build expiry.'
    }
    if (@($Plan.history | Where-Object {
        $_.experimentId -eq $Id -or
        $_.userApprovalReference -eq $approval.userApprovalReference -or
        ($_.PSObject.Properties.Name -contains 'outcome' -and $null -eq $_.outcome)
    }).Count) {
        throw 'Diagnostics blocked: supplemental approval is consumed or a claim is pending.'
    }
    Assert-DiagnosticBudget $Plan $Root 'Build' 0 0
    $approval.remainingBuilds = 0
    $approval.status = 'consumed'
    $Plan.history = @($Plan.history) + [pscustomobject]@{
        experimentId = $Id; action = 'Build'; kind = 'Supplemental'
        supplementalCampaignId = $state.Campaign.id
        userApprovalReference = $approval.userApprovalReference
        claimedAtUtc = $now.ToString('o'); processId = $PID
        hypothesis = $approval.hypothesis; newEvidence = $approval.newEvidence
        expectedObservation = $approval.expectedObservation; stopCondition = $approval.stopCondition
        fingerprint = Get-DiagnosticFingerprint $approval
        outcome = $null
    }
    return $true
}

function Assert-DiagnosticExperiment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Build', 'Run')][string]$Action,
        [Parameter(Mandatory)][string]$WorkspaceRoot,
        [int]$RunSeconds = 0,
        [int]$RunStorageMiB = 0,
        [AllowEmptyString()][string]$CampaignExperimentId
    )
    Invoke-DiagnosticPlanTransaction $WorkspaceRoot {
        param($plan, $root)
        if ($CampaignExperimentId) {
            if ($Action -eq 'Build' -and
                (Claim-DiagnosticSupplementalBuild $plan $root $CampaignExperimentId)) { return }
            $state = Get-DiagnosticCampaignState $plan
            $campaign = $state.Campaign
            $experiment = $campaign.experiment
            if ($null -eq $experiment -or $experiment.id -ne $CampaignExperimentId -or $experiment.action -ne $Action) {
                throw 'Diagnostics blocked: requested action does not match the selected campaign experiment.'
            }
            Assert-DiagnosticCampaignSelection $plan $state $experiment
            Assert-DiagnosticBudget $plan $root $Action $RunSeconds $RunStorageMiB
            $claimTime = [DateTimeOffset]::UtcNow
            if ($claimTime.AddSeconds($RunSeconds) -ge $state.Expiry) {
                throw 'Diagnostics blocked: the requested attempt exceeds the remaining campaign time.'
            }
            $counter = if ($Action -eq 'Build') { 'buildsClaimed' } else { 'runsClaimed' }
            $campaign.$counter = [int]$campaign.$counter + 1
            $campaign.experiment = $null
            $plan.history = @($plan.history) + [pscustomobject]@{
                experimentId = $CampaignExperimentId; action = $Action; kind = $experiment.kind
                claimedAtUtc = $claimTime.ToString('o'); processId = $PID
                campaignId = $campaign.id; userApprovalReference = $campaign.userApprovalReference
                campaignActivatedAtUtc = $campaign.activatedAtUtc; campaignExpiresAtUtc = $campaign.expiresAtUtc
                campaignBuildLimit = $campaign.buildLimit; campaignRunLimit = $campaign.runLimit
                hypothesis = $experiment.hypothesis; newEvidence = $experiment.newEvidence
                expectedObservation = $experiment.expectedObservation; stopCondition = $experiment.stopCondition
                fingerprint = Get-DiagnosticFingerprint $experiment
                repeatOf = $experiment.repeatOf; repeatPurpose = $experiment.repeatPurpose
                outcome = $null
            }
            Write-Verbose "Claimed campaign $Action allowance for $CampaignExperimentId."
            return
        }

        if ($plan.status -ne 'approved') {
            throw 'Diagnostics paused: one explicit, user-approved experiment is required.'
        }
        $legacyPause = Join-Path $root 'CLEANUP-2026-11-06\DIAGNOSTICS-PAUSED.txt'
        if (Test-Path -LiteralPath $legacyPause) { throw "Diagnostics remain paused: $legacyPause" }
        Assert-DiagnosticEvidence $plan
        foreach ($field in 'experimentId', 'userApprovalReference', 'expiresAtUtc') {
            if ([string]::IsNullOrWhiteSpace([string]$plan.$field)) { throw "Diagnostics blocked: missing $field." }
        }
        $expiry = [DateTimeOffset]::Parse([string]$plan.expiresAtUtc)
        $now = [DateTimeOffset]::UtcNow
        if ($expiry -le $now -or $expiry -gt $now.AddHours(24)) {
            throw 'Diagnostics blocked: approval must expire within the next 24 hours.'
        }
        foreach ($field in 'remainingBuilds', 'remainingRuns') {
            if ($plan.PSObject.Properties.Name -notcontains $field -or $plan.$field -notin 0, 1) {
                throw 'Diagnostics blocked: at most one build and one run may be approved.'
            }
        }
        $counter = if ($Action -eq 'Build') { 'remainingBuilds' } else { 'remainingRuns' }
        if ([int]$plan.$counter -le 0) {
            throw "Diagnostics blocked: $Action allowance already consumed; review before another attempt."
        }
        $history = @($plan.history)
        if (@($history | Where-Object { $_.experimentId -eq $plan.experimentId -and $_.action -eq $Action }).Count) {
            throw 'Diagnostics blocked: this experiment/action was already attempted.'
        }
        if (@($history | Where-Object {
            $_.experimentId -ne $plan.experimentId -and $_.userApprovalReference -eq $plan.userApprovalReference
        }).Count) {
            throw 'Diagnostics blocked: the same approval cannot authorize a renamed experiment.'
        }
        $fingerprint = Get-DiagnosticFingerprint $plan
        if (@($history | Where-Object { $_.action -eq $Action -and $_.fingerprint -eq $fingerprint }).Count) {
            throw 'Diagnostics blocked: unchanged hypothesis/evidence was already tested; review instead of repeating.'
        }
        Assert-DiagnosticBudget $plan $root $Action $RunSeconds $RunStorageMiB
        $plan.$counter = 0
        $plan.history = $history + [pscustomobject]@{
            experimentId = [string]$plan.experimentId; action = $Action
            claimedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); processId = $PID
            hypothesis = [string]$plan.hypothesis; userApprovalReference = [string]$plan.userApprovalReference
            fingerprint = $fingerprint
        }
        if ([int]$plan.remainingBuilds -eq 0 -and [int]$plan.remainingRuns -eq 0) {
            $plan.status = 'awaiting_review'
        }
        Write-Verbose "Claimed the single $Action allowance for $($plan.experimentId)."
    }
}
