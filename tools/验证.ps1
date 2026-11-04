. (Join-Path $PSScriptRoot '环境.ps1')
Push-Location (Split-Path $PSScriptRoot)
try {
    $taskDotnet = Get-NetOpsDotnet
    & $taskDotnet restore NetOpsStudio.slnx --locked-mode --artifacts-path artifacts/验证构建
    if ($LASTEXITCODE -ne 0) { throw '依赖锁定检查失败。' }
    & $taskDotnet build NetOpsStudio.slnx -c Release --no-restore --artifacts-path artifacts/验证构建
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    & $taskDotnet artifacts/验证构建/bin/NetOps.Tests/release/NetOps.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw '功能验证失败。' }
} finally { Pop-Location }
