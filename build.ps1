param([Parameter(Mandatory=$true)][string]$RedFruitDir)
$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot 'build'
$payload = Join-Path $build 'payload'
$player = Join-Path $payload 'MiniVideoFloat'
$redfruit = Join-Path $payload 'hongguo-v1.2.17-windows-amd64'
New-Item -ItemType Directory -Force -Path $player,$redfruit | Out-Null
dotnet publish (Join-Path $PSScriptRoot 'player\MiniVideoFloat.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $player
if ($LASTEXITCODE -ne 0) { throw '播放器发布失败' }
foreach ($name in @('红果短剧.exe','ffmpeg.exe','HEVC视频扩展-双击安装.appx','MicrosoftEdgeWebview2Setup.exe','使用说明.txt')) {
    Copy-Item -LiteralPath (Join-Path $RedFruitDir $name) -Destination $redfruit -Force
}
$zip = Join-Path $build 'payload.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip,[System.IO.Compression.CompressionLevel]::Optimal,$false)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$installer = Join-Path $build 'MiniVideoFloat-Setup.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$installer" "/resource:$zip,payload.zip" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:Microsoft.CSharp.dll (Join-Path $PSScriptRoot 'installer\Setup.cs')
if ($LASTEXITCODE -ne 0) { throw '安装程序编译失败' }
Write-Host "安装包：$installer"
