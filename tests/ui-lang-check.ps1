<#
  Lists console UI texts without a Ukrainian translation (src\Cst\Ui\Lang.uk.cs) and translations no text uses.
    tests\ui-lang-check.ps1            # exit 1 when a text has no translation
    tests\ui-lang-check.ps1 -Keys      # prints every key the UI uses
  Keys: literals passed to T("...") / F("...") in src\Cst\Ui, the Option("name", "note") and Header(target, "step")
  texts of the import wizard, element folder titles (ElementFolders.Title) and stage names (Log.Stage / ImportStages).
#>
param([switch]$Keys)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot '..\src\Cst'
$lit = '"((?:[^"\\]|\\.)*)"'
$used = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
function Add-Matches($text, $pattern) { foreach ($m in [regex]::Matches($text, $pattern)) { for ($i = 1; $i -lt $m.Groups.Count; $i++) { [void]$used.Add($m.Groups[$i].Value) } } }

foreach ($f in Get-ChildItem (Join-Path $src 'Ui') -Filter *.cs | Where-Object { $_.Name -ne 'Lang.uk.cs' }) {
    $t = [IO.File]::ReadAllText($f.FullName)
    Add-Matches $t "(?<![\w.])(?:L\.)?[TF]\($lit"
    Add-Matches $t "Option\($lit, $lit\)"
    Add-Matches $t "Header\(target, $lit\)"
}
$folders = [IO.File]::ReadAllText((Join-Path $src 'Creatio\ElementFolders.cs'))
Add-Matches ([regex]::Match($folders, '(?s)Title\(.*').Value) "=> $lit"
Add-Matches ([IO.File]::ReadAllText((Join-Path $src 'Transfer\Importer.cs'))) "(?:Check|Packages|SqlBefore|Schemas|DbStructure|Compile|Content|SqlAfter|Data|SqlAfterData|Files) = $lit"
Add-Matches ([IO.File]::ReadAllText((Join-Path $src 'Transfer\Restorer.cs'))) "Log\.Stage\($lit\)"

if ($Keys) { $used; return }

$uk = [IO.File]::ReadAllText((Join-Path $src 'Ui\Lang.uk.cs'))
$have = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($m in [regex]::Matches($uk, "(?m)^\s*\[$lit\]\s*=")) { [void]$have.Add($m.Groups[1].Value) }

$missing = @($used | Where-Object { -not $have.Contains($_) })
$unused = @($have | Where-Object { -not $used.Contains($_) })
"UI texts: $($used.Count), Ukrainian: $($have.Count), missing: $($missing.Count), unused: $($unused.Count)"
if ($missing.Count) { 'Missing:'; $missing | ForEach-Object { "  $_" } }
if ($unused.Count) { 'Unused:'; $unused | ForEach-Object { "  $_" } }
if ($missing.Count) { exit 1 }
