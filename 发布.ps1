param(
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\CodexUsageWidget\CodexUsageWidget.csproj'
$output = Join-Path $PSScriptRoot 'artifacts\publish'

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(),
    '-p:PublishSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $output
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "发布失败，dotnet 退出码：$LASTEXITCODE"
}

Write-Host "已发布：$(Join-Path $output 'CodexUsageWidget.exe')"
