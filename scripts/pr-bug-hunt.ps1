<#
Runs the bug hunt (.claude/skills/bug-hunt) on a PR's head in a clean headless `claude -p` session and posts its
findings to the PR as one review, one thread per finding, so pr-threads / pr-reply / pr-status work as they did
for Copilot (D-155). The session gets only that skill as its system prompt, a packet (intent, changed-file table,
code diff with the enclosing method per hunk) on stdin, and Read/Grep/Glob over this checkout: no CLAUDE.md,
no settings, no MCP, no shell, so it reads cold and cannot change anything. It starts in a scratch folder
outside the repo so CLAUDE.md discovery finds nothing, and reaches the checkout through --add-dir.

Auth: the token saved by Set-BugHuntToken (~/.claude/bug-hunt-token.txt, DPAPI, machine-wide for all repos)
when present, else the CLI's own login. Bills that Claude account.

Usage: pwsh scripts/pr-bug-hunt.ps1 <pr> [-Level auto|low|standard|high|max] [-Model opus|sonnet] [-Effort ...]
         [-Path <pathspec>...] [-PacketOnly] [-NoPost] [-Force]
  Run it in the background (minutes). The PR head must be checked out here with a clean tree.
  -PacketOnly writes the packet and prints the level, runs nothing. -NoPost runs but does not post.
  -Force: past the budget (2 per PR), onto a head already hunted, or before CI is green; only when the user asks.
Exit: 0 reviewed (findings or not), no-code, packet; 1 refused or failed.
#>
param(
  [Parameter(Mandatory, Position = 0)][int]$Pr,
  [ValidateSet('auto', 'low', 'standard', 'high', 'max')][string]$Level = 'auto',
  [ValidateSet('opus', 'sonnet')][string]$Model,
  [ValidateSet('low', 'medium', 'high', 'xhigh', 'max')][string]$Effort,
  [string[]]$Path,
  [string]$Base = 'origin/main',
  [ValidateRange(0, 60)][int]$Context = 15,
  [ValidateRange(16, 1024)][int]$MaxPacketKB = 160,
  [switch]$PacketOnly, [switch]$NoPost, [switch]$Force
)
. "$PSScriptRoot/pr-lib.ps1"
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$Utf8 = [System.Text.UTF8Encoding]::new($false)
function Stop-Hunt([string]$Msg) { "#$Pr $Msg"; exit 1 }

$repo = Get-RepoSlug
$d = Get-PrData $Pr $repo
if ($d.State -ne 'OPEN') { Stop-Hunt "is $($d.State)" }
$root = (git rev-parse --show-toplevel).Trim() -replace '/', '\'
$head = (git rev-parse HEAD).Trim()
if ($d.Head -ne $head) { Stop-Hunt "head is $($d.Head.Substring(0,7)) but HEAD here is $($head.Substring(0,7)): check out the PR head (the reviewer reads files from disk)" }
if (git status --porcelain --untracked-files=no) { Stop-Hunt 'working tree has uncommitted changes: the reviewer would read them; commit or set them aside' }

$runs = $d.BugHunts.Count
if (-not $PacketOnly -and -not $Force) {
  if ($runs -and $d.BugHunts[-1].Body -match "head=$head") { "#$Pr bug hunt already ran on $($head.Substring(0,7)) ($runs of $script:BugHuntBudget)"; exit 0 }
  if ($runs -ge $script:BugHuntBudget) { Stop-Hunt "has $runs bug hunts: budget is $script:BugHuntBudget (-Force only when the user asks)" }
  if ($d.Ci -ne 'pass') { Stop-Hunt "CI is $($d.Ci) on this head: hunt once it is green (pr-wait.ps1), or -Force" }
}

git fetch -q origin ($Base -replace '^origin/', '') 2>$null
$mergeBase = (git merge-base $Base $head).Trim()
if (-not $mergeBase) { Stop-Hunt "no merge base with $Base" }
$spec = Get-BugHuntPathspec $Path
$numstat = @(git diff --numstat -M $mergeBase $head @spec | Where-Object { $_ })
if ($numstat.Count -eq 0) { "#$Pr no-code: the diff touches no reviewable code, nothing run"; exit 0 }

$runDir = Join-Path ([System.IO.Path]::GetTempPath()) "bug-hunt\$($repo -replace '/', '_')\pr$Pr\$($head.Substring(0, 12))"
New-Item -ItemType Directory -Force $runDir | Out-Null

