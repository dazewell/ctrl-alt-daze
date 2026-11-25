# Shared helpers for scripts/pr-*.ps1 (D-122). Dot-source it: . "$PSScriptRoot/pr-lib.ps1"
# Pure functions (ConvertTo-PrData, Get-Verdict, Format-*, Get-FailedTests ...) are covered by scripts/tests/pr-lib.tests.ps1.
# Output rule for every pr-* script: one short line per fact, bodies cut to 300 chars, never JSON.

$script:PrQuery = @'
query($owner:String!,$name:String!,$n:Int!){repository(owner:$owner,name:$name){pullRequest(number:$n){
 number title state isDraft headRefOid mergeStateStatus mergeable
 files(first:100){totalCount nodes{path additions}}
 reviewThreads(first:100){totalCount nodes{id isResolved isOutdated path line originalLine comments(first:20){totalCount nodes{author{login} body}}}}
 reviews(last:30){nodes{id state body submittedAt author{login} commit{oid}}}
 commits(last:1){nodes{commit{statusCheckRollup{contexts(first:100){totalCount nodes{__typename
   ... on CheckRun{name status conclusion} ... on StatusContext{context state}}}}}}}
}}}
'@

function Get-RepoSlug {
  $s = gh repo view --json nameWithOwner -q .nameWithOwner 2>$null
  if (-not $s) { throw 'cannot resolve the repo with gh (run from a checkout, gh logged in)' }
  $s.Trim()
}

function Format-Text([string]$Text, [int]$Max = 300) {
  $t = ($Text -replace '\s+', ' ').Trim()
  if ($t.Length -gt $Max) { $t.Substring(0, $Max) + '… (-Full for all)' } else { $t }
}

function Test-CodePath([string]$Path) {
  # "non-test code" for the bug-hunt rule: not docs, not tests (repo-workflow section 6)
  -not ($Path -match '^docs/' -or $Path -match '\.md$' -or $Path -match '^tests/' -or $Path -match '\.tests\.ps1$')
}

function ConvertTo-PrData($Raw) {
  $p = $Raw.data.repository.pullRequest
  if (-not $p) { throw 'PR not found' }
  $threads = @($p.reviewThreads.nodes | ForEach-Object {
      [pscustomobject]@{
        Id = $_.id; Resolved = [bool]$_.isResolved; Outdated = [bool]$_.isOutdated; Path = $_.path
        Line = $(if ($_.line) { $_.line } else { $_.originalLine })
        CommentsTotal = $_.comments.totalCount
        Comments = @($_.comments.nodes | ForEach-Object { [pscustomobject]@{ Author = $_.author.login; Body = $_.body } })
      } })
  $reviews = @($p.reviews.nodes | ForEach-Object {
      [pscustomobject]@{ Id = $_.id; Author = $_.author.login; State = $_.state; Body = $_.body; At = $_.submittedAt; Commit = $_.commit.oid } })
  # bug-hunt reviews carry a marker (pr-bug-hunt.ps1); the author is whoever ran it
  $hunts = @($reviews | Where-Object { $_.Body -match '<!-- bug-hunt head=' } | Sort-Object At)

  $ctx = @($p.commits.nodes[0].commit.statusCheckRollup.contexts.nodes | Where-Object { $_ })
  $failed = @(); $running = 0
  foreach ($c in $ctx) {
    if ($c.__typename -eq 'CheckRun') {
      if ($c.status -ne 'COMPLETED') { $running++ }
      elseif ($c.conclusion -in 'FAILURE', 'TIMED_OUT', 'CANCELLED', 'ACTION_REQUIRED', 'STARTUP_FAILURE') { $failed += $c.name }
    } else {
      if ($c.state -in 'PENDING', 'EXPECTED') { $running++ } elseif ($c.state -in 'FAILURE', 'ERROR') { $failed += $c.context }
    }
  }
  $ciTruncated = ($p.commits.nodes[0].commit.statusCheckRollup.contexts.totalCount -gt 100)
  $ci = if ($ctx.Count -eq 0) { 'none' } elseif ($failed) { 'fail' } elseif ($running) { 'pending' } else { 'pass' }
  $adds = 0; $codeFiles = 0
  foreach ($f in $p.files.nodes) { if (Test-CodePath $f.path) { $adds += $f.additions; $codeFiles++ } }

  [pscustomobject]@{
    Number = $p.number; Title = $p.title; State = $p.state; IsDraft = [bool]$p.isDraft; Head = $p.headRefOid
    MergeState = $p.mergeStateStatus; Mergeable = $p.mergeable
    Threads = $threads; Reviews = $reviews; BugHunts = $hunts
    Ci = $ci; CiFailed = $failed; CiRunning = $running; CiTotal = $ctx.Count
    CodeAdds = $adds; CodeFiles = $codeFiles; FilesTruncated = ($p.files.totalCount -gt 100); ThreadsTruncated = ($p.reviewThreads.totalCount -gt 100); CiTruncated = $ciTruncated
  }
}

