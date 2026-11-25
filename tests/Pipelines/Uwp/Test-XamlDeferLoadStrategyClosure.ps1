param(
    [Parameter(Mandatory)][string]$Cli,
    [Parameter(Mandatory)][string]$WinApp,
    [Parameter(Mandatory)][string]$Target,
    [Parameter(Mandatory)][string]$Artifacts,
    [ValidateSet('Capture', 'Complete', 'Review')][string]$Phase = 'Capture',
    [switch]$DifferentClockInputs,
    [switch]$ExpectedUnsupportedHost,
    [switch]$ExpectedApplicationDifference,
    [int]$ClientWidth = 1400,
    [int]$ClientHeight = 900
)

$ErrorActionPreference = 'Stop'
$Target = (Resolve-Path -LiteralPath $Target).Path
$Cli = (Resolve-Path -LiteralPath $Cli).Path
$WinApp = (Resolve-Path -LiteralPath $WinApp).Path
New-Item -ItemType Directory -Path $Artifacts -Force | Out-Null
$Artifacts = (Resolve-Path -LiteralPath $Artifacts).Path
$script:commandIndex = @(Get-ChildItem -LiteralPath $Artifacts -Filter '*.command.json').Count

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-Product([string[]]$Command, [switch]$AllowFailure) {
    $script:commandIndex++
    $stem = Join-Path $Artifacts ('{0:d3}' -f $script:commandIndex)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = & $Cli @Command 2> "$stem.stderr.log" | Out-String
    $exitCode = $LASTEXITCODE
    $watch.Stop()
    $output | Set-Content -LiteralPath "$stem.stdout.json" -Encoding utf8
    @{ command = @($Cli) + $Command; exitCode = $exitCode; elapsedSeconds = $watch.Elapsed.TotalSeconds } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$stem.command.json" -Encoding utf8
    if (-not $AllowFailure) {
        Require ($exitCode -eq 0) "CLI exited $exitCode; inspect $stem.stdout.json and $stem.stderr.log"
    }
    return ($output | ConvertFrom-Json -Depth 100)
}

function Assess {
    return Invoke-Product -Command @('uwp', 'closure', 'assess', $Target)
}

function Get-Replay {
    return Get-Content -LiteralPath (Join-Path $Target '.migration-evidence\enforcement\replay-attestation.json') -Raw |
        ConvertFrom-Json
}

function Get-TargetWindow {
    $replay = Get-Replay
    $probe = Invoke-Product -Command @('uwp', 'closure', 'capture', $Target, '--winapp', $WinApp,
        '--', 'list-windows', '--json')
    $windows = Get-Content -LiteralPath (Join-Path $Target $probe.stdout) -Raw | ConvertFrom-Json
    $window = @($windows | Where-Object processId -eq $replay.processId |
        Where-Object { $_.width -gt 300 -and $_.height -gt 300 }) | Select-Object -First 1
    Require ($null -ne $window) 'No window belongs to the authenticated canonical replay.'
    return $window.hwnd
}

function Source-Capture([string[]]$Command, [long]$Window) {
    return Invoke-Product -Command (@('uwp', 'source', 'capture', $Target, '--winapp', $WinApp, '--') +
        $Command + @('-w', "$Window"))
}

function Bind-Source([string]$Id, [object[]]$Probes) {
    $command = @('uwp', 'closure', 'bind-source', $Target, '--surface', $Id)
    foreach ($probe in $Probes) { $command += @('--probe', $probe.artifact) }
    return Invoke-Product -Command $command
}

function Navigate-Target([string]$Id, [long]$Window) {
    $snapshot = Invoke-Product -Command @('uwp', 'closure', 'navigate', $Target,
        '--surface', $Id, '--window', "$Window", '--winapp', $WinApp)
    $state = $snapshot.StateVerifications | Where-Object Id -eq $Id
    Write-Host "$Id : $($state.Result)"
    return $snapshot
}

function Read-Plan {
    return Get-Content -LiteralPath (Join-Path $Target '.migration-evidence\state-plan.json') -Raw |
        ConvertFrom-Json -Depth 100
}

