param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [Parameter(Mandatory=$true)][string]$PayloadRoot,
    [string]$BackupRoot = (Join-Path $env:TEMP ('LSImmersiveLife-Dispatch-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path (Join-Path $GameRoot 'GTA5_Enhanced.exe'))) { throw 'GTA V Enhanced executable was not found at GameRoot.' }
if (Get-Process -Name 'GTA5_Enhanced','GTA5' -ErrorAction SilentlyContinue) { throw 'Close GTA before deploying.' }
$scripts = Join-Path $GameRoot 'scripts'
$data = Join-Path $scripts 'LSImmersiveLife'
$files = @('LSImmersiveLife.dll', 'LSImmersiveLife/XML/LSPDDispatchEvent.xml', 'LSImmersiveLife/XML/LSPDCriminalProfile.xml', 'LSImmersiveLife/ResponseAudio.xml')
foreach ($file in $files) {
    if (!(Test-Path (Join-Path $PayloadRoot $file))) { throw "Payload file missing: $file" }
}
# Back up every destination before the first replacement. No source/catalog
# is removed; shared SHVDN, LemonUI, NAudio and saved Police profiles stay local.
foreach ($file in ($files + @('LSImmersiveMainUI.xml'))) {
    $existing = Join-Path $scripts $file
    if (Test-Path $existing) {
        $backup = Join-Path $BackupRoot $file
        New-Item -ItemType Directory -Force -Path (Split-Path $backup) | Out-Null
        Copy-Item -LiteralPath $existing -Destination $backup
    }
}
function Load-Xml([string]$Path) {
    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $true
    $doc.XmlResolver = $null
    $doc.Load($Path)
    return ,$doc
}
function Merge-Nodes($Destination, $Source, [string]$ParentPath, [string]$ChildName, [string[]]$OnlyIds = @()) {
    $destParent = $Destination.SelectSingleNode($ParentPath)
    $sourceParent = $Source.SelectSingleNode($ParentPath)
    if ($null -eq $destParent -or $null -eq $sourceParent) { throw "Unexpected XML structure: $ParentPath" }
    foreach ($node in $sourceParent.SelectNodes($ChildName)) {
        $id = $node.GetAttribute('id')
        if ($OnlyIds.Count -gt 0 -and $id -notin $OnlyIds) { continue }
        $old = @($destParent.SelectNodes($ChildName)) | Where-Object { $_.GetAttribute('id') -eq $id } | Select-Object -First 1
        $new = $Destination.ImportNode($node, $true)
        if ($null -ne $old) { [void]$destParent.ReplaceChild($new, $old) }
        else { [void]$destParent.AppendChild($new) }
    }
}
# Stage merged data in the backup directory; validate every merge before copying.
$staging = Join-Path $BackupRoot 'merged-payload'
New-Item -ItemType Directory -Force -Path $staging | Out-Null
foreach ($file in $files) {
    $sourcePath = Join-Path $PayloadRoot $file
    $target = Join-Path $scripts $file
    $staged = Join-Path $staging $file
    New-Item -ItemType Directory -Force -Path (Split-Path $staged) | Out-Null
    if ($file -like '*.dll' -or !(Test-Path $target)) { Copy-Item $sourcePath $staged; continue }
    $dest = Load-Xml $target
    $source = Load-Xml $sourcePath
    if ($file -like '*LSPDDispatchEvent.xml') {
        Merge-Nodes $dest $source '/LSPDDispatchEventCatalog/Events' 'Event'
        Merge-Nodes $dest $source '/LSPDDispatchEventCatalog/Branches' 'Branch'
    } elseif ($file -like '*LSPDCriminalProfile.xml') {
        Merge-Nodes $dest $source '/LSPDCriminalProfiles/Profiles' 'Profile' @('dispatch_naked_subject','dispatch_knife_assailant')
        Merge-Nodes $dest $source '/LSPDCriminalProfiles/AssetPools' 'Pool' @('dispatch_naked_peds','dispatch_knife_weapons')
    } else {
        Merge-Nodes $dest $source '/LSImmersiveLifeAudio' 'Group' @('dispatch_activity_expansion')
        $oldAliases = $dest.SelectSingleNode('/LSImmersiveLifeAudio/IncidentAliases')
        if ($null -eq $oldAliases) { [void]$dest.DocumentElement.AppendChild($dest.CreateElement('IncidentAliases')) }
        # Alias identity uses type, so merge without replacing unrelated aliases.
        foreach ($alias in $source.SelectNodes('/LSImmersiveLifeAudio/IncidentAliases/Alias')) {
            $parent = $dest.SelectSingleNode('/LSImmersiveLifeAudio/IncidentAliases')
            $old = @($parent.SelectNodes('Alias')) | Where-Object { $_.GetAttribute('type') -eq $alias.GetAttribute('type') } | Select-Object -First 1
            $new = $dest.ImportNode($alias,$true)
            if ($null -ne $old) { [void]$parent.ReplaceChild($new,$old) } else { [void]$parent.AppendChild($new) }
        }
    }
    $dest.Save($staged)
}
$configPath = Join-Path $scripts 'LSImmersiveMainUI.xml'
if (Test-Path $configPath) {
    $config = Load-Xml $configPath
    $dispatch = $config.SelectSingleNode('//Police/Dispatch')
    if ($null -eq $dispatch) { throw 'Saved Dispatch settings were not found; update maximumSuspects to 5 manually before deploying.' }
    if ([int]$dispatch.GetAttribute('maximumSuspects') -lt 5) { $dispatch.SetAttribute('maximumSuspects','5') }
    $config.Save((Join-Path $staging 'LSImmersiveMainUI.xml'))
}
foreach ($file in ($files + @('LSImmersiveMainUI.xml'))) {
    $staged = Join-Path $staging $file
    if (!(Test-Path $staged)) { continue }
    $target = Join-Path $scripts $file
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath $staged -Destination $target -Force
}
Write-Host "Dispatch payload deployed. Backup and merged files: $BackupRoot"
Write-Host 'GTA observation is still required. No shared dependencies, location data, Crime Activity, Convoy source or saved Profile was replaced.'
