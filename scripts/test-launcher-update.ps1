param([string]$Package = (Join-Path $PSScriptRoot '../build/update-check-package'))
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path (Join-Path $repoRoot 'build') ('updater-integration-' + [guid]::NewGuid().ToString('N'))
$package = [IO.Path]::GetFullPath($Package)
New-Item -ItemType Directory -Path $testRoot | Out-Null
Get-ChildItem -LiteralPath $package | Copy-Item -Destination $testRoot -Recurse
$testExe = Join-Path $testRoot 'CreateLauncher.exe'
$workspace = Join-Path $testRoot ('data/launcher-updates/' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $workspace 'app'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Get-ChildItem -LiteralPath $package | Copy-Item -Destination $stage -Recurse
Set-Content -LiteralPath (Join-Path $testRoot 'README.md') -Value 'old application file' -NoNewline
Set-Content -LiteralPath (Join-Path $stage 'README.md') -Value 'updated application file' -NoNewline
$configuration = Get-Content -LiteralPath (Join-Path $testRoot 'launcher.json') -Raw
$settings = '{"nickname":"UpdateCheck","memoryMb":7425}'
Set-Content -LiteralPath (Join-Path $testRoot 'data/settings.json') -Value $settings -NoNewline
$files = @('CreateLauncher.exe', 'README.md') + @(Get-ChildItem -LiteralPath $stage -Filter '*.dll' | ForEach-Object Name)
$files | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workspace 'files.json')
$parent = $null
$helper = $null
try {
    $parent = Start-Process -FilePath $testExe -ArgumentList @('--settings', '--ui-check') -WindowStyle Hidden -PassThru
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 200
        $parent.Refresh()
        if ($parent.HasExited) { throw 'Test launcher exited before update' }
        if ($deadline.Elapsed.TotalSeconds -gt 15) { throw 'Test launcher did not open' }
    } while ($parent.MainWindowHandle -eq 0)
    $helper = Start-Process -FilePath (Join-Path $stage 'CreateLauncher.exe') -ArgumentList @('--apply-launcher-update', $parent.Id, $testRoot) -WindowStyle Hidden -PassThru
    $deadline.Restart()
    while (!(Test-Path -LiteralPath (Join-Path $workspace 'ready'))) {
        Start-Sleep -Milliseconds 100
        if ($helper.HasExited -or $deadline.Elapsed.TotalSeconds -gt 15) { throw 'Update helper did not become ready' }
    }
    if (!(Test-Path -LiteralPath (Join-Path $testRoot 'data/settings.json'))) { throw 'Settings missing before replacement' }
    if (!$parent.CloseMainWindow()) { throw 'Could not close isolated launcher normally' }
    if (!$parent.WaitForExit(15000)) { throw 'Original test launcher did not exit' }
    if (!$helper.WaitForExit(20000)) { throw 'Update helper did not exit' }
    if ($helper.ExitCode -ne 0) { throw "Update helper failed: $($helper.ExitCode)" }
    $deadline.Restart()
    do {
        Start-Sleep -Milliseconds 200
        $restarted = @(Get-Process -Name CreateLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $testExe -and $_.Id -ne $parent.Id })
        if ($deadline.Elapsed.TotalSeconds -gt 15) { throw 'Updated launcher did not restart' }
    } while (!$restarted -or $restarted[0].MainWindowHandle -eq 0)
    if ((Get-Content -LiteralPath (Join-Path $testRoot 'README.md') -Raw) -ne 'updated application file') { throw 'Application files were not updated' }
    if ((Get-Content -LiteralPath (Join-Path $testRoot 'launcher.json') -Raw) -ne $configuration) { throw 'Launcher configuration changed' }
    if ((Get-Content -LiteralPath (Join-Path $testRoot 'data/settings.json') -Raw) -ne $settings) { throw 'Player settings changed' }
    if (!(Test-Path -LiteralPath (Join-Path $workspace 'backup/CreateLauncher.exe'))) { throw 'Rollback executable missing' }
    Write-Output "PASS: real EXE helper handoff, normal shutdown, replacement, restart and settings preservation ($testRoot)"
} finally {
    foreach ($process in @(Get-Process -Name CreateLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $testExe -or $_.Path -eq (Join-Path $stage 'CreateLauncher.exe') })) {
        $process.CloseMainWindow() | Out-Null
        if (!$process.WaitForExit(5000)) { Stop-Process -Id $process.Id }
    }
}