function Set-CaseViewport([long]$SourceFrame, [int]$SourceProcess, [long]$TargetWindow) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class AcceptanceViewport {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    public delegate bool ChildCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hwnd, ChildCallback callback, IntPtr data);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    public static IntPtr FindCore(IntPtr frame, uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(frame, (hwnd, _) => {
            uint owner; GetWindowThreadProcessId(hwnd, out owner);
            var name = new StringBuilder(256); GetClassName(hwnd, name, 256);
            if (owner == pid && name.ToString() == "Windows.UI.Core.CoreWindow") found = hwnd;
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
    $previousDpi = [AcceptanceViewport]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
    try {
        $core = [AcceptanceViewport]::FindCore([IntPtr]$SourceFrame, [uint32]$SourceProcess)
        Require ($core -ne 0) 'The source frame does not contain the authenticated source process.'
        $dpi = [AcceptanceViewport]::GetDpiForWindow($core)
        Require ($dpi -gt 0 -and $dpi -eq [AcceptanceViewport]::GetDpiForWindow([IntPtr]$TargetWindow)) `
            'Source and target must use the same capture DPI.'
        $width = [int][Math]::Round($ClientWidth * $dpi / 96)
        $height = [int][Math]::Round($ClientHeight * $dpi / 96)
        foreach ($pair in @(@($SourceFrame, $core), @($TargetWindow, $TargetWindow))) {
            $client = [AcceptanceViewport+Rect]::new()
            $outer = [AcceptanceViewport+Rect]::new()
            Require ([AcceptanceViewport]::GetClientRect([IntPtr]$pair[1], [ref]$client)) 'Cannot read the application client.'
            Require ([AcceptanceViewport]::GetWindowRect([IntPtr]$pair[0], [ref]$outer)) 'Cannot read the application frame.'
            Require ([AcceptanceViewport]::SetWindowPos([IntPtr]$pair[0], 0, 0, 0,
                $width + $outer.Right - $outer.Left - $client.Right,
                $height + $outer.Bottom - $outer.Top - $client.Bottom, 0x16)) 'Cannot set the test viewport.'
            Start-Sleep -Milliseconds 300
            Require ([AcceptanceViewport]::GetClientRect([IntPtr]$pair[1], [ref]$client)) 'Cannot verify the test viewport.'
            Require ($client.Right -eq $width -and $client.Bottom -eq $height) 'The requested client viewport was not applied.'
        }
        @{ sourceFrame = $SourceFrame; sourceCore = $core.ToInt64(); sourceProcessId = $SourceProcess;
            targetWindow = $TargetWindow; dpi = $dpi; clientWidth = $ClientWidth; clientHeight = $ClientHeight;
            physicalClientWidth = $width; physicalClientHeight = $height } |
            ConvertTo-Json | Set-Content (Join-Path $Artifacts 'viewport.json') -Encoding utf8
    }
    finally { [AcceptanceViewport]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null }
}

if ($Phase -eq 'Capture') {
    $plan = Read-Plan
    Require ((@($plan.surfaces.id) -join ',') -eq 'main,basic,adaptive,template') 'Expected the complete four-surface sample inventory.'
    Require (@($plan.surfaces | Where-Object { $_.disposition -ne 'required' }).Count -eq 0) 'No sample surface may be excluded.'
    Require (@($plan.surfaces | Where-Object { $_.source.status -eq 'captured' }).Count -eq 0) `
        'Capture requires a fresh case; never replace an existing frozen source baseline.'
    $report = Get-Content -LiteralPath (Join-Path $Target 'migration-report.json') -Raw | ConvertFrom-Json
    $sourceRun = Get-Content -LiteralPath (Join-Path $Target ($report.validation.sourceBaseline.evidenceRoot +
        '\gateway\current-run.json')) -Raw | ConvertFrom-Json
    Require ($sourceRun.exitCode -eq 0) 'Run the source gateway successfully before collecting UI evidence.'
    $sourceClass = if ($ExpectedUnsupportedHost) { 'Windows.UI.Core.CoreWindow' } else { 'ApplicationFrameWindow' }
    $sourceWindow = @($sourceRun.authorizedWindows | Where-Object {
        $_.className -eq $sourceClass -and -not [string]::IsNullOrWhiteSpace($_.title)
    }) |
        Select-Object -First 1
    Require ($null -ne $sourceWindow) 'The source gateway authorized no titled window of the expected host class.'
    $actionWindow = $sourceRun.authorizedWindows | Where-Object {
        $_.className -eq 'ApplicationFrameWindow' -and -not [string]::IsNullOrWhiteSpace($_.title)
    } | Select-Object -First 1
    Require ($null -ne $actionWindow) 'The source application frame is required for navigation.'
    $readiness = Source-Capture -Command @('inspect', '--json', '--depth', '30') -Window $actionWindow.hwnd
    $readinessTree = Get-Content -LiteralPath (Join-Path $Target $readiness.stdout) -Raw | ConvertFrom-Json -Depth 100
    $frame = $readinessTree.windows | Where-Object hwnd -eq $actionWindow.hwnd | Select-Object -First 1
    Require ($null -ne $frame -and $frame.elementCount -gt 1) `
        'The source sample exposes only a window shell. Refusing to bind this capture: verify an active interactive desktop and a responsive, unsuspended source application.'
    $targetWindow = Get-TargetWindow
    Set-CaseViewport -SourceFrame $actionWindow.hwnd -SourceProcess $sourceRun.processId -TargetWindow $targetWindow
    $initial = Source-Capture -Command @('inspect', '--json', '--depth', '30') -Window $sourceWindow.hwnd
    Bind-Source -Id 'main' -Probes @($initial) | Out-Null
    Bind-Source -Id 'basic' -Probes @($initial) | Out-Null
    foreach ($id in @('main', 'basic')) {
        $snapshot = Navigate-Target -Id $id -Window $targetWindow
        $state = $snapshot.StateVerifications | Where-Object Id -eq $id
        $expected = if ($ExpectedUnsupportedHost) { 'human-validation-required' } else { 'surface-parity-verified' }
        Require ($state.Result -eq $expected) "$id did not produce the expected complete-tree result."
    }

    # This unmodified sample formats DateTime.Now to minute precision. Verify actual equal values,
    # without changing the system clock, application formulas, captures, or comparison rules.
    if (-not $ExpectedUnsupportedHost) {
        while ((Get-Date).Second -gt 5) { Start-Sleep -Seconds 1 }
    }
    $adaptive = $plan.surfaces | Where-Object id -eq 'adaptive'
    $sourceAction = Source-Capture -Command $adaptive.navigation.requiredActions[0].command -Window $actionWindow.hwnd
    $sourceAdaptive = Source-Capture -Command @('inspect', '--json', '--depth', '30') -Window $sourceWindow.hwnd
    Bind-Source -Id 'adaptive' -Probes @($initial, $sourceAction, $sourceAdaptive) | Out-Null
    if ($DifferentClockInputs) {
        $minute = (Get-Date).ToString('yyyyMMddHHmm')
        while ((Get-Date).ToString('yyyyMMddHHmm') -eq $minute) { Start-Sleep -Seconds 1 }
    }
    $snapshot = Navigate-Target -Id 'adaptive' -Window $targetWindow
    $state = $snapshot.StateVerifications | Where-Object Id -eq 'adaptive'
    $expected = if ($ExpectedUnsupportedHost) { 'human-validation-required' } elseif ($DifferentClockInputs -or $ExpectedApplicationDifference) { 'surface-mismatch' } else { 'surface-parity-verified' }
    Require ($state.Result -eq $expected) `
        'Adaptive result did not reflect the deliberately controlled minute-input condition.'
    $template = $plan.surfaces | Where-Object id -eq 'template'
    $sourceAction = Source-Capture -Command $template.navigation.requiredActions[0].command -Window $actionWindow.hwnd
    $sourceTemplate = Source-Capture -Command @('inspect', '--json', '--depth', '30') -Window $sourceWindow.hwnd
    Bind-Source -Id 'template' -Probes @($sourceAdaptive, $sourceAction, $sourceTemplate) | Out-Null
    $snapshot = Navigate-Target -Id 'template' -Window $targetWindow
    $expectedMatches = if ($ExpectedUnsupportedHost) { 0 } elseif ($DifferentClockInputs -or $ExpectedApplicationDifference) { 3 } else { 4 }
    Require (@($snapshot.StateVerifications | Where-Object Result -eq 'surface-parity-verified').Count -eq $expectedMatches) `
        "Expected $expectedMatches full-tree matches."
    Require (($snapshot.Gates | Where-Object id -eq 'source-oracle').status -eq 'passed') 'Source discovery is incomplete.'
    $contracts = Get-ChildItem -LiteralPath (Join-Path $Target '.migration-evidence\enforcement\navigation') `
        -Recurse -Filter contract.json | Get-FileHash
    $supplement = Source-Capture -Command @('inspect', '--json', '--depth', '30') -Window $sourceWindow.hwnd
    Require ($supplement.exitCode -eq 0) 'Supplemental capture after source discovery failed.'
    foreach ($contract in $contracts) {
        Require ((Get-FileHash -LiteralPath $contract.Path).Hash -eq $contract.Hash) 'Supplemental capture changed a frozen source contract.'
    }
    $snapshot = Assess
    $snapshot | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $Artifacts 'captured-assessment.json') -Encoding utf8
    @{ targetWindow = $targetWindow; sourceWindows = $sourceRun.authorizedWindows;
        sourceProcessId = $sourceRun.processId; differentClockInputs = [bool]$DifferentClockInputs } |
        ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Artifacts 'owned-windows.json') -Encoding utf8
    Write-Host "Captured all four surfaces; matching=$expectedMatches; supplemental source capture preserved all baseline contracts."
}
elseif ($Phase -eq 'Complete') {
    $before = Assess
    Require (@($before.StateVerifications | Where-Object Result -eq 'surface-parity-verified').Count -eq 4) `
        'A complete positive case is required before testing replay durability.'
    $replay = Get-Replay
    $process = Get-Process -Id $replay.processId -ErrorAction Stop
    Require ($process.Path.StartsWith($Target + '\', [StringComparison]::OrdinalIgnoreCase)) 'Refusing to close an unrelated process.'
    Require ($process.CloseMainWindow()) 'The owned target did not accept a normal window-close request.'
    Require ($process.WaitForExit(10000)) 'The owned target did not exit after closing its window.'
    $closed = Assess
    Require (@($closed.StateVerifications | Where-Object Result -eq 'surface-parity-verified').Count -eq 4) `
        'Closing the historical process erased authenticated surface facts.'
    $after = Invoke-Product -Command @('uwp', 'closure', 'attest', $Target, '--force-replay', '--winapp', $WinApp)
    $next = Get-Replay
    Require ($next.id -ne $replay.id) 'No new canonical replay was created.'
    Require ($next.buildAttestationId -eq $replay.buildAttestationId) 'Replay unexpectedly required a different build.'
    Require ($next.processId -ne $replay.processId -or $next.processStartedAt -ne $replay.processStartedAt) `
        'The canonical process was not replaced.'
    Require (@($after.StateVerifications | Where-Object Result -eq 'surface-parity-verified').Count -eq 4) `
        'A new replay erased same-build authenticated surface facts.'
    $complete = Invoke-Product -Command @('uwp', 'closure', 'finalize', $Target, '--strict')
    Require ($complete.Status -eq 'passed' -and $complete.Completion.outcome -eq 'complete') `
        'The positive case did not genuinely complete strict closure.'
    $complete | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $Artifacts 'complete-assessment.json') -Encoding utf8
    Write-Host 'Strict closure passed with all four surfaces and a replacement canonical process.'
}
else {
    $complete = Invoke-Product -Command @('uwp', 'closure', 'finalize', $Target)
    Require ($complete.Status -ne 'passed') 'An unresolved comparison was incorrectly accepted.'
    $before = Assess
    if ($ExpectedApplicationDifference) {
        Require ($before.Disposition.kind -eq 'continue') 'An application difference must remain actionable.'
        Require ((@($before.Completion.actionableSurfaceIds) -join ',') -eq 'adaptive') 'Expected the real adaptive application difference.'
        Require (@($before.Completion.inputReviewSurfaceIds).Count -eq 0) 'An application difference was hidden as input review.'
        $rejected = Invoke-Product -Command @('uwp', 'closure', 'finalize', $Target, '--strict') -AllowFailure
        Require ($rejected.Status -ne 'passed') 'Strict finalization accepted an application difference.'
        $rejected | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $Artifacts 'application-difference-assessment.json') -Encoding utf8
        Write-Host 'The real application difference remains actionable and strict closure was rejected.'
        exit 0
    }
    $expectedKind = if ($ExpectedUnsupportedHost) { 'tool-blocked' } else { 'input-review-required' }
    $expectedMatches = if ($ExpectedUnsupportedHost) { 0 } else { 3 }
    Require ($before.Disposition.kind -eq $expectedKind) "Expected an explicit $expectedKind stop."
    Require (-not $before.Disposition.retryable) 'Input review incorrectly authorizes an unchanged retry.'
    Require (@($before.Completion.actionableSurfaceIds).Count -eq 0) 'Input review was misreported as application repair.'
    if ($ExpectedUnsupportedHost) {
        Require (@($before.Completion.toolBlockedSurfaceIds).Count -eq 4) 'Expected all four unsupported host comparisons.'
    }
    else {
        Require ((@($before.Completion.inputReviewSurfaceIds) -join ',') -eq 'adaptive') 'Expected only adaptive input review.'
    }
    $replay = Get-Replay
    $progressPath = Join-Path $Target '.migration-evidence\enforcement\progress.json'
    $operations = @((Get-Content $progressPath -Raw | ConvertFrom-Json).operations).Count
    $retry = Invoke-Product -Command @('uwp', 'closure', 'attest', $Target, '--force-replay', '--winapp', $WinApp)
    Require ((Get-Replay).id -eq $replay.id) 'Input review created an unchanged canonical replay.'
    Require (@((Get-Content $progressPath -Raw | ConvertFrom-Json).operations).Count -eq $operations) `
        'Input review consumed another build/replay operation.'
    Require (@($retry.StateVerifications | Where-Object Result -eq 'surface-parity-verified').Count -eq $expectedMatches) `
        'Honest negative finalization erased unrelated successful surface evidence.'
    $retry | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $Artifacts 'input-review-assessment.json') -Encoding utf8
    Write-Host "$expectedKind preserved $expectedMatches matches, honest partial finalization, and zero unchanged build/replay retries."
}
