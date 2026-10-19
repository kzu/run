#Requires -Version 7
<#
.SYNOPSIS
  Plan, pack, and push file-based tool packages (pointer + RID-specific).
.PARAMETER ChangedSince
  With -Mode plan, only root .cs files that differ from this commit are planned.
  Forty zeros means a newly created ref. Omit to plan every root script.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('plan', 'rid', 'pointer', 'push')]
    [string]$Mode,

    [string]$ChangedSince,
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

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$ArgumentList
    )

    $hasNativePreference = [bool](Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)
    $savedPreference = if ($hasNativePreference) { $PSNativeCommandUseErrorActionPreference } else { $false }
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        $output = @(& $FileName @ArgumentList)
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Lines    = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        }
    }
    finally {
        if ($hasNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $savedPreference
        }
    }
}

function Select-RootCsFiles($Paths) {
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @($Paths)) {
        if ([string]::IsNullOrWhiteSpace("$path")) {
            continue
        }

        $rel = ("$path" -replace '\\', '/').Trim()
        $leaf = [IO.Path]::GetFileName($rel)
        if ($rel -ne $leaf) {
            continue
        }
        if (-not $leaf.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if (-not (Test-Path -LiteralPath $leaf -PathType Leaf)) {
            continue
        }
        [void]$seen.Add($leaf)
    }

    return @($seen | Sort-Object)
}

function Get-CompareApiPaths([string]$Base) {
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
        throw "Commit $Base is unavailable locally and GITHUB_REPOSITORY is not set."
    }

    $head = $env:GITHUB_SHA
    if ([string]::IsNullOrWhiteSpace($head)) {
        $rev = Invoke-NativeCommand git @('rev-parse', 'HEAD')
        if ($rev.ExitCode -ne 0 -or $rev.Lines.Count -lt 1) {
            throw 'Could not resolve HEAD.'
        }
        $head = $rev.Lines[0]
    }

    $endpoint = "repos/$($env:GITHUB_REPOSITORY)/compare/${Base}...${head}"
    Write-Host "gh api $endpoint"
    $compare = Invoke-NativeCommand gh @(
        'api', $endpoint,
        '--jq', '.files[] | select(.status != "removed" and .status != "unchanged") | .filename'
    )
    if ($compare.ExitCode -ne 0) {
        throw "GitHub compare failed for $Base...$head (exit $($compare.ExitCode))."
    }

    return @($compare.Lines)
}

function Get-DiffNames([string[]]$Revisions) {
    $argumentList = @('diff', '--name-only', '--diff-filter=ACMR') + $Revisions + @('--', '*.cs')
    Write-Host "git $($argumentList -join ' ')"
    return Invoke-NativeCommand git $argumentList
}

function Get-NewRefChangedPaths {
    Write-Host 'New git ref; comparing with origin/main.'
    $fetch = Invoke-NativeCommand git @('fetch', '--no-tags', 'origin', 'main')
    if ($fetch.ExitCode -ne 0) {
        throw "Could not fetch origin/main to plan a newly pushed branch (exit $($fetch.ExitCode))."
    }

    $base = Invoke-NativeCommand git @('rev-parse', 'origin/main')
    $head = Invoke-NativeCommand git @('rev-parse', 'HEAD')
    if ($base.ExitCode -ne 0 -or $head.ExitCode -ne 0 -or $base.Lines.Count -lt 1 -or $head.Lines.Count -lt 1) {
        throw 'Could not resolve origin/main or HEAD.'
    }

    if ($base.Lines[0] -eq $head.Lines[0]) {
        $branch = Invoke-NativeCommand git @('rev-parse', '--abbrev-ref', 'HEAD')
        $name = if ($branch.ExitCode -eq 0 -and $branch.Lines.Count -ge 1) { $branch.Lines[0] } else { '' }
        if ($name -eq 'main') {
            Write-Host 'Initial push of main; planning every root .cs file.'
            return @(Get-ChildItem -File -Filter *.cs | Sort-Object Name | ForEach-Object { $_.Name })
        }

        Write-Host 'New branch matches main; no .cs changes to publish.'
        return @()
    }

    $diff = Get-DiffNames -Revisions @('origin/main...HEAD')
    if ($diff.ExitCode -ne 0) {
        Write-Host 'No merge base with main; diffing the two trees.'
        $diff = Get-DiffNames -Revisions @('origin/main', 'HEAD')
        if ($diff.ExitCode -ne 0) {
            throw "git diff against origin/main failed with exit $($diff.ExitCode)."
        }
    }

    return @($diff.Lines)
}

