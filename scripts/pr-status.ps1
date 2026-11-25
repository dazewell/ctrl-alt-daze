<#
One short status block (state, CI, threads, mergeability, bug hunt) ending in READY or exactly what blocks. Read-only.
Usage: pwsh scripts/pr-status.ps1 <pr> [-Ready]     (-Ready prints only the verdict)
Exit: 0 READY, 1 blocked.
#>
param([Parameter(Mandatory, Position = 0)][int]$Pr, [switch]$Ready)
. "$PSScriptRoot/pr-lib.ps1"
$ErrorActionPreference = 'Stop'
$d = Get-PrData $Pr
$v = Get-Verdict $d { Test-LocalConflict $Pr $d.Head }
if (-not $Ready) {
  "#$Pr $($d.State)$(if ($d.IsDraft) { ' draft' }) head $($d.Head.Substring(0,7)) `"$(Format-Text $d.Title 70)`""
  $ci = switch ($d.Ci) { 'pass' { "pass ($($d.CiTotal))" } 'fail' { "FAIL $($d.CiFailed -join ', ')" } 'pending' { "pending ($($d.CiRunning) running)" } default { 'no checks yet' } }
  "ci: $ci"
  "threads: $(@(Get-Unresolved $d).Count) unresolved of $($d.Threads.Count)"
  "merge: $($d.MergeState) (mergeable $($d.Mergeable))"
  "bug hunt: $(Format-HuntState $d)"
}
Write-Verdict $v $d.Head
exit $(if ($v.Ready) { 0 } else { 1 })
