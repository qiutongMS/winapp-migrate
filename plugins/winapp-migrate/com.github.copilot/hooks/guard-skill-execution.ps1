# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ContractPath
)

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

function Get-ToolArguments {
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
            return $toolArgs
        }
    }
    return $toolArgs
}

function Get-JsonPathValue {
    param(
        [Parameter(Mandatory)] $Value,
        [Parameter(Mandatory)] [string] $Path
    )

    $current = $Value
    foreach ($segment in $Path.Split('.')) {
        if ($null -eq $current) {
            return $null
        }
        $property = $current.PSObject.Properties[$segment]
        if ($null -eq $property) {
            return $null
        }
        $current = $property.Value
    }
    return $current
}

function Test-WorkspaceIdentity {
    param(
        [Parameter(Mandatory)] $Document,
        [Parameter(Mandatory)] $WorkspaceContract
    )

    $requirementsProperty =
        $WorkspaceContract.PSObject.Properties['jsonRequirements']
    $requirements = if ($null -eq $requirementsProperty) {
        @()
    }
    else {
        @($requirementsProperty.Value)
    }
    foreach ($requirement in $requirements) {
        $value = Get-JsonPathValue $Document ([string] $requirement.path)
        $equalsProperty = $requirement.PSObject.Properties['equals']
        $requiredProperty = $requirement.PSObject.Properties['required']
        if ($null -ne $equalsProperty -and
            [string] $value -cne [string] $requirement.equals) {
            return $false
        }
        if ($null -ne $requiredProperty -and
            $requiredProperty.Value -eq $true -and
            ($null -eq $value -or
             $value -is [string] -and
             [string]::IsNullOrWhiteSpace([string] $value))) {
            return $false
        }
    }
    return $true
}

