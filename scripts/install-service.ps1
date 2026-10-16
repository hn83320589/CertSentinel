#Requires -RunAsAdministrator
<#
.SYNOPSIS
    建置並安裝 CertSentinel Windows 服務與 certsentinel 命令列工具。

.DESCRIPTION
    1. dotnet publish 服務與 CLI 到同一個資料夾(或使用 -PublishDir 指定已發佈的資料夾)
    2. 複製到安裝目錄(預設 C:\Program Files\CertSentinel)
    3. 建立 %ProgramData%\CertSentinel 並設定權限(關閉繼承,僅 SYSTEM、Administrators、服務帳號可存取)
    4. 首次安裝時複製 appsettings.sample.json 為 %ProgramData%\CertSentinel\appsettings.json
    5. 註冊事件檢視器來源 CertSentinel
    6. 建立(或更新)服務:延遲自動啟動、失敗自動重啟
    7. 啟動服務(設定檔為首次建立時不啟動,需先填寫 SMTP)

    可重複執行;服務已存在時會停止、更新檔案後重新啟動(升級)。

.EXAMPLE
    .\scripts\install-service.ps1
.EXAMPLE
    .\scripts\install-service.ps1 -AddToPath
.EXAMPLE
    .\scripts\install-service.ps1 -PublishDir .\artifacts\publish -NoStart
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'CertSentinel'),

    # 已發佈的資料夾;未指定時從原始碼 dotnet publish
    [string] $PublishDir,

    # 預設自含 .NET Runtime(伺服器不需另裝 .NET 9);指定此參數則改為相依框架(需安裝 .NET 9 Runtime)
    [switch] $FrameworkDependent,

    # 將安裝目錄加入系統 PATH,之後可直接執行 certsentinel
    [switch] $AddToPath,

    [switch] $NoStart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'CertSentinel'
$DisplayName = 'CertSentinel 憑證哨兵'
$Description = '定期檢查網站 SSL/TLS 憑證,快到期或異常時寄信通知。'
$ServiceAccount = 'NT AUTHORITY\LocalService'
$LocalServiceSid = '*S-1-5-19'
$SystemSid = '*S-1-5-18'
$AdministratorsSid = '*S-1-5-32-544'
$DataDir = Join-Path $env:ProgramData 'CertSentinel'
$RepoRoot = Split-Path $PSScriptRoot -Parent

function Invoke-Native {
    param([string] $File, [string[]] $Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File $($Arguments -join ' ') 失敗(結束碼 $LASTEXITCODE)"
    }
}

# 1. 發佈
if (-not $PublishDir) {
    $PublishDir = Join-Path $RepoRoot 'artifacts\publish'
    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
    $mode = if ($FrameworkDependent) { @('--self-contained', 'false') } else { @('--self-contained', 'true') }
    foreach ($project in 'src\CertSentinel.Service', 'src\CertSentinel.Cli') {
        Write-Host "發佈 $project ..."
        Invoke-Native dotnet (@('publish', (Join-Path $RepoRoot $project), '-c', 'Release', '-r', 'win-x64', '-o', $PublishDir, '--nologo', '-v', 'quiet') + $mode)
    }
}
$PublishDir = (Resolve-Path $PublishDir).Path
if (-not (Test-Path (Join-Path $PublishDir 'CertSentinel.Service.exe'))) {
    throw "在 $PublishDir 找不到 CertSentinel.Service.exe"
}

# 2. 停止既有服務並複製檔案
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host '停止既有服務 ...'
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

Write-Host "複製檔案到 $InstallDir ..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $PublishDir '*') -Destination $InstallDir -Recurse -Force

