# CertSentinel 設計文件(草案 v1,待確認)

> 環境確認:.NET SDK 9.0.315、git 2.54 已安裝;目錄為空、尚未 `git init`。

---

## 1. Solution 結構

```
CertSentinel/
├─ CertSentinel.sln
├─ Directory.Build.props          # net9.0、Nullable、TreatWarningsAsErrors、LangVersion
├─ src/
│  ├─ CertSentinel.Core/          # 純 net9.0 Class Library — 業務邏輯,不碰 SQLite / SMTP 實作
│  ├─ CertSentinel.Infrastructure/# SQLite Repository + SMTP 寄信實作(服務與 CLI 共用)
│  ├─ CertSentinel.Service/       # Worker Service(Windows 服務)
│  └─ CertSentinel.Cli/           # Console:certsentinel.exe
├─ tests/
│  └─ CertSentinel.Tests/         # xUnit:單元測試 + loopback TLS 整合測試
├─ scripts/
│  ├─ install-service.ps1
│  └─ uninstall-service.ps1
├─ appsettings.sample.json
├─ .github/workflows/ci.yml
└─ README.md
```

依賴方向(只往內指):

```
Service ──┐
          ├──► Infrastructure ──► Core
Cli ──────┘                         ▲
Tests ──────────────────────────────┘(也參考 Infrastructure 以測真實 SQLite)
```

**為何多一個 Infrastructure 專案**:題目要求 Core 只放「憑證檢查、狀態判定、通知規則」且 SMTP 要抽象成介面;
SQLite 存取與 SMTP 實作是服務與 CLI 共用的,放在 Infrastructure 可避免兩邊重複,也讓 Core 單元測試完全不需要資料庫。

---

## 2. Core(CertSentinel.Core)

### 2.1 主要型別

| 型別 | 職責 |
|---|---|
| `TargetAddress`(record:Host, Port) | 正規化後的「主機 + 埠號」,`ToString()` → `example.com:443` |
| `TargetParser.TryParse(string, out TargetAddress, out string error)` | 三種輸入格式正規化 + 驗證 |
| `HostnameMatcher.Matches(string pattern, string host)` | 單一層級萬用字元比對 |
| `CertificateProbeResult` | 一次 TLS 探測的原始結果(憑證資訊 or 失敗原因) |
| `ICertificateProbe` / `TlsCertificateProbe` | 實際建立 TCP + TLS,取得 leaf 憑證 |
| `StatusClassifier.Classify(notAfterUtc, nowUtc, thresholds)` | OK / Warning / Critical / Expired |
| `NotificationPolicy.Evaluate(state, observation, now)` | **純函式**:決定要不要寄、寄什麼、新狀態為何 |
| `CheckCoordinator` | 一輪檢查的編排:並行上限、單一失敗隔離、寫入、通知 |
| `IEmailSender` | 寄信抽象(測試用 Fake 可模擬失敗) |
| `ITargetRepository` / `ICheckResultRepository` / `INotificationStateRepository` | 儲存抽象 |
| 時間 | 使用 BCL 的 `TimeProvider`(測試用 `FakeTimeProvider`)— 見決策點 D4 |

### 2.2 目標正規化規則(TargetParser)

| 輸入 | 結果 |
|---|---|
| `example.com` | `example.com:443` |
| `example.com:8443` | `example.com:8443` |
| `https://example.com:8443/path?q=1` | `example.com:8443`(路徑、查詢字串捨棄) |
| `https://Example.COM` | `example.com:443`(主機一律小寫) |
| `[::1]:8443` / `127.0.0.1` | 支援 IP(IPv6 需中括號) |
| `http://...`、`ftp://...` | 拒絕:「僅支援 https:// 或不帶 scheme 的主機名稱」 |
| 空字串、含空白、`*.example.com`、`user@host`、埠號非 1–65535、label 超過 63 字元、非法字元 | 拒絕並說明是哪一條規則 |

