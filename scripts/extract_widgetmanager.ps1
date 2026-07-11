# Extract methods from WidgetManager.cs into 4 partial class files
param(
    [string]$SourceFile = "src/DeskBox/Services/WidgetManager.cs"
)

$lines = Get-Content $SourceFile -Encoding UTF8
$totalLines = $lines.Count

# Define method groups: name => target file
$groups = @{
    "TrayAnimation" = @(
        'PrepareWidgetForBatchShowAsync',
        'PlayPreparedTrayShowAnimations',
        'PrepareTrayShowAnimations',
        'PlayPreparedTrayHideAnimations',
        'ApplyTrayAnimationGroupOffset',
        'GetAnimationWorkAreaKey',
        'GetAnimationWorkArea',
        'ActivateLastRaisedWindow',
        'QueueTrayRaiseTopMostConfirmation',
        'ConfirmTrayRaiseTopMost',
        'SaveBatchVisibilityState',
        'RaiseWidgetsFromTrayAsync'
    )
    "ZOrder" = @(
        'RestoreRaisedWidgetsToDesktopLayer',
        'ForceRestoreRaisedWidgetsToDesktopLayer',
        'BringAllVisibleWidgetsToFront',
        'RequestRestoreRaisedWidgetsToDesktopLayer',
        'QueueRequestedLayerRestoreCheck',
        'TryRestoreRaisedWidgetsAfterInteraction',
        'StartTrayLayerRestoreMonitor',
        'StopTrayLayerRestoreMonitor',
        'InstallTrayLayerMouseHook',
        'UninstallTrayLayerMouseHook',
        'TrayLayerMouseHookProc',
        'IsMouseDownMessage',
        'RestoreRaisedWidgetsForExternalMouseDown',
        'IsCursorOverAnyWidget',
        'IsCursorOverWindow',
        'TrayLayerRestoreTimer_Tick',
        'IsPointerOverDeskBoxWindow',
        'IsForegroundDeskBoxWindow',
        'IsDeskBoxForegroundWindow',
        'IsPointerOverTaskbar',
        'IsTaskbarWindow',
        'IsDesktopShellWindow',
        'WindowOrAncestorHasClass',
        'WindowHasClass',
        'TryGetCursorPosition',
        'SetWidgetsRaisedFromTray',
        'FormatWidgetList',
        'FormatWidget',
        'FormatHostWindow',
        'ShortId',
        'FormatPoint'
    )
    "Storage" = @(
        'CanCleanupManagedStorageForWidget',
        'GetOrphanManagedStorageFolders',
        'MoveOrphanManagedStorageFolderContentsToDesktopAsync',
        'DeleteOrphanManagedStorageFolderAsync',
        'RestoreOrphanManagedStorageFoldersAsync',
        'SyncMappedWidgetShortcut',
        'SyncStorageFolderEntries',
        'RemoveMappedWidgetShortcut',
        'DeleteMappedWidgetShortcut',
        'RemoveStaleMappedWidgetShortcuts',
        'RemoveAllMappedWidgetShortcuts',
        'GetExistingMappedWidgetShortcutPath',
        'BuildAvailableMappedShortcutPath',
        'CanUseMappedShortcutPath',
        'IsDeskBoxMappedWidgetShortcut',
        'GetDeskBoxMappedWidgetShortcutId',
        'BuildMappedWidgetShortcutDescription',
        'ApplyWidgetRemovalActionAsync',
        'MoveManagedFolderContentsToDesktopAsync',
        'ValidateOrphanManagedStorageFolderPath',
        'IsDefaultManagedStorageFolder',
        'GetPossibleManagedStoragePaths',
        'CountDirectoryEntries',
        'BuildManagedFolderPath',
        'GetManagedStorageRootPath',
        'CreateManagedFolderName',
        'IsManagedWidgetNameInUse',
        'IsUnavailableManagedFolderPath',
        'ShouldMoveManagedItems',
        'UpdateDefaultManagedStorageRootAsync',
        'SetManagedStorageMigrationBusy',
        'RenameManagedWidgetFolderAsync',
        'NotifyItemsMovedOutAsync'
    )
    "FeatureWidgets" = @(
        'GetFeatureWidget',
        'IsFeatureWidgetEnabled',
        'CreateOrShowFeatureWidgetAsync',
        'SetFeatureWidgetEnabledAsync',
        'ResetFeatureWidgetAsync',
        'CreateDefaultFeatureWidgetConfig',
        'ResetFeatureWidgetConfig',
        'GetDefaultFeatureWidgetSize',
        'CloseLoadedFeatureWidgetWindows',
        'SetTodoEnabledAsync',
        'SetContentFeatureWidgetEnabledAsync',
        'SetWeatherFeatureWidgetEnabledAsync',
        'GetFeatureWidgetEnabledState',
        'IsContentFeatureWidgetKind',
        'SetFeatureWidgetEnabledState',
        'HideAndCloseFeatureWidgetAsync',
        'CloseFeatureWidgetInstance',
        'CreateFeatureWidgetHandlers',
        'CreateWindowProviders',
        'ApplyFeatureWidgetEnabledState',
        'RestoreDeletedQuickCaptureConfigs',
        'SetQuickCaptureEnabledAsync',
        'DeduplicateFeatureWidgets',
        'RepairLegacyContentFeatureFileShells',
        'IsLegacyEmptyContentFeatureFileShell',
        'IsDefaultMusicTitle',
        'CreateSingletonContentFeatureWidgetAsync',
        'GetDefaultFeatureWidgetTitle',
        'CreateOrShowQuickCaptureWidgetAsync',
        'CloseLoadedQuickCaptureWidgets',
        'CreateQuickCaptureWidgetFromConfigAsync',
        'QueueDeferredQuickCaptureInitialization',
        'GetQuickCaptureFileWidgetTargets',
        'GetLastQuickCaptureFileWidgetTarget',
        'SaveQuickCaptureItemToFileWidgetAsync',
        'RememberLastQuickCaptureFileWidgetTarget',
        'SaveQuickCaptureImageToFolderAsync',
        'SaveQuickCaptureTextToFolderAsync',
        'SaveQuickCaptureLinkToFolderAsync',
        'BuildQuickCaptureContentFileName',
        'TryGetFileWidgetFolderPath',
        'ShowTodoReminderTargetAsync',
        'CreateTodoWidgetAsync'
    )
}

