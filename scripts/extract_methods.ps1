$file = "src\DeskBox\Views\WidgetWindow.xaml.cs"
$content = [System.IO.File]::ReadAllText((Resolve-Path $file))
$lines = $content -split "`r`n|`r|`n"

$methods = @(
    'StartRename','PrepareRenameEditor','CommitRenameAsync','CancelRename',
    'TitleEditBox_LostFocus','TitleEditBox_KeyDown',
    'StartItemRenameAsync','SelectFilenameWithoutExtension',
    'FocusTextInputEditor','FocusTextInputEditorCore',
    'ItemRenameTextBox_KeyDown','ItemRenameTextBox_LostFocus',
    'CommitItemRenameAsync','CancelItemRename','CompleteItemRename',
    'PositionItemRenameTextBox','FindItemNameElement',
    'ShowStatusToast','StatusToastTimer_Tick',
    'TryShowNativeContextMenu','AddMultiSelectionItems',
    'CreateContentAreaFlyout','AddCurrentWidgetContentActions',
    'CreateMoreFlyout','AddCreateWidgetItems','GetCreateEntryText','CreateNewWidgetFlyout',
    'ShowDeleteItemsConfirmFlyout','TrackMoreFlyoutAnchor',
    'ShowDeleteWidgetFlyout','CreateDeleteWidgetFlyout',
    'CreateDeleteActionItem','CreateCancelDeleteItem',
    'ShowErrorDialogAsync','FormatUserFacingError','TryExtractQuotedPath',
    'ShowFlyoutWithElevation'
)

$ranges = @()

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
    Write-Host "FOUND: $m lines $($startIdx+1)-$($endIdx+1)"
}

# Also remove the field declarations
$fieldsToRemove = @(
    'private WidgetItem\? _itemRenameTarget;',
    'private TextBlock\? _itemRenameNameText;',
    'private bool _isCommittingTitleRename;',
    'private bool _isCommittingItemRename;',
    'private bool _isCancellingTitleRename;',
    'private bool _isCancellingItemRename;',
    'private MenuFlyout\? _itemDeleteConfirmFlyout;',
    'private Flyout\? _messageFlyout;',
    'private MenuFlyout\? _deleteWidgetFlyout;',
    'private FrameworkElement\? _lastMoreFlyoutTarget;',
    'private Windows\.Foundation\.Point\? _lastMoreFlyoutPosition;',
    'private bool _isDeleteWidgetFlyoutOpen;',
    'private bool _isInlineFlyoutOpen;'
)

$fieldRanges = @()
for ($i = 0; $i -lt $lines.Count; $i++) {
    foreach ($pattern in $fieldsToRemove) {
        if ($lines[$i] -match "^\s*$pattern\s*$") {
            $fieldRanges += ,@($i, $i)
            Write-Host "FIELD: line $($i+1): $($lines[$i].Trim())"
            break
        }
    }
}

# Sort all ranges descending by start line
$allRanges = $ranges + $fieldRanges
$allRanges = $allRanges | Sort-Object { $_[0] } -Descending

# Remove ranges
foreach ($r in $allRanges) {
    $start = $r[0]
    $end = $r[1]
    # Also remove the blank line after the method if present
    if ($end + 1 -lt $lines.Count -and $lines[$end + 1].Trim() -eq "") {
        $end = $end + 1
    }
    if ($end + 1 -lt $lines.Count) {
        $lines = $lines[0..($start - 1)] + $lines[($end + 1)..($lines.Count - 1)]
    } else {
        $lines = $lines[0..($start - 1)]
    }
}

$result = $lines -join "`r`n"
[System.IO.File]::WriteAllText((Resolve-Path $file), $result)
Write-Host "Done. Total lines: $($lines.Count)"
