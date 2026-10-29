# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-HookResult {
    param([Parameter(Mandatory)] [hashtable] $Value)

    [Console]::Out.WriteLine(($Value | ConvertTo-Json -Compress -Depth 8))
}

function Stop-Hook {
    param([Parameter(Mandatory)] [string] $Message)

    [Console]::Error.WriteLine($Message)
    exit 2
}

function Get-ToolCommand {
    param([Parameter(Mandatory)] $Payload)

    $toolArgsProperty = $Payload.PSObject.Properties['toolArgs']
    $toolInputProperty = $Payload.PSObject.Properties['tool_input']
    $toolArgs = if ($null -ne $toolArgsProperty) {
        $toolArgsProperty.Value
    }
    elseif ($null -ne $toolInputProperty) {
        $toolInputProperty.Value
    }
    else {
        $null
    }
    if ($toolArgs -is [string]) {
        try {
            $toolArgs = $toolArgs | ConvertFrom-Json
        }
        catch {
            Stop-Hook "The shell tool arguments are not valid JSON."
        }
    }
    if ($null -eq $toolArgs -or
        $null -eq $toolArgs.PSObject.Properties['command'] -or
        [string]::IsNullOrWhiteSpace([string] $toolArgs.command)) {
        Stop-Hook "The shell tool payload does not contain a command."
    }
    return [string] $toolArgs.command
}

function Find-MigrationRoot {
    param([Parameter(Mandatory)] [string] $StartDirectory)

    try {
        $current = [System.IO.DirectoryInfo]::new(
            [System.IO.Path]::GetFullPath($StartDirectory))
    }
    catch {
        Stop-Hook "The hook working directory is invalid: $($_.Exception.Message)"
    }

    while ($null -ne $current) {
        $reportPath = [System.IO.Path]::Combine(
            $current.FullName,
            'migration-report.json')
        if ([System.IO.File]::Exists($reportPath)) {
            try {
                $report = Get-Content -LiteralPath $reportPath -Raw |
                    ConvertFrom-Json
            }
            catch {
                Stop-Hook "migration-report.json is malformed: $($_.Exception.Message)"
            }
            if ($report.schemaVersion -ne '1.5' -or
                $null -eq $report.source -or
                [string]::IsNullOrWhiteSpace([string] $report.source.root) -or
                [string]::IsNullOrWhiteSpace([string] $report.source.projectFile) -or
                $null -eq $report.target) {
                Stop-Hook 'migration-report.json does not satisfy the UWP schema 1.5 workspace identity contract.'
            }
            return $current.FullName
        }
        $parent = $current.Parent
        if ($null -eq $parent -or $parent.FullName -eq $current.FullName) {
            break
        }
        $current = $parent
    }
    return $null
}

function Get-SessionStatePath {
    param([Parameter(Mandatory)] [string] $SessionId)

    $copilotPluginData = [Environment]::GetEnvironmentVariable(
        'COPILOT_PLUGIN_DATA')
    $pluginData = [Environment]::GetEnvironmentVariable('PLUGIN_DATA')
    $dataRoot = if (-not [string]::IsNullOrWhiteSpace($copilotPluginData)) {
        $copilotPluginData
    }
    elseif (-not [string]::IsNullOrWhiteSpace($pluginData)) {
        $pluginData
    }
    else {
        $null
    }
    if ($null -eq $dataRoot) {
        return $null
    }
    $safeSessionId = [regex]::Replace(
        $SessionId,
        '[^A-Za-z0-9_.-]',
        '_')
    return [System.IO.Path]::Combine(
        $dataRoot,
        'winapp-boundary',
        "$safeSessionId.json")
}

function Save-SessionMigrationRoot {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $MigrationRoot
    )

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        @{
            schemaVersion = '1.0'
            migrationRoot = $MigrationRoot
        } | ConvertTo-Json -Compress |
            Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Read-SessionMigrationRoot {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    try {
        $state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        Stop-Hook "The persisted WinApp boundary state is malformed: $($_.Exception.Message)"
    }
    if ($state.schemaVersion -ne '1.0' -or
        [string]::IsNullOrWhiteSpace([string] $state.migrationRoot)) {
        Stop-Hook 'The persisted WinApp boundary state is invalid.'
    }
    return [string] $state.migrationRoot
}

