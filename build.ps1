[CmdletBinding()]
param([switch] $Restore, [string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root '.build'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'bin' }
$version = '1.0.4370.26'
$sdk = Join-Path $build "webview2-$version"
$sdkLibraries = Join-Path $sdk 'lib/net462'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'

Push-Location $root
try {
    New-Item -ItemType Directory -Path $build, (Join-Path $output 'assets'), (Join-Path $output 'licenses') -Force | Out-Null
    if ($Restore -or -not (Test-Path 'node_modules/xlsx-vanilla-viewer/package.json')) {
        npm.cmd ci --no-audit --no-fund --cache (Join-Path $build 'npm-cache')
        if ($LASTEXITCODE) { throw 'npm ci failed.' }
    }

    if (-not (Test-Path (Join-Path $sdkLibraries 'Microsoft.Web.WebView2.WinForms.dll'))) {
        $archive = Join-Path $build "webview2-$version.zip"
        $package = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$version/microsoft.web.webview2.$version.nupkg"
        Invoke-WebRequest -Uri $package -OutFile $archive -TimeoutSec 60
        Expand-Archive -LiteralPath $archive -DestinationPath $sdk -Force
    }

    npm.cmd run bundle -- "--outfile=$(Join-Path $output 'assets/app.js')"
    if ($LASTEXITCODE) { throw 'Frontend bundle failed.' }
    Copy-Item -LiteralPath 'app/web/index.html', 'app/web/app.css' -Destination (Join-Path $output 'assets')

    $dlls = 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll'
    $sources = Get-ChildItem -LiteralPath 'app' -Filter '*.cs' -File | Sort-Object Name | Select-Object -ExpandProperty FullName
    $arguments = @('/nologo', '/utf8output', '/codepage:65001', '/langversion:5', '/platform:x64', '/target:winexe', '/optimize+')
    $arguments += "/out:$(Join-Path $output 'ScdDesktop.exe')"
    $arguments += "/win32manifest:$(Join-Path $root 'app/ScdDesktop.manifest')"
    $arguments += @('System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'Microsoft.CSharp.dll') |
        ForEach-Object { "/reference:$(Join-Path $framework $_)" }
    $arguments += $dlls | ForEach-Object { "/reference:$(Join-Path $sdkLibraries $_)" }
    & $compiler @arguments @sources
    if ($LASTEXITCODE) { throw 'Windows host build failed.' }

    foreach ($dll in $dlls) { Copy-Item -LiteralPath (Join-Path $sdkLibraries $dll) -Destination $output }
    Copy-Item -LiteralPath (Join-Path $sdk 'runtimes/win-x64/native/WebView2Loader.dll') -Destination $output
    Copy-Item -LiteralPath 'app/ScdDesktop.exe.config' -Destination $output
    $licenses = Join-Path $output 'licenses'
    foreach ($package in 'xlsx-vanilla-viewer', 'ooxml-core', 'ooxml-ui', '@christophervr/xlsx-core') {
        $label = $package.Split('/')[-1]
        foreach ($file in 'LICENSE', 'NOTICE', 'THIRD-PARTY-LICENSES') {
            $original = Join-Path "node_modules/$package" $file
            if (Test-Path -LiteralPath $original) {
                Copy-Item -LiteralPath $original -Destination (Join-Path $licenses "$label-$file.txt")
            }
        }
    }
    foreach ($file in 'LICENSE.txt', 'NOTICE.txt') {
        Copy-Item -LiteralPath (Join-Path $sdk $file) -Destination (Join-Path $licenses "WebView2-$file")
    }
    Get-Item -LiteralPath (Join-Path $output 'ScdDesktop.exe')
}
finally {
    Pop-Location
}