$files = [System.Collections.Generic.List[string]]::new()
$rows = foreach ($line in $numstat) {
  $parts = $line -split "`t"; $file = $parts[-1]
  if ($file -match '=> ') { $file = ($file -replace '\{[^}]*=> ([^}]*)\}', '$1') -replace '^.* => ', '' }
  $files.Add($file)
  $full = Join-Path $root ($file -replace '/', '\')
  $n = if (Test-Path $full -PathType Leaf) { [System.IO.File]::ReadAllLines($full).Count } else { 0 }
  $read = if ($n -eq 0) { 'deleted' } elseif ($n -gt 1500) { 'grep only' } else { 'readable' }
  "| ``$file`` | +$($parts[0]) -$($parts[1]) | $n | $read |"
}
# .gitattributes gives *.cs the csharp hunk-header driver, so each hunk names its enclosing method.
$diff = @(git diff "-U$Context" -M --no-color $mergeBase $head @spec)
$risk = Get-BugHuntRisk $diff $files
$reasons = [System.Collections.Generic.List[string]]::new(); $risk.Reasons | ForEach-Object { $reasons.Add($_) }
if ($Level -eq 'auto') { $Level = $risk.Level } else { $reasons.Add("level set by hand, auto was $($risk.Level)") }
if (-not $Model) { $Model = $script:BugHuntLevels[$Level].model }
if (-not $Effort) { $Effort = $script:BugHuntLevels[$Level].effort }

$packet = @"
# Review packet

Intent: $($d.Title)

Repository root: ``$root`` (the working tree is at the reviewed head $head; read and grep files there)
Base: merge-base with $Base, $mergeBase

## Changed files

| File | Lines | At head | Read |
|---|---|---|---|
$($rows -join "`n")

## Diff

``````diff
$($diff -join "`n")
``````
"@
$packetFile = Join-Path $runDir 'packet.md'
[System.IO.File]::WriteAllText($packetFile, $packet, $Utf8)
$kb = [math]::Round($Utf8.GetByteCount($packet) / 1KB, 1)
"#$Pr level $Level ($Model/$Effort), score $($risk.Score): $(if ($reasons.Count) { $reasons -join ', ' } else { 'no risk signals' })"
"packet: $kb KB, $($numstat.Count) file(s), $packetFile"
if ($PacketOnly) { exit 0 }
if ($kb -gt $MaxPacketKB) { Stop-Hunt "packet is $kb KB, over $MaxPacketKB KB: split with -Path, or raise -MaxPacketKB if the user agreed to the cost" }
$skill = Join-Path $PSScriptRoot '..\.claude\skills\bug-hunt\SKILL.md'
if (-not (Test-Path $skill)) { Stop-Hunt "skill not found at $skill" }

$schema = @'
{"type":"object","additionalProperties":false,"required":["findings","coverage"],
 "properties":{
  "findings":{"type":"array","maxItems":10,"items":{"type":"object","additionalProperties":false,
   "required":["severity","confidence","file","line","title","scenario","evidence","fix"],
   "properties":{
    "severity":{"enum":["critical","important"]},
    "confidence":{"enum":["confirmed","plausible"]},
    "file":{"type":"string"},"line":{"type":"integer"},
    "title":{"type":"string"},"scenario":{"type":"string"},
    "evidence":{"type":"array","items":{"type":"string"}},
    "fix":{"type":"string"}}}},
  "coverage":{"type":"string"}}}
'@ -replace '\s+', ' '

# The token saved by Set-BugHuntToken (DPAPI) overrides the shared CLI login for these claude calls only, so the
# hunt does not fail when another claude process rotates the login's refresh token.
$tokenFile = Join-Path $HOME '.claude\bug-hunt-token.txt'
$started = Get-Date
try {
  if (Test-Path $tokenFile) {
    $env:CLAUDE_CODE_OAUTH_TOKEN = [System.Net.NetworkCredential]::new('', ((Get-Content $tokenFile -Raw).Trim() | ConvertTo-SecureString)).Password
  }
  # auth status exits non-zero when signed out, so read its JSON, not its code.
  $auth = try { & claude auth status 2>$null | Out-String | ConvertFrom-Json } catch { $null }
  if (-not $auth -or -not $auth.loggedIn) { Stop-Hunt 'the claude CLI is not signed in (or the saved bug-hunt token is invalid): the user runs Set-BugHuntToken once' }
  Push-Location $runDir
  try {
    $raw = $packet | & claude -p --model $Model --effort $Effort --system-prompt-file $skill `
      --tools 'Read,Grep,Glob' --allowedTools 'Read,Grep,Glob' --add-dir $root `
      --strict-mcp-config --setting-sources '' --no-session-persistence `
      --json-schema $schema --output-format json
    $exitCode = $LASTEXITCODE
  } finally { Pop-Location }
} finally { Remove-Item Env:CLAUDE_CODE_OAUTH_TOKEN -ErrorAction SilentlyContinue }
$raw = $raw | Out-String
[System.IO.File]::WriteAllText((Join-Path $runDir 'raw.json'), $raw, $Utf8)
$run = try { $raw | ConvertFrom-Json } catch { $null }
if ($exitCode -ne 0 -or -not $run -or $run.is_error) { Stop-Hunt "review session failed: $(Format-Text "$($run.result)" 200) (raw: $runDir\raw.json)" }
$review = if ($run.PSObject.Properties['structured_output'] -and $run.structured_output) { $run.structured_output } else { $run.result | ConvertFrom-Json }

$result = [pscustomobject]@{
  head = $head; run = $runs + 1; level = $Level; model = $Model; effort = $Effort; reasons = @($reasons)
  findings = @($review.findings); coverage = $review.coverage; turns = $run.num_turns
  minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1); costUsd = $run.total_cost_usd
}
[System.IO.File]::WriteAllText((Join-Path $runDir 'result.json'), ($result | ConvertTo-Json -Depth 8), $Utf8)
"reviewed in $($result.minutes) min, $($result.turns) turns, `$$([math]::Round([double]$result.costUsd, 2)) at API rates: $($result.findings.Count) finding(s)"
foreach ($f in $result.findings) { "  $($f.severity) $($f.confidence) $($f.file):$($f.line) $(Format-Text $f.title 160)" }
if ($NoPost) { "not posted (-NoPost); result: $runDir\result.json"; exit 0 }

# One review: findings on a line GitHub shows in the PR diff go inline; the rest become file-level threads on
# their own file, or on the first changed file when the diff never touched theirs.
$lineMap = Get-DiffLineMap @(git diff -U3 -M --no-color $mergeBase $head)
$prFiles = @(git diff --name-only -M $mergeBase $head)
$inline = [System.Collections.Generic.List[object]]::new(); $fileLevel = [System.Collections.Generic.List[object]]::new(); $moved = @()
foreach ($f in $result.findings) {
  $p = ($f.file -replace '\\', '/') -replace '^\./', ''
  if ($root) { $p = $p -replace ('^' + [regex]::Escape(($root -replace '\\', '/')) + '/'), '' }
  $body = Format-BugHuntFinding $f
  if ($lineMap[$p] -and $lineMap[$p].Contains([int]$f.line)) { $inline.Add(@{ path = $p; line = [int]$f.line; side = 'RIGHT'; body = $body }) }
  elseif ($p -in $prFiles) { $fileLevel.Add(@{ path = $p; body = "``$($p):$($f.line)``  $body" }) }
  else { $moved += "``$($p):$($f.line)``"; $fileLevel.Add(@{ path = $prFiles[0]; body = "``$($p):$($f.line)`` (outside this PR's files)  $body" }) }
}
$payload = @{ commit_id = $head; event = 'COMMENT'; body = (Format-BugHuntBody $result $moved); comments = @($inline) }
$tmp = Join-Path $runDir 'review.json'
[System.IO.File]::WriteAllText($tmp, ($payload | ConvertTo-Json -Depth 6), $Utf8)
$posted = gh api -X POST "repos/$repo/pulls/$Pr/reviews" --input $tmp 2>&1
if ($LASTEXITCODE -ne 0) { Stop-Hunt "posting the review failed: $(Format-Text "$posted" 200) (findings: $runDir\result.json)" }
foreach ($c in $fileLevel) {
  $out = gh api -X POST "repos/$repo/pulls/$Pr/comments" -f "body=$($c.body)" -f "commit_id=$head" -f "path=$($c.path)" -f 'subject_type=file' 2>&1
  if ($LASTEXITCODE -ne 0) { "warn: a file-level finding on $($c.path) was not posted: $(Format-Text "$out" 160)" }
}
"posted: review with $($inline.Count) inline + $($fileLevel.Count) file-level thread(s) (run $($result.run) of $script:BugHuntBudget)"
if ($result.findings.Count) { 'next: verify each cited path:line yourself, fix or decline, then pr-reply.ps1 per thread' }
exit 0