function Get-PrData([int]$Pr, [string]$Repo) {
  if (-not $Repo) { $Repo = Get-RepoSlug }
  $o, $n = $Repo -split '/'
  $json = gh api graphql -f query=$script:PrQuery -f owner=$o -f name=$n -F n=$Pr 2>&1
  if ($LASTEXITCODE -ne 0) { throw "gh graphql failed: $json" }
  ConvertTo-PrData ($json | ConvertFrom-Json)
}

function Get-Unresolved($D) { @($D.Threads | Where-Object { -not $_.Resolved }) }

function Test-ReviewRequired($D) { $D.CodeAdds -gt 100 }

# Returns @{ Ready; Blockers[]; Warnings[] }. $ConflictCheck: scriptblock returning 'conflict'|'clean'|'unknown' (git merge-tree), only called when GitHub reports a conflict.
function Get-Verdict($D, [scriptblock]$ConflictCheck = { 'unknown' }) {
  $b = [System.Collections.Generic.List[string]]::new(); $w = [System.Collections.Generic.List[string]]::new()
  if ($D.State -ne 'OPEN') { $b.Add("PR is $($D.State)") }
  if ($D.IsDraft) { $b.Add('PR is a draft (gh pr ready <n>)') }
  switch ($D.Ci) {
    'fail' { $b.Add("CI failed: $($D.CiFailed -join ', ') (pr-rerun.ps1 if flaky)") }
    'pending' { $b.Add("CI still running ($($D.CiRunning) of $($D.CiTotal))") }
    'none' { $b.Add('no CI checks yet (just pushed? pr-wait.ps1)') }
  }
  $un = Get-Unresolved $D
  if ($un.Count) { $b.Add("$($un.Count) unresolved thread(s) (pr-threads.ps1, then pr-reply.ps1)") }
  if (Test-ReviewRequired $D) {
    if ($D.BugHunts.Count -eq 0) { $b.Add("bug hunt required ($($D.CodeAdds) code lines added) and not run (pr-bug-hunt.ps1 once CI is green)") }
    elseif ($D.BugHunts[-1].Commit -ne $D.Head) { $w.Add('the bug hunt read an older commit than the head (fine unless you pushed big changes since)') }
  }
  # fail closed when a connection was cut at 100: hidden files/threads/checks could change the verdict (review of PR 186)
  if ($D.FilesTruncated) { $b.Add('more than 100 files changed: code-line count unreliable, decide the bug-hunt rule by hand') }
  if ($D.ThreadsTruncated) { $b.Add('more than 100 review threads: cannot verify all are resolved') }
  if ($D.CiTruncated) { $b.Add('more than 100 checks: cannot verify all passed') }
  if ($D.MergeState -eq 'DIRTY' -or $D.Mergeable -eq 'CONFLICTING') {
    switch (& $ConflictCheck) {
      'conflict' { $b.Add('merge conflict with main (git merge origin/main, resolve, push)') }
      'clean' { $w.Add('GitHub reports a conflict but git merge-tree finds none: stale notice, recheck in a minute') }
      default { $b.Add('GitHub reports a merge conflict (could not verify locally; notices can be stale)') }
    }
  } elseif ($D.MergeState -eq 'UNKNOWN' -or $D.Mergeable -eq 'UNKNOWN') { $b.Add('GitHub is still computing mergeability: retry in a few seconds') }
  elseif ($D.MergeState -eq 'UNSTABLE') { $b.Add('mergeStateStatus UNSTABLE: a check GitHub counts as failing (maybe external) is not in the list above') }
  elseif ($D.MergeState -eq 'BEHIND') { $w.Add('branch is behind main (merge commit still fine unless the ruleset needs it up to date)') }
  elseif ($D.MergeState -eq 'BLOCKED' -and $b.Count -eq 0) { $b.Add('mergeStateStatus BLOCKED by a ruleset/required check not listed above (approval? required check name?)') }
  [pscustomobject]@{ Ready = ($b.Count -eq 0); Blockers = @($b); Warnings = @($w) }
}