# Fields to extract per group
$fieldGroups = @{
    "TrayAnimation" = @(
        'private const double OffscreenAnimationPadding',
        'private long _trayRaiseBatchGeneration'
    )
    "ZOrder" = @(
        'private DispatcherQueueTimer\? _trayLayerRestoreTimer',
        'private readonly Win32Helper\.LowLevelMouseProc _mouseHookProc',
        'private IntPtr _mouseHookHandle',
        'private bool _widgetsRaisedFromTray',
        'private bool _isTogglingWidgetsDesktopLayer',
        'private string _lastWidgetLayerMode',
        'private DateTime _lastTrayLayerToggleUtc',
        'private DateTime _suppressTrayLayerRestoreUntilUtc'
    )
    "FeatureWidgets" = @(
        'private readonly Dictionary<WidgetKind, bool> _lastFeatureWidgetEnabledStates',
        'private readonly Dictionary<WidgetKind, FeatureWidgetHandler> _featureWidgetHandlers',
        'private readonly Dictionary<WidgetKind, WidgetWindowProvider> _windowProviders',
        'private bool _isApplyingAppearancePreview'
    )
}

$linesToRemove = [System.Collections.Generic.HashSet[int]]::new()

# Find method blocks
function FindMethodBlock($lines, $methodName, $totalLines) {
    $startLine = -1
    for ($i = 0; $i -lt $totalLines; $i++) {
        if ($lines[$i] -match "^\s+(public|private|internal|protected)\s+(static\s+)?(async\s+)?(override\s+)?\S+\s+$methodName\s*[<(]") {
            $startLine = $i
            break
        }
    }
    if ($startLine -eq -1) { return $null }

    # Find opening brace
    $braceLine = $startLine
    for ($i = $startLine; $i -lt $totalLines; $i++) {
        if ($lines[$i] -match "^\s*\{") {
            $braceLine = $i
            break
        }
    }

    # Find closing brace
    $depth = 0
    $endLine = -1
    for ($i = $braceLine; $i -lt $totalLines; $i++) {
        $line = $lines[$i]
        $opens = ([regex]::Matches($line, "\{")).Count
        $closes = ([regex]::Matches($line, "\}")).Count
        $depth += $opens - $closes
        if ($depth -eq 0 -and $i -gt $braceLine) {
            $endLine = $i
            break
        }
    }
    if ($endLine -eq -1) { return $null }

    # Include preceding doc comments and attributes
    $actualStart = $startLine
    for ($i = $startLine - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match "^\s*///" -or $lines[$i] -match "^\s*\[") {
            $actualStart = $i
        } else {
            break
        }
    }

    return @{ Start = $actualStart; End = $endLine }
}