function Find-ContractWorkspaceRoot {
    param(
        [Parameter(Mandatory)] [string] $StartDirectory,
        [Parameter(Mandatory)] $WorkspaceContract
    )

    try {
        $current = [System.IO.DirectoryInfo]::new(
            [System.IO.Path]::GetFullPath($StartDirectory))
    }
    catch {
        Stop-Hook "The hook working directory is invalid: $($_.Exception.Message)"
    }

    while ($null -ne $current) {
        $markerPath = [System.IO.Path]::Combine(
            $current.FullName,
            [string] $WorkspaceContract.marker)
        if ([System.IO.File]::Exists($markerPath)) {
            try {
                $document = Get-Content -LiteralPath $markerPath -Raw |
                    ConvertFrom-Json
            }
            catch {
                Stop-Hook "The Skill execution workspace marker is malformed: $($_.Exception.Message)"
            }
            if (-not (Test-WorkspaceIdentity $document $WorkspaceContract)) {
                Stop-Hook 'The workspace marker does not satisfy the active Skill execution contract.'
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
    param(
        [Parameter(Mandatory)] [string] $SessionId,
        [Parameter(Mandatory)] [string] $ContractId
    )

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
    $safeContractId = [regex]::Replace(
        $ContractId,
        '[^A-Za-z0-9_.-]',
        '_')
    return [System.IO.Path]::Combine(
        $dataRoot,
        'skill-execution-contract',
        "$safeContractId-$safeSessionId.json")
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

function Test-RuleExecutable {
    param(
        [Parameter(Mandatory)] $CommandAst,
        [Parameter(Mandatory)] $Rule
    )

    $name = $CommandAst.GetCommandName()
    if (-not [string]::IsNullOrWhiteSpace($name)) {
        try {
            $leaf = [System.IO.Path]::GetFileName($name)
        }
        catch {
            $leaf = $name
        }
        if (@($Rule.executables) -contains $leaf) {
            return $true
        }
    }

    $elements = @($CommandAst.CommandElements)
    if ($elements.Count -eq 0) {
        return $false
    }
    $invocation = $elements[0].Extent.Text.Trim()
    $environmentProperty =
        $Rule.PSObject.Properties['environmentInvocations']
    if ($null -ne $environmentProperty -and
        @($environmentProperty.Value) -contains $invocation) {
        return $true
    }
    $literal = $invocation.Trim("'`"")
    try {
        return @($Rule.executables) -contains
            [System.IO.Path]::GetFileName($literal)
    }
    catch {
        return $false
    }
}

function Find-DeniedShellOperation {
    param(
        [Parameter(Mandatory)] [string] $Command,
        [Parameter(Mandatory)] $ShellRules,
        [string] $DynamicInvocationReason,
        $OpaqueProcessLaunchers
    )

    if (-not [string]::IsNullOrWhiteSpace($DynamicInvocationReason) -and
        $Command -match '(?i)\b(?:Invoke-Expression|iex)\b') {
        return @{
            Reason = $DynamicInvocationReason
            Verb = 'dynamic invocation'
        }
    }
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
    foreach ($rule in @($ShellRules)) {
        foreach ($commandAst in $commands) {
            if (Test-RuleExecutable $commandAst $rule) {
                $verb = Get-CommandVerb $commandAst
                $allowHelpProperty =
                    $rule.PSObject.Properties['allowHelp']
                if (@($rule.verbs) -contains $verb -and
                    -not ($null -ne $allowHelpProperty -and
                         $allowHelpProperty.Value -eq $true -and
                         (Test-CommandHelp $commandAst))) {
                    return @{
                        Rule = $rule
                        Verb = $verb.ToLowerInvariant()
                    }
                }
            }

            $name = $commandAst.GetCommandName()
            $leaf = if ([string]::IsNullOrWhiteSpace($name)) {
                ''
            }
            else {
                [System.IO.Path]::GetFileNameWithoutExtension($name)
            }
            if ($leaf -match '^(?i:cmd|powershell|pwsh)$') {
                $environmentProperty =
                    $rule.PSObject.Properties['environmentInvocations']
                $environmentInvocations = if ($null -eq $environmentProperty) {
                    @()
                }
                else {
                    @($environmentProperty.Value)
                }
                $executables = @($rule.executables) +
                    $environmentInvocations
                $executablePattern = ($executables |
                    ForEach-Object { [regex]::Escape([string] $_) }) -join '|'
                $verbPattern = (@($rule.verbs) |
                    ForEach-Object { [regex]::Escape([string] $_) }) -join '|'
                if ($commandAst.Extent.Text -match
                    "(?i)(?:$executablePattern)[\s'`"]+($verbPattern)\b") {
                    return @{
                        Rule = $rule
                        Verb = $Matches[1].ToLowerInvariant()
                    }
                }
            }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($DynamicInvocationReason)) {
        foreach ($commandAst in $commands) {
            $name = $commandAst.GetCommandName()
            $alias = if ([string]::IsNullOrWhiteSpace($name)) {
                $null
            }
            else {
                Get-Alias -Name $name -ErrorAction Ignore
            }
            $resolvedName = if ($null -eq $alias) {
                $name
            }
            else {
                $alias.ResolvedCommand.Name
            }
            $leaf = if ([string]::IsNullOrWhiteSpace($name)) {
                ''
            }
            else {
                [System.IO.Path]::GetFileNameWithoutExtension($resolvedName)
            }
            if ($name -match '^(?i:Invoke-Expression|iex)$' -or
                $commandAst.InvocationOperator -in @(
                    [System.Management.Automation.Language.TokenKind]::Ampersand,
                    [System.Management.Automation.Language.TokenKind]::Dot) -and
                [string]::IsNullOrWhiteSpace($name) -or
                @($OpaqueProcessLaunchers) -contains $leaf) {
                return @{
                    Reason = $DynamicInvocationReason
                    Verb = 'dynamic invocation'
                }
            }
        }
    }
    return $null
}

function Resolve-ToolPaths {
    param(
        [Parameter(Mandatory)] $ToolArguments,
        [Parameter(Mandatory)] $WriteContract,
        [Parameter(Mandatory)] [string] $WorkingDirectory
    )

    $values = [System.Collections.Generic.List[string]]::new()
    if ($ToolArguments -is [string]) {
        foreach ($match in [regex]::Matches(
            $ToolArguments,
            '(?m)^\*\*\* (?:Add|Update|Delete) File: (.+)$|^\*\*\* Move to: (.+)$')) {
            $value = if ($match.Groups[1].Success) {
                $match.Groups[1].Value
            }
            else {
                $match.Groups[2].Value
            }
            $values.Add($value.Trim())
        }
    }
    else {
        foreach ($propertyName in @($WriteContract.pathProperties)) {
            $property = $ToolArguments.PSObject.Properties[[string] $propertyName]
            if ($null -ne $property -and
                -not [string]::IsNullOrWhiteSpace([string] $property.Value)) {
                $values.Add([string] $property.Value)
            }
        }
        foreach ($propertyName in @($WriteContract.patchProperties)) {
            $property = $ToolArguments.PSObject.Properties[[string] $propertyName]
            if ($null -ne $property -and $property.Value -is [string]) {
                foreach ($match in [regex]::Matches(
                    [string] $property.Value,
                    '(?m)^\*\*\* (?:Add|Update|Delete) File: (.+)$|^\*\*\* Move to: (.+)$')) {
                    $value = if ($match.Groups[1].Success) {
                        $match.Groups[1].Value
                    }
                    else {
                        $match.Groups[2].Value
                    }
                    $values.Add($value.Trim())
                }
            }
        }
    }

    return @($values | ForEach-Object {
        if ([System.IO.Path]::IsPathRooted($_)) {
            [System.IO.Path]::GetFullPath($_)
        }
        else {
            [System.IO.Path]::GetFullPath(
                [System.IO.Path]::Combine($WorkingDirectory, $_))
        }
    } | Sort-Object -Unique)
}

function Test-PathContained {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $Candidate
    )

    $rootPath = [System.IO.Path]::TrimEndingDirectorySeparator(
        (Resolve-PhysicalPath $Root))
    $candidatePath = Resolve-PhysicalPath $Candidate
    return $candidatePath.Equals(
        $rootPath,
        [StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith(
            "$rootPath$([System.IO.Path]::DirectorySeparatorChar)",
            [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-PhysicalPath {
    param([Parameter(Mandatory)] [string] $Path)

    $normalizedPath = $Path
    if ($IsWindows) {
        if ($normalizedPath.StartsWith(
            '\\?\UNC\',
            [StringComparison]::OrdinalIgnoreCase)) {
            $normalizedPath = '\\' + $normalizedPath.Substring(8)
        }
        elseif ($normalizedPath.StartsWith(
            '\\?\',
            [StringComparison]::OrdinalIgnoreCase) -or
            $normalizedPath.StartsWith(
                '\??\',
                [StringComparison]::OrdinalIgnoreCase)) {
            $normalizedPath = $normalizedPath.Substring(4)
        }
        elseif ($normalizedPath.StartsWith(
            '\\.\',
            [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Hook "Device paths are not valid Skill contract file-write paths: $Path"
        }
    }
    $fullPath = [System.IO.Path]::GetFullPath($normalizedPath)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($pathRoot)) {
        Stop-Hook "The file-write path has no filesystem root: $Path"
    }
    $current = $pathRoot
    $relative = $fullPath.Substring($pathRoot.Length)
    $segments = $relative.Split(
        [char[]] @(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar),
        [StringSplitOptions]::RemoveEmptyEntries)
    foreach ($segment in $segments) {
        $candidate = [System.IO.Path]::Combine($current, $segment)
        if ([System.IO.File]::Exists($candidate) -or
            [System.IO.Directory]::Exists($candidate)) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $resolved = $item.ResolveLinkTarget($true)
                if ($null -eq $resolved) {
                    Stop-Hook "The file-write path contains an unresolved reparse point: $candidate"
                }
                $current = $resolved.FullName
                continue
            }
        }
        $current = $candidate
    }
    return [System.IO.Path]::GetFullPath($current)
}

function Test-WriteProtectionActive {
    param(
        [Parameter(Mandatory)] [string] $WorkspaceRoot,
        [Parameter(Mandatory)] $WriteContract
    )

    $activationFile = [System.IO.Path]::Combine(
        $WorkspaceRoot,
        [string] $WriteContract.activationFile)
    if (Test-Path -LiteralPath $activationFile -PathType Leaf) {
        return $true
    }
    $anchorProperty =
        $WriteContract.PSObject.Properties['activationCngKeyPrefix']
    if ($null -eq $anchorProperty -or
        [string]::IsNullOrWhiteSpace([string] $anchorProperty.Value) -or
        -not $IsWindows) {
        return $false
    }
    $normalized = [System.IO.Path]::GetFullPath(
        $WorkspaceRoot).ToUpperInvariant()
    $hash = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.Encoding]::UTF8.GetBytes($normalized))
    ).ToLowerInvariant().Substring(0, 32)
    return [System.Security.Cryptography.CngKey]::Exists(
        [string] $anchorProperty.Value + $hash)
}

if (-not (Test-Path -LiteralPath $ContractPath -PathType Leaf)) {
    Stop-Hook "The Skill execution contract was not found: $ContractPath"
}
try {
    $contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
}
catch {
    Stop-Hook "The Skill execution contract is malformed: $($_.Exception.Message)"
}
if ($contract.schemaVersion -ne '1.0' -or
    [string]::IsNullOrWhiteSpace([string] $contract.id) -or
    $null -eq $contract.workspace -or
    [string]::IsNullOrWhiteSpace([string] $contract.workspace.marker)) {
    Stop-Hook 'The Skill execution contract is invalid.'
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

$statePath = Get-SessionStatePath $sessionId ([string] $contract.id)
$migrationRoot = Find-ContractWorkspaceRoot $cwd $contract.workspace
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

$toolArguments = Get-ToolArguments $payload
if ($toolName -match '^(?i:powershell|bash)$') {
    if ($null -eq $toolArguments -or
        $toolArguments -is [string] -or
        $null -eq $toolArguments.PSObject.Properties['command'] -or
        [string]::IsNullOrWhiteSpace([string] $toolArguments.command)) {
        Stop-Hook "The shell tool payload does not contain a command."
    }
    $shellRulesProperty = $contract.PSObject.Properties['shellRules']
    $shellRules = if ($null -eq $shellRulesProperty) {
        @()
    }
    else {
        @($shellRulesProperty.Value)
    }
    $dynamicReasonProperty =
        $contract.PSObject.Properties['dynamicShellInvocationReason']
    $dynamicReason = if ($null -eq $dynamicReasonProperty) {
        $null
    }
    else {
        [string] $dynamicReasonProperty.Value
    }
    $launchersProperty =
        $contract.PSObject.Properties['opaqueProcessLaunchers']
    $opaqueProcessLaunchers = if ($null -eq $launchersProperty) {
        @()
    }
    else {
        @($launchersProperty.Value)
    }
    $operation = Find-DeniedShellOperation (
        [string] $toolArguments.command) $shellRules $dynamicReason (
        $opaqueProcessLaunchers)
    if ($null -eq $operation) {
        Write-HookResult @{}
        exit 0
    }
    $reason = if ($operation.ContainsKey('Reason')) {
        [string] $operation.Reason
    }
    else {
        ([string] $operation.Rule.reason).Replace(
            '{verb}',
            [string] $operation.Verb)
    }
    Write-HookResult @{
        permissionDecision = 'deny'
        permissionDecisionReason = $reason
    }
    exit 0
}

$writeProperty = $contract.PSObject.Properties['protectedWrites']
$writeContract = if ($null -eq $writeProperty) {
    $null
}
else {
    $writeProperty.Value
}
$normalizedToolName = ($toolName -split '\.')[-1]
if ($null -eq $writeContract -or
    @($writeContract.toolNames) -notcontains $normalizedToolName -or
    -not (Test-WriteProtectionActive $migrationRoot $writeContract)) {
    Write-HookResult @{}
    exit 0
}

$paths = @(Resolve-ToolPaths $toolArguments $writeContract $cwd)
if ($paths.Count -eq 0) {
    Write-HookResult @{
        permissionDecision = 'deny'
        permissionDecisionReason = [string] $writeContract.unknownPathReason
    }
    exit 0
}
$protected = @($paths | Where-Object {
    Test-PathContained $migrationRoot $_
})
if ($protected.Count -eq 0) {
    Write-HookResult @{}
    exit 0
}

Write-HookResult @{
    permissionDecision = 'deny'
    permissionDecisionReason = [string] $writeContract.reason
}
