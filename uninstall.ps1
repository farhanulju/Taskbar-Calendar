# Removes Taskbar Calendar's shortcuts, stops it, and removes its Claude Code status line (if it added one).
# Your settings are kept unless you pass -DeleteSettings.
#
#   powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-DeleteSettings]
param([switch]$DeleteSettings)
$dir = $PSScriptRoot

Get-CimInstance Win32_Process -Filter "Name='pythonw.exe'" |
    Where-Object { $_.CommandLine -like '*TaskbarCalendar.pyw*' -or $_.CommandLine -like '*taskbar_calendar.pyw*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

foreach ($folder in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Startup'))) {
    Remove-Item (Join-Path $folder 'Taskbar Calendar.lnk') -ErrorAction SilentlyContinue
}

$python = Join-Path $dir '.venv\Scripts\python.exe'
if (Test-Path $python) {
    Push-Location $dir
    & $python -c "from taskbar_calendar.claude_usage import remove_statusline; remove_statusline()"
    Pop-Location
}

if ($DeleteSettings) {
    Remove-Item (Join-Path $env:APPDATA 'TaskbarCalendar') -Recurse -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA 'TaskbarCalendar') -Recurse -ErrorAction SilentlyContinue
    Write-Host 'Settings and data deleted.'
}
Write-Host 'Taskbar Calendar is uninstalled. You can now delete this folder.'
