param([string]$数据目录)
. (Join-Path $PSScriptRoot '环境.ps1')
$taskRoot = Split-Path $PSScriptRoot
Push-Location $taskRoot
try {
    $taskDotnet = Get-NetOpsDotnet
    $taskAppArgs = @()
    if ($数据目录) { $taskAppArgs = @('--workspace', [System.IO.Path]::GetFullPath($数据目录)) }
    & $taskDotnet run --project 'src/NetOps.Desktop' -c Release -- @taskAppArgs
    if ($LASTEXITCODE -ne 0) { throw '应用启动失败，请查看上方输出。' }
} finally { Pop-Location }
