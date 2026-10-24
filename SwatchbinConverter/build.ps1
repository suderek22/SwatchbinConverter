param(
    [string]$ForzaTechDir = "E:\ForzaTech",
    [Parameter(Mandatory = $true)]
    [string]$StockSwatchbinDir,
    [Parameter(Mandatory = $true)]
    [string]$SourcePngDir,
    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$StockSwatchbinDir = [IO.Path]::GetFullPath($StockSwatchbinDir)
$SourcePngDir = [IO.Path]::GetFullPath($SourcePngDir)
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnetCommand) {
    throw "dotnet.exe is required to run the converter."
}
$runtimeConfig = Join-Path $ForzaTechDir "ForzaTech Studio.runtimeconfig.json"
if (-not (Test-Path -LiteralPath $runtimeConfig)) {
    throw "ForzaTech Studio.runtimeconfig.json was not found under '$ForzaTechDir'."
}

$runtimeRoot = Join-Path (Split-Path -Parent $dotnetCommand.Source) "shared\Microsoft.NETCore.App"
$runtimeConfigData = Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json
$frameworkVersion = [version]$runtimeConfigData.runtimeOptions.framework.version
$runtimeDirectory = Get-ChildItem -LiteralPath $runtimeRoot -Directory |
    Where-Object {
        $version = [version]$_.Name
        $version.Major -eq $frameworkVersion.Major -and
        $version.Minor -eq $frameworkVersion.Minor
    } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if (-not $runtimeDirectory) {
    throw "No Microsoft.NETCore.App runtime was found."
}

$compiler = Get-Command csc.exe -ErrorAction SilentlyContinue
if ($compiler) {
    $compilerPath = $compiler.Source
}
else {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "csc.exe was not found; install the Visual Studio C# compiler."
    }
    $installationPath = & $vswhere -latest -products "*" -property installationPath
    $compilerPath = Join-Path $installationPath "MSBuild\Current\Bin\Roslyn\csc.exe"
    if (-not (Test-Path -LiteralPath $compilerPath)) {
        throw "The Visual Studio Roslyn compiler was not found."
    }
}

$libraryDirectory = Join-Path $ForzaTechDir "libs"
$encoderPath = Join-Path $libraryDirectory "BCnEncoder.dll"
$toolkitPath = Join-Path $libraryDirectory "Microsoft.Toolkit.HighPerformance.dll"
if (-not (Test-Path -LiteralPath $encoderPath) -or
    -not (Test-Path -LiteralPath $toolkitPath)) {
    throw "BCnEncoder or Microsoft.Toolkit.HighPerformance is missing from '$libraryDirectory'."
}

$buildDirectory = Join-Path $env:TEMP "SwatchbinConverter"
New-Item -ItemType Directory -Force -Path $buildDirectory | Out-Null
$outputAssembly = Join-Path $buildDirectory "build_swatchbins.dll"
$excludedAssemblies = @(
    "System.IO.Compression.Native.dll",
    "msquic.dll",
    "mscorrc.dll",
    "mscordbi.dll",
    "mscordaccore_amd64_amd64_9.0.2126.50404.dll",
    "mscordaccore.dll",
    "Microsoft.DiaSymReader.Native.amd64.dll",
    "hostpolicy.dll",
    "coreclr.dll",
    "clrjit.dll",
    "clrgcexp.dll",
    "clrgc.dll",
    "clretwrc.dll"
)
$references = Get-ChildItem -LiteralPath $runtimeDirectory.FullName -Filter "*.dll" |
    Where-Object {
        $_.Name -notin $excludedAssemblies -and $_.Name -notlike "mscordaccore*"
    } |
    ForEach-Object { "/reference:$($_.FullName)" }
$compilerArguments = @(
    "/noconfig",
    "/nostdlib+",
    "/target:exe",
    "/out:$outputAssembly",
    "/reference:$encoderPath",
    "/reference:$toolkitPath"
) + $references + @((Join-Path $projectRoot "build_swatchbins.cs"))

& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Swatchbin converter compilation failed with exit code $LASTEXITCODE."
}

& $dotnetCommand.Source exec --runtimeconfig $runtimeConfig $outputAssembly `
    $ForzaTechDir $StockSwatchbinDir $SourcePngDir $OutputDir
if ($LASTEXITCODE -ne 0) {
    throw "Swatchbin conversion failed with exit code $LASTEXITCODE."
}