function Get-CommandVerb {
    param([Parameter(Mandatory)] $CommandAst)

    $elements = @($CommandAst.CommandElements)
    for ($index = 1; $index -lt $elements.Count; $index++) {
        $value = $elements[$index].Extent.Text.Trim("'`"")
        if ($value -eq '--') {
            return $null
        }
        if (-not $value.StartsWith('-', [StringComparison]::Ordinal)) {
            return $value
        }
    }
    return $null
}

function Test-CommandHelp {
    param([Parameter(Mandatory)] $CommandAst)

    $elements = @($CommandAst.CommandElements)
    foreach ($element in $elements) {
        $value = $element.Extent.Text.Trim("'`"")
        if ($value -eq '--') {
            return $false
        }
        if ($value -eq '--help') {
            return $true
        }
    }
    return $false
}

function Test-WinAppExecutable {
    param([Parameter(Mandatory)] $CommandAst)

    $name = $CommandAst.GetCommandName()
    if (-not [string]::IsNullOrWhiteSpace($name)) {
        try {
            $leaf = [System.IO.Path]::GetFileName($name)
        }
        catch {
            $leaf = $name
        }
        if ($leaf -match '^(?i:winapp(?:\.exe)?)$') {
            return $true
        }
    }

    $elements = @($CommandAst.CommandElements)
    if ($elements.Count -eq 0) {
        return $false
    }
    $invocation = $elements[0].Extent.Text.Trim()
    if ($invocation -match '^(?i:\$env:WINAPP_MIGRATE_WINAPP_PATH)$') {
        return $true
    }
    $literal = $invocation.Trim("'`"")
    try {
        return [System.IO.Path]::GetFileName($literal) -match
            '^(?i:winapp(?:\.exe)?)$'
    }
    catch {
        return $false
    }
}

function Find-RawWinAppOperation {
    param([Parameter(Mandatory)] [string] $Command)

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput(
        $Command,
        [ref] $tokens,
        [ref] $parseErrors)
    $commands = $ast.FindAll(
        {
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst]
        },
        $true)
    foreach ($commandAst in $commands) {
        if (Test-WinAppExecutable $commandAst) {
            $verb = Get-CommandVerb $commandAst
            if ($verb -match '^(?i:run|build|ui)$' -and
                -not (Test-CommandHelp $commandAst)) {
                return $verb.ToLowerInvariant()
            }
        }

        $name = $commandAst.GetCommandName()
        $leaf = if ([string]::IsNullOrWhiteSpace($name)) {
            ''
        }
        else {
            [System.IO.Path]::GetFileNameWithoutExtension($name)
        }
        if ($leaf -match '^(?i:cmd|powershell|pwsh)$' -and
            $commandAst.Extent.Text -match
                '(?i)(?:winapp(?:\.exe)?|\$env:WINAPP_MIGRATE_WINAPP_PATH)[\s''"]+(run|build|ui)\b') {
            return $Matches[1].ToLowerInvariant()
        }
    }
    return $null
}

$rawInput = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($rawInput)) {
    Stop-Hook 'The preToolUse hook received an empty payload.'
}
try {
    $payload = $rawInput | ConvertFrom-Json
}
catch {
    Stop-Hook "The preToolUse hook payload is not valid JSON: $($_.Exception.Message)"
}

$toolName = if ($null -ne $payload.PSObject.Properties['toolName']) {
    [string] $payload.toolName
}
elseif ($null -ne $payload.PSObject.Properties['tool_name']) {
    [string] $payload.tool_name
}
else {
    ''
}
if ($toolName -notmatch '^(?i:powershell|bash)$') {
    Write-HookResult @{}
    exit 0
}

$sessionId = if ($null -ne $payload.PSObject.Properties['sessionId']) {
    [string] $payload.sessionId
}
elseif ($null -ne $payload.PSObject.Properties['session_id']) {
    [string] $payload.session_id
}
else {
    ''
}
$cwd = if ($null -ne $payload.PSObject.Properties['cwd']) {
    [string] $payload.cwd
}
else {
    ''
}
if ([string]::IsNullOrWhiteSpace($sessionId) -or
    [string]::IsNullOrWhiteSpace($cwd)) {
    Stop-Hook 'The preToolUse hook payload is missing sessionId or cwd.'
}

$statePath = Get-SessionStatePath $sessionId
$migrationRoot = Find-MigrationRoot $cwd
if ($null -ne $migrationRoot -and $null -ne $statePath) {
    Save-SessionMigrationRoot $statePath $migrationRoot
}
elseif ($null -eq $migrationRoot -and $null -ne $statePath) {
    $migrationRoot = Read-SessionMigrationRoot $statePath
}
if ($null -eq $migrationRoot) {
    Write-HookResult @{}
    exit 0
}

$command = Get-ToolCommand $payload
$operation = Find-RawWinAppOperation $command
if ($null -eq $operation) {
    Write-HookResult @{}
    exit 0
}

Write-HookResult @{
    permissionDecision = 'deny'
    permissionDecisionReason = "Raw 'winapp $operation' is not permitted in an active UWP migration workspace. Use 'winapp-migrate uwp source ...' for source-oracle operations or 'winapp-migrate uwp closure ...' for target operations so evidence and process identity remain provenance-bound."
}
