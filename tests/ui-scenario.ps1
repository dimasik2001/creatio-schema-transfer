<#
  Runs the cst console UI with scripted keys and writes a text transcript of every frame (tests\Cst.UiScenario).
    tests\ui-scenario.ps1 -Settings <appsettings.json> -Scenario <file.keys> -Out <transcript.txt> [-Width 120] [-Height 40]
  Passwords come from appsettings.json or CST_PASSWORD_<ENV> variables, as for cst itself.
  Scenarios: tests\ui-scenarios\*.keys
#>
param(
    [Parameter(Mandatory)][string]$Settings,
    [Parameter(Mandatory)][string]$Scenario,
    [Parameter(Mandatory)][string]$Out,
    [int]$Width = 120,
    [int]$Height = 40
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Cst.UiScenario'
# Own output folder: a cst.exe running from src\Cst\bin (someone using the UI) must not block this build.
$outDir = Join-Path $project 'out'
dotnet build $project -v q -nologo -o $outDir -p:UseAppHost=true | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build of $project failed" }
$exe = Join-Path $outDir 'Cst.UiScenario.exe'
& $exe $Settings $Scenario $Out $Width $Height