- 國際化網域以 `IdnMapping` 轉 punycode 後儲存。
- 重複判定以正規化後的 `(host, port)` 為準(SQLite `UNIQUE(host, port)`),錯誤訊息:「目標 example.com:443 已存在(名稱:XXX),同一主機與埠號不可重複新增」。

### 2.3 萬用字元比對(HostnameMatcher)

- 以憑證 SAN 的 DNS 名稱為準;憑證沒有 SAN 擴充時才退回 Subject CN。目標為 IP 時比對 SAN 的 IP Address。
- `*` 只允許出現在最左側整個 label(`*.example.com`);`w*.example.com` 這類部分萬用字元不支援(視為不符)。
- `*.example.com` 涵蓋 `www.example.com`;**不**涵蓋 `a.b.example.com`、**不**涵蓋 `example.com`。
- 比對不分大小寫、忽略結尾的 `.`。

### 2.4 TLS 探測(TlsCertificateProbe)

```
DNS 解析 → TCP 連線 → SslStream.AuthenticateAsClientAsync → 擷取 leaf
└──────────────── 整段共用一個 10 秒逾時(CancellationTokenSource,與停止訊號 linked)
```

- `SslClientAuthenticationOptions.TargetHost = host` → 正確送出 **SNI**。
- `RemoteCertificateValidationCallback` 一律回傳 `true`(讓過期 / 自簽 / 名稱不符仍能完成握手),並在 callback 中**複製**憑證與 `SslPolicyErrors`、`chain.ChainStatus`。
- 只要 callback 取得憑證,即使之後握手失敗,也記錄憑證資訊(滿足「只要握手能取得憑證就必須記錄」)。
- 鏈信任:`X509ChainPolicy` 設定 `RevocationMode = NoCheck`(不查 OCSP/CRL)、`DisableCertificateDownloads = true`(**不從 AIA 下載中繼憑證,避免呼叫外部網路**)。信任根預設為系統信任庫;測試可注入自訂信任根(`CustomRootTrust`)以驗證「受信任」路徑。
- 名稱比對用自己的 `HostnameMatcher`(可單元測試),不依賴 `SslPolicyErrors.RemoteCertificateNameMismatch`。
- 記錄欄位:NotBefore/NotAfter(UTC)、Subject、Issuer、SHA-256 指紋(十六進位大寫)、SAN 清單、NameMatches、ChainTrusted、ChainErrors(文字原因)。
- 失敗分類(ErrorKind):

| 情境 | ErrorKind | 原因範例 |
|---|---|---|
| 10 秒內未完成 | `Timeout` | 「連線或握手超過 10 秒」 |
| `SocketError.ConnectionRefused` | `ConnectionRefused` | 「連線被拒絕」 |
| DNS 失敗 | `DnsFailure` | 「無法解析主機名稱」 |
| `AuthenticationException` / 握手中斷 | `HandshakeFailed` | 例外訊息 |
| 其他網路錯誤 | `NetworkError` | 例外訊息 |

- 可測試性:探測器透過 `IEndpointResolver` 取得連線位址。整合測試把 `a.test`、`b.test` 解析到 `127.0.0.1`,同時 SNI 仍送 `a.test`,以驗證「伺服器依主機名稱回傳不同憑證時,拿到的是對的那張」。逾時值可注入(測試用 1 秒)。

### 2.5 狀態分級(StatusClassifier)

```
remaining = NotAfterUtc − nowUtc
days      = floor(remaining.TotalDays)
remaining ≤ 0              → Expired
days ≤ CriticalDays(7)     → Critical
days ≤ WarningDays(30)     → Warning
其餘                        → OK
無法完成檢查                 → Error
```

邊界(單元測試皆涵蓋):剛好 30 天 → Warning;31 天差 1 秒(向下取整為 30)→ Warning;剛好 31 天 → OK;剛好 7 天 → Critical;剩 23 小時 → Critical(days = 0);剛好到期時刻 → Expired。
名稱不符 / 不受信任 **不改變**分級,只記錄並寫入通知內文。

