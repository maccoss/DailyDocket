<#
.SYNOPSIS
  Phase 1 spike: what Claude Code in -p mode can do for DailyDocket on this machine.

.DESCRIPTION
  Checks, in order:
    tools    the claude.ai connectors (Gmail, Calendar, Slack) load in -p mode, and their exact tool names
    capture  a PostToolUse hook from a per-run --settings file receives each connector result verbatim,
             and updatedToolOutput replaces what Claude sees with a short receipt
    guard    a PreToolUse hook denies a connector write tool
    arch     whether claude.exe and this process are native to the machine's CPU

  Everything runs read-only, from an empty folder under %LOCALAPPDATA%\DailyDocket\spike (outside
  the repo, so no project CLAUDE.md or hooks are picked up); the summary is copied to spike\out. The guard check asks for a Gmail draft (never a send), with the draft tool
  left off the allowlist: if the guard did nothing, -p mode's own permission check still refuses it.

  spike\out is git-ignored: captures hold real mail.

.EXAMPLE
  pwsh spike\run-spike.ps1
  pwsh spike\run-spike.ps1 -Step capture
#>
param(
    [ValidateSet('all', 'tools', 'capture', 'guard', 'arch', 'shapes')]
    [string]$Step = 'all',
    [string]$Model = 'haiku'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runName = "$stamp-$($env:COMPUTERNAME.ToLowerInvariant())"
# Runs (and their captures, which hold real mail) live outside the repo: Claude Code walks up
# from its working folder looking for CLAUDE.md, and a run under spike\out read this repo's.
$out = Join-Path $env:LOCALAPPDATA "DailyDocket\spike\$runName"
$report = Join-Path $PSScriptRoot "out\$runName"
New-Item -ItemType Directory -Force $out, $report | Out-Null
$claude = (Get-Command claude -ErrorAction Stop).Source
$summary = [System.Collections.Generic.List[string]]::new()

function Note([string]$line) { $summary.Add($line); Write-Host $line }

function Find-Hook {
    $candidates = @(
        "$repo\src\DailyDocket.Hook\bin\Release\net10.0\win-$($env:PROCESSOR_ARCHITECTURE.ToLowerInvariant() -replace 'amd64','x64')\publish\DailyDocket.Hook.exe",
        "$repo\src\DailyDocket.Hook\bin\Debug\net10.0\DailyDocket.Hook.exe"
    )
    $hook = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $hook) { throw "Build the hook first: dotnet build src\DailyDocket.Hook" }
    return ($hook -replace '\\', '/')
}

# One headless run. Returns the parsed stream-json events and saves the raw stream.
function Invoke-Claude([string]$name, [string]$prompt, [string[]]$extra) {
    $dir = Join-Path $out $name
    New-Item -ItemType Directory -Force $dir | Out-Null
    $log = Join-Path $dir 'stream.jsonl'
    # Launch the way the app will: nothing inherited from a Claude session this script may run
    # inside (the desktop app sets CLAUDE_CODE_* host-auth and session variables).
    Get-ChildItem env: | Where-Object Name -match '^(CLAUDE|ANTHROPIC)' | ForEach-Object { Remove-Item "env:$($_.Name)" }
    Push-Location $dir
    try {
        & $claude -p $prompt --output-format stream-json --verbose --model $Model --max-turns 6 @extra 2>&1 |
            Out-File -FilePath $log -Encoding utf8
    }
    finally { Pop-Location }
    $events = Get-Content $log | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json }
    $result = $events | Where-Object type -eq 'result' | Select-Object -Last 1
    if ($result.is_error) { Note "  ERROR: $($result.result)" }
    return $events
}

function Write-Settings([string]$path, [hashtable]$hooks) {
    @{ hooks = $hooks } | ConvertTo-Json -Depth 10 | Out-File -FilePath $path -Encoding utf8NoBOM
}

function Get-ToolName([string[]]$tools, [string]$pattern) {
    $tools | Where-Object { $_ -like $pattern } | Select-Object -First 1
}

$toolsFile = Join-Path $PSScriptRoot 'out\connector-tools.txt'

if ($Step -in 'all', 'tools') {
    Note "== tools: claude.ai connectors in -p mode"
    # The claude.ai connectors connect after the init event is sent, so init lists none of them
    # (seen on 2.1.221). They are deferred tools; ToolSearch is how a run finds them.
    $prompt = 'Call ToolSearch three times, with max_results 60 each: "+Gmail", "+Calendar", "+Slack". ' +
        'Then list the exact name of every tool you found whose name starts with mcp__claude_ai_, one per line, and nothing else.'
    $events = Invoke-Claude 'tools' $prompt @('--allowedTools', 'ToolSearch')
    $init = $events | Where-Object { $_.type -eq 'system' -and $_.subtype -eq 'init' } | Select-Object -First 1
    Note "  claude_code_version: $($init.claude_code_version)"
    Note "  mcp servers listed in init: $(@($init.mcp_servers).Count)"
    $answer = ($events | Where-Object type -eq 'result' | Select-Object -Last 1).result
    $connectorTools = $answer -split '\r?\n' | ForEach-Object { $_.Trim(' ', '`', '-', '*') } |
        Where-Object { $_ -match '^mcp__claude_ai_\w+__\w+$' } | Sort-Object -Unique
    Note "  connector tools: $($connectorTools.Count)"
    $connectorTools | Out-File -FilePath $toolsFile -Encoding utf8
    $connectorTools | ForEach-Object { Note "    $_" }
}

