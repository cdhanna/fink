<#
    Makes double-clicking a .fink file open the newest build of Fink.

    It publishes tools/FinkLauncher to %LOCALAPPDATA%\Fink\launcher, tells it where this repo is,
    and registers .fink for the current user (no admin). The launcher runs a copy of the newest
    build in src/Fink.App/bin, so a running Fink never stops you building. Just build as usual:
    the next double-click picks the new build up.

        tools/install-launcher.ps1              install (again, after changing the launcher)
        tools/install-launcher.ps1 -Uninstall   remove the .fink association and the launcher
#>
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Fink\launcher'
$exe = Join-Path $dest 'FinkLauncher.exe'
$classes = 'HKCU:\Software\Classes'

if ($Uninstall) {
    foreach ($key in "$classes\.fink", "$classes\Fink.Project") {
        if (Test-Path $key) { Remove-Item $key -Recurse -Force }
    }
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Write-Host 'Removed the .fink association and the launcher. (Copies of builds stay in %LOCALAPPDATA%\Fink\builds.)'
}
else {
    dotnet publish (Join-Path $repo 'tools\FinkLauncher') -c Release -o $dest --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Building the launcher failed.' }
    @{ repo = $repo } | ConvertTo-Json | Out-File (Join-Path $dest 'launcher.json') -Encoding utf8

    New-Item "$classes\.fink" -Force | Out-Null
    Set-ItemProperty "$classes\.fink" '(default)' 'Fink.Project'
    New-Item "$classes\Fink.Project\DefaultIcon" -Force | Out-Null
    Set-ItemProperty "$classes\Fink.Project" '(default)' 'Fink project'
    Set-ItemProperty "$classes\Fink.Project\DefaultIcon" '(default)' "`"$exe`",0"
    New-Item "$classes\Fink.Project\shell\open\command" -Force | Out-Null
    Set-ItemProperty "$classes\Fink.Project\shell\open\command" '(default)' "`"$exe`" `"%1`""
    Write-Host "Installed. Double-clicking a .fink file now runs the newest build from $repo\src\Fink.App\bin."
    Write-Host "Launcher: $exe (pin it, or make a shortcut, to start Fink the same way without a file)."
}

# tell Explorer, so icons and the association update without signing out
Add-Type -Namespace Win32 -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, int f, IntPtr a, IntPtr b);'
[Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
