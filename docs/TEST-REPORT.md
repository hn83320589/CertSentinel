# CertSentinel 測試報告

| 項目 | 內容 |
|---|---|
| 報告日期 | 2026-10-16 |
| 受測版本 | commit `0dc0107`（自動化測試與 CI）；實機驗收為 commit `ceba38f`（差異說明見 §4） |
| 測試環境 | Windows 11 Pro 10.0.26200、.NET SDK 9.0.315；CI 為 GitHub Actions `windows-latest`、.NET 9.0.x |
| Repo | <https://github.com/hn83320589/CertSentinel> |

## 1. 結論

**18 項驗收標準全部通過**，功能需求、技術限制、交付物與假設事項也逐條核對過，結果都是符合。

| 指標 | 結果 |
|---|---|
| 自動化測試 | **201 / 201 通過**，失敗 0、略過 0（要求至少 30 個） |
| 建置 | Release 建置 0 警告、0 錯誤（全部專案開啟「警告視為錯誤」）；`dotnet format --verify-no-changes` 通過 |
| CI | HEAD `0dc0107` 綠燈：[run 38841788583](https://github.com/hn83320589/CertSentinel/actions/runs/38841788583)，Summary 頁面列有測試通過數 |
| 實機驗收 | 已安裝為 Windows 服務，實測 #1、#2、#8、#10、#11、#12、#14、#15，全部符合，原始輸出見 [ACCEPTANCE.md](ACCEPTANCE.md) |
| 缺陷 | 開發與驗收過程共發現 **12 個缺陷，全部已修正**，其中 11 個附有防止回歸的測試或驗證步驟（見 §6） |
| 私鑰檢查 | Repo 中沒有任何 `.pfx`、`.pem`、`.key`、`.p12`、`.cer`、`.crt` 檔，也沒有 `BEGIN … PRIVATE KEY` 字串 |

## 2. 測試方法

| 層級 | 方法 | 範圍 |
|---|---|---|
| 單元測試 | xUnit，用 `FakeTimeProvider` 控制時間，探測器與寄信器用 Fake | 目標正規化、萬用字元比對、狀態分級、通知規則、排程、CLI 輸出 |
| 整合測試（SQLite） | 每個測試使用獨立的暫存資料庫檔 | 儲存、交易、保留清理、兩個連線同時讀寫、模擬重啟 |
| 整合測試（TLS） | 在 `127.0.0.1` 啟動 TLS 伺服器，憑證於執行時以 `CertificateRequest` 動態產生 | SNI、過期、自簽、名稱不符、萬用字元、逾時、拒絕連線、握手失敗 |
| 整合測試（SMTP） | loopback 迷你 SMTP 伺服器 | 信件內容、收件人、連線失敗 |
| 實機驗收 | 安裝為 Windows 服務、本機 SMTP 接收程式、實際的公開測試站（badssl.com 等） | 啟動、停止、併發、重試、重啟、CLI 輸出 |
| 獨立稽核 | 另開一個沒有實作脈絡的審查 agent，拿原始需求逐條對照程式碼 | 全部需求，結果併入 §5 與 §6 |

測試全程不依賴外部網路（實機驗收連線的受監控目標除外）。

## 3. 驗收標準結果

| # | 標準 | 判定方式 | 結果 | 證據 |
|---|---|---|---|---|
| 1 | 服務可安裝、啟動、停止，啟動後立即產生第一輪檢查紀錄 | 實測 | ✅ | 0.83 秒啟動，第一輪立即開始，11.7 秒內 6 個目標都有紀錄。單元測試 `CheckWorkerTests.啟動後立即檢查一輪_之後依間隔重複` |
| 2 | add 正確正規化三種輸入格式；重複目標與不合法輸入有明確錯誤訊息 | 單元測試＋實測 | ✅ | `TargetParserTests`（46 個）、`CliTests.add_*`；實測輸出見 ACCEPTANCE §#2 |
| 3 | loopback 檢查能取得到期時間、指紋、簽發者；SNI 拿到對的那張 | 整合測試 | ✅ | `TlsCertificateProbeTests.取得到期時間_指紋_簽發者_且鏈受信任`、`伺服器依SNI回傳不同憑證_取得的是對應主機的那張` |
| 4 | 過期、自簽、名稱不符仍記錄憑證資訊並附原因 | 整合測試 | ✅ | `已過期憑證_…`（NotTimeValid）、`自簽憑證_…`（系統信任庫）、`名稱不符_…`；實機上 badssl.com 三種情況也都正確 |
| 5 | 萬用字元只涵蓋單一層級 | 單元測試 | ✅ | `HostnameMatcherTests`（15 個）、整合測試 `萬用字元憑證_涵蓋單一層級子網域`；實機上 `wrong.host.badssl.com` 對 `*.badssl.com` 正確判為不符 |
| 6 | 門檻邊界（30 天、7 天、不足一天、到期時刻） | 單元測試 | ✅ | `StatusClassifierTests`（20 個），含 31 天差 1 秒、23 小時、剩 1 秒、剛好到期等案例 |
| 7 | 逾時、拒絕連線、握手失敗各自記為無法檢查，且不影響同一輪其他目標 | 整合測試 | ✅ | `CheckRoundIntegrationTests.同一輪混合成功與各種失敗_各自記錄且互不影響`、`TlsCertificateProbeTests` 的逾時、拒絕、握手失敗、DNS 失敗 |
| 8 | 惡化、Error 連續 3 次、恢復 OK 各只寄一封；狀態不變不重寄 | 單元測試 | ✅ | `NotificationPolicyTests`（33 個）；實機上 Error 在第 3 輪才寄出 |
| 9 | Critical、Expired、Error 持續期間每 24 小時提醒一次，期間內不重複 | 單元測試（控制時間） | ✅ | `持續Critical或Expired_每24小時提醒一次_期間內不重複`、`持續Error_…`、`探測耗時每輪不同_提醒仍在滿24小時的那一輪寄出_不順延` |
| 10 | 模擬重啟（重建物件、沿用同一資料庫）後不重寄 | 單元測試 | ✅ | `模擬服務重啟_沿用同一資料庫_不重複寄出已通知過的狀態`、`模擬服務重啟_連續錯誤次數延續_第三次才寄`；實機重啟前後都是 3 封 |
| 11 | 寄信失敗時下一輪重試，成功後才視為已通知 | 單元測試 | ✅ | `寄信失敗_下一輪重試_成功後才視為已通知` 等 4 個；實機上第 1 輪失敗、第 2 輪重試成功 |
| 12 | status、check、history 輸出正確；`--json` 為合法 JSON；指紋變動標示「憑證已更新」 | 實測＋測試 | ✅ | `CliTests`（17 個，JSON 會實際 parse 驗證）；實機 4 種 `--json` 輸出都通過 `JsonDocument.Parse` |
| 13 | 保留清理只刪過期歷史，不影響目前狀態與通知進度 | 單元測試 | ✅ | `保留清理_只刪過期歷史_…`，以及 `保留清理_存在從未檢查過的目標時_仍刪除過期歷史`（目標啟用、停用兩種情況） |
| 14 | 服務與 CLI 同時存取資料庫不丟出鎖定錯誤 | 實測 | ✅ | 98 秒內讀取 196 次、寫入 9 次，失敗 0 次，服務端 Error 事件 0 筆。另有測試 `兩個獨立連線同時讀寫_不丟出鎖定錯誤`（400 筆寫入） |
| 15 | 停止時進行中的檢查在 5 秒內結束，不留殘缺紀錄 | 實測 | ✅ | 0.30 秒停止，4 個進行中的目標都沒有寫入紀錄。單元測試 `停止要求_取消進行中的檢查並在5秒內結束`、`停止要求_進行中的檢查取消_不寫入殘缺紀錄` |
| 16 | 核心邏輯單元測試至少 30 個，dotnet test 全數通過 | 執行測試 | ✅ | 201 / 201 通過 |
| 17 | README 內容 | 檢視 README | ✅ | 安裝步驟、設定說明、CLI 範例輸出、架構、狀態判定與通知規則、設計取捨、已知限制、測試憑證的產生方式 |
| 18 | GitHub Actions 在 Windows runner 建置並測試，綠燈，README 有徽章 | 檢視 Actions | ✅ | [run 38841788583](https://github.com/hn83320589/CertSentinel/actions/runs/38841788583)，徽章顯示 passing |

## 4. 版本差異說明

實機驗收在 `ceba38f` 進行，之後還有兩個程式修正：

| commit | 內容 | 對實機驗收結果的影響 |
|---|---|---|
| `b2b993d` | SMTP 不查詢憑證撤銷狀態 | 實機驗收的 SMTP 不加密，不受影響 |
| `4040636` | 寄信後目標被刪除時，略過寫入通知進度 | 驗收中沒有發生這個競態 |

兩者都有自動化測試涵蓋，並由 CI 在 `0dc0107` 驗證通過。

## 5. 需求追溯

### 5.1 功能需求

| 需求 | 結果 | 實作 | 驗證 |
|---|---|---|---|
| Worker Service，可由 SCM 啟動與停止 | ✅ | `Service/Program.cs`（`AddWindowsService`） | 實機 #1 #15 |
| 設定檔含間隔、並行上限、警告與緊急門檻、保留天數、SMTP、收件人 | ✅ | `CertSentinelSettings`、`appsettings.sample.json` | `SettingsTests`（9 個） |
| 啟動立即檢查一輪，之後依間隔重複，上一輪未完成不開始下一輪 | ✅ | `CheckWorker`（循序 await 搭配 `PeriodicTimer`） | `CheckWorkerTests`（4 個） |
| CLI 的 add、list、remove、enable、disable | ✅ | `CliApp` | `CliTests` |
| 三種格式正規化為主機加埠號，預設 443；重複拒絕；不合法不崩潰 | ✅ | `TargetParser` | `TargetParserTests`、實機 #2 |
| 清單存入 SQLite | ✅ | `SqliteTargetRepository`，`UNIQUE(host, port)` | `SqliteStoreTests` |
| 記錄到期（UTC）、剩餘天數、簽發者、指紋、名稱涵蓋（萬用字元單層）、鏈信任與原因 | ✅ | `TlsCertificateProbe`、`CertificateNames`、`HostnameMatcher` | TLS 整合測試 |
| 過期、不受信任、名稱不符仍記錄憑證資訊 | ✅ | 驗證 callback 一律接受；握手後續失敗也保留憑證資訊 | #4 |
| 每個目標 10 秒逾時，涵蓋 DNS、連線、握手 | ✅ | 單一 linked `CancellationTokenSource` | `伺服器不回應_記為逾時`、實機 |
| 連線失敗、逾時、握手失敗記為無法檢查並附原因；單一失敗不影響其他 | ✅ | `ProbeErrorKind`；`ProcessTargetAsync` 逐目標隔離例外 | #7、`單一目標發生非預期例外_不影響其他目標` |
| 五種狀態與通知規則（惡化、Error 3 次、恢復、不重複、24 小時提醒、重啟、寄信失敗） | ✅ | `NotificationPolicy`（純函式）、`CheckCoordinator` | #8–#11、實機 |
| status 表格（名稱、主機與埠號、狀態、剩餘天數、本機 yyyy-MM-dd 到期日、最後檢查）與 `--json` | ✅ | `Views.Status` | #12 |
| check 立即檢查、寫入歷史、不觸發通知 | ✅ | `CheckCoordinator.CheckOnceAsync` | `手動檢查_寫入歷史但不寄信也不改變通知進度` |
| history `--days N`，指紋變動標示「憑證已更新」 | ✅ | `CertificateRenewal` | `CertificateRenewalTests`、`CliTests` |
| 每次結果寫入 SQLite；服務定期清理，不影響狀態與通知進度 | ✅ | `PurgeHistoryAsync`（每輪結束後執行） | #13 |
| 停止時取消檢查，5 秒內結束 | ✅ | `ShutdownTimeout = 5s`，取消權杖傳到每一層 | #15 |
| 日誌用 Microsoft.Extensions.Logging，寫入事件檢視器，每個目標結果與每封通知都有紀錄 | ✅ | `LoggerMessage` 事件 1100、1200、1201 | 實機事件紀錄 |

### 5.2 技術與平台限制

| 限制 | 結果 | 說明 |
|---|---|---|
| C#、.NET 9、Worker Service 加 Console，不用 WPF、WinForms、WinUI | ✅ | `net9.0`；`global.json` 鎖定 9.0.x SDK |
| SQLite 版本與 .NET 9 對齊 | ✅ | Microsoft.Data.Sqlite 9.0.21；Microsoft.Extensions.* 全為 9.0.21 |
| 服務與 CLI 共用資料庫，處理兩個程序同時存取 | ✅ | WAL、`busy_timeout`、`BEGIN IMMEDIATE`；實機 #14 |
| 核心邏輯放在純 net9.0 Class Library；時間與 SMTP 抽象化 | ✅ | `CertSentinel.Core` 只依賴 Logging.Abstractions；`IEmailSender`；時間用 BCL 的 `TimeProvider`（見 §7） |
| xUnit；loopback TLS 整合測試，動態產生憑證，不依賴外部網路 | ✅ | `LoopbackTlsServer`、`TestCertificates` |
| CLI 用 System.CommandLine 或自行解析 | ✅ | System.CommandLine 2.0.13 |
| 不使用付費授權套件 | ✅ | MailKit / MimeKit（MIT）、System.CommandLine（MIT）、Microsoft.*（MIT）、xUnit（Apache-2.0） |
| 除目標與 SMTP 外不呼叫外部網路 | ✅ | TLS 探測關閉 OCSP、CRL 與 AIA 下載；SMTP 關閉撤銷檢查（`b2b993d`）；沒有遙測 |

### 5.3 交付物

| 交付物 | 結果 | 位置 |
|---|---|---|
| Solution（Worker Service、CLI、Core、Tests，另有 Infrastructure） | ✅ | `CertSentinel.sln` |
| README（功能、建置與安裝、設定、CLI 範例、設計取捨、已知限制） | ✅ | `README.md` |
| 範例設定檔、服務安裝與解除安裝腳本 | ✅ | `appsettings.sample.json`、`scripts/` |
| 測試憑證產生方式說明，私鑰不提交 | ✅ | README「測試與測試憑證」；`TestCertificates.cs` |
| CI（GitHub Actions，setup-dotnet 9.0.x） | ✅ | `.github/workflows/ci.yml` |

### 5.4 假設事項

| 假設 | 結果 | 說明 |
|---|---|---|
| 只做直接 TLS；不處理 STARTTLS、OCSP、CRL、Proxy | ✅ | |
| 只評估 leaf；鏈信任只作資訊記錄 | ✅ | 鏈信任不影響分級 |
| 剩餘天數向下取整，判定順序 Expired → Critical → Warning → OK | ✅ | `StatusClassifier` 使用 `Math.Floor` |
| 名稱不符、不受信任不改變分級，但寫進結果與通知內文 | ✅ | 實機通知信含「鏈信任：不受信任：NotTimeValid…」 |
| 同一主機不同埠號視為不同目標 | ✅ | `同一主機不同埠號_視為不同目標` |
| 時間以 UTC 儲存，只在 CLI 顯示時轉為本機時間 | ✅ | ISO 8601 UTC；`Views` 負責轉換 |
| SMTP 單一寄件地址、固定收件人；無驗證或帳密驗證加 TLS | ✅ | `SmtpSecurity` 可選 None、StartTls、SslOnConnect |
| 資料庫與日誌預設位置 `%ProgramData%\CertSentinel\` | ✅ | 資料庫在該位置；日誌寫入事件檢視器（README 說明） |

## 6. 自動化測試結果（commit `0dc0107`）

執行時間 2026-10-16 10:40:47～10:40:54（7 秒）

| 測試類別 | 案例數 | 通過 | 範圍 |
|---|---:|---:|---|
| `Core.TargetParserTests` | 46 | 46 | 三種格式、大小寫、IDN、IPv6、IPv4 簡寫、各種不合法輸入 |
| `Core.NotificationPolicyTests` | 33 | 33 | 惡化、Error×3、恢復、提醒與容差、寄信失敗重試 |
| `Core.StatusClassifierTests` | 20 | 20 | 門檻邊界、自訂門檻、嚴重度比較 |
| `Cli.CliTests` | 17 | 17 | 全部指令、錯誤訊息、JSON、憑證已更新 |
| `Infrastructure.SqliteStoreTests` | 17 | 17 | CRUD、交易、保留清理、併發讀寫、刪除競態 |
| `Core.CheckCoordinatorTests` | 15 | 15 | 一輪檢查、並行上限、重啟、重試、提醒抖動、取消 |
| `Core.HostnameMatcherTests` | 15 | 15 | 萬用字元單一層級 |
| `Tls.TlsCertificateProbeTests` | 12 | 12 | SNI、過期、自簽、名稱不符、CN fallback、逾時、拒絕、握手失敗、DNS、取消 |
| `Infrastructure.SettingsTests` | 9 | 9 | 預設值、驗證、繫結 |
| `Infrastructure.SmtpEmailSenderTests` | 5 | 5 | 寄送內容、連線失敗、不查詢撤銷狀態 |
| `Cli.TextTableTests` | 5 | 5 | 中文字寬對齊 |
| `Service.CheckWorkerTests` | 4 | 4 | 立即執行、不重疊、5 秒內停止、單輪例外不中止 |
| `Core.CertificateRenewalTests` | 2 | 2 | 指紋變動標示 |
| `Tls.CheckRoundIntegrationTests` | 1 | 1 | 同一輪混合各種失敗 |
| **合計** | **201** | **201** | |

重新產生方式：

```powershell
dotnet test CertSentinel.sln -c Release --logger "trx;LogFileName=results.trx"
```

## 7. 缺陷紀錄

| # | 缺陷 | 發現方式 | 嚴重度 | 修正 commit | 防止回歸 |
|---|---|---|---|---|---|
| 1 | 安裝腳本：`%ProgramData%\CertSentinel` 可能被一般使用者預先建立，或放入 junction，藉此植入設定檔 | 背景安全審查 | 高 | `28f41df`、`7245216` | PS 5.1 與 7 的情境驗證（未信任資料夾、junction、多餘 ACE） |
| 2 | 解除安裝：PS 5.1 的 `Remove-Item -Recurse` 會跟隨 junction 刪除目標內容 | 背景安全審查 | 高 | `28f41df` | PS 5.1 與 7 的 junction 刪除驗證 |
| 3 | CI 使用 runner 預裝的 SDK 10，新的分析規則讓建置失敗 | CI | 中 | `29bc4b1` | `global.json` 鎖定 SDK |
| 4 | 事件檢視器來源名稱沒有生效，服務日誌寫不進事件檢視器 | **實機驗收** | 高 | `36e0bf0` | 實機事件紀錄 |
| 5 | 保留清理在存在從未檢查過的目標時一筆都不刪（`NOT IN` 遇到 NULL） | 外部審查 | 高 | `544a8ee` | `保留清理_存在從未檢查過的目標時_仍刪除過期歷史`（2 個） |
| 6 | 24 小時提醒因各輪探測耗時不同而順延一整輪 | 外部審查 | 中 | `fe21a17` | `探測耗時每輪不同_…`、抖動容差測試（5 個） |
| 7 | 提醒信內文寫死「每 24 小時」 | 外部審查 | 低 | `fe21a17` | `提醒信內文使用設定的提醒間隔` |
| 8 | `DatabasePath` 空白時直接崩潰，看不到友善訊息 | 外部審查 | 低 | `08a3e1d` | 手動重現驗證 |
| 9 | IPv4 簡寫（如 `10` 被轉成 `0.0.0.10`）被悄悄轉成其他位址 | 外部審查 | 中 | `a2ba7e1` | `IPv4簡寫或數字主機名稱_…`（14 個） |
| 10 | MailKit 預設查詢 SMTP 憑證撤銷狀態，違反「不呼叫外部網路」 | 獨立稽核 | 中 | `b2b993d` | `不檢查SMTP憑證撤銷狀態_…`（3 個） |
| 11 | 寄信後目標被刪除時觸發外鍵錯誤，信已寄出卻記為失敗 | 獨立稽核 | 低 | `4040636` | `目標已被刪除時儲存通知進度_不寫入也不丟例外` |
| 12 | PowerShell 腳本缺 BOM，PS 5.1 讀取中文會亂碼 | 自我檢查 | 低 | `8f067a7` | PS 5.1 parser 驗證 |

缺陷 #4、#5 都是原本的自動化測試沒有涵蓋的情境：一個要實機才會出現，另一個是測試資料剛好都有歷史紀錄。這說明了實機驗收與外部審查的必要。

## 8. 偏差與設計決策

以下各點都符合需求的字面或合理解讀，但特別列出供審查判斷：

| 項目 | 說明 |
|---|---|
| 額外的「恢復檢查但狀態非 OK」通知 | 已通知「無法檢查」後，若恢復檢查但狀態是 Warning 或 Critical，會寄一封狀態通知。需求只列了三種寄信情境，這一種是設計階段經確認的決策（設計文件的 D6），理由是收件人最後只知道「無法檢查」，需要被告知實際狀態 |
| 時間抽象用 `TimeProvider` | 這是 BCL 的抽象類別，不是字面上的「介面」。它是 .NET 8 起的標準時間抽象，`PeriodicTimer` 與 `FakeTimeProvider` 都直接支援，測試可完全控制時間（設計決策 D4） |
| `check` 只能檢查已在清單中的目標 | 檢查結果要寫入歷史，而歷史需要對應到某個目標 |
| CLI 需以系統管理員身分執行 | 資料夾 ACL 的刻意限制，用來防止設定檔被植入（缺陷 #1） |

## 9. 已知限制

完整清單見 README「已知限制」，與測試相關的重點如下：

- **`status` 是快照**：顯示的是最後一次檢查的結果，不會在查詢當下重算。
- **停止時限的理論邊界**：SQLite 寫入衝突時最多等待 10 秒，理論上可能超過 5 秒的停止時限。實測兩次分別是 0.31 秒與 0.30 秒。
- **多位址連線**：主機解析出多個 IP 時依序嘗試，沒有 Happy Eyeballs。
- **通知為 at-least-once**：寄出後、寫入資料庫前若程序中斷，可能重寄一次。
- **未實機驗證的部分**：
  - 真實 SMTP 伺服器的 STARTTLS 或 465 寄信沒有在實機上驗證過，只有 loopback 迷你 SMTP 的整合測試。
  - 實機上沒有發生換憑證，所以「憑證已更新」標示只由自動化測試驗證。
