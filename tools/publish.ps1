<#
.SYNOPSIS
    配布用の自己完結型ビルドを dist\ に作る。

.DESCRIPTION
    WinUI 3 は単一ファイル publish に対応していない（.xbf リソースをバンドルから読めず
    XamlParseException で落ちる）ため、ランタイム一式は dist\app\ にまとめ、
    dist\ の直下には起動用のショートカットだけを置く。

.EXAMPLE
    pwsh -File tools\publish.ps1
    pwsh -File tools\publish.ps1 -Runtime win-arm64
    pwsh -File tools\publish.ps1 -StopRunning
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime = 'win-x64',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # 実行中の dist\app\Widgets.exe を終了させてから publish する。
    # 指定しない場合、実行中なら中断する（使用中のファイルを半端に上書きしないため）。
    [switch]$StopRunning,

    # デスクトップのショートカットを作り直さない。
    [switch]$SkipDesktopShortcut
)

$ErrorActionPreference = 'Stop'

# --- .NET 11 SDK の解決 --------------------------------------------------------
# 本体は net11.0 を対象にしている。.NET 11 SDK をユーザー領域 (%USERPROFILE%\.dotnet)
# へ入れた場合 PATH には載らないので、PATH 上の dotnet が古ければそちらへ切り替える。
function Resolve-Dotnet {
    $candidates = @()

    $onPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if ($onPath) { $candidates += $onPath }

    $userLocal = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $userLocal) { $candidates += $userLocal }

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks -match '^11\.') { return $candidate }
    }

    throw ".NET 11 SDK が見つかりません。https://dotnet.microsoft.com/download から入れるか、" +
          "次のコマンドでユーザー領域へ入れてください:`n" +
          "  & ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Channel 11.0 -Quality preview"
}

$dotnet = Resolve-Dotnet
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
Write-Host "dotnet: $dotnet" -ForegroundColor DarkGray

$repoRoot = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repoRoot 'src\Widgets.App\Widgets.App.csproj'
$dist     = Join-Path $repoRoot 'dist'
$appDir   = Join-Path $dist 'app'
$exePath  = Join-Path $appDir 'Widgets.exe'

if (-not (Test-Path $project)) {
    throw "プロジェクトが見つかりません: $project"
}

# --- 実行中インスタンスの確認 ------------------------------------------------
# publish は使用中の DLL を上書きできずに途中で失敗するので、先に必ず確認する。
$running = @(Get-Process -Name 'Widgets' -ErrorAction SilentlyContinue |
             Where-Object { $_.Path -and $_.Path.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase) })

if ($running.Count -gt 0) {
    if (-not $StopRunning) {
        throw "dist の Widgets.exe が実行中です (PID: $($running.Id -join ', '))。終了させてから再実行するか、-StopRunning を付けてください。"
    }

    Write-Host "実行中の Widgets を終了します (PID: $($running.Id -join ', '))" -ForegroundColor Yellow
    $running | Stop-Process -Force
    # ファイルハンドルが解放されるまで待つ。
    for ($i = 0; $i -lt 20 -and (Get-Process -Name 'Widgets' -ErrorAction SilentlyContinue |
                                 Where-Object { $_.Path -and $_.Path.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase) }); $i++) {
        Start-Sleep -Milliseconds 250
    }
}

# --- 旧レイアウトの掃除 -------------------------------------------------------
# 以前は publish 出力を dist 直下にぶちまけていた。exe が 229 個のファイルに埋もれて
# 見つけられないので、残っていれば消してから作り直す。
if (Test-Path $dist) {
    Write-Host "dist を掃除します" -ForegroundColor Cyan
    Get-ChildItem -LiteralPath $dist -Force | Remove-Item -Recurse -Force
}

New-Item -ItemType Directory -Path $appDir -Force | Out-Null

# --- publish ------------------------------------------------------------------
Write-Host "publish: $Configuration / $Runtime -> dist\app" -ForegroundColor Cyan

& $dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $appDir `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish が失敗しました (exit $LASTEXITCODE)"
}

if (-not (Test-Path $exePath)) {
    throw "publish は成功しましたが exe が見つかりません: $exePath"
}

# Widgets.pdb は意図して残す。crash.log のスタックトレースに行番号が載るので、
# ベータ版の不具合報告を追うのに必要。

# --- 起動用ショートカット -----------------------------------------------------
# これが「dist を開いたときに最初に目に入るもの」。
$shortcut = Join-Path $dist 'Widgets.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath       = $exePath
$link.WorkingDirectory = $appDir
$link.IconLocation     = "$exePath,0"
$link.Description      = 'Widgets - デスクトップウィジェット'
$link.Save()

# デスクトップにも同じショートカットを置く。dist を開かずに起動できるようにするため。
if (-not $SkipDesktopShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $desktopLink = Join-Path $desktop 'Widgets.lnk'

    $link2 = $shell.CreateShortcut($desktopLink)
    $link2.TargetPath       = $exePath
    $link2.WorkingDirectory = $appDir
    $link2.IconLocation     = "$exePath,0"
    $link2.Description      = 'Widgets - デスクトップウィジェット'
    $link2.Save()

    Write-Host "デスクトップにショートカットを作成しました: $desktopLink" -ForegroundColor Cyan
}

[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

# --- 案内テキスト -------------------------------------------------------------
$version = ([xml](Get-Content $project)).Project.PropertyGroup.InformationalVersion |
           Where-Object { $_ } | Select-Object -First 1

@"
Widgets $version ($Runtime)

「Widgets」ショートカットをダブルクリックすると起動します。

  Widgets.lnk  … 起動用のショートカット
  app\         … 本体とランタイム一式（触る必要はありません）
  app\Widgets.exe  … 本体。ショートカットが使えない場合はこちらを直接実行してください

.NET のインストールは不要です（自己完結型ビルド）。
設定とウィジェットの定義は %LOCALAPPDATA%\Widgets\widgets.json に保存されます。
"@ | Set-Content -LiteralPath (Join-Path $dist 'はじめに.txt') -Encoding utf8

# --- 結果 ---------------------------------------------------------------------
$size = (Get-ChildItem -LiteralPath $appDir -Recurse -File | Measure-Object -Sum Length).Sum / 1MB

Write-Host ""
Write-Host "完了" -ForegroundColor Green
Write-Host ("  dist\Widgets.lnk   起動用ショートカット")
Write-Host ("  dist\app\          {0:N0} ファイル / {1:N0} MB" -f (Get-ChildItem -LiteralPath $appDir -Recurse -File).Count, $size)