### 2.6 通知規則(NotificationPolicy)— 核心設計

**關鍵想法:通知決策比對的是「已成功通知過的狀態」,而不是「上一次觀察到的狀態」。**
如此「寄信失敗 → 下一輪重試」與「服務重啟不重寄」都自然成立,不需要額外的待寄佇列。

每個目標持久化的 `NotificationState`(獨立資料表,與歷史分開):

| 欄位 | 說明 |
|---|---|
| `ConsecutiveErrors` | 服務檢查連續 Error 次數(成功一次歸零) |
| `NotifiedStatus` | 最後一次**成功寄出**(或靜默確認)的狀態;新目標為 null |
| `NotifiedAtUtc` | 最後一次成功寄出的時間(提醒計時基準) |

演算法(每輪對每個目標):

1. 計算「有效狀態」E:
   - 觀察結果非 Error → E = 觀察狀態,`ConsecutiveErrors = 0`
   - 觀察為 Error → `ConsecutiveErrors++`;若 ≥ 3 則 E = Error,否則**本輪不做通知判斷**(維持原狀)
2. 比較 E 與 `NotifiedStatus`(嚴重度 OK < Warning < Critical < Expired):

| 條件 | 動作 |
|---|---|
| NotifiedStatus = null 且 E = OK | 靜默記錄 NotifiedStatus = OK(新目標的基準,不寄信) |
| E 比 NotifiedStatus 嚴重(含 null → Warning/Critical/Expired) | 寄「狀態惡化」通知 |
| E = Error 且 NotifiedStatus ≠ Error | 寄「無法檢查」通知 |
| NotifiedStatus = Error 且 E 為 Warning/Critical/Expired | 寄「狀態通知」(收件人上次只知道「無法檢查」,需告知實際狀態) |
| E = OK 且 NotifiedStatus ∉ {null, OK} | 寄「恢復」通知 |
| E 比 NotifiedStatus 輕但非 OK(如 Critical → Warning) | 不寄信,靜默更新 NotifiedStatus(避免對已改善的狀態繼續提醒;日後再惡化會再通知) |
| E = NotifiedStatus ∈ {Critical, Expired, Error} 且 now − NotifiedAtUtc ≥ 24h | 寄「持續提醒」 |
| 其他(狀態不變) | 不寄 |

3. 寄信:
   - 成功 → 更新 `NotifiedStatus = E`、`NotifiedAtUtc = now`
   - 失敗 → **不更新**通知欄位(只更新 ConsecutiveErrors),記錄 Warning 日誌;下一輪重新評估時會得出相同決策 → 自動重試
4. `NotificationPolicy` 是純函式:輸入 (state, observation, now),輸出 (決策, 成功時的新 state, 失敗時的新 state)。驗收 #8–#11 全部可在不碰網路、不碰時間的情況下測。

已知限制:「SMTP 已送出 → 寫入 DB 前程序崩潰」的極小視窗可能重寄一次(at-least-once),README 註明。

### 2.7 一輪檢查(CheckCoordinator)

- 讀取所有 enabled 目標 → `Parallel.ForEachAsync(MaxDegreeOfParallelism = 設定值)`。
- 每個目標包在 try/catch 內:任何例外只影響該目標(記為 Error 並寫日誌),不影響其他目標。
- 每個目標的「檢查結果 + 狀態更新」在**同一個 SQLite transaction** 寫入;取消(停止服務)時不寫入,因此不會有殘缺紀錄。
- 每封通知為「每個目標一封」,主旨例:`[CertSentinel] Critical — shop.example.com:443 剩餘 5 天`;純文字內文含到期日、剩餘天數、簽發者、指紋、名稱是否相符、信任狀態與原因、錯誤原因。

---

## 3. 儲存(Infrastructure / SQLite)

### 3.1 Schema