# Local second opinion on a reported conflict; fetches main + the PR head, merge-tree on the exact head SHA.
function Test-LocalConflict([int]$Pr, [string]$Head) {
  git fetch -q origin main "pull/$Pr/head" 2>$null
  if ($LASTEXITCODE -ne 0) { return 'unknown' }
  git merge-tree --write-tree --quiet origin/main $Head 2>$null | Out-Null
  if ($LASTEXITCODE -eq 0) { 'clean' } elseif ($LASTEXITCODE -eq 1) { 'conflict' } else { 'unknown' }
}

function Write-Verdict($V, [string]$Sha) {
  foreach ($w in $V.Warnings) { "warn: $w" }
  if ($V.Ready) { "READY (head $($Sha.Substring(0,7)))" } else { "BLOCKED (head $($Sha.Substring(0,7)))"; foreach ($x in $V.Blockers) { "  - $x" } }
}

function Format-Thread($T, [int]$Max = 300) {
  $c = $T.Comments[0]
  $more = if ($T.Comments.Count -gt 1) { " (+$($T.Comments.Count - 1) replies)" } else { '' }
  $flag = if ($T.Outdated) { ' [outdated]' } else { '' }
  $loc = if ($T.Line) { "$($T.Path):$($T.Line)" } else { $T.Path }
  "$($T.Id) $loc @$($c.Author)$flag$more`n    $(Format-Text $c.Body $Max)"
}

function Format-Review($R, [int]$Max = 300) {
  "review $($R.Id) @$($R.Author) $($R.State) @$($R.Commit.Substring(0,7))`n    $(Format-Text $R.Body $Max)"
}

# "id=message" pairs; id may be a unique prefix, message keeps any further '='.
function ConvertFrom-ReplyPairs([string[]]$Pairs) {
  foreach ($p in $Pairs) {
    $i = $p.IndexOf('=')
    if ($i -lt 1 -or $i -eq $p.Length - 1) { throw "bad pair '$p': expected id=message" }
    [pscustomobject]@{ Id = $p.Substring(0, $i).Trim(); Message = $p.Substring($i + 1).Trim() }
  }
}

function Resolve-ThreadId($D, [string]$IdOrPrefix) {
  # Case-sensitive: GitHub node ids can differ only by case (PRRT_..MgP and PRRT_..Mgp on PR 193).
  $m = @($D.Threads | Where-Object { $_.Id -ceq $IdOrPrefix })
  if (-not $m) { $m = @($D.Threads | Where-Object { $_.Id.StartsWith($IdOrPrefix, [StringComparison]::Ordinal) }) }
  if ($m.Count -ne 1) { throw "thread '$IdOrPrefix' matches $($m.Count) threads of this PR" }
  $m[0]
}

# Known flaky tests: rerun the failed jobs instead of investigating (docs/DEADENDS.md X-012).
$script:KnownFlaky = @('TriggerServiceTests')

# Failed test names from `gh run view --log-failed` text (dotnet test / MTP print "failed <Name> (<time>)" or "[FAIL]").
function Get-FailedTests([string[]]$Log) {
  $names = foreach ($l in $Log) {
    $t = $l -replace '^.*?\d{4}-\d\d-\d\dT[\d:.]+Z\s?', ''
    if ($t -match '^\s*(?:failed|Failed)\s+(\S+)') { $Matches[1] }
    elseif ($t -match '^\s*\[FAIL\]\s+(\S+)') { $Matches[1] }
  }
  @($names | Where-Object { $_ } | Sort-Object -Unique)
}

function Test-Flaky([string]$Name) { [bool]($script:KnownFlaky | Where-Object { $Name -like "*$_*" }) }

# ---- bug hunt (pr-bug-hunt.ps1, .claude/skills/bug-hunt) ----

