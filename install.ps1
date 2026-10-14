# Installs Taskbar Calendar for the current user: a private Python environment, Start menu and
# start-with-Windows shortcuts, then launches it. Safe to run again to repair or update.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

function Find-Python {
    foreach ($cmd in @('py -3', 'python')) {
        $exe, $arg = $cmd -split ' ', 2
        if (Get-Command $exe -ErrorAction SilentlyContinue) {
            $version = & $exe $arg -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null
            if ($LASTEXITCODE -eq 0 -and [version]$version -ge [version]'3.10') { return @($exe, $arg) }
        }
    }
    throw "Python 3.10 or newer is required. Install it from https://www.python.org/downloads/ (tick 'Add python.exe to PATH') and run this again."
}

# Stop a running copy so its files can be replaced.
Get-CimInstance Win32_Process -Filter "Name='pythonw.exe'" |
    Where-Object { $_.CommandLine -like '*TaskbarCalendar.pyw*' -or $_.CommandLine -like '*taskbar_calendar.pyw*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

$venv = Join-Path $dir '.venv'
if (-not (Test-Path (Join-Path $venv 'Scripts\pythonw.exe'))) {
    $exe, $arg = Find-Python
    Write-Host 'Creating Python environment...'
    & $exe $arg -m venv $venv
}
$python = Join-Path $venv 'Scripts\python.exe'
$pythonw = Join-Path $venv 'Scripts\pythonw.exe'
Write-Host 'Installing dependencies...'
& $python -m pip install --quiet --disable-pip-version-check -r (Join-Path $dir 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Installing dependencies failed.' }

$launcher = Join-Path $dir 'TaskbarCalendar.pyw'
$shell = New-Object -ComObject WScript.Shell
$programs = [Environment]::GetFolderPath('Programs')
foreach ($folder in @($programs, [Environment]::GetFolderPath('Startup'))) {
    $link = $shell.CreateShortcut((Join-Path $folder 'Taskbar Calendar.lnk'))
    $link.TargetPath = $pythonw
    $link.Arguments = '"' + $launcher + '"'
    $link.WorkingDirectory = $dir
    $link.Description = 'Next meeting, Claude usage and network speed on the taskbar'
    $link.IconLocation = Join-Path $dir 'assets\icon.ico'
    $link.Save()
}

Start-Process $pythonw -ArgumentList ('"' + $launcher + '"') -WorkingDirectory $dir
Write-Host ''
Write-Host 'Taskbar Calendar is running and will start when you sign in.'
Write-Host 'Settings open automatically the first time. Later: right-click the widget > Settings,'
Write-Host 'or open "Taskbar Calendar" from the Start menu.'
