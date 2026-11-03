$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Get-NetOpsDotnet {
    $taskLocalSdk = Join-Path $env:LOCALAPPDATA 'NetOpsStudio\dotnet\dotnet.exe'
    $taskCandidates = @($taskLocalSdk)
    if (Get-Command dotnet -ErrorAction SilentlyContinue) { $taskCandidates += (Get-Command dotnet).Source }
    foreach ($taskCandidate in $taskCandidates) {
        if (Test-Path -LiteralPath $taskCandidate) {
            $taskVersion = & $taskCandidate --version 2>$null
            if ($LASTEXITCODE -eq 0 -and $taskVersion -match '^10\.') {
                $env:DOTNET_ROOT = Split-Path $taskCandidate
                return $taskCandidate
            }
        }
    }
    throw '未找到 .NET 10 SDK。请从 https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0 安装 Windows x64 SDK 后重试。'
}
