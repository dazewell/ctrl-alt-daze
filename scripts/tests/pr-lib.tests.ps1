# Plain-script tests for scripts/pr-lib.ps1 pure functions (no Pester in this repo). Run: pwsh scripts/tests/pr-lib.tests.ps1
# Exit 0 = all passed. CI runs it (ci.yml).
. "$PSScriptRoot/../pr-lib.ps1"
$ErrorActionPreference = 'Stop'
$script:fails = 0
function Check([string]$Name, $Actual, $Expected) {
  if ("$Actual" -ne "$Expected") { Write-Host "FAIL $Name`n  expected: $Expected`n  actual:   $Actual"; $script:fails++ } else { Write-Host "ok   $Name" }
}

function New-Raw([hashtable]$o = @{}) {
  $files = $o.files ?? @(@{ path = 'src/A.cs'; additions = 150 }, @{ path = 'tests/A.Tests/ATests.cs'; additions = 400 }, @{ path = 'docs/X.md'; additions = 50 })
  $threads = $o.threads ?? @()
  $reviews = $o.reviews ?? @()
  $checks = $o.checks ?? @(@{ __typename = 'CheckRun'; name = 'build'; status = 'COMPLETED'; conclusion = 'SUCCESS' })
  @{ data = @{ repository = @{ pullRequest = @{
          number = 7; title = 'feat: x'; state = ($o.state ?? 'OPEN'); isDraft = $false; headRefOid = 'abcdef1234567890'
          mergeStateStatus = ($o.merge ?? 'CLEAN'); mergeable = ($o.mergeable ?? 'MERGEABLE')
          files = @{ totalCount = $files.Count; nodes = $files }
          reviewThreads = @{ totalCount = $threads.Count; nodes = $threads }
          reviews = @{ nodes = $reviews }
          commits = @{ nodes = @(@{ commit = @{ statusCheckRollup = @{ contexts = @{ nodes = $checks } } } }) }
        } } } } | ConvertTo-Json -Depth 20 | ConvertFrom-Json
}
$hunt = @{ id = 'PRR_1'; state = 'COMMENTED'; body = "<!-- bug-hunt head=abcdef1234567890 findings=1 -->`n**Bug hunt**"; submittedAt = '2026-11-12T10:00:00Z'; author = @{ login = 'dazewell' }; commit = @{ oid = 'abcdef1234567890' } }
$oldHunt = @{ id = 'PRR_0'; state = 'COMMENTED'; body = '<!-- bug-hunt head=1111111222222 findings=0 -->'; submittedAt = '2026-11-11T10:00:00Z'; author = @{ login = 'dazewell' }; commit = @{ oid = '1111111222222' } }
$plain = @{ id = 'PRR_2'; state = 'COMMENTED'; body = 'looks fine'; submittedAt = '2026-11-12T11:00:00Z'; author = @{ login = 'someone' }; commit = @{ oid = 'abcdef1234567890' } }
$thread = @{ id = 'PRRT_aaa111'; isResolved = $false; isOutdated = $false; path = 'src/A.cs'; line = 12; originalLine = 12
  comments = @{ nodes = @(@{ author = @{ login = 'dazewell' }; body = ('x' * 400) }) } }

# code-line counting: docs and tests excluded
$d = ConvertTo-PrData (New-Raw)
Check 'code adds exclude tests/docs' $d.CodeAdds 150
Check 'review required over 100' (Test-ReviewRequired $d) $true
Check 'tests path is not code' (Test-CodePath 'tests/X/Y.cs') $false
Check 'md is not code' (Test-CodePath 'README.md') $false
Check 'script is code' (Test-CodePath 'scripts/pr-lib.ps1') $true
Check 'script tests are not code' (Test-CodePath 'scripts/tests/pr-lib.tests.ps1') $false

