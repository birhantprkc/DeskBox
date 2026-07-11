param(
    [string]$StepName,
    [string]$MethodList,
    [string]$Usings
)

$file = "src\DeskBox\Views\WidgetWindow.xaml.cs"
$newFile = "src\DeskBox\Views\WidgetWindow.$StepName.cs"
$content = [System.IO.File]::ReadAllText((Resolve-Path $file))
$lines = $content -split "`r`n|`r|`n"
$methods = $MethodList -split ',' | ForEach-Object { $_.Trim() }

$ranges = @()
$extractedBlocks = @()

foreach ($m in $methods) {
    if ([string]::IsNullOrWhiteSpace($m)) { continue }
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
        Write-Host "NO END BRACE: $m (start=$($startIdx+1))"
        continue
    }
    $ranges += ,@($startIdx, $endIdx)
    $extractedBlocks += ,($lines[$startIdx..$endIdx] -join "`r`n")
    Write-Host "FOUND: $m lines $($startIdx+1)-$($endIdx+1)"
}

# Build the new file
$header = "// Copyright (c) DeskBox. All rights reserved.`r`n`r`n"
$header += $Usings
$header += "`r`nnamespace DeskBox.Views;`r`n`r`n"
$header += "public sealed partial class WidgetWindow`r`n{`r`n"

$body = ($extractedBlocks -join "`r`n`r`n")
$newContent = $header + "    " + ($body -replace "`r`n", "`r`n    ") + "`r`n}`r`n"

[System.IO.File]::WriteAllText((Resolve-Path ".").Path + "\" + $newFile.Replace("/", "\"), $newContent)
Write-Host "Created $newFile with $($extractedBlocks.Count) methods"

# Remove from main file
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
$result = $lines -join "`r`n"
[System.IO.File]::WriteAllText((Resolve-Path $file), $result)
Write-Host "Done. Total lines: $($lines.Count)"
