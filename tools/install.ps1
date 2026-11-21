# Собирает LiteBrowser в один exe, ставит в %LOCALAPPDATA%\Programs\LiteBrowser
# и создаёт ярлыки на рабочем столе и в меню «Пуск». Права администратора не нужны.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $env:LOCALAPPDATA 'Programs\LiteBrowser'

dotnet publish "$root\LiteBrowser.csproj" -c Release -o "$root\publish" -nologo -v q
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$root\publish\LiteBrowser.exe" $dest -Force

$exe = Join-Path $dest 'LiteBrowser.exe'
$shell = New-Object -ComObject WScript.Shell
foreach ($dir in [Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs')) {
    $lnk = $shell.CreateShortcut((Join-Path $dir 'LiteBrowser.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $dest
    $lnk.Description = 'LiteBrowser — лёгкий и безопасный браузер'
    $lnk.IconLocation = "$exe,0"
    $lnk.Save()
}
"Установлено: $exe"