```sql
CREATE TABLE targets (
  id           INTEGER PRIMARY KEY,
  host         TEXT    NOT NULL,
  port         INTEGER NOT NULL,
  name         TEXT    NOT NULL,          -- add --name,預設 host:port
  enabled      INTEGER NOT NULL DEFAULT 1,
  created_utc  TEXT    NOT NULL,
  UNIQUE(host, port)
);

CREATE TABLE check_results (
  id              INTEGER PRIMARY KEY,
  target_id       INTEGER NOT NULL REFERENCES targets(id) ON DELETE CASCADE,
  checked_utc     TEXT    NOT NULL,
  source          TEXT    NOT NULL,       -- Service / Cli
  status          TEXT    NOT NULL,       -- OK/Warning/Critical/Expired/Error
  not_before_utc  TEXT, not_after_utc TEXT, days_remaining INTEGER,
  subject TEXT, issuer TEXT, thumbprint_sha256 TEXT, san TEXT,
  name_matches INTEGER, chain_trusted INTEGER, chain_errors TEXT,
  error_kind TEXT, error_message TEXT
);
CREATE INDEX ix_results_target_time ON check_results(target_id, checked_utc);

CREATE TABLE notification_state (
  target_id          INTEGER PRIMARY KEY REFERENCES targets(id) ON DELETE CASCADE,
  consecutive_errors INTEGER NOT NULL DEFAULT 0,
  notified_status    TEXT,
  notified_at_utc    TEXT
);

CREATE TABLE schema_version (version INTEGER NOT NULL);
```

- 時間一律以 ISO 8601 UTC(`O` 格式)存為 TEXT。
- Schema 以內建簡易 migration(`schema_version`)建立,服務與 CLI 任一方先啟動都會建立。

### 3.2 兩個程序同時存取

- `PRAGMA journal_mode = WAL`:讀寫互不阻擋(服務寫入中 `status` 仍可讀)。
- `PRAGMA busy_timeout = 5000` + Microsoft.Data.Sqlite `Default Timeout`:寫入衝突時等待而非立即丟 `SQLITE_BUSY`。
- 寫入交易一律 `BEGIN IMMEDIATE`(避免 deferred 交易升級鎖時的死結型 BUSY)。
- 每次操作短連線、短交易;不在交易中做網路 I/O(TLS 探測完成後才開交易寫入)。
- 資料夾 `%ProgramData%\CertSentinel\` 由安裝腳本設定 ACL:Administrators 與服務帳號皆可修改(WAL 的 `-wal`、`-shm` 檔需雙方可寫)。

### 3.3 保留期限清理

```sql
DELETE FROM check_results
WHERE checked_utc < @cutoff
  AND id NOT IN (SELECT MAX(id) FROM check_results GROUP BY target_id);
```

- 只動 `check_results`;`notification_state` 與 `targets` 完全不碰 → 通知進度不受影響。
- 永遠保留每個目標最新一筆,`status` 指令即使目標停用很久也能顯示最後結果。
- 服務每輪結束後執行一次。

### 3.4 SMTP(`SmtpEmailSender : IEmailSender`)

- 支援無驗證 / 帳密驗證;TLS 模式:`None` / `StartTls` / `SslOnConnect`(465)。
- 套件見決策點 D1。

---

## 4. Windows 服務(CertSentinel.Service)

- `Host.CreateApplicationBuilder` + `AddWindowsService(o => o.ServiceName = "CertSentinel")`。
- `HostOptions.ShutdownTimeout = 5s`。
- `CheckWorker : BackgroundService`:

```
啟動 → 立即執行第 1 輪
loop:
  await RunRoundAsync(stoppingToken)       // 循序 await,上一輪沒完成不會開始下一輪
  await PeriodicTimer(Interval).WaitForNextTickAsync(stoppingToken)
        // PeriodicTimer 不會累積錯過的 tick:若一輪跑超過間隔,結束後立刻開始下一輪,但絕不重疊
