param([Parameter(Mandatory=$true)][string]$RedFruitDir)
$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot 'build'
$payload = Join-Path $build 'payload'
$player = Join-Path $payload 'MiniVideoFloat'
$redfruit = Join-Path $payload 'hongguo-v1.2.17-windows-amd64'
$search = Join-Path $player 'search'
$sdkDir = Join-Path $build 'hongguo-sdk'
New-Item -ItemType Directory -Force -Path $player,$redfruit,$search,$sdkDir | Out-Null
$sdkArchive = Join-Path $sdkDir 'openhongguo-cli_v0.2.0_windows_amd64.zip'
if (-not (Test-Path -LiteralPath $sdkArchive)) {
    Invoke-WebRequest -Uri 'https://github.com/beilunyang/openhongguo/releases/download/v0.2.0/openhongguo-cli_v0.2.0_windows_amd64.zip' -OutFile $sdkArchive
}
if ((Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne '08d0c1c274a26bed3fbb7ae0d07b90ff44d4c5599a12184e73282f8600f9b560') { throw '搜索组件校验失败' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sdkZip = [System.IO.Compression.ZipFile]::OpenRead($sdkArchive)
try {
    foreach ($name in @('ohg.exe','LICENSE')) {
        $entry = $sdkZip.GetEntry("openhongguo-cli_v0.2.0_windows_amd64/$name")
        if ($null -eq $entry) { throw '搜索组件内容不完整' }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $search $name), $true)
    }
} finally { $sdkZip.Dispose() }
dotnet publish (Join-Path $PSScriptRoot 'player\MiniVideoFloat.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $player
if ($LASTEXITCODE -ne 0) { throw '播放器发布失败' }
foreach ($name in @('红果短剧.exe','ffmpeg.exe','HEVC视频扩展-双击安装.appx','MicrosoftEdgeWebview2Setup.exe','使用说明.txt')) {
    Copy-Item -LiteralPath (Join-Path $RedFruitDir $name) -Destination $redfruit -Force
}
$zip = Join-Path $build 'payload.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip,[System.IO.Compression.CompressionLevel]::Optimal,$false)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$installer = Join-Path $build 'MiniVideoFloat-Setup.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$installer" "/resource:$zip,payload.zip" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:Microsoft.CSharp.dll (Join-Path $PSScriptRoot 'installer\Setup.cs')
if ($LASTEXITCODE -ne 0) { throw '安装程序编译失败' }
Write-Host "安装包：$installer"