# Process each group
foreach ($groupName in $groups.Keys) {
    $methodNames = $groups[$groupName]
    $methodBlocks = @()

    foreach ($methodName in $methodNames) {
        $block = FindMethodBlock $lines $methodName $totalLines
        if ($block -eq $null) {
            Write-Warning "[$groupName] Method not found: $methodName"
            continue
        }

        $blockLines = $lines[$block.Start..$block.End]
        $methodBlocks += ,@{ Name = $methodName; Lines = $blockLines; StartLine = $block.Start; EndLine = $block.End }

        for ($i = $block.Start; $i -le $block.End; $i++) {
            $linesToRemove.Add($i) | Out-Null
        }
        # Remove trailing blank line
        if ($block.End + 1 -lt $totalLines -and $lines[$block.End + 1] -match "^\s*$") {
            $linesToRemove.Add($block.End + 1) | Out-Null
        }
        Write-Host "[$groupName] Found: $methodName (lines $($block.Start + 1)-$($block.End + 1))"
    }

    # Find fields
    $fieldLines = @()
    if ($fieldGroups.ContainsKey($groupName)) {
        foreach ($pattern in $fieldGroups[$groupName]) {
            for ($i = 0; $i -lt $totalLines; $i++) {
                if ($lines[$i] -match $pattern -and -not $linesToRemove.Contains($i)) {
                    $fieldLines += $lines[$i]
                    $linesToRemove.Add($i) | Out-Null
                    Write-Host "[$groupName] Found field: $($lines[$i].Trim())"
                    break
                }
            }
        }
    }

    # Write the partial file
    $usingLines = @(
        "// Copyright (c) DeskBox. All rights reserved.",
        "",
        "using DeskBox.Models;",
        "using DeskBox.Helpers;",
        "using DeskBox.Controls.WidgetContents;",
        "using DeskBox.ViewModels;",
        "using DeskBox.Views;",
        "using Microsoft.UI.Dispatching;",
        "using Microsoft.UI.Windowing;",
        "using Microsoft.UI.Xaml;",
        "",
        "namespace DeskBox.Services;",
        "",
        "/// <summary>",
        "/// Partial class containing $groupName logic for WidgetManager.",
        "/// </summary>",
        "public sealed partial class WidgetManager",
        "{"
    )

    $output = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $usingLines) { $output.Add($line) }
    $output.Add("")

    # Write fields
    if ($fieldLines.Count -gt 0) {
        foreach ($field in $fieldLines) { $output.Add($field) }
        $output.Add("")
    }

    # Write methods sorted by line number
    $sortedBlocks = $methodBlocks | Sort-Object { $_.StartLine }
    foreach ($block in $sortedBlocks) {
        foreach ($line in $block.Lines) { $output.Add($line) }
        $output.Add("")
    }

    $output.Add("}")

    $outputPath = "src/DeskBox/Services/WidgetManager.$groupName.cs"
    $output -join "`r`n" | Set-Content $outputPath -Encoding UTF8 -NoNewline
    Write-Host "Created: $outputPath ($($output.Count) lines)`n"
}

# Write modified source file
$newLines = [System.Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt $totalLines; $i++) {
    if (-not $linesToRemove.Contains($i)) {
        $newLines.Add($lines[$i])
    }
}

# Clean up 3+ consecutive blank lines
$cleanedLines = [System.Collections.Generic.List[string]]::new()
$blankCount = 0
foreach ($line in $newLines) {
    if ($line -match "^\s*$") {
        $blankCount++
        if ($blankCount -le 2) { $cleanedLines.Add($line) }
    } else {
        $blankCount = 0
        $cleanedLines.Add($line)
    }
}

$cleanedLines -join "`r`n" | Set-Content $SourceFile -Encoding UTF8 -NoNewline
Write-Host "Modified: $SourceFile (removed $($linesToRemove.Count) lines, $($cleanedLines.Count) lines remaining)"
