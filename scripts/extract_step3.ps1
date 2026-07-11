$file = "src\DeskBox\Views\WidgetWindow.xaml.cs"
$newFile = "src\DeskBox\Views/WidgetWindow.ItemSurface.cs"
$content = [System.IO.File]::ReadAllText((Resolve-Path $file))
$lines = $content -split "`r`n|`r|`n"

$methods = @(
    'ApplySurfaceStyle','UpdateInteractiveSurfaces',
    'ApplyWidgetItemSurfaceState','ResetItemSurfaceBrushCache',
    'EnsureItemSurfaceBrushCache',
    'WidgetItemSurface_Loaded','WidgetItemSurface_Unloaded',
    'WidgetItemSurface_PointerEntered','WidgetItemSurface_PointerExited',
    'WidgetItemSurface_PointerPressed','WidgetItemSurface_PointerReleased',
    'WidgetItemSurface_PointerCaptureLost',
    'WidgetItemSurface_DragStarting','WidgetItemSurface_DropCompleted'
)

$ranges = @()
$extractedBlocks = @()

foreach ($m in $methods) {
    $startIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "private.*\b$m\b") {
            $startIdx = $i
            break
        }
    }
    if ($startIdx -eq -1) {
        Write-Host "NOT FOUND: $m"
        continue
    }

    $braceCount = 0
    $endIdx = -1
    $foundOpen = $false
    for ($i = $startIdx; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $opens = ([regex]::Matches($line, '\{')).Count
        $closes = ([regex]::Matches($line, '\}')).Count
        if ($opens -gt 0) { $foundOpen = $true }
        $braceCount += $opens - $closes
        if ($foundOpen -and $braceCount -le 0 -and $i -gt $startIdx) {
            $endIdx = $i
            break
        }
    }
    if ($endIdx -eq -1) {
        Write-Host "NO END BRACE: $m (start=$startIdx)"
        continue
    }
    $ranges += ,@($startIdx, $endIdx)
    
    # Extract the method body
    $methodLines = $lines[$startIdx..$endIdx]
    $extractedBlocks += ,$methodLines
    Write-Host "FOUND: $m lines $($startIdx+1)-$($endIdx+1) ($($endIdx-$startIdx+1) lines)"
}

# Build the new partial class file
$header = @"
// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views;

/// <summary>
/// Partial class containing widget item surface styling, brush cache, and pointer state logic.
/// </summary>
public sealed partial class WidgetWindow
{
    private enum ItemSurfaceState
    {
        Normal,
        Hover,
        Pressed,
        DropTarget
    }

    private SolidColorBrush? _normalItemSurfaceBrush;
    private SolidColorBrush? _selectedItemSurfaceBrush;
    private SolidColorBrush? _hoverItemSurfaceBrush;
    private SolidColorBrush? _pressedItemSurfaceBrush;
    private SolidColorBrush? _selectedHoverItemSurfaceBrush;
    private SolidColorBrush? _dropTargetItemSurfaceBrush;
    private SolidColorBrush? _normalItemBorderBrush;
    private SolidColorBrush? _dropTargetItemBorderBrush;
    private bool? _itemSurfaceBrushesAreDark;
    private Windows.UI.Color? _itemSurfaceBrushesAccentColor;

"@

$body = ($extractedBlocks | ForEach-Object { $_ -join "`r`n" + "    " }) -join "`r`n    `r`n    "
# Actually, let's just join them properly
$bodyParts = @()
foreach ($block in $extractedBlocks) {
    $bodyParts += ($block -join "`r`n")
}
$body = $bodyParts -join "`r`n`r`n"

$footer = "`r`n}`r`n"

$newContent = $header + "    " + $body + $footer
[System.IO.File]::WriteAllText((Resolve-Path ".").Path + "\" + $newFile.Replace("/", "\"), $newContent)
Write-Host "Created $newFile with $($extractedBlocks.Count) methods"

# Sort descending and remove from main file
$allRanges = $ranges | Sort-Object { $_[0] } -Descending

foreach ($r in $allRanges) {
    $start = $r[0]
    $end = $r[1]
    if ($end + 1 -lt $lines.Count -and $lines[$end + 1].Trim() -eq "") {
        $end = $end + 1
    }
    if ($end + 1 -lt $lines.Count) {
        $lines = $lines[0..($start - 1)] + $lines[($end + 1)..($lines.Count - 1)]
    } else {
        $lines = $lines[0..($start - 1)]
    }
}

# Also remove the enum and fields from main file
$fieldPatterns = @(
    'private enum ItemSurfaceState',
    'private SolidColorBrush\? _normalItemSurfaceBrush',
    'private SolidColorBrush\? _selectedItemSurfaceBrush',
    'private SolidColorBrush? _hoverItemSurfaceBrush',
    'private SolidColorBrush\? _pressedItemSurfaceBrush',
    'private SolidColorBrush\? _selectedHoverItemSurfaceBrush',
    'private SolidColorBrush\? _dropTargetItemSurfaceBrush',
    'private SolidColorBrush\? _normalItemBorderBrush',
    'private SolidColorBrush\? _dropTargetItemBorderBrush',
    'private bool\? _itemSurfaceBrushesAreDark',
    'private Windows\.UI\.Color\? _itemSurfaceBrushesAccentColor'
)

# Remove field/enum lines (and the enum block)
for ($i = $lines.Count - 1; $i -ge 0; $i--) {
    foreach ($pattern in $fieldPatterns) {
        if ($lines[$i] -match $pattern) {
            # Check if it's the enum - need to remove the whole enum block
            if ($pattern -eq 'private enum ItemSurfaceState') {
                # Actually the enum was already declared with "private enum ItemSurfaceState" on the same line
                # Let's just remove lines that match
                $lines[$i] = $null
            } else {
                $lines[$i] = $null
            }
            break
        }
    }
}
$lines = $lines | Where-Object { $_ -ne $null }

$result = $lines -join "`r`n"
[System.IO.File]::WriteAllText((Resolve-Path $file), $result)
Write-Host "Done. Total lines: $($lines.Count)"