# 3. 資料夾與權限。%ProgramData% 預設允許一般使用者建立資料夾與檔案,因此:
#    - 新建立時:立即關閉繼承並限制權限,接著確認資料夾仍為空(建立與上鎖之間若有人寫入內容就中止)。
#    - 已存在時:只接受先前由本腳本建立的狀態(見 Assert-TrustedDataDir),否則中止並請管理員檢查,
#      絕不沿用或修改來源不明的內容(例如被預先放入的設定檔、資料庫或連結)。
function Assert-TrustedDataDir {
    param([Parameter(Mandatory)] [string] $Path)

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "$Path 是連結(junction/symlink),為安全起見停止安裝。"
    }

    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    $trusted = @($SystemSid, $AdministratorsSid, $LocalServiceSid) | ForEach-Object { $_.TrimStart('*') }
    $untrusted = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.AccessControlType -eq 'Allow' -and $trusted -notcontains $_.IdentityReference.Value })
    if ($owner -notin $trusted[0..1] -or -not $acl.AreAccessRulesProtected -or $untrusted.Count -gt 0) {
        throw ("$Path 已存在,但不是由本安裝腳本建立的安全狀態(擁有者 $owner、繼承" +
            $(if ($acl.AreAccessRulesProtected) { '已關閉' } else { '未關閉' }) +
            ",其他可存取者 $($untrusted.Count) 個)。請檢查其中內容,確認無誤後刪除或備份該資料夾再重新安裝。")
    }

    # -ErrorAction Stop:無法列舉時視為不安全(fail closed)。
    $links = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -Attributes ReparsePoint -ErrorAction Stop)
    if ($links.Count -gt 0) {
        throw "$Path 含有連結(junction/symlink):$($links.FullName -join ', ')。為安全起見停止安裝。"
    }
}

if (Test-Path -LiteralPath $DataDir) {
    Assert-TrustedDataDir -Path $DataDir
    Write-Host "沿用既有資料夾 $DataDir(權限檢查通過)"
} else {
    New-Item -ItemType Directory -Path $DataDir | Out-Null   # 已存在(被搶先建立)時會丟出例外
    Invoke-Native icacls @($DataDir, '/setowner', $AdministratorsSid, '/C', '/Q')
    Invoke-Native icacls @($DataDir, '/inheritance:r', '/grant:r',
        "${SystemSid}:(OI)(CI)F", "${AdministratorsSid}:(OI)(CI)F", "${LocalServiceSid}:(OI)(CI)M", '/C', '/Q')
    if (@(Get-ChildItem -LiteralPath $DataDir -Force -ErrorAction Stop).Count -gt 0) {
        throw "建立 $DataDir 期間有其他程序寫入內容,為安全起見停止安裝。請檢查並刪除該資料夾後重試。"
    }

    Assert-TrustedDataDir -Path $DataDir
    Write-Host "已設定 $DataDir 權限(擁有者 Administrators;SYSTEM、Administrators 完全控制;LocalService 修改)"
}

# 4. 設定檔
$configPath = Join-Path $DataDir 'appsettings.json'
$configCreated = $false
if (-not (Test-Path $configPath)) {
    Copy-Item (Join-Path $RepoRoot 'appsettings.sample.json') $configPath
    $configCreated = $true
    Write-Host "已建立設定檔 $configPath,請填寫 SMTP 設定與收件人。" -ForegroundColor Yellow
}

# 5. 事件檢視器來源
if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, 'Application')
    Write-Host "已註冊事件來源 $ServiceName(Windows 記錄 → 應用程式)"
}

# 6. 建立或更新服務
$binPath = '"{0}"' -f (Join-Path $InstallDir 'CertSentinel.Service.exe')
if ($existing) {
    Invoke-Native sc.exe @('config', $ServiceName, 'binPath=', $binPath, 'start=', 'delayed-auto', 'obj=', $ServiceAccount, 'password=', '', 'DisplayName=', $DisplayName)
} else {
    Invoke-Native sc.exe @('create', $ServiceName, 'binPath=', $binPath, 'start=', 'delayed-auto', 'obj=', $ServiceAccount, 'password=', '', 'DisplayName=', $DisplayName)
}
Invoke-Native sc.exe @('description', $ServiceName, $Description)
# 失敗後 1 分鐘重啟(前兩次),一天後重置計數
Invoke-Native sc.exe @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000//0')
Write-Host "已設定服務 $ServiceName(帳號 $ServiceAccount,延遲自動啟動)"

if ($AddToPath) {
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    if (($machinePath -split ';') -notcontains $InstallDir) {
        [Environment]::SetEnvironmentVariable('Path', "$machinePath;$InstallDir", 'Machine')
        Write-Host "已將 $InstallDir 加入系統 PATH(重新開啟終端機後生效)"
    }
}

# 7. 啟動
if ($NoStart) {
    Write-Host '略過啟動(-NoStart)。'
} elseif ($configCreated) {
    Write-Host "設定檔為首次建立,請先編輯 $configPath 再執行:Start-Service $ServiceName" -ForegroundColor Yellow
} else {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    Write-Host "服務 $ServiceName 已啟動。" -ForegroundColor Green
}

Write-Host ''
Write-Host "CLI:`"$(Join-Path $InstallDir 'certsentinel.exe')`" status"
