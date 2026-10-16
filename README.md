# CertSentinel 憑證哨兵

[![CI](https://github.com/hn83320589/CertSentinel/actions/workflows/ci.yml/badge.svg)](https://github.com/hn83320589/CertSentinel/actions/workflows/ci.yml)

常駐在 Windows 伺服器上的 SSL/TLS 憑證到期監控服務。定期檢查監控清單中每個網站的憑證，快到期、已過期或連不上時寄信通知，並提供 `certsentinel` 命令列工具管理清單、查詢狀態與歷史。

- **Windows 服務**：.NET 9 Worker Service，可由 SCM 啟動與停止。啟動後立即檢查一輪，之後依間隔重複。
- **CLI**：`add`、`list`、`remove`、`enable`、`disable`、`status`、`check`、`history`，支援 `--json`
- **通知**：狀態惡化、連續無法檢查、恢復 OK 時寄信；Critical、Expired、Error 持續期間每 24 小時提醒一次
- **儲存**：SQLite，服務與 CLI 可同時存取
- **日誌**：寫入 Windows 事件檢視器（應用程式記錄，來源 `CertSentinel`）

---

## 目錄

1. [建置與測試](#建置與測試)
2. [安裝為 Windows 服務](#安裝為-windows-服務)
3. [設定檔說明](#設定檔說明)
4. [CLI 使用方式與範例輸出](#cli-使用方式與範例輸出)
5. [狀態判定規則](#狀態判定規則)
6. [通知規則](#通知規則)
7. [架構說明](#架構說明)
8. [測試與測試憑證](#測試與測試憑證)
9. [設計取捨](#設計取捨)
10. [已知限制](#已知限制)

---

## 建置與測試

需求：.NET SDK 9.0.x、Windows 10/11 或 Windows Server 2016 以上。

```powershell
dotnet build CertSentinel.sln -c Release
dotnet test  CertSentinel.sln -c Release
```

測試全部在本機執行，不需要外部網路。TLS 整合測試會在 `127.0.0.1` 啟動測試伺服器。

---

## 安裝為 Windows 服務

以**系統管理員身分**開啟 PowerShell，在 Repo 根目錄執行：

```powershell
# 1. 建置並安裝（預設自含 .NET Runtime，伺服器不需另裝 .NET）
.\scripts\install-service.ps1 -AddToPath

# 2. 首次安裝會建立設定檔，請填寫 SMTP 與收件人
notepad $env:ProgramData\CertSentinel\appsettings.json

# 3. （建議）以系統環境變數提供 SMTP 密碼，不寫在檔案裡
[Environment]::SetEnvironmentVariable('CertSentinel__Smtp__Password', '<密碼>', 'Machine')

# 4. 啟動服務
Start-Service CertSentinel
Get-Service CertSentinel
```

安裝腳本做的事：

| 步驟 | 內容 |
|---|---|
| 發佈 | `dotnet publish` 服務與 CLI 到同一資料夾（`-FrameworkDependent` 可改為依賴已安裝的 .NET 9 Runtime） |
| 安裝 | 複製到 `C:\Program Files\CertSentinel` |
| 資料夾 | 建立 `%ProgramData%\CertSentinel` 並**關閉權限繼承**，只允許 SYSTEM、Administrators（完全控制）與 LocalService（修改）存取。資料夾若已存在，但不是本腳本建立的安全狀態（擁有者不對、繼承未關閉、有多餘權限、含 junction 或 symlink），會**中止安裝**並請管理員檢查，不沿用來源不明的內容 |
| 設定檔 | 首次安裝時把 `appsettings.sample.json` 複製為 `%ProgramData%\CertSentinel\appsettings.json`；升級時不會覆蓋 |
| 事件來源 | 在「Windows 記錄 → 應用程式」註冊來源 `CertSentinel` |
| 服務 | 帳號 `NT AUTHORITY\LocalService`、延遲自動啟動、失敗後 1 分鐘自動重啟 |

腳本可重複執行。服務已存在時，會先停止、更新檔案，再重新啟動，所以也用來**升級**。

解除安裝：

```powershell
.\scripts\uninstall-service.ps1              # 保留資料庫與設定檔
.\scripts\uninstall-service.ps1 -RemoveData  # 一併刪除 %ProgramData%\CertSentinel
```

### 查看日誌

事件檢視器 → Windows 記錄 → 應用程式，篩選來源 `CertSentinel`。也可以用 PowerShell：

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'CertSentinel' } -MaxEvents 20 |
    Format-Table TimeCreated, Id, LevelDisplayName, Message -Wrap
```

| 事件 ID | 內容 |
|---|---|
| 100 / 101 / 102 | 服務啟動、收到停止要求、單輪失敗 |
| 1000 / 1001 | 每輪開始、每輪結束摘要 |
| 1100 | **每個目標的檢查結果**（狀態、剩餘天數、錯誤原因） |
| 1101 / 1102 / 1103 | 目標在檢查中被刪除、處理目標時的非預期錯誤 |
| 1200 / 1201 | **每封通知**的寄送成功、寄送失敗（下一輪重試） |
| 1300 | 歷史清理 |

---

## 設定檔說明

設定依以下順序載入，後者覆寫前者：

1. 安裝目錄的 `appsettings.json`（預設值，升級時會被覆蓋）
2. `%ProgramData%\CertSentinel\appsettings.json`（**請編輯這份**）
3. 環境變數，例如 `CertSentinel__Smtp__Password`

完整範例見 [`appsettings.sample.json`](appsettings.sample.json)。

| 設定 | 預設 | 說明 |
|---|---|---|
| `DatabasePath` | `%ProgramData%\CertSentinel\certsentinel.db` | SQLite 檔路徑，服務與 CLI 共用。支援 `%環境變數%` |
| `CheckIntervalMinutes` | 360 | 檢查間隔（分鐘） |
| `MaxConcurrency` | 5 | 同時檢查的目標數上限 |
| `WarningDays` | 30 | 警告門檻（天） |
| `CriticalDays` | 7 | 緊急門檻（天），需小於 `WarningDays` |
| `RetentionDays` | 90 | 歷史保留天數 |
| `ErrorNotifyThreshold` | 3 | 連續幾次無法檢查才寄通知 |
| `ReminderIntervalHours` | 24 | Critical、Expired、Error 持續期間的提醒間隔 |
| `Smtp:Host` / `Port` | — / 587 | SMTP 伺服器 |
| `Smtp:Security` | `StartTls` | `None`（不加密）、`StartTls`（587）、`SslOnConnect`（465） |
| `Smtp:UserName` / `Password` | 空 | 帳號留空代表不驗證 |
| `Smtp:From` / `To` | — | 寄件地址、收件人清單 |
| `Smtp:TimeoutSeconds` | 30 | SMTP 逾時 |

每個目標的 TLS 檢查逾時依需求固定為 10 秒，不提供設定。服務啟動時會驗證設定，有誤時會在事件檢視器寫入 Critical 事件說明原因，並且不會啟動。

---

## CLI 使用方式與範例輸出

CLI 與服務安裝在同一資料夾，讀取相同的設定檔。需要以系統管理員身分執行，因為資料夾權限只開放 Administrators 與服務帳號。

```
certsentinel add <目標> [--name <名稱>]
certsentinel list [--json]
certsentinel remove <目標>
certsentinel enable <目標>
certsentinel disable <目標>
certsentinel status [--json]
certsentinel check <目標> [--json]          # 寫入歷史，不觸發通知
certsentinel history <目標> [--days N] [--json]   # N 預設 7
共用選項：--db <路徑>（覆寫資料庫位置）、--help
```

`<目標>` 接受 `example.com`、`example.com:8443`、`https://example.com:8443/path`，一律正規化為「小寫主機名稱:埠號」，預設埠號 443。所以 `remove https://Example.com/` 等同於 `remove example.com:443`。

結束碼：`0` 成功；`1` 輸入錯誤（格式不合法、重複、找不到目標）；`2` 無法存取資料庫。

### add

```
> certsentinel add github.com --name "GitHub 官網"
已新增 GitHub 官網(github.com:443)。

> certsentinel add https://expired.badssl.com/
已新增 expired.badssl.com:443。

> certsentinel add github.com:443
錯誤:目標 github.com:443 已存在(名稱:GitHub 官網),同一主機與埠號不可重複新增。

> certsentinel add http://example.com
錯誤:目標格式不合法:不支援的 scheme「http」:僅支援 https:// 或不帶 scheme 的主機名稱。

> certsentinel add example.com:99999
錯誤:目標格式不合法:埠號「99999」不合法:埠號必須是 1–65535 的整數。
```

### status

```
> certsentinel status
名稱                    主機:埠號               狀態     剩餘天數  到期日      最後檢查          備註
GitHub 官網             github.com:443          OK       44        2026-11-30  2026-10-16 09:31
expired.badssl.com:443  expired.badssl.com:443  Expired  -4205     2015-04-13  2026-10-16 09:31  不受信任
```

到期日與最後檢查以本機時間顯示。「備註」欄會列出 已停用、名稱不符、不受信任，以及無法檢查的原因。

```
> certsentinel status --json
[
  {
    "name": "GitHub 官網",
    "host": "github.com",
    "port": 443,
    "enabled": true,
    "status": "OK",
    "daysRemaining": 44,
    "notAfterUtc": "2026-11-29T23:59:59+00:00",
    "lastCheckedUtc": "2026-10-16T01:31:11.7486584+00:00",
    "nameMatches": true,
    "chainTrusted": true,
    "errorMessage": null
  },
  ...
]
```

### check

```
> certsentinel check expired.badssl.com
目標          expired.badssl.com:443(expired.badssl.com:443)
狀態          Expired
檢查時間      2026-10-16 09:31
剩餘天數      -4205
到期日        2015-04-13 07:59(UTC 2015-04-12 23:59)
生效日        2015-04-09 08:00
主體          CN=*.badssl.com, OU=PositiveSSL Wildcard, OU=Domain Control Validated
簽發者        CN=COMODO RSA Domain Validation Secure Server CA, O=COMODO CA Limited, L=Salford, S=Greater Manchester, C=GB
SHA-256 指紋  BA105CE02BAC76888ECEE47CD4EB7941653E9AC993B61B2EB3DCC82014D21B4F
涵蓋名稱      *.badssl.com, badssl.com
名稱比對      相符
鏈信任        不受信任:NotTimeValid: 與目前的系統時鐘或簽署檔案的時間戳記核對時，所需的憑證不在有效日期內。
```

### history

指紋與前一筆成功取得的憑證不同時，會標示「憑證已更新」：

```
> certsentinel history shop.example.com --days 30
檢查時間          來源     狀態      剩餘天數  到期日      指紋               備註
2026-10-16 09:00  Service  OK        89        2027-01-13  9F3A1C0B77E2D415…  憑證已更新
2026-10-15 21:00  Service  Critical  5         2026-10-21  41BE07C2A9D3F086…
2026-10-15 15:00  Cli      Error     -         -           -                  連線被拒絕(該埠號沒有服務在聽)。
```

`--json` 每筆會帶 `certificateRenewed: true/false`。

---

## 狀態判定規則

只評估伺服器送出的末端（leaf）憑證。

```
剩餘時間 = 到期時間 − 目前 UTC 時間
剩餘天數 = floor(剩餘時間.TotalDays)

剩餘時間 ≤ 0            → Expired
剩餘天數 ≤ CriticalDays → Critical（預設 7）
剩餘天數 ≤ WarningDays  → Warning（預設 30）
其餘                    → OK
無法完成檢查             → Error
```

| 剩餘時間 | 剩餘天數 | 狀態 |
|---|---|---|
| 31 天 | 31 | OK |
| 31 天差 1 秒 | 30 | Warning |
| 剛好 30 天 | 30 | Warning |
| 剛好 7 天 | 7 | Critical |
| 23 小時 | 0 | Critical |
| 剛好到期時刻 | 0 | Expired |

- **名稱不符**與**不受信任**不改變分級，但會記錄在結果中，並寫進通知內文。
- 名稱比對以憑證 SAN 為準，沒有 SAN 時才比對 Subject CN。萬用字元只能出現在最左側，且只涵蓋單一層級：`*.example.com` 涵蓋 `www.example.com`，不涵蓋 `a.b.example.com`，也不涵蓋 `example.com`。
- 鏈信任以作業系統信任庫判斷，不受信任時記錄原因（例如 `UntrustedRoot`、`NotTimeValid`、`PartialChain`）。
- **無法檢查**（Error）的類型：`Timeout`（10 秒內未完成）、`ConnectionRefused`、`DnsFailure`、`HandshakeFailed`、`NetworkError`，都會附上原因。只要握手過程中拿到了憑證，即使後續失敗也會記錄憑證資訊。

---

## 通知規則

| 情境 | 寄信 |
|---|---|
| 狀態惡化：OK → Warning、Warning → Critical、任何狀態 → Expired；新目標首次檢查就不是 OK | 「狀態惡化」一封 |
| 連續 3 次無法檢查 | 「無法檢查」一封 |
| 回到 OK（例如換了新憑證） | 「已恢復 OK」一封 |
| 通知過無法檢查後，恢復檢查但狀態不是 OK | 「狀態通知」一封 |
| 狀態沒有變化 | 不寄 |
| Critical、Expired、Error 持續 | 距上次寄出每滿 24 小時提醒一次（`ReminderIntervalHours`）。時間以「該輪開始時間」計算，不受各輪探測耗時影響，並有 1 分鐘容差吸收排程誤差，所以提醒不會因為差幾秒而順延一整輪 |
| 改善但未回到 OK（例如 Critical → Warning） | 不寄，之後不再以 Critical 提醒；若再惡化會重新通知 |
| 新目標首次檢查為 OK | 不寄，只建立基準 |

`certsentinel check` 只寫入歷史，不會觸發通知，也不影響通知進度。

### 實作方式

每個目標在 `notification_state` 資料表保存三個欄位：

- `consecutive_errors`：連續失敗次數
- `notified_status`：**最後一次成功寄出**的狀態
- `notified_at_utc`：最後一次成功寄出的時間

每輪比對的對象是「最後成功通知的狀態」，不是「上一輪觀察到的狀態」。因此：

- **寄信失敗**時，通知進度不前進，下一輪會得出相同決策，自動重試；寄出成功後才算已通知。
- **服務重啟**後，通知進度從資料庫讀回，不會重寄已通知過的狀態。
- **保留清理**只刪除 `check_results`，不碰 `notification_state`。

決策邏輯是純函式 `NotificationPolicy.Evaluate(state, observedStatus, now)`，所有規則都有不依賴時間與網路的單元測試。

---

## 架構說明

```
┌────────────────────────┐      ┌───────────────────────────┐
│ CertSentinel.Service   │      │ CertSentinel.Cli          │
│ (Windows 服務)          │      │ (certsentinel.exe)        │
│  CheckWorker           │      │  CliApp (System.Command-  │
│   └ PeriodicTimer      │      │   Line) / Views / Table   │
└───────────┬────────────┘      └─────────────┬─────────────┘
            │                                 │
            ▼                                 ▼
┌──────────────────────────────────────────────────────────┐
│ CertSentinel.Infrastructure                               │
│  SqliteDatabase / SqliteTargetRepository / SqliteCheckStore│
│  SmtpEmailSender (MailKit) / CertSentinelSettings         │
└───────────────────────────┬──────────────────────────────┘
                            ▼
┌──────────────────────────────────────────────────────────┐
│ CertSentinel.Core（純 net9.0，不依賴 SQLite 與 SMTP 實作）  │
│  Targets:  TargetParser、TargetAddress                     │
│  Probing:  ICertificateProbe → TlsCertificateProbe         │
│  Certificates: HostnameMatcher、CertificateNames            │
│  Status:   StatusClassifier                               │
│  Notifications: NotificationPolicy、NotificationComposer、  │
│                 IEmailSender                               │
│  Checks:   CheckCoordinator、ITargetRepository、ICheckStore │
│  時間：BCL TimeProvider（測試用 FakeTimeProvider）          │
└──────────────────────────────────────────────────────────┘
```

### 一輪檢查的流程

1. `CheckWorker` 啟動後立即執行第一輪，之後以 `PeriodicTimer` 依間隔重複。每輪循序 `await`，上一輪沒完成不會開始下一輪；若一輪跑超過間隔，結束後只補跑一次，不會累積。
2. `CheckCoordinator` 讀取所有啟用的目標，以 `Parallel.ForEachAsync` 依並行上限檢查。每個目標包在 try/catch 中，失敗只影響該目標。
3. `TlsCertificateProbe` 解析 DNS、建立 TCP 連線，以 `SslStream` 送出 SNI 完成握手，並在驗證 callback 中擷取憑證資訊。
4. `StatusClassifier` 判定狀態，`NotificationPolicy` 決定是否寄信。
5. 檢查結果與「未寄信時的通知進度」在同一個 SQLite 交易中寫入；寄信成功後才更新通知進度。
6. 每輪結束後清理超過保留天數的歷史。

### 資料庫

| 資料表 | 內容 |
|---|---|
| `targets` | 監控目標，`UNIQUE(host, port)` |
| `check_results` | 每次檢查的結果與憑證資訊（時間一律以 ISO 8601 UTC 保存） |
| `notification_state` | 每個目標的通知進度 |
| `schema_version` | Schema 版本 |

### 服務與 CLI 同時存取

- 開啟 `PRAGMA journal_mode = WAL`：讀寫互不阻擋，服務寫入時 `status` 照樣能讀。
- 設定 `busy_timeout` 加上 Microsoft.Data.Sqlite 的 `Default Timeout`：寫入衝突時等待，不會立刻丟出 `SQLITE_BUSY`。
- 寫入交易一律用 `BEGIN IMMEDIATE`，避免 deferred 交易在升級鎖定時失敗。
- 交易中不做網路 I/O，TLS 檢查完成後才開交易寫入。

### 優雅停止

- `HostOptions.ShutdownTimeout = 5 秒`。
- 停止訊號會傳到 DNS、TCP、TLS、SMTP 的每一層，進行中的檢查會立即取消。
- 被取消的目標不寫入任何紀錄，所以不會留下殘缺資料。
- 寄到一半被取消的通知，視為未寄出，下次啟動時重新評估。

---

## 測試與測試憑證

`tests/CertSentinel.Tests`（xUnit），共 170 多個案例：

| 範圍 | 檔案 | 對應驗收 |
|---|---|---|
| 目標正規化 | `Core/TargetParserTests.cs` | #2 |
| 萬用字元比對 | `Core/HostnameMatcherTests.cs` | #5 |
| 狀態分級邊界 | `Core/StatusClassifierTests.cs` | #6 |
| 通知規則（控制時間） | `Core/NotificationPolicyTests.cs` | #8 #9 #11 |
| 一輪檢查、重啟、寄信失敗、取消 | `Core/CheckCoordinatorTests.cs` | #10 #11 #15 |
| SQLite、保留清理、併發寫入 | `Infrastructure/SqliteStoreTests.cs` | #13 #14 |
| SMTP（loopback 迷你 SMTP 伺服器） | `Infrastructure/SmtpEmailSenderTests.cs` | — |
| TLS 整合 | `Tls/TlsCertificateProbeTests.cs`、`Tls/CheckRoundIntegrationTests.cs` | #3 #4 #7 |
| 服務排程（立即執行、不重疊、停止） | `Service/CheckWorkerTests.cs` | #1 #15 |
| CLI 輸出與 JSON | `Cli/CliTests.cs`、`Cli/TextTableTests.cs` | #2 #12 |

實機安裝為服務的驗收輸出（#1、#14、#15）記錄在 [`docs/ACCEPTANCE.md`](docs/ACCEPTANCE.md)。CI 每次執行都會在 Actions 的 Summary 頁面列出測試通過數。

### 測試憑證怎麼產生

測試憑證**全部在執行時動態產生**，Repo 中沒有任何憑證或私鑰檔（見 `tests/CertSentinel.Tests/Tls/TestCertificates.cs`）：

1. 以 `System.Security.Cryptography.X509Certificates.CertificateRequest` 產生 RSA 2048 測試根 CA，以及由它簽發的伺服器憑證，可指定 SAN、生效日與到期日，用來產生過期憑證、名稱不符憑證和萬用字元憑證。
2. 自簽憑證用 `CreateSelfSigned` 產生。
3. Windows SChannel 不能直接使用暫時金鑰，所以會先匯出成記憶體中的 PFX 再匯入。匯入時不設 `PersistKeySet`，憑證釋放後金鑰即刪除。
4. `LoopbackTlsServer` 在 `127.0.0.1:0` 啟動，依 SNI 主機名稱挑選憑證。測試用的 `IEndpointResolver` 把 `a.test`、`b.test` 解析到 loopback，SNI 仍送原本的主機名稱。這樣可以驗證「伺服器依主機名稱回傳不同憑證時，拿到的是對的那張」。
5. 「受信任」的情境用 `X509ChainPolicy.CustomTrustStore` 信任測試根 CA，不會修改系統信任庫。「自簽不受信任」的情境則使用系統信任庫。

---

## 設計取捨

| 決策 | 理由 |
|---|---|
| 另設 `Infrastructure` 專案放 SQLite 與 SMTP | 服務與 CLI 共用，Core 保持純粹，單元測試不需要資料庫 |
| 通知比對「最後成功通知的狀態」 | 寄信失敗重試、重啟不重寄、清理不影響進度，三件事一次解決，不需要待寄佇列 |
| 時間用 BCL `TimeProvider` 而非自訂 `IClock` | .NET 8 起的標準抽象，`PeriodicTimer` 和 `FakeTimeProvider` 都直接支援，連排程都能用假時間測試 |
| 自己實作萬用字元比對，不依賴 `SslPolicyErrors.RemoteCertificateNameMismatch` | 規則明確可測，且憑證不受信任時仍能判斷名稱 |
| 關閉 OCSP/CRL 與 AIA 下載 | 需求是除了目標與 SMTP 外不得呼叫外部網路；信任度只作資訊記錄 |
| MailKit 取代 `System.Net.Mail.SmtpClient` | 支援 465 埠直接 TLS，且官方已不建議新程式使用 `SmtpClient`。MIT 授權 |
| 日誌寫事件檢視器而非檔案 | Windows 服務原生支援，不需第三方套件，也不必處理檔案輪替 |
| Microsoft.Data.Sqlite 而非 EF Core | 只有 4 張資料表、十幾個查詢，直接寫 SQL 最清楚，也能精確控制交易模式 |
| 每個目標各寄一封 | 主旨即可看出目標與狀態，規則與測試都較單純 |
| 服務帳號 LocalService | 最小權限，只需要對外連線與寫入資料夾 |
| 預設自含 Runtime 發佈 | 小公司伺服器不必另外安裝、更新 .NET Runtime |

---

## 已知限制

- 只檢查直接以 TLS 握手的連線，例如 HTTPS、LDAPS。不支援 STARTTLS（SMTP 25/587、IMAP 等），不使用 Proxy。
- 不檢查 OCSP、CRL，不評估中繼憑證的到期。
- 不從網路下載中繼憑證。若伺服器沒有送出完整憑證鏈，可能顯示「不受信任（PartialChain）」。這只是資訊，不影響狀態分級。
- 通知為 at-least-once：SMTP 已送出但寫入資料庫前程序中斷時，下一輪可能重寄一次。
- 連線被拒絕時，Windows 會先重試約 2 秒才回報，所以 `ConnectionRefused` 的檢查會花約 2 秒。
- 同時有大量目標變化時，每個目標各寄一封，信件數量較多。
- CLI 需以系統管理員身分執行（資料夾權限的刻意限制）。
- 服務只支援單一執行個體。
- SMTP 密碼若寫在設定檔中為明文，建議改用系統環境變數。