# verdicts
$v = Get-Verdict $d
Check 'required review missing blocks' $v.Ready $false
Check 'missing review message' ($v.Blockers -join '|' -match 'bug hunt required') $true
$d = ConvertTo-PrData (New-Raw @{ reviews = @($hunt) })
Check 'bug hunt on head, CI green, no threads: ready' (Get-Verdict $d).Ready $true
Check 'hunt state text' (Format-HuntState $d) '1 of 2, last on abcdef1 with 1 finding(s); required'
$d = ConvertTo-PrData (New-Raw @{ reviews = @($plain) })
Check 'a review without the marker is not a bug hunt' $d.BugHunts.Count 0
$d = ConvertTo-PrData (New-Raw @{ reviews = @($hunt, $oldHunt) })
Check 'hunts sorted oldest first' $d.BugHunts[-1].Id 'PRR_1'
$d = ConvertTo-PrData (New-Raw @{ reviews = @($oldHunt) })
$v = Get-Verdict $d
Check 'hunt on an older head is a warning only' $v.Ready $true
Check 'older head warning' ($v.Warnings -join '|' -match 'older commit') $true
Check 'older head in state text' ((Format-HuntState $d) -match 'older than head') $true
$d = ConvertTo-PrData (New-Raw @{ reviews = @($hunt); threads = @($thread) })
$v = Get-Verdict $d
Check 'unresolved thread blocks' $v.Ready $false
Check 'unresolved count' @(Get-Unresolved $d).Count 1
$d = ConvertTo-PrData (New-Raw @{ files = @(@{ path = 'docs/a.md'; additions = 900 }) })
Check 'docs-only needs no review' (Get-Verdict $d).Ready $true
$d = ConvertTo-PrData (New-Raw @{ files = @(@{ path = 'src/A.cs'; additions = 20 }) })
Check 'small change needs no review' (Get-Verdict $d).Ready $true
$d = ConvertTo-PrData (New-Raw @{ checks = @(@{ __typename = 'CheckRun'; name = 'build'; status = 'IN_PROGRESS'; conclusion = $null }); files = @(@{ path = 'src/A.cs'; additions = 5 }) })
Check 'running CI blocks' (Get-Verdict $d).Ready $false
Check 'ci pending' $d.Ci 'pending'
$d = ConvertTo-PrData (New-Raw @{ checks = @(@{ __typename = 'CheckRun'; name = 'build'; status = 'COMPLETED'; conclusion = 'FAILURE' }) })
Check 'ci fail' $d.Ci 'fail'
Check 'ci failed names' ($d.CiFailed -join ',') 'build'
$d = ConvertTo-PrData (New-Raw @{ checks = @() })
Check 'no checks' $d.Ci 'none'
$d = ConvertTo-PrData (New-Raw @{ merge = 'DIRTY'; mergeable = 'CONFLICTING'; files = @(@{ path = 'src/A.cs'; additions = 5 }) })
Check 'conflict confirmed blocks' (Get-Verdict $d { 'conflict' }).Ready $false
$v = Get-Verdict $d { 'clean' }
Check 'stale conflict notice is a warning only' $v.Ready $true
Check 'stale warning text' ($v.Warnings -join '|' -match 'stale') $true
Check 'unverifiable conflict blocks' (Get-Verdict $d { 'unknown' }).Ready $false
$d = ConvertTo-PrData (New-Raw @{ state = 'MERGED' })
Check 'merged PR is not ready' (Get-Verdict $d).Ready $false
$d = ConvertTo-PrData (New-Raw @{ merge = 'BLOCKED'; files = @(@{ path = 'src/A.cs'; additions = 5 }) })
Check 'BLOCKED with nothing else listed blocks' (Get-Verdict $d).Ready $false

# formatting
$d = ConvertTo-PrData (New-Raw @{ threads = @($thread) })
$f = Format-Thread $d.Threads[0]
Check 'thread line has id, location, author' ($f -match '^PRRT_aaa111 src/A.cs:12 @dazewell') $true
Check 'thread body truncated' ($f.Length -lt 400) $true
Check 'Format-Text collapses whitespace' (Format-Text "a`n  b`t c") 'a b c'
Check 'Format-Text no truncation when short' (Format-Text 'abc' 5) 'abc'

# reply pairs
$p = @(ConvertFrom-ReplyPairs @('PRRT_a=Fixed in abc', 'PRRT_b=x=y is kept'))
Check 'pair id' $p[0].Id 'PRRT_a'
Check 'pair message' $p[0].Message 'Fixed in abc'
Check 'message keeps =' $p[1].Message 'x=y is kept'
$threw = $false; try { ConvertFrom-ReplyPairs @('novalue') | Out-Null } catch { $threw = $true }
Check 'bad pair throws' $threw $true
Check 'prefix resolves' (Resolve-ThreadId $d 'PRRT_aa').Id 'PRRT_aaa111'
$threw = $false; try { Resolve-ThreadId $d 'nope' | Out-Null } catch { $threw = $true }
Check 'unknown thread throws' $threw $true
$cased = [pscustomobject]@{ Threads = @([pscustomobject]@{ Id = 'PRRT_xMgP' }, [pscustomobject]@{ Id = 'PRRT_xMgp' }) }
Check 'ids differing only by case resolve exactly' (Resolve-ThreadId $cased 'PRRT_xMgp').Id 'PRRT_xMgp'