function Get-PushChangedPaths([string]$Since) {
    $known = Invoke-NativeCommand git @('rev-parse', '--verify', '--quiet', "${Since}^{commit}")
    if ($known.ExitCode -ne 0) {
        Write-Host "Pre-push commit $Since is not in the checkout; fetching it."
        $fetch = Invoke-NativeCommand git @('fetch', '--no-tags', '--depth=1', 'origin', $Since)
        $known = Invoke-NativeCommand git @('rev-parse', '--verify', '--quiet', "${Since}^{commit}")
        if ($fetch.ExitCode -ne 0 -or $known.ExitCode -ne 0) {
            Write-Host "Could not fetch $Since; using the GitHub compare API."
            return @(Get-CompareApiPaths $Since)
        }
    }

    $diff = Get-DiffNames -Revisions @($Since, 'HEAD')
    if ($diff.ExitCode -ne 0) {
        throw "git diff $Since HEAD failed with exit $($diff.ExitCode)."
    }

    return @($diff.Lines)
}

function Get-ChangedRootCsFiles([string]$Since) {
    $root = Get-RepoRoot
    Push-Location $root
    try {
        if ([string]::IsNullOrWhiteSpace($Since)) {
            throw '-ChangedSince is empty. Refusing to plan every root script.'
        }

        $paths = if ($Since -match '^0+$') {
            @(Get-NewRefChangedPaths)
        }
        else {
            @(Get-PushChangedPaths $Since)
        }

        return @(Select-RootCsFiles $paths)
    }
    finally {
        Pop-Location
    }
}

function Test-NeedsPointerPack($Plan) {
    $rids = @($Plan.rids)
    $tfm = "$($Plan.targetFramework)"
    return -not ($rids.Count -eq 0 -and $tfm -match '-windows')
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

function Get-PlanCandidates {
    if (-not $script:LimitToChanged) {
        return @(Get-ChildItem -File -Filter *.cs | Sort-Object Name | ForEach-Object { $_.Name })
    }

    $changed = @(Get-ChangedRootCsFiles -Since $ChangedSince)
    if ($changed.Count -eq 0) {
        Write-Host 'No changed root .cs files.'
    }
    else {
        Write-Host "Changed root .cs files: $($changed -join ', ')"
    }

    return $changed
}

function Invoke-Plan {
    $root = Get-RepoRoot
    Push-Location $root
    try {
        $files = @()
        $matrix = [ordered]@{}
        $hasPointerPacks = $false

        foreach ($cs in @(Get-PlanCandidates)) {
            if (-not (Test-HasPackageId $cs)) {
                continue
            }

            $plan = Get-FilePackPlan $cs
            if ($null -eq $plan) {
                continue
            }

            $files += $plan
            if (Test-NeedsPointerPack $plan) {
                $hasPointerPacks = $true
            }

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
        $packed = @($files | ForEach-Object { $_.file })
        $packedText = if ($packed.Count -eq 0) { 'none' } else { $packed -join ', ' }
        Write-Host "Wrote $planFullPath ($($packed.Count) packable file(s): $packedText; $($matrixItems.Count) RID job(s))."

        $hasRidPacks = $matrixItems.Count -gt 0
        $hasPackages = $files.Count -gt 0
        Write-GitHubOutput 'has-rid-packs' ($(if ($hasRidPacks) { 'true' } else { 'false' }))
        Write-GitHubOutput 'has-packages' ($(if ($hasPackages) { 'true' } else { 'false' }))
        Write-GitHubOutput 'has-pointer-packs' ($(if ($hasPointerPacks) { 'true' } else { 'false' }))
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
            if (-not (Test-NeedsPointerPack $file)) {
                continue
            }

            $rids = @($file.rids)
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

$script:LimitToChanged = $PSBoundParameters.ContainsKey('ChangedSince')

switch ($Mode) {
    'plan' { Invoke-Plan }
    'rid' { Invoke-RidPack }
    'pointer' { Invoke-PointerPack }
    'push' { Invoke-Push }
}
