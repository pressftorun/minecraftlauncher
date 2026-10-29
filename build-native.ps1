param(
    [ValidateSet('win-x64', 'linux-x64')][string]$Runtime = 'win-x64',
    [string]$PackArchive,
    [string]$OutputDirectory,
    [switch]$WithoutClient
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'desktop/CreateLauncher.csproj'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory, $PSScriptRoot) } else { Join-Path $PSScriptRoot "dist/native/$Runtime" }
$packages = Join-Path $PSScriptRoot '.nuget/packages'
dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
    --configfile (Join-Path $PSScriptRoot 'NuGet.Config') `
    "-p:RestorePackagesPath=$packages" -o $output
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать лаунчер' }
$includeClient = $PackArchive -and !$WithoutClient
foreach ($document in @('README.md', 'LICENSE', 'NOTICE.md', 'CHANGELOG.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $document) -Destination $output
}
# Use the exact self-contained runtime selected by publish, including its bundled components.
$framework = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.TargetFramework
$runtimeConfig = Get-Content -LiteralPath (Join-Path $PSScriptRoot "desktop/bin/Release/$framework/$Runtime/CreateLauncher.runtimeconfig.json") -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
if (!$runtimeVersion) { throw 'Не найдена версия .NET runtime для лицензионных уведомлений.' }
$runtimePackage = Join-Path $packages "microsoft.netcore.app.runtime.$Runtime/$runtimeVersion"
$notices = Join-Path $output 'NOTICE.md'
foreach ($document in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
    $text = Get-Content -LiteralPath (Join-Path $runtimePackage $document) -Raw -Encoding utf8
    if ([string]::IsNullOrWhiteSpace($text)) { throw "Пустое уведомление .NET: $document" }
    [IO.File]::AppendAllText($notices, "`n`n## Microsoft.NETCore.App $runtimeVersion ($Runtime) / $document`n`n``````text`n$text`n```````n")
}
if ($includeClient) {
    if (!(Test-Path -LiteralPath $PackArchive)) { throw 'Нет client.zip по указанному пути.' }
    Copy-Item -LiteralPath $PackArchive -Destination (Join-Path $output 'client.zip')
}
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$archive = Join-Path $PSScriptRoot "dist/CreateLauncher-$version-$Runtime.zip"
$stream = [IO.File]::Create($archive)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $output -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($output, $file.FullName).Replace('\', '/')
        if ($relative -eq 'CreateLauncher-next.exe' -or $relative.StartsWith('data/') -or $relative.EndsWith('.log') -or $relative.EndsWith('.pdb') -or (!$includeClient -and $relative -eq 'client.zip')) { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose(); $stream.Dispose() }
Get-Item -LiteralPath $archive | Select-Object FullName, Length
