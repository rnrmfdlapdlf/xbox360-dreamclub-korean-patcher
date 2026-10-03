param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$buildTime = Get-Date
$buildVersion = '{0}.{1}.{2}.{3}' -f ($buildTime.Year % 100), $buildTime.Month, $buildTime.Day, ([int][Math]::Floor($buildTime.TimeOfDay.TotalSeconds / 2))
$displayVersion = 'v' + $buildTime.ToString('yyMMdd')
$source = @"
using System.Reflection;
[assembly: AssemblyVersion("$buildVersion")]
[assembly: AssemblyFileVersion("$buildVersion")]
[assembly: AssemblyInformationalVersion("$displayVersion")]
"@
$versionPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($versionPath)) | Out-Null
[IO.File]::WriteAllText($versionPath, $source, (New-Object Text.UTF8Encoding($false)))
