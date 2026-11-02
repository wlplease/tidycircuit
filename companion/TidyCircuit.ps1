#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='High')]
param(
  [ValidateSet('Report','Plan','Quarantine','Restore')][string]$Action='Report',
  [string]$Id,
  [int]$MinAgeDays=7
)
$ErrorActionPreference='Stop'
if($MinAgeDays -lt 1 -or $MinAgeDays -gt 365){throw 'MinAgeDays must be 1–365.'}
$stateRoot=Join-Path $env:LOCALAPPDATA 'TidyCircuit'
$vault=Join-Path $stateRoot 'Quarantine'
$manifest=Join-Path $stateRoot 'operations.jsonl'
New-Item -ItemType Directory -Force -Path $vault | Out-Null
function Write-Record($entry){$entry | ConvertTo-Json -Compress -Depth 6 | Add-Content -LiteralPath $manifest -Encoding UTF8}
function Get-Candidates {
  $temp=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
  $cutoff=(Get-Date).AddDays(-$MinAgeDays)
  Get-ChildItem -LiteralPath $temp -File -Recurse -Force -ErrorAction SilentlyContinue | Where-Object {
    -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $_.LastWriteTime -lt $cutoff -and $_.FullName.StartsWith($temp+'\',[StringComparison]::OrdinalIgnoreCase)
  } | Select-Object FullName,Length,LastWriteTime
}
function Get-Report {
  $os=Get-CimInstance Win32_OperatingSystem
  $battery=Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
  $disks=Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { [ordered]@{name=$_.DeviceID;sizeBytes=[long]$_.Size;freeBytes=[long]$_.FreeSpace} }
  $candidates=@(Get-Candidates)
  [ordered]@{schema='tidycircuit.report.v1';createdAt=(Get-Date).ToUniversalTime().ToString('o');computer=$env:COMPUTERNAME;os=$os.Caption;memory=@{totalBytes=[long]$os.TotalVisibleMemorySize*1024;freeBytes=[long]$os.FreePhysicalMemory*1024};battery=if($battery){@{chargePercent=$battery.EstimatedChargeRemaining;status=$battery.BatteryStatus}}else{$null};drives=@($disks);cleanup=@{scope='Current-user TEMP files only';minimumAgeDays=$MinAgeDays;candidateCount=$candidates.Count;candidateBytes=($candidates|Measure-Object Length -Sum).Sum}} | ConvertTo-Json -Depth 6
}
switch($Action){
 'Report' {Get-Report}
 'Plan' {@(Get-Candidates) | ConvertTo-Json -Depth 4}
 'Quarantine' {
   if(-not $Id){throw 'Provide the exact full file path with -Id.'}
   $target=[IO.Path]::GetFullPath($Id)
   $temp=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
   if(-not $target.StartsWith($temp+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only files inside the current-user TEMP directory are eligible.'}
   if(-not (@(Get-Candidates | Where-Object { $_.FullName -eq $target }).Count)){throw 'File is not in the current cleanup plan.'}
   $file=Get-Item -LiteralPath $target -Force
   if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.LastWriteTime -ge (Get-Date).AddDays(-$MinAgeDays)){throw 'File is ineligible, linked, or too recent.'}
   $operationId=[guid]::NewGuid().ToString('N')
   $destination=Join-Path $vault $operationId
   if($PSCmdlet.ShouldProcess($target,'Move to TidyCircuit quarantine')){
     Move-Item -LiteralPath $target -Destination $destination -ErrorAction Stop
     Write-Record ([ordered]@{id=$operationId;action='quarantine';time=(Get-Date).ToUniversalTime().ToString('o');original=$target;quarantine=$destination;bytes=$file.Length})
     [ordered]@{id=$operationId;original=$target;quarantine=$destination} | ConvertTo-Json
   }
 }
 'Restore' {
   if(-not $Id){throw 'Provide the operation ID with -Id.'}
   if($Id -notmatch '^[a-fA-F0-9]{32}$'){throw 'Invalid operation ID.'}
   $entry=Get-Content -LiteralPath $manifest -ErrorAction Stop | ForEach-Object {$_|ConvertFrom-Json} | Where-Object {$_.id -eq $Id -and $_.action -eq 'quarantine'} | Select-Object -Last 1
   if(-not $entry){throw 'Operation not found.'}
   if(Test-Path -LiteralPath $entry.original){throw 'Original path is occupied; restore manually from quarantine.'}
   $source=Join-Path $vault $Id
   if(-not (Test-Path -LiteralPath $source)){throw 'Quarantined file is missing.'}
   if($PSCmdlet.ShouldProcess($entry.original,'Restore quarantined file')){
     Move-Item -LiteralPath $source -Destination $entry.original -ErrorAction Stop
     Write-Record ([ordered]@{id=$Id;action='restore';time=(Get-Date).ToUniversalTime().ToString('o');original=$entry.original})
     [ordered]@{id=$Id;restored=$entry.original} | ConvertTo-Json
   }
 }
}
