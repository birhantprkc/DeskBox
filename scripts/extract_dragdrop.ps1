# Extract drag/drop methods from WidgetWindow.xaml.cs into WidgetWindow.DragDrop.cs
param(
    [string]$SourceFile = "src/DeskBox/Views/WidgetWindow.xaml.cs"
)

$lines = Get-Content $SourceFile -Encoding UTF8
$totalLines = $lines.Count

# Method signatures to extract (exact method name to match)
$methodNames = @(
    'InstallFileDropSubclass',
    'RemoveFileDropSubclass',
    'FileDropSubclassProc',
    'ImportNativeDropPathsAsync',
    'RootGrid_DragOver',
    'RootGrid_DragEnter',
    'GetRootFolderDropCaptionKey',
    'NormalizePathDropOperation',
    'GetAcceptedOperationCaption',
    'ShouldMoveForAcceptedOperation',
    'RootGrid_DragLeave',
    'RootGrid_Drop',
    'ItemsView_DragItemsStarting',
    'ItemsView_DragItemsCompleted',
    'HandleItemDragCompletedAsync',
    'GetDragItems',
    'TryPrepareItemDragPackage',
    'SyncMoveSourceAsync',
    'HasDeskBoxInternalDragData',
    'HasPathDropData',
    'GetDropPathsAsync',
    'TryNormalizeDroppedPath',
    'LogDropDiagnostic',
    'FormatDataPackageFormats',
    'WidgetItemSurface_DragOver',
    'WidgetItemSurface_DragLeave',
    'WidgetItemSurface_Drop',
    'TryGetFolderDropTarget',
    'SetFolderDropTarget',
    'ClearFolderDropTarget',
    'IsPointerOverFolderDropTarget',
    'IsInvalidFolderDrop',
    'GetManagedDropOperation',
    'MoveDraggedPathsBackToDesktopAsync',
    'GetAcceptedDropOperation',
    'SupportsOperation',
    'CanMoveItemsBackToDesktop'
)

# Find each method's start line (0-indexed) and extract its block
$methodBlocks = @()
$linesToRemove = [System.Collections.Generic.HashSet[int]]::new()

foreach ($methodName in $methodNames) {
    # Find the method signature line
    $startLine = -1
    for ($i = 0; $i -lt $totalLines; $i++) {
        if ($lines[$i] -match "^\s+(private|public|internal|protected).*\b$methodName\b\s*[\(<]") {
            $startLine = $i
            break
        }
    }

    if ($startLine -eq -1) {
        Write-Warning "Method not found: $methodName"
        continue
    }

    # Find the opening brace
    $braceLine = $startLine
    $foundOpenBrace = $false
    for ($i = $startLine; $i -lt $totalLines; $i++) {
        if ($lines[$i] -match "^\s*\{") {
            $braceLine = $i
            $foundOpenBrace = $true
            break
        }
    }

    if (-not $foundOpenBrace) {
        Write-Warning "Opening brace not found for: $methodName (line $($startLine + 1))"
        continue
    }

    # Find the closing brace by counting braces
    $depth = 0
    $endLine = -1
    for ($i = $braceLine; $i -lt $totalLines; $i++) {
        $line = $lines[$i]
        # Count opening and closing braces (ignoring string literals - simple approach)
        $opens = ([regex]::Matches($line, "\{")).Count
        $closes = ([regex]::Matches($line, "\}")).Count
        $depth += $opens - $closes
        if ($depth -eq 0 -and $i -gt $braceLine) {
            $endLine = $i
            break
        }
    }

    if ($endLine -eq -1) {
        Write-Warning "Closing brace not found for: $methodName (line $($startLine + 1))"
        continue
    }

    # Also include any preceding doc comments
    $actualStart = $startLine
    for ($i = $startLine - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match "^\s*///" -or $lines[$i] -match "^\s*\[") {
            $actualStart = $i
        } else {
            break
        }
    }

    # Extract the method block
    $block = $lines[$actualStart..$endLine]
    $methodBlocks += ,@{ Name = $methodName; Lines = $block; StartLine = $actualStart; EndLine = $endLine }

    # Mark lines for removal (including trailing blank line)
    for ($i = $actualStart; $i -le $endLine; $i++) {
        $linesToRemove.Add($i) | Out-Null
    }
    # Also remove trailing blank line if present
    if ($endLine + 1 -lt $totalLines -and $lines[$endLine + 1] -match "^\s*$") {
        $linesToRemove.Add($endLine + 1) | Out-Null
    }

    Write-Host "Found: $methodName (lines $($actualStart + 1)-$($endLine + 1))"
}

