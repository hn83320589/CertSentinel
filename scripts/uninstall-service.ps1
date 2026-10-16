#Requires -RunAsAdministrator
<#
.SYNOPSIS
    解除安裝 CertSentinel 服務。

.DESCRIPTION
    停止並刪除服務、移除事件來源與安裝目錄,並從系統 PATH 移除。
    預設保留 %ProgramData%\CertSentinel(資料庫與設定檔);指定 -RemoveData 才會刪除。

.EXAMPLE
    .\scripts\uninstall-service.ps1
.EXAMPLE
    .\scripts\uninstall-service.ps1 -RemoveData
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'CertSentinel'),
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'CertSentinel'
$DataDir = Join-Path $env:ProgramData 'CertSentinel'

# Windows PowerShell 5.1 的 Remove-Item -Recurse 會跟隨 junction 刪除目標內容;
# 這裡自行遞迴,遇到連結(reparse point)只刪除連結本身,不進入。
function Remove-TreeWithoutFollowingLinks {
    param([Parameter(Mandatory)] [string] $Path)

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($item.FullName, $false) } else { [IO.File]::Delete($item.FullName) }
        return
    }

    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $item.FullName -Force) {
            Remove-TreeWithoutFollowingLinks -Path $child.FullName
        }

        [IO.Directory]::Delete($item.FullName, $false)
    } else {
        $item.Attributes = [IO.FileAttributes]::Normal
        [IO.File]::Delete($item.FullName)
    }
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host '停止服務 ...'
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    if ($PSCmdlet.ShouldProcess($ServiceName, '刪除服務')) {
        & sc.exe delete $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe delete 失敗(結束碼 $LASTEXITCODE)" }
        Write-Host "已刪除服務 $ServiceName"
    }
} else {
    Write-Host "服務 $ServiceName 不存在,略過。"
}

if ([System.Diagnostics.EventLog]::SourceExists($ServiceName) -and $PSCmdlet.ShouldProcess($ServiceName, '移除事件來源')) {
    [System.Diagnostics.EventLog]::DeleteEventSource($ServiceName)
    Write-Host "已移除事件來源 $ServiceName"
}

if ((Test-Path $InstallDir) -and $PSCmdlet.ShouldProcess($InstallDir, '刪除安裝目錄')) {
    Remove-TreeWithoutFollowingLinks -Path $InstallDir
    Write-Host "已刪除 $InstallDir"
}

$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$entries = $machinePath -split ';' | Where-Object { $_ -and $_ -ne $InstallDir }
if (($entries -join ';') -ne $machinePath -and $PSCmdlet.ShouldProcess('系統 PATH', "移除 $InstallDir")) {
    [Environment]::SetEnvironmentVariable('Path', ($entries -join ';'), 'Machine')
    Write-Host "已從系統 PATH 移除 $InstallDir"
}

if ($RemoveData) {
    if ((Test-Path $DataDir) -and $PSCmdlet.ShouldProcess($DataDir, '刪除資料庫與設定檔')) {
        Remove-TreeWithoutFollowingLinks -Path $DataDir
        Write-Host "已刪除 $DataDir"
    }
} else {
    Write-Host "保留資料與設定:$DataDir(加上 -RemoveData 可一併刪除)"
}