```

- 停止:`stoppingToken` 傳到 DNS/TCP/TLS/SMTP 每一層 → 進行中的探測立即取消、該目標不寫入、未寄出的通知下次啟動會重評估。
- 設定以 Options pattern + `ValidateOnStart`(間隔 ≥ 1、Critical < Warning、收件人非空等),設定錯誤時服務啟動失敗並寫入事件檢視器。

### 4.1 appsettings.json

```json
{
  "CertSentinel": {
    "DatabasePath": "%ProgramData%\\CertSentinel\\certsentinel.db",
    "CheckIntervalMinutes": 360,
    "MaxConcurrency": 5,
    "WarningDays": 30,
    "CriticalDays": 7,
    "RetentionDays": 90,
    "ErrorNotifyThreshold": 3,
    "ReminderIntervalHours": 24,
    "Smtp": {
      "Host": "smtp.example.com",
      "Port": 587,
      "Security": "StartTls",
      "UserName": "",
      "Password": "",
      "From": "certsentinel@example.com",
      "To": [ "ops@example.com" ]
    }
  },
  "Logging": { "LogLevel": { "Default": "Information" },
               "EventLog": { "LogLevel": { "Default": "Information" } } }
}
```

- SMTP 密碼可用環境變數 `CertSentinel__Smtp__Password` 覆寫,避免明文寫在檔案(README 說明)。
- 逾時 10 秒依題目固定,僅測試可注入。

### 4.2 日誌

- 預設寫入 **Windows 事件檢視器**(`Application` log,來源 `CertSentinel`),見決策點 D2。
- 每個目標一筆結果日誌(含狀態、剩餘天數、錯誤原因);每封通知一筆(成功 Information / 失敗 Warning);每輪開始/結束摘要。
- 使用 `LoggerMessage` source generator 定義事件 ID,方便在事件檢視器篩選。

---

## 5. CLI(certsentinel.exe)

與服務部署在同一資料夾、共用 `appsettings.json`(取得 DB 路徑與門檻);另支援 `--db <path>` 覆寫(測試與除錯用)。

```
certsentinel add <target> [--name <名稱>]
certsentinel list [--json]
certsentinel remove <target>
certsentinel enable <target>
certsentinel disable <target>
certsentinel status [--json]
certsentinel check <target>              # 寫入歷史(source=Cli),不更新通知狀態、不寄信
certsentinel history <target> [--days N] [--json]   # N 預設 7
```

- `<target>` 一律先經 `TargetParser` 正規化,所以 `remove https://example.com/` 等同 `remove example.com:443`。
- 結束碼:0 成功、1 輸入錯誤(格式不合法、重複、找不到目標)、2 執行期錯誤(DB 無法開啟等)。所有錯誤以一行說明輸出到 stderr,不顯示 stack trace。
- `Console.OutputEncoding = UTF8`,表格以東亞字寬計算對齊。
- `status` 範例:

```
名稱        主機:埠號               狀態      剩餘天數  到期日       最後檢查
公司官網    www.example.com:443     OK        84        2027-01-08   2026-10-16 09:00
商店        shop.example.com:443    Critical  5         2026-10-21   2026-10-16 09:00
內部 LDAP   ldap.example.com:636    Error     -         -            2026-10-16 09:00
```

- `history` 範例(指紋與前一筆成功紀錄不同時標示):

```
檢查時間            來源     狀態      剩餘天數  指紋(前 16 碼)       備註
2026-10-16 09:00    Service  OK        89        9F3A1C...             憑證已更新
2026-10-15 21:00    Service  Critical  5         41BE07...
2026-10-15 15:00    Cli      Error     -         -                     連線被拒絕
```

- `--json`:以 `System.Text.Json` 輸出,時間為 ISO 8601 UTC,`certificateRenewed: true/false`。

---

## 6. 測試策略(tests/CertSentinel.Tests)