# What makes a change risky here, matched against its added and removed lines. The weight is what a mistake in
# that area costs: an orphaned process, lost config or a leaked secret outranks a UI slip. Each area counts once.
$script:BugHuntSignals = [ordered]@{
  process   = @{ weight = 3; pattern = 'Process\.Start|ProcessStartInfo|\.Kill\(|WaitForExit|JobObject|CreateProcess' }
  data      = @{ weight = 3; pattern = 'File\.(Write|Move|Delete|Replace|Copy|Create|Open)|Directory\.(Delete|Move|Create)|FileStream|StreamWriter|Registry\w*|ConfigDocument|ConfigMapper|Serializ|Deserializ|FileSystemWatcher' }
  secrets   = @{ weight = 3; pattern = 'Secret|Credential|[Pp]assword|PasswordVault|Cred(Read|Write)|ProtectedData|Authorization|Bearer' }
  async     = @{ weight = 2; pattern = 'CancellationToken|CancelAfter|Timeout|WaitAsync|\.Wait\(|\.Result\b|GetResult\(\)|async void|TaskCompletionSource|ContinueWith|Task\.(Run|WhenAny|WhenAll)' }
  threads   = @{ weight = 2; pattern = '\block\s*\(|SemaphoreSlim|Interlocked|Volatile|ConcurrentDictionary|Channel<|new Thread\(|ThreadPool|DispatcherQueue|SendMessage|PostMessage|WndProc' }
  idle      = @{ weight = 2; pattern = 'System\.Threading\.Timer|new Timer\(|PeriodicTimer|DispatcherTimer|Task\.Delay|Thread\.Sleep|while\s*\(\s*true\s*\)|SetTimer' }
  native    = @{ weight = 2; pattern = 'LibraryImport|DllImport|Marshal\.|SafeHandle|CloseHandle|ComWrappers|CoCreateInstance|IUnknown|\bunsafe\b|PInvoke\.|GetLastError|HRESULT' }
  aot       = @{ weight = 2; pattern = 'Activator\.|System\.Reflection|GetMethod\(|GetProperty\(|MakeGenericType|\bdynamic\b|Assembly\.Load|Type\.GetType\(|RequiresUnreferencedCode|UnconditionalSuppressMessage' }
  lifecycle = @{ weight = 2; pattern = 'Dispose|IDisposable|Register\w*\(|Unregister\w*|Subscribe|Unsubscribe|\+=\s*(On[A-Z]|\(|async\b|delegate)|Reload|OnClosed|Unloaded' }
  focus     = @{ weight = 2; pattern = 'SetForegroundWindow|\.Activate\(|ShowWindow|SW_SHOW\b|NOACTIVATE|SetFocus|AllowSetForegroundWindow' }
  ipc       = @{ weight = 2; pattern = 'NamedPipe|PipeStream|PipeServer|PipeClient' }
  text      = @{ weight = 1; pattern = 'Substring|\.IndexOf\(|\.Split\(|\[\^\d|\.\.\^?\d*\]|Span<|\.Length\s*-\s*1|TryParse|\.Parse\(|CultureInfo|StringComparison' }
}
# Files every run, reload or pipe call goes through; a slip there reaches every flow.
$script:BugHuntCoreFiles = 'FlowRunner|FlowLauncher|EngineController|EngineHost|PipeServer|Protocol|ConfigMapper|ConfigDocument|ConfigValidator|TriggerService|Scheduler|ComThread|StepValues'
$script:BugHuntLevels = [ordered]@{
  low      = @{ model = 'sonnet'; effort = 'medium' }
  standard = @{ model = 'opus'; effort = 'medium' }
  high     = @{ model = 'opus'; effort = 'high' }
  max      = @{ model = 'opus'; effort = 'xhigh' }
}
# Score at or above each bound picks that level; below standard is low. A diff over 800 changed lines with any
# risk at all goes up a level.
$script:BugHuntBounds = [ordered]@{ max = 12; high = 7; standard = 3 }
$script:BugHuntBudget = 2

# Paths the hunt reads (git pathspec): the same "non-test code" as Test-CodePath, minus binaries.
function Get-BugHuntPathspec([string[]]$Path) {
  @('--') + $(if ($Path) { $Path } else { @('.') }) + @(':(exclude)*.md', ':(exclude)docs/**', ':(exclude)tests/**',
    ':(exclude)*.tests.ps1', ':(exclude)*.png', ':(exclude)*.ico', ':(exclude)*.jpg', ':(exclude)*.gif')
}