# Also extract fields
$fieldPatterns = @(
    'private const string DeskBoxInternalDragToken',
    'private static readonly UIntPtr FileDropSubclassId',
    'private readonly Win32Helper.SubclassProc _fileDropSubclassProc',
    'private string\[\] _activeDragSourcePaths',
    'private bool _activeDragHasStorageItems',
    'private string\? _lastRootDragDiagnosticSignature',
    'private string\? _lastFolderDragDiagnosticSignature',
    'private Border\? _folderDropTarget',
    'private bool _surfaceDragCompletionHandled',
    'private bool _isFileDropSubclassInstalled'
)

$fieldLines = @()
foreach ($pattern in $fieldPatterns) {
    for ($i = 0; $i -lt $totalLines; $i++) {
        if ($lines[$i] -match $pattern) {
            $fieldLines += $lines[$i]
            $linesToRemove.Add($i) | Out-Null
            # Also remove trailing blank line if the next line is blank
            if ($i + 1 -lt $totalLines -and $lines[$i + 1] -match "^\s*$") {
                # Don't remove blank line - it might be between other fields
            }
            Write-Host "Found field: $($lines[$i].Trim())"
            break
        }
    }
}

# Write the new DragDrop.cs file
$usingLines = @(
    "// Copyright (c) DeskBox. All rights reserved.",
    "",
    "using DeskBox.Helpers;",
    "using DeskBox.Models;",
    "using DeskBox.Services;",
    "using Microsoft.UI;",
    "using Microsoft.UI.Xaml;",
    "using Microsoft.UI.Xaml.Controls;",
    "using Microsoft.UI.Xaml.Input;",
    "using Microsoft.UI.Xaml.Media;",
    "using Windows.ApplicationModel.DataTransfer;",
    "using WinRT.Interop;",
    "",
    "namespace DeskBox.Views;",
    "",
    "/// <summary>",
    "/// Partial class containing drag-and-drop, file drop subclass,",
    "/// folder drop target, and item drag package logic for WidgetWindow.",
    "/// </summary>",
    "public sealed partial class WidgetWindow",
    "{"
)

# Write fields first
$output = [System.Collections.Generic.List[string]]::new()
foreach ($line in $usingLines) {
    $output.Add($line)
}
$output.Add("")

# Write fields
foreach ($field in $fieldLines) {
    $output.Add($field)
}
$output.Add("")

# Write methods in original order
$sortedBlocks = $methodBlocks | Sort-Object { $_.StartLine }
foreach ($block in $sortedBlocks) {
    foreach ($line in $block.Lines) {
        $output.Add($line)
    }
    $output.Add("")
}

$output.Add("}")

# Write the new file
$outputPath = "src/DeskBox/Views/WidgetWindow.DragDrop.cs"
$output -join "`r`n" | Set-Content $outputPath -Encoding UTF8 -NoNewline
Write-Host "`nCreated: $outputPath ($($output.Count) lines)"

# Write the modified source file (removing extracted lines)
$newLines = [System.Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt $totalLines; $i++) {
    if (-not $linesToRemove.Contains($i)) {
        $newLines.Add($lines[$i])
    }
}

# Clean up multiple consecutive blank lines (more than 2)
$cleanedLines = [System.Collections.Generic.List[string]]::new()
$blankCount = 0
foreach ($line in $newLines) {
    if ($line -match "^\s*$") {
        $blankCount++
        if ($blankCount -le 2) {
            $cleanedLines.Add($line)
        }
    } else {
        $blankCount = 0
        $cleanedLines.Add($line)
    }
}

$cleanedLines -join "`r`n" | Set-Content $SourceFile -Encoding UTF8 -NoNewline
Write-Host "Modified: $SourceFile (removed $($linesToRemove.Count) lines, $($cleanedLines.Count) lines remaining)"
