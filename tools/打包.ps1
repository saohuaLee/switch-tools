. (Join-Path $PSScriptRoot '环境.ps1')
Push-Location (Split-Path $PSScriptRoot)
try {
    $taskDotnet = Get-NetOpsDotnet
    & $taskDotnet publish src/NetOps.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NetOpsPublish=true -p:RestoreLockedMode=true -o artifacts/应用
    if ($LASTEXITCODE -ne 0) { throw '应用打包失败。' }
    Write-Host '已输出到 artifacts\应用\NetOpsStudio.exe，可在无需预装 .NET 的 Windows x64 环境运行。运行兼容性仍需在目标电脑验证。'
} finally { Pop-Location }
