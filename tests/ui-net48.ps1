<#
  Dev helper: runs the Debug net48 build of cst (src\Cst.Lib\bin\Debug) inside Windows PowerShell 5.1,
  the same way transfer\cst-ps\run.ps1 does. Without arguments it opens the console UI.
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\ui-net48.ps1 [-Settings <appsettings.json>] [cst args]
#>
param([string]$Settings, [Parameter(ValueFromRemainingArguments = $true)][string[]]$CstArgs)
$ErrorActionPreference = 'Stop'
$dir = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'src\Cst.Lib\bin\Debug'
$script:cstDir = $dir
$resolve = [ResolveEventHandler] {
    param($s, $e)
    $n = ($e.Name -split ',')[0]
    foreach ($a in [System.AppDomain]::CurrentDomain.GetAssemblies()) { if ($a.GetName().Name -eq $n) { return $a } }
    $p = [System.IO.Path]::Combine($script:cstDir, "$n.dll")
    if ([System.IO.File]::Exists($p)) { return [System.Reflection.Assembly]::LoadFrom($p) }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolve)
try {
    Get-ChildItem $dir -Filter *.dll | Where-Object Name -ne 'cst.dll' | ForEach-Object { [void][System.Reflection.Assembly]::LoadFrom($_.FullName) }
    [void][System.Reflection.Assembly]::LoadFrom((Join-Path $dir 'cst.dll'))
    $all = @($CstArgs | Where-Object { $_ })
    if ($Settings) { $all += @('--settings', $Settings) }
    $code = [Cst.CstEntry]::Run([string[]]$all)
}
finally { [System.AppDomain]::CurrentDomain.remove_AssemblyResolve($resolve) }
Write-Host "exit=$code"