# $Diff: unified diff lines; $Files: changed paths. Returns @{ Score; Reasons[]; Level }.
function Get-BugHuntRisk([string[]]$Diff, [string[]]$Files) {
  $changed = @($Diff | Where-Object { $_ -match '^[+-](?![+-]{2} )' })
  $score = 0; $reasons = [System.Collections.Generic.List[string]]::new()
  foreach ($name in $script:BugHuntSignals.Keys) {
    $hits = @($changed | Where-Object { $_ -cmatch $script:BugHuntSignals[$name].pattern }).Count
    if ($hits) { $score += $script:BugHuntSignals[$name].weight; $reasons.Add("$name($hits)") }
  }
  $core = @($Files | Where-Object { $_ -match "/($script:BugHuntCoreFiles)\.cs$" }).Count
  if ($core) { $score += 2; $reasons.Add("core-files($core)") }
  $level = 'low'
  foreach ($name in $script:BugHuntBounds.Keys) { if ($score -ge $script:BugHuntBounds[$name]) { $level = $name; break } }
  if ($changed.Count -gt 800 -and $score -gt 0 -and $level -ne 'max') {
    $keys = @($script:BugHuntLevels.Keys); $level = $keys[$keys.IndexOf($level) + 1]; $reasons.Add("big-diff($($changed.Count) lines)")
  }
  [pscustomobject]@{ Score = $score; Reasons = @($reasons); Level = $level }
}

# New-side line numbers inside each file's hunks, from `git diff -U3` text: where GitHub accepts an inline comment.
function Get-DiffLineMap([string[]]$Diff) {
  $map = @{}; $file = $null
  foreach ($l in $Diff) {
    if ($l -match '^\+\+\+ (?:b/)?(.+)$') { $file = if ($Matches[1] -eq '/dev/null') { $null } else { $Matches[1] }; if ($file -and -not $map[$file]) { $map[$file] = [System.Collections.Generic.HashSet[int]]::new() } }
    elseif ($file -and $l -match '^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@') {
      $start = [int]$Matches[1]; $count = if ($Matches[2]) { [int]$Matches[2] } else { 1 }
      for ($i = $start; $i -lt $start + $count; $i++) { [void]$map[$file].Add($i) }
    }
  }
  $map
}

function Format-BugHuntFinding($F) {
  $ev = if ($F.evidence) { "`n`nEvidence: " + (($F.evidence | ForEach-Object { "``$_``" }) -join ', ') } else { '' }
  "**Bug hunt · $($F.severity) · $($F.confidence)** — $($F.title)`n`n$($F.scenario)$ev`n`nFix: $($F.fix)"
}

# Review body; the first line's marker is how pr-lib finds bug-hunt reviews and counts the budget.
function Format-BugHuntBody($R, [string[]]$Unanchored) {
  $n = @($R.findings).Count
  $lines = @("<!-- bug-hunt head=$($R.head) findings=$n -->",
    "**Bug hunt** (run $($R.run) of $script:BugHuntBudget, $($R.level): $($R.model)/$($R.effort); $(($R.reasons) -join ', '))",
    '', $(if ($n) { "$n finding(s), one thread each. Reply and resolve each: fixed in <sha>, or declined and why." } else { 'No findings.' }),
    '', "Coverage: $($R.coverage)")
  if ($Unanchored) { $lines += @('', 'Findings outside the PR diff are anchored on the file they name, or on the first changed file:') + ($Unanchored | ForEach-Object { "- $_" }) }
  $lines -join "`n"
}

function Format-HuntState($D) {
  $req = if (Test-ReviewRequired $D) { 'required' } else { 'not required' }
  if (-not $D.BugHunts.Count) { return "none; $req ($($D.CodeAdds) code lines added)" }
  $h = $D.BugHunts[-1]; $n = if ($h.Body -match 'findings=(\d+)') { $Matches[1] } else { '?' }
  $at = if ($h.Body -match 'head=([0-9a-f]{7})') { $Matches[1] } else { '?' }
  "$($D.BugHunts.Count) of $script:BugHuntBudget, last on $at with $n finding(s)$(if ($h.Body -notmatch "head=$($D.Head)") { ' (older than head)' }); $req"
}
