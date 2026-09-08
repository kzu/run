#Requires -Version 7
<#
.SYNOPSIS
  Plan, pack, and push file-based tool packages (pointer + RID-specific).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('plan', 'rid', 'pointer', 'push')]
    [string]$Mode,

    [string]$Rid,
    [string]$PlanPath = 'pack-plan.json',
    [string]$Artifacts = 'artifacts',
    [string]$Configuration = $(if ($env:Configuration) { $env:Configuration } else { 'Release' }),
    [switch]$PushNuGet,
    [string]$NuGetApiKey = $env:NUGET_API_KEY,
    [string]$SleetConnection = $env:SLEET_CONNECTION,
    [string]$SleetFeedUrl = $env:SLEET_FEED_URL
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RidRunners = [ordered]@{
    'linux-x64'   = 'ubuntu-latest'
    'linux-arm64' = 'ubuntu-24.04-arm'
    'win-x64'     = 'windows-latest'
    'win-arm64'   = 'windows-11-arm'
    'osx-x64'     = 'macos-15-intel'
    'osx-arm64'   = 'macos-latest'
}

function Get-RepoRoot {
    if ($PSScriptRoot) {
        return (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    }
    return (Get-Location).Path
}

function Write-GitHubOutput([string]$Name, [string]$Value) {
    if (-not $env:GITHUB_OUTPUT) {
        return
    }
    Add-Content -Path $env:GITHUB_OUTPUT -Value "$Name=$Value"
}

function ConvertTo-JsonArray($Value) {
    if ($null -eq $Value -or @($Value).Count -eq 0) {
        return '[]'
    }
    return (@($Value) | ConvertTo-Json -Compress -Depth 8 -AsArray)
}

function Split-Rids([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return @()
    }
    return @($Value.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Test-HasPackageId([string]$File) {
    $head = Get-Content -Path $File -TotalCount 40 -ErrorAction SilentlyContinue
    return [bool]($head | Select-String -Pattern '^#:property\s+PackageId=' -Quiet)
}

function Get-FilePackPlan([string]$File) {
    $raw = & dotnet build $File -nologo -t:GetPackPlan --getItem:PackPlan
    if ($LASTEXITCODE -ne 0) {
        throw "GetPackPlan failed for $File (exit $LASTEXITCODE)."
    }

    $text = @($raw) -join "`n"
    $start = $text.IndexOf('{')
    $end = $text.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) {
        return $null
    }

    $json = $text.Substring($start, $end - $start + 1) | ConvertFrom-Json
    $items = @($json.Items.PackPlan)
    if ($items.Count -eq 0 -or [string]::IsNullOrWhiteSpace($items[0].PackageId)) {
        return $null
    }

    $item = $items[0]
    return [ordered]@{
        file             = $item.Identity
        packageId        = $item.PackageId
        publishAot       = "$($item.PublishAot)" -eq 'true'
        rids             = @(Split-Rids $item.ToolPackageRuntimeIdentifiers)
        targetFramework  = "$($item.TargetFramework)"
    }
}

function Add-MatrixEntry($Matrix, [string]$Os, [string]$RidValue) {
    $key = "$Os|$RidValue"
    if ($Matrix.Contains($key)) {
        return
    }
    $Matrix[$key] = [ordered]@{ os = $Os; rid = $RidValue }
}

function Invoke-Plan {
    $root = Get-RepoRoot
    Push-Location $root
    try {
        $files = @()
        $matrix = [ordered]@{}

        foreach ($cs in Get-ChildItem -File -Filter *.cs | Sort-Object Name) {
            if (-not (Test-HasPackageId $cs.Name)) {
                continue
            }

            $plan = Get-FilePackPlan $cs.Name
            if ($null -eq $plan) {
                continue
            }

            $files += $plan
            $rids = @($plan.rids)
            $tfm = $plan.targetFramework

            if ($rids.Count -eq 0) {
                if ($tfm -match '-windows') {
                    Add-MatrixEntry $matrix 'windows-latest' 'classic'
                }
                continue
            }

            if (-not $plan.publishAot) {
                continue
            }

            foreach ($rid in $rids) {
                if ($rid -eq 'any') {
                    continue
                }
                $os = $RidRunners[$rid]
                if (-not $os) {
                    throw "No GitHub runner mapping for RID '$rid' ($($plan.file))."
                }
                Add-MatrixEntry $matrix $os $rid
            }
        }

        $matrixItems = @($matrix.Values)
        $document = [ordered]@{
            files  = @($files)
            matrix = $matrixItems
        }

        $planFullPath = Join-Path $root $PlanPath
        $document | ConvertTo-Json -Depth 8 | Set-Content -Path $planFullPath -Encoding utf8
        Write-Host "Wrote $planFullPath ($($files.Count) packable file(s), $($matrixItems.Count) RID job(s))."

        $hasRidPacks = $matrixItems.Count -gt 0
        Write-GitHubOutput 'has-rid-packs' ($(if ($hasRidPacks) { 'true' } else { 'false' }))
        Write-GitHubOutput 'matrix' (ConvertTo-JsonArray $matrixItems)
    }
    finally {
        Pop-Location
    }
}

function Read-Plan {
    $root = Get-RepoRoot
    $path = Join-Path $root $PlanPath
    if (-not (Test-Path $path)) {
        throw "Pack plan not found: $path"
    }
    return Get-Content -Raw $path | ConvertFrom-Json
}

function Invoke-DotnetPack([string]$File, [string[]]$ExtraArgs) {
    $args = @('pack', $File, '-c', $Configuration, '-o', $Artifacts, '--nologo')
    $base = [IO.Path]::GetFileNameWithoutExtension($File)
    $suffix = 'pointer'
    if ($ExtraArgs -contains '-r') {
        $rIndex = [Array]::IndexOf($ExtraArgs, '-r')
        if ($rIndex -ge 0 -and $rIndex + 1 -lt $ExtraArgs.Count) {
            $suffix = $ExtraArgs[$rIndex + 1]
        }
    }
    $args += "-bl:$base.$suffix.binlog"
    $args += $ExtraArgs

    Write-Host "dotnet $($args -join ' ')"
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for $File (exit $LASTEXITCODE)."
    }
}

function Invoke-RidPack {
    if ([string]::IsNullOrWhiteSpace($Rid)) {
        throw "-Rid is required for mode 'rid'."
    }

    $root = Get-RepoRoot
    Push-Location $root
    try {
        New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
        $plan = Read-Plan

        foreach ($file in @($plan.files)) {
            $rids = @($file.rids)
            if ($Rid -eq 'classic') {
                if ($rids.Count -eq 0 -and $file.targetFramework -match '-windows') {
                    Invoke-DotnetPack $file.file @()
                }
                continue
            }

            if ($file.publishAot -and $rids -contains $Rid) {
                Invoke-DotnetPack $file.file @('-r', $Rid)
            }
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-PointerPack {
    $root = Get-RepoRoot
    Push-Location $root
    try {
        New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
        $plan = Read-Plan

        foreach ($file in @($plan.files)) {
            $rids = @($file.rids)
            if ($rids.Count -eq 0 -and $file.targetFramework -match '-windows') {
                continue
            }

            Invoke-DotnetPack $file.file @()

            if ($rids -contains 'any') {
                Invoke-DotnetPack $file.file @('-r', 'any', '-p:PublishAot=false')
            }
        }
    }
    finally {
        Pop-Location
    }
}

function Read-NupkgMetadata([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.Name -like '*.nuspec' } | Select-Object -First 1
        if (-not $entry) {
            throw "No nuspec in $Path"
        }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try {
            $xml = [xml]$reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $types = @($xml.package.metadata.packageTypes.packageType) | ForEach-Object { $_.name }
        return [pscustomobject]@{
            Path    = $Path
            Id      = $xml.package.metadata.id
            Version = $xml.package.metadata.version
            Types   = @($types)
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Invoke-Push {
    $root = Get-RepoRoot
    Push-Location $root
    try {
        $plan = Read-Plan
        $nupkgs = @(Get-ChildItem -Path $Artifacts -Filter *.nupkg -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike '*.symbols.nupkg' })
        if ($nupkgs.Count -eq 0) {
            throw "No nupkgs found in $Artifacts."
        }

        $meta = @($nupkgs | ForEach-Object { Read-NupkgMetadata $_.FullName })
        $runtime = @($meta | Where-Object { $_.Types -contains 'DotnetToolRidPackage' })
        $pointer = @($meta | Where-Object { $_.Types -contains 'DotnetTool' -and $_.Types -notcontains 'DotnetToolRidPackage' })

        foreach ($file in @($plan.files)) {
            $id = $file.packageId
            $ptr = @($pointer | Where-Object { $_.Id -eq $id })
            if ($ptr.Count -eq 0) {
                throw "Missing pointer package '$id'."
            }

            foreach ($rid in @($file.rids)) {
                $ridId = "$id.$rid"
                $pkg = @($runtime | Where-Object { $_.Id -eq $ridId })
                if ($pkg.Count -eq 0) {
                    throw "Missing RID package '$ridId'."
                }
            }
        }

        $ordered = @($runtime + $pointer)
        Write-Host "Packages: $($runtime.Count) RID, $($pointer.Count) pointer."

        if ($PushNuGet) {
            if ([string]::IsNullOrWhiteSpace($NuGetApiKey)) {
                throw 'NUGET_API_KEY is required for nuget.org push.'
            }
            foreach ($pkg in $ordered) {
                Write-Host "nuget push $($pkg.Id) $($pkg.Version) ($($pkg.Types -join ';'))"
                & dotnet nuget push $pkg.Path -s https://api.nuget.org/v3/index.json -k $NuGetApiKey --skip-duplicate
                if ($LASTEXITCODE -ne 0) {
                    throw "nuget push failed for $($pkg.Path)"
                }
            }
        }

        if ($SleetConnection) {
            $sleetVersion = $null
            if ($SleetFeedUrl) {
                try {
                    $sleetVersion = (Invoke-RestMethod -Uri $SleetFeedUrl).'sleet:version'
                }
                catch {
                    Write-Warning "Could not read sleet:version from $SleetFeedUrl"
                }
            }
            if ($sleetVersion) {
                & dotnet tool update sleet -g --allow-downgrade --version $sleetVersion
            }
            else {
                & dotnet tool update sleet -g --allow-downgrade
            }
            if ($LASTEXITCODE -ne 0) {
                throw 'dotnet tool update sleet failed.'
            }

            $runtimeDir = Join-Path ([IO.Path]::GetTempPath()) 'runtime-packages'
            $pointerDir = Join-Path ([IO.Path]::GetTempPath()) 'pointer-packages'
            New-Item -ItemType Directory -Force -Path $runtimeDir, $pointerDir | Out-Null
            if ($runtime.Count -gt 0) {
                Copy-Item $runtime.Path -Destination $runtimeDir
                & sleet push $runtimeDir --config none -f --verbose `
                    -p "SLEET_FEED_CONTAINER=nuget" `
                    -p "SLEET_FEED_CONNECTIONSTRING=$SleetConnection" `
                    -p "SLEET_FEED_TYPE=azure"
                if ($LASTEXITCODE -ne 0) {
                    throw 'sleet push of RID packages failed.'
                }
            }
            Copy-Item $pointer.Path -Destination $pointerDir
            & sleet push $pointerDir --config none -f --verbose `
                -p "SLEET_FEED_CONTAINER=nuget" `
                -p "SLEET_FEED_CONNECTIONSTRING=$SleetConnection" `
                -p "SLEET_FEED_TYPE=azure"
            if ($LASTEXITCODE -ne 0) {
                throw 'sleet push of pointer packages failed.'
            }
        }
    }
    finally {
        Pop-Location
    }
}

switch ($Mode) {
    'plan' { Invoke-Plan }
    'rid' { Invoke-RidPack }
    'pointer' { Invoke-PointerPack }
    'push' { Invoke-Push }
}
