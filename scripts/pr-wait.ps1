<#
BLOCKS until CI has finished on the PR head, then prints CI and the bug-hunt state and exits.
Run with run_in_background so the agent is re-invoked on exit and the wait costs no model turns (D-079, D-122).
The bug hunt runs locally (pr-bug-hunt.ps1, D-155), so there is no review to wait for here.
Usage: pwsh scripts/pr-wait.ps1 <pr> [-IntervalSeconds 30] [-TimeoutMinutes 30]
Exit: 0 CI green; 1 CI failed; 2 timed out; 3 PR closed/merged.
#>
param([Parameter(Mandatory, Position = 0)][int]$Pr, [int]$IntervalSeconds = 30, [int]$TimeoutMinutes = 30)
. "$PSScriptRoot/pr-lib.ps1"
$ErrorActionPreference = 'Stop'
$repo = Get-RepoSlug; $end = (Get-Date).AddMinutes($TimeoutMinutes)
$d = Get-PrData $Pr $repo
while ($true) {
  if ($d.State -ne 'OPEN') { "#$Pr is $($d.State)"; exit 3 }
  if ($d.Ci -in 'pass', 'fail') { break }
  if ((Get-Date) -ge $end) { "#$Pr timed out after $TimeoutMinutes min (ci: $($d.Ci))"; exit 2 }
  Start-Sleep -Seconds $IntervalSeconds
  $d = Get-PrData $Pr $repo
}
"review: $(Format-HuntState $d), $(@(Get-Unresolved $d).Count) unresolved thread(s)"
switch ($d.Ci) {
  'pass' { "ci: pass ($($d.CiTotal) checks)" }
  'fail' { "ci: FAIL $($d.CiFailed -join ', ')" }
  default { "ci: $($d.Ci)" }
}
exit $(if ($d.Ci -eq 'fail') { 1 } else { 0 })