| 類別 | 內容 | 對應驗收 |
|---|---|---|
| TargetParser | 三種格式、大小寫、IPv6、各種非法輸入(約 12 案例) | #2 |
| HostnameMatcher | 單層萬用字元、多層、apex、大小寫、部分萬用字元(約 8) | #5 |
| StatusClassifier | 30/7 天邊界、<1 天、到期時刻(約 8) | #6 |
| NotificationPolicy | 惡化、Error 1/2/3 次、恢復、不變、24h 提醒前後、Critical→Warning、Error→Warning(約 15) | #8 #9 |
| 重啟模擬 | 建立 Coordinator + 真實 SQLite 暫存檔 → 跑一輪 → 丟掉物件重建 → 再跑 → 不重寄 | #10 |
| 寄信失敗 | FakeEmailSender 第一次丟例外 → 下一輪重試 → 成功後才不再寄 | #11 |
| 保留清理 | 舊紀錄刪除、最新一筆保留、notification_state 不變 | #13 |
| Repository | 重複新增、enable/disable、併發寫入不丟 BUSY(兩個連線同時寫) | #2 #14 |
| CLI 輸出 | status/history 格式、`--json` 可被 `JsonDocument.Parse`、「憑證已更新」標示 | #12 |
| **TLS 整合** | loopback `TcpListener` + `SslStream` 伺服器,`CertificateRequest` 動態產生憑證(不落地私鑰):SNI 選對憑證、過期、自簽、名稱不符、逾時(只 accept 不回應)、拒絕連線(關閉的埠)、握手失敗(回傳垃圾位元組)、同一輪混合成功與失敗互不影響 | #3 #4 #7 |

- 時間控制:`FakeTimeProvider`(Microsoft.Extensions.TimeProvider.Testing)。
- 合計預估 60+ 案例,超過 30 的要求。

---

## 7. 部署腳本與 CI

- `install-service.ps1`(需系統管理員):`dotnet publish`(或指定已發佈資料夾)→ 複製到 `C:\Program Files\CertSentinel` → 建立 `%ProgramData%\CertSentinel` 並設 ACL → 若不存在則複製 `appsettings.sample.json` → 註冊事件來源 → `New-Service`(延遲自動啟動、帳號 `NT AUTHORITY\LocalService`)→ `sc.exe failure` 設定失敗自動重啟 → 啟動。
- `uninstall-service.ps1`:停止 → 刪除服務 → 移除事件來源;`-RemoveData` 才刪除 `%ProgramData%\CertSentinel`。
- `.github/workflows/ci.yml`:`windows-latest`、`actions/setup-dotnet@v4`(`9.0.x`)、`dotnet build -c Release` + `dotnet test`,README 放 Actions 徽章。

---

## 8. 開發順序(每步可編譯、可測試、獨立 commit)

1. Solution 骨架 + Directory.Build.props + CI 設定
2. `TargetParser`(TDD)
3. `HostnameMatcher`(TDD)
4. `StatusClassifier`(TDD)
5. `NotificationPolicy`(TDD)
6. SQLite schema + Repository + 保留清理(含併發測試)
7. `TlsCertificateProbe` + loopback 整合測試
8. `CheckCoordinator`(重啟、寄信失敗、單一失敗隔離測試)
9. SMTP 寄信實作
10. Worker Service + 設定驗證 + 事件檢視器日誌
11. CLI 全部指令 + 輸出測試
12. 安裝/解除安裝腳本、appsettings.sample.json
13. README(含架構圖、規則說明、範例輸出、設計取捨、已知限制)
14. 實機驗收:安裝服務、啟停、併發 `status`、停止 5 秒內結束

---

## 9. 已知限制(將寫入 README)

- 不支援 STARTTLS、OCSP/CRL、Proxy;僅評估 leaf 憑證。
- 通知為 at-least-once:寄出後、寫入 DB 前若程序崩潰可能重寄一次。
- 不從網路下載中繼憑證:伺服器若未送完整鏈,可能顯示「不受信任(鏈不完整)」,這是刻意的(避免外部網路呼叫),且不影響狀態分級。
- 每個目標各寄一封信,同時大量到期時信件較多。
- SMTP 密碼建議以環境變數提供。
