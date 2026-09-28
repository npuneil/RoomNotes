[CmdletBinding()]
param([Parameter(Mandatory=$true)][int]$AppProcessId)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$process = Get-Process -Id $AppProcessId
$script:window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
if (-not $window) { throw 'RoomNotes has no native window.' }
function Find-Control([string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $deadline = (Get-Date).AddSeconds(5)
    do {
        $control = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($control) { break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    if (-not $control) { throw "UI control not found: $Id" }
    return $control
}
function Invoke-Control([string]$Id) {
    $control = Find-Control $Id
    if (-not $control.Current.IsEnabled) { throw "$Id is disabled." }
    ([System.Windows.Automation.InvokePattern]$control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 200
}
function Wait-Ready {
    $deadline = (Get-Date).AddSeconds(150)
    do {
        $status = (Find-Control 'StatusText').Current.Name
        if ($status -like 'Analysis complete.*') { return }
        if ($status -like 'Operation failed*' -or $status -like '*timed out*') { throw "UI analysis failed: $status" }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw "UI analysis timed out: $status"
}
function New-Session {
    Invoke-Control 'NewButton'
    Start-Sleep -Milliseconds 250
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Discard session')
    $discard = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($discard) {
        ([System.Windows.Automation.InvokePattern]$discard.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Milliseconds 250
    }
}
$readyDeadline = (Get-Date).AddSeconds(150)
while (-not (Find-Control 'NewButton').Current.IsEnabled) {
    if ((Get-Date) -ge $readyDeadline) { throw 'The app did not become idle before UI verification.' }
    Start-Sleep -Milliseconds 300
}
$templates = @(
    @{Name='Legal consultation';Field='client_name'},
    @{Name='Patient consultation';Field='patient'},
    @{Name='Board session';Field='meeting_title'}
)
foreach ($template in $templates) {
    New-Session
    $picker = Find-Control 'TemplatePicker'
    $expand = [System.Windows.Automation.ExpandCollapsePattern]$picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 200
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, [string]$template.Name)
    $options = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $selected = $false
    foreach($option in $options) {
        $pattern = $null
        if($option.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
            $selected = $true
            break
        }
    }
    if(-not $selected) { throw "Template option not selectable: $($template.Name)" }
    $expand.Collapse()
    Start-Sleep -Milliseconds 250
    Invoke-Control 'SampleButton'
    Invoke-Control 'AnalyzeButton'
    Wait-Ready
    $stats = (Find-Control 'FormStats').Current.Name
    if ($stats -notlike '6 / 6*') { throw "$($template.Name): $stats" }
    if ((Find-Control 'ExportButton').Current.IsEnabled) { throw 'Export was enabled before review.' }
    $review = [System.Windows.Automation.TogglePattern](Find-Control 'ReviewCheck').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $review.Toggle()
    Start-Sleep -Milliseconds 150
    if (-not (Find-Control 'ExportButton').Current.IsEnabled) { throw 'Reviewed form cannot export.' }
    $review.Toggle()
    if($template.Name -eq 'Legal consultation') {
        $field = [System.Windows.Automation.ValuePattern](Find-Control $template.Field).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $field.SetValue('Cedar Works - human reviewed')
        Invoke-Control 'AnalyzeButton'
        Wait-Ready
        if ($field.Current.Value -ne 'Cedar Works - human reviewed') { throw 'Re-analysis overwrote a manual edit.' }
        if ((Find-Control 'ExportButton').Current.IsEnabled) { throw 'Edit/re-analysis did not invalidate review.' }
    }
    Write-Output "PASS UI: $($template.Name), 6/6 fields, review/export gates."
}
Write-Output 'PASS UI: all templates, manual-edit preservation, no first-run operation error. Board draft left open.'
