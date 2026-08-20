param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2

$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$workspace = Split-Path -Parent $projectRoot
$outputRoot = Join-Path $projectRoot "bin\Release"
$assetsRoot = Join-Path $outputRoot "Assets"
$runtimeRoot = Join-Path $outputRoot "Runtime"
$fontsRoot = Join-Path $runtimeRoot "Fonts"
$generatedRoot = Join-Path $projectRoot "obj\ReleaseGenerated"

function Remove-GeneratedDirectory {
    param([string]$Path, [string]$ExpectedParent)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $parent = [IO.Path]::GetFullPath($ExpectedParent)
    if (-not $resolved.StartsWith(
        $parent + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace an unexpected directory: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

Remove-GeneratedDirectory $outputRoot (Join-Path $projectRoot "bin")
Remove-GeneratedDirectory $generatedRoot (Join-Path $projectRoot "obj")
Remove-GeneratedDirectory (Join-Path $projectRoot "dist\DreamClubKoreanPatcher") `
    (Join-Path $projectRoot "dist")

foreach ($directory in @($outputRoot, $assetsRoot, $runtimeRoot, $fontsRoot, $generatedRoot)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    throw ".NET Framework 4.x C# compiler was not found."
}

& node (Join-Path $projectRoot "BuildAssets.js") (Join-Path $workspace "input") $assetsRoot
if ($LASTEXITCODE -ne 0) { throw "Asset packaging failed with exit code $LASTEXITCODE" }

Copy-Item -LiteralPath (Join-Path $workspace "font_220929\title_Medium.ttf") -Destination $fontsRoot
Copy-Item -LiteralPath (Join-Path $workspace "font_220929\title_Bold.ttf") -Destination $fontsRoot
Copy-Item -LiteralPath (Join-Path $workspace "tools\exiso.exe") -Destination $runtimeRoot

$fontSource = Get-Content -Raw -Encoding UTF8 -LiteralPath `
    (Join-Path $workspace "tools-src\DreamClubKoreanPatcher\Program.cs")
$fontSource = $fontSource.Replace(
    "namespace DreamClubKoreanPatcher",
    "namespace DreamClubFontPatcher")
$fontSource = [Regex]::Replace(
    $fontSource,
    '(?s)private const string OriginalWarning\s*=.*?;\s*\r?\n\s*private const string KoreanWarning',
    'private const string OriginalWarning = "";' + "`r`n`r`n        private const string KoreanWarning")
$fontSource = [Regex]::Replace(
    $fontSource,
    'private const string JapaneseCompareText\s*=\s*.*?;',
    'private const string JapaneseCompareText = "";')
$cleanFontSource = Join-Path $generatedRoot "DreamClubFontPatcher.cs"
[IO.File]::WriteAllText($cleanFontSource, $fontSource, (New-Object Text.UTF8Encoding($false)))

$sources = @(
    (Join-Path $projectRoot "Program.cs"),
    (Join-Path $projectRoot "MainForm.cs"),
    (Join-Path $projectRoot "PatchRunner.cs"),
    (Join-Path $projectRoot "PatchPipeline.cs"),
    (Join-Path $projectRoot "RuntimeMetadataBuilder.cs"),
    (Join-Path $projectRoot "Properties\AssemblyInfo.cs"),
    (Join-Path $workspace "tools-src\DefaultExeRelocator\Program.cs"),
    (Join-Path $workspace "tools-src\S00DialoguePatcher\Program.cs"),
    (Join-Path $workspace "tools-src\AllTranslatedContentPatcher\Program.cs"),
    (Join-Path $workspace "tools-src\SafeSupplementalPswPatcher\Program.cs"),
    (Join-Path $workspace "tools-src\SafeSupplementalPswRelocator\Program.cs"),
    $cleanFontSource,
    (Join-Path $workspace "tools-src\AmaneMailPatcher\Program.cs"),
    (Join-Path $workspace "tools-src\DreamClubUiTexturePatcher\Program.cs")
)

$uiBundleSource = Join-Path $projectRoot "Assets\ui_resources.dat"
$uiBundleOutput = Join-Path $assetsRoot "ui_resources.dat"
if (-not (Test-Path -LiteralPath $uiBundleSource -PathType Leaf)) {
    throw "UI resource bundle was not found: $uiBundleSource"
}
[byte[]]$uiBundleHeader = [IO.File]::ReadAllBytes($uiBundleSource)
if ($uiBundleHeader.Length -lt 12 -or
    $uiBundleHeader[0] -ne 0x44 -or $uiBundleHeader[1] -ne 0x43 -or
    $uiBundleHeader[2] -ne 0x52 -or $uiBundleHeader[3] -ne 0x31) {
    throw "UI resource bundle has an invalid header: $uiBundleSource"
}
Copy-Item -LiteralPath $uiBundleSource -Destination $uiBundleOutput

$arguments = @(
    "/nologo", "/target:winexe", "/optimize+",
    "/main:DreamClubKoreanPatcher.Program",
    "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll",
    "/r:System.Windows.Forms.dll", "/r:System.Web.Extensions.dll",
    "/out:$(Join-Path $outputRoot 'DreamClubKoreanPatcher.exe')"
) + $sources
& $csc $arguments
if ($LASTEXITCODE -ne 0) { throw "C# build failed with exit code $LASTEXITCODE" }

Copy-Item -LiteralPath (Join-Path $projectRoot "App.config") `
    -Destination (Join-Path $outputRoot "DreamClubKoreanPatcher.exe.config")
Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination $outputRoot

$unexpectedAssetFiles = Get-ChildItem -LiteralPath $assetsRoot -Recurse -File |
    Where-Object {
        $_.Extension -ne ".jsonl" -and
        -not $_.FullName.Equals(
            $uiBundleOutput, [StringComparison]::OrdinalIgnoreCase)
    }
if (@($unexpectedAssetFiles).Count -ne 0) {
    throw "Assets contains a non-JSONL file."
}
foreach ($asset in Get-ChildItem -LiteralPath $assetsRoot -Recurse -File -Filter "*.jsonl") {
    foreach ($line in Get-Content -Encoding UTF8 -LiteralPath $asset.FullName) {
        if ([String]::IsNullOrWhiteSpace($line)) { continue }
        $row = $line | ConvertFrom-Json
        $keys = @($row.PSObject.Properties.Name | Sort-Object)
        if ($keys.Count -ne 2 -or $keys[0] -ne "id" -or $keys[1] -ne "translation") {
            throw "Asset row contains fields other than id and translation: $($asset.FullName)"
        }
    }
}

foreach ($folder in @(
    (Join-Path $outputRoot "Engine"),
    (Join-Path $outputRoot "Tools"),
    (Join-Path $outputRoot "tools"))) {
    if (Test-Path -LiteralPath $folder) {
        throw "Obsolete folder remains in Release output: $folder"
    }
}

Write-Output "Build complete: $outputRoot"