# failed-test parsing and flake rule
$log = @(
  "build`tTest`t2026-11-12T01:00:00.1234567Z   failed TriggerServiceTests.Fires_once (12ms)",
  "build`tTest`t2026-11-12T01:00:00.1234567Z   failed TriggerServiceTests.Fires_once (12ms)",
  "build`tTest`t2026-11-12T01:00:01.0000000Z   [FAIL] Foo.BarTests.Baz",
  "build`tTest`t2026-11-12T01:00:02.0000000Z   passed Other (1ms)")
$t = Get-FailedTests $log
Check 'failed tests parsed and deduped' ($t -join ',') 'Foo.BarTests.Baz,TriggerServiceTests.Fires_once'
Check 'TriggerServiceTests is flaky' (Test-Flaky 'TriggerServiceTests.Fires_once') $true
Check 'other tests are not flaky' (Test-Flaky 'Foo.BarTests.Baz') $false

# bug hunt: risk level, diff line map, review body
$r = Get-BugHuntRisk @('+    var p = Process.Start(psi);', '+    await Task.Delay(10);', ' unchanged Process.Start') @('src/CtrlAltDaze.Providers/Process/Run.cs')
Check 'process + idle scores 5' $r.Score 5
Check 'standard level' $r.Level 'standard'
Check 'context lines do not count' ($r.Reasons -join ',') 'process(1),idle(1)'
$r = Get-BugHuntRisk @('+ int x = 1;') @('src/A/B.cs')
Check 'no signals is low' "$($r.Level) $($r.Score)" 'low 0'
$r = Get-BugHuntRisk @('+ File.WriteAllText(p, s);', '+ Process.Start(x);', '+ lock (gate) {', '+ CancellationToken ct') @('src/CtrlAltDaze.Runtime/FlowRunner.cs')
Check 'core file adds 2, reaches high' "$($r.Score) $($r.Level)" '12 max'
$big = @('+ Thread.Sleep(1);') + @(1..801 | ForEach-Object { '+ x' })
Check 'big diff bumps low to standard' (Get-BugHuntRisk $big @('src/A.cs')).Level 'standard'
$big = @('+ Thread.Sleep(1);', '+ Process.Start(a);') + @(1..801 | ForEach-Object { '+ x' })
Check 'big diff bumps standard to high' (Get-BugHuntRisk $big @('src/A.cs')).Level 'high'
$m = Get-DiffLineMap @('diff --git a/src/A.cs b/src/A.cs', '--- a/src/A.cs', '+++ b/src/A.cs', '@@ -10,4 +12,6 @@ void M()', ' a', '@@ -40 +50 @@', '+b', '--- a/old.cs', '+++ /dev/null', '@@ -1,3 +0,0 @@')
Check 'hunk lines mapped' (($m['src/A.cs'] | Sort-Object) -join ',') '12,13,14,15,16,17,50'
Check 'deleted file has no lines' $m.ContainsKey('/dev/null') $false
$body = Format-BugHuntBody ([pscustomobject]@{ head = 'abc123'; run = 1; level = 'low'; model = 'sonnet'; effort = 'medium'; reasons = @('text(1)'); findings = @(); coverage = 'walked x' }) @()
Check 'body starts with the marker' ($body -split "`n")[0] '<!-- bug-hunt head=abc123 findings=0 -->'
Check 'body says no findings' ($body -match 'No findings') $true
$f = Format-BugHuntFinding ([pscustomobject]@{ severity = 'important'; confidence = 'plausible'; title = 'T'; scenario = 'S'; evidence = @('a.cs:1'); fix = 'F' })
Check 'finding text' ($f -match '^\*\*Bug hunt · important · plausible\*\* — T' -and $f -match '`a.cs:1`') $true
$r = Get-BugHuntRisk @('+++ b/scripts/x.ps1', "+  process = 'Process\.Start|Kill'", '+++ b/src/A.cs', '+ Thread.Sleep(1);') @('scripts/x.ps1', 'src/A.cs')
Check 'only C#/XAML lines are scored' ($r.Reasons -join ',') 'idle(1)'
Check 'pathspec excludes tests and docs' ((Get-BugHuntPathspec) -join ' ' -match 'tests/\*\*.*\*\.tests\.ps1') $true

if ($script:fails) { Write-Host "$script:fails failed"; exit 1 } else { Write-Host 'all passed' }
