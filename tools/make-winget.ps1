# Создаёт манифест winget для опубликованного релиза (файл full.exe с GitHub).
# Запуск: powershell -ExecutionPolicy Bypass -File tools\make-winget.ps1 -Version 3.4.0
# Результат — папка winget\manifests\... Её можно отправить в github.com/microsoft/winget-pkgs (pull request),
# после проверки Microsoft браузер ставится командой: winget install LiteBrowser.LiteBrowser
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
$id = 'LiteBrowser.LiteBrowser'
$url = "https://github.com/sasiskax27/LiteBrowser/releases/download/v$Version/LiteBrowser-$Version-full.exe"
$tmp = Join-Path $env:TEMP "LiteBrowser-$Version-full.exe"
Write-Host "Скачиваю $url"
Invoke-WebRequest $url -OutFile $tmp
$sha = (Get-FileHash $tmp -Algorithm SHA256).Hash
$dir = Join-Path $PSScriptRoot "..\winget\manifests\l\LiteBrowser\LiteBrowser\$Version"
New-Item -ItemType Directory -Force $dir | Out-Null

@"
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: ru-RU
ManifestType: version
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $dir "$id.yaml") -Encoding utf8

@"
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: portable
Commands:
  - litebrowser
Installers:
  - Architecture: x64
    InstallerUrl: $url
    InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $dir "$id.installer.yaml") -Encoding utf8

@"
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: ru-RU
Publisher: LiteBrowser
PublisherUrl: https://github.com/sasiskax27
PackageName: LiteBrowser
PackageUrl: https://github.com/sasiskax27/LiteBrowser
License: Proprietary
ShortDescription: Лёгкий, быстрый и безопасный браузер на WebView2 со встроенным ИИ
Tags:
  - browser
  - webview2
  - adblock
  - privacy
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $dir "$id.locale.ru-RU.yaml") -Encoding utf8

Remove-Item $tmp
Write-Host "Готово: $dir (SHA256 $sha)"