$connectorTools = if (Test-Path $toolsFile) { Get-Content $toolsFile } else { @() }

if ($Step -in 'all', 'capture') {
    Note "== capture: PostToolUse hook from --settings, with and without a receipt"
    $hook = Find-Hook
    $search = Get-ToolName $connectorTools '*Gmail*search_threads'
    $events = Get-ToolName $connectorTools '*Calendar*list_events'
    $slack = Get-ToolName $connectorTools '*Slack*search_public_and_private'
    if (-not $search) { Note "  SKIPPED: no Gmail search_threads tool (run -Step tools first, signed in)" }
    else {
        # Run 1: Gmail with a receipt. Claude should see the receipt, not the mail.
        $cap = (Join-Path $out 'capture-gmail\captures') -replace '\\', '/'
        $settings = Join-Path $out 'capture-gmail.settings.json'
        Write-Settings $settings @{ PostToolUse = @(@{ matcher = 'mcp__claude_ai_.*'; hooks = @(@{ type = 'command'; command = "`"$hook`" capture --dir `"$cap`" --receipt"; timeout = 30 }) }) }
        $ev = Invoke-Claude 'capture-gmail' "Call $search exactly once with the query 'newer_than:2d' and at most 5 results. Then quote, verbatim, the complete text the tool returned to you, and say nothing else." @('--settings', $settings, '--allowedTools', "ToolSearch,$search")
        $files = @(Get-ChildItem $cap -Filter *.json -ErrorAction SilentlyContinue)
        Note "  gmail captures written: $($files.Count)"
        if ($files) {
            $first = Get-Content $files[0].FullName -Raw | ConvertFrom-Json
            Note "  captured tool_name: $($first.tool_name); tool_response type: $($first.tool_response.GetType().Name); size: $($files[0].Length) bytes"
        }
        # Judge by the tool_result Claude received, not its answer: the model paraphrases.
        $answer = ($ev | Where-Object type -eq 'result' | Select-Object -Last 1).result
        $received = $ev | Where-Object type -eq 'user' | ForEach-Object { $_.message.content } |
            Where-Object { $_.type -eq 'tool_result' -and "$($_.content)" -match 'DailyDocket saved this' }
        Note "  Claude received the receipt instead of the mail: $([bool]$received)"
        $answer | Out-File (Join-Path $out 'capture-gmail\answer.txt') -Encoding utf8

        # Run 2: Calendar and Slack, capture only, to record their raw shapes for the parsers.
        $cap2 = (Join-Path $out 'capture-cal-slack\captures') -replace '\\', '/'
        $settings2 = Join-Path $out 'capture-cal-slack.settings.json'
        Write-Settings $settings2 @{ PostToolUse = @(@{ matcher = 'mcp__claude_ai_.*'; hooks = @(@{ type = 'command'; command = "`"$hook`" capture --dir `"$cap2`""; timeout = 30 }) }) }
        $allowed = @($events, $slack) | Where-Object { $_ }
        $null = Invoke-Claude 'capture-cal-slack' "Call $events once for my primary calendar for the next 2 days, then call $slack once for messages that mention me from the last 2 days. Reply with only the number of events and messages found." @('--settings', $settings2, '--allowedTools', ((@('ToolSearch') + $allowed) -join ','))
        $files2 = @(Get-ChildItem $cap2 -Filter *.json -ErrorAction SilentlyContinue)
        Note "  calendar and slack captures written: $($files2.Count) ($(($files2 | ForEach-Object { (Get-Content $_.FullName -Raw | ConvertFrom-Json).tool_name }) -join ', '))"
    }
}

if ($Step -in 'all', 'guard') {
    Note "== guard: PreToolUse deny on a connector write tool"
    $hook = Find-Hook
    $draft = Get-ToolName $connectorTools '*Gmail*create_draft'
    if (-not $draft) { Note "  SKIPPED: no Gmail create_draft tool" }
    else {
        $claims = (Join-Path $out 'guard\no-claims.json') -replace '\\', '/'
        $settings = Join-Path $out 'guard.settings.json'
        Write-Settings $settings @{ PreToolUse = @(@{ matcher = [regex]::Escape($draft); hooks = @(@{ type = 'command'; command = "`"$hook`" guard --claims `"$claims`""; timeout = 30 }) }) }
        # Every Gmail tool that sends stays disallowed outright, belt and braces.
        $sends = $connectorTools | Where-Object { $_ -match 'Gmail__(send_message|reply|forward)$' }
        $ev = Invoke-Claude 'guard' "Load $draft with ToolSearch (query 'select:$draft'), then call it exactly once to create a draft to nobody@example.com with the subject 'DailyDocket spike' and the body 'test'. Then report the exact error or result text you got." @('--settings', $settings, '--allowedTools', 'ToolSearch', '--disallowedTools', ($sends -join ','))
        $toolResults = $ev | Where-Object type -eq 'user' | ForEach-Object { $_.message.content } | Where-Object type -eq 'tool_result'
        $byGuard = ($toolResults | ConvertTo-Json -Depth 10) -match 'DailyDocket send guard'
        Note "  denied by the guard hook (not just the allowlist): $byGuard"
    }
}

if ($Step -eq 'shapes') {
    # Not part of 'all': captures every read call the pull jobs make, with the artifact's exact
    # arguments, so the C# parsers are written against real shapes. Only the shapes (keys and
    # types) are ever copied into the repo; the captures stay here.
    Note "== shapes: one capture per pull-job read call"
    $hook = Find-Hook
    $cap = (Join-Path $out 'shapes\captures') -replace '\\', '/'
    $settings = Join-Path $out 'shapes.settings.json'
    Write-Settings $settings @{ PostToolUse = @(@{ matcher = 'mcp__claude_ai_.*'; hooks = @(@{ type = 'command'; command = "`"$hook`" capture --dir `"$cap`" --receipt"; timeout = 30 }) }) }
    $g = 'mcp__claude_ai_Gmail__'; $s = 'mcp__claude_ai_Slack__'
    $after = (Get-Date).AddDays(-3).ToString('yyyy-MM-dd')
    $calls = @(
        "$($g)search_threads with {`"query`":`"in:inbox newer_than:3d`",`"pageSize`":5,`"view`":`"THREAD_VIEW_MINIMAL`"}",
        "$($g)search_threads with {`"query`":`"in:sent newer_than:3d`",`"pageSize`":5,`"view`":`"THREAD_VIEW_METADATA_ONLY`"}",
        "$($g)list_drafts with {`"pageSize`":5,`"view`":`"DRAFT_VIEW_METADATA_ONLY`"}",
        "$($g)get_thread with {`"threadId`":<the first thread id from the first search>,`"messageFormat`":`"METADATA_ONLY`"}",
        "$($g)get_thread with {`"threadId`":<the same id>,`"messageFormat`":`"PLAIN_TEXT`"}",
        "$($s)slack_search_public_and_private with {`"filters`":`"after:$after`",`"channel_types`":`"im,mpim`",`"natural_language_query`":`"`",`"sort`":`"timestamp`",`"sort_dir`":`"desc`",`"limit`":5,`"include_context`":false,`"response_format`":`"detailed`"}",
        "$($s)slack_read_thread with {`"channel_id`":<a channel id from that search>,`"message_ts`":<that message's Message_ts>,`"limit`":10,`"response_format`":`"detailed`"}",
        "$($s)slack_get_reactions with {`"channel_id`":<the same channel id>,`"message_ts`":<the same Message_ts>}"
    )
    $tools = (@('ToolSearch') + @($calls | ForEach-Object { ($_ -split ' ')[0] }) | Sort-Object -Unique) -join ','
    $prompt = "Make these tool calls in order, each exactly once, with exactly these arguments (fill each <...> from earlier results; the results you see are receipts that list ids). Load tools with ToolSearch first. Then reply DONE.`n" + ($calls -join "`n")
    $null = Invoke-Claude 'shapes' $prompt @('--settings', $settings, '--allowedTools', $tools, '--max-turns', '20')
    $files = @(Get-ChildItem $cap -Filter *.json -ErrorAction SilentlyContinue)
    Note "  captures written: $($files.Count) ($(($files | ForEach-Object { ((Get-Content $_.FullName -Raw | ConvertFrom-Json).tool_name -split '__')[-1] }) -join ', '))"
}

if ($Step -in 'all', 'arch') {
    Note "== arch"
    Note "  machine: $env:PROCESSOR_ARCHITECTURE; process: $([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)"
    $bytes = [System.IO.File]::ReadAllBytes($claude)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
    $kind = switch ($machine) { 0x8664 { 'x64' } 0xAA64 { 'ARM64' } 0x014C { 'x86' } default { '0x{0:X4}' -f $machine } }
    Note "  claude.exe: $kind ($claude)"
}

$summary | Out-File (Join-Path $out 'summary.txt') -Encoding utf8
$summary | Out-File (Join-Path $report 'summary.txt') -Encoding utf8
Write-Host "`nRuns and captures: $out`nSummary: $report\summary.txt"
