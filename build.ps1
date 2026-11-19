[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $PSBoundParameters.ContainsKey('OutputDirectory')) {
    $OutputDirectory = Join-Path $PSScriptRoot 'release'
}
$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$projectFolder = Join-Path $PSScriptRoot 'LcdWidget'
$project = Join-Path $projectFolder 'LcdWidget.csproj'
[xml] $definition = Get-Content -LiteralPath $project -Raw -Encoding UTF8
$version = $definition.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid release version: $version" }

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$outputRoot = (Resolve-Path -LiteralPath $outputRoot).ProviderPath
$destination = Join-Path $outputRoot "v$version"
if (Test-Path -LiteralPath $destination) {
    throw "Output already exists. Choose a new -OutputDirectory. $destination"
}

$dotnet = (Get-Command dotnet.exe -CommandType Application | Select-Object -First 1).Source
$staging = Join-Path $outputRoot ('.build-' + [Guid]::NewGuid().ToString('N'))
$buildArtifacts = Join-Path $staging 'dotnet'
$portableName = '机核仪表'
$payload = Join-Path $staging $portableName
$result = Join-Path $staging 'result'
$zipName = "LcdWidget-v$version-win-x64.zip"
Push-Location -LiteralPath $PSScriptRoot
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    & $dotnet restore $project -r win-x64 --locked-mode --artifacts-path $buildArtifacts -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed ($LASTEXITCODE)." }
    & $dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore --artifacts-path $buildArtifacts -o $payload -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }

    Copy-Item -LiteralPath (Join-Path $payload 'LcdWidget.exe') -Destination (Join-Path $payload '机核仪表.exe')
    foreach ($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $payload -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/SKIN-ASSETS.md') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/PORTABLE.md') -Destination (Join-Path $payload '使用说明.md')

    $runtimeConfig = Get-Content -LiteralPath (Join-Path $payload 'LcdWidget.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $frameworks = $runtimeConfig.runtimeOptions.includedFrameworks
    if (-not $frameworks) { throw 'The published app is missing its self-contained runtime list.' }

    $restore = Get-Content -LiteralPath (Join-Path $buildArtifacts 'obj/LcdWidget/project.assets.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $packFolders = @($restore.packageFolders.PSObject.Properties.Name) + (Join-Path (Split-Path -Parent $dotnet) 'packs')
    $licenseFolder = Join-Path $payload 'licenses'
    foreach ($framework in $frameworks) {
        $packName = $framework.name + '.Runtime.win-x64'
        $packDirectory = $packFolders | ForEach-Object {
            Join-Path (Join-Path $_ $packName) $framework.version
        } | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (-not $packDirectory) { throw "Runtime pack for $($framework.name) $($framework.version) was not found." }
        $notices = @(Get-ChildItem -LiteralPath $packDirectory -File | Where-Object {
            $_.Name -match '^(LICENSE|THIRD-?PARTY-?NOTICES)(\.txt|\.md)?$'
        })
        if (-not ($notices | Where-Object Name -match '^LICENSE')) { throw "License missing from $packName." }
        foreach ($file in $notices) {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $licenseFolder ($framework.name + '-' + $file.Name))
        }
    }
    $frameworks | ForEach-Object { "$($_.name) $($_.version)" } |
        Set-Content -LiteralPath (Join-Path $payload 'RUNTIME-VERSIONS.txt') -Encoding ASCII

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $result | Out-Null
    $zip = Join-Path $result $zipName
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create, [Text.Encoding]::UTF8)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName) {
            $relativePath = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
            $entryName = "$portableName/$relativePath"
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
    $sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    Set-Content -LiteralPath ($zip + '.sha256') -Value "$sha256  $zipName" -Encoding ASCII

    [IO.Directory]::Move($result, $destination)
    Write-Output (Join-Path $destination $zipName)
    Write-Output (Join-Path $destination ($zipName + '.sha256'))
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $staging) {
        $resolved = (Resolve-Path -LiteralPath $staging).ProviderPath
        if ((Split-Path -Parent $resolved) -eq $outputRoot -and (Split-Path -Leaf $resolved) -match '^\.build-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
