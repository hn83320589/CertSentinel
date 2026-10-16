# 驗收紀錄

本文件保存實機驗收時的**實際輸出**（未經修飾），作為「實測」類驗收標準的證據。

- 日期：2026-10-16
- 環境：Windows 11 Pro 10.0.26200、.NET SDK 9.0.315
- 安裝方式：`scripts\install-service.ps1`（自含 Runtime，服務帳號 `NT AUTHORITY\LocalService`）
- 測試設定：SMTP 暫時指向 `127.0.0.1:2525`（沒有程式在聽，用來觀察寄送失敗與重試），檢查間隔暫設 1 分鐘
- 測試目標：`github.com`、`expired.badssl.com`、`wrong.host.badssl.com`、`self-signed.badssl.com`、`localhost:1`（拒絕連線）、`127.0.0.1:9999`（只接受連線、永不回應，用來模擬「檢查進行中」）

> **版本說明**：以下實機輸出錄於 commit `36e0bf0`。之後依外部審查修正了保留清理 SQL、提醒時間基準、IPv4 簡寫與啟動時的設定驗證（commit `544a8ee`～`08a3e1d`），這些修正都由自動化測試涵蓋，並由 CI 驗證。
>
> CI 的每次執行都會在 Actions 的 **Summary** 頁面列出各測試專案的通過數：<https://github.com/hn83320589/CertSentinel/actions/workflows/ci.yml>

---

## #1 服務可安裝、啟動、停止，啟動後立即產生第一輪檢查紀錄

```
PS> Get-Service CertSentinel | Format-List Name, DisplayName, Status, StartType
Name        : CertSentinel
DisplayName : CertSentinel 憑證哨兵
Status      : Stopped
StartType   : Automatic

StartName : NT AUTHORITY\LocalService
PathName  : "C:\Program Files\CertSentinel\CertSentinel.Service.exe"

PS> Start-Service CertSentinel   （量測）
Start-Service 完成: 0.9s, 狀態 Running
6 個目標全部有紀錄: 11.5s 內        ← 慢在 127.0.0.1:9999 要等滿 10 秒逾時
```

```
PS> certsentinel status
名稱                        主機:埠號                   狀態     剩餘天數  到期日      最後檢查          備註
127.0.0.1:9999              127.0.0.1:9999              Error    -         -           2026-10-16 09:54  連線或握手超過 10 秒未完成。
expired.badssl.com:443      expired.badssl.com:443      Expired  -4205     2015-04-13  2026-10-16 09:53  不受信任
github.com:443              github.com:443              OK       44        2026-11-30  2026-10-16 09:53
localhost:1                 localhost:1                 Error    -         -           2026-10-16 09:53  連線被拒絕(該埠號沒有服務在聽)。
self-signed.badssl.com:443  self-signed.badssl.com:443  OK       727       2028-10-13  2026-10-16 09:53  不受信任
wrong.host.badssl.com:443   wrong.host.badssl.com:443   OK       73        2026-12-29  2026-10-16 09:53  名稱不符
```

### 事件檢視器（應用程式記錄，來源 CertSentinel）

這份紀錄是用安裝腳本升級服務時錄下的，所以前面先有一筆停止事件，接著才是重新啟動：

```
時間     ProviderName   Id LevelDisplayName 訊息
09:56:50 CertSentinel    0 資訊             Service stopped successfully.
09:56:55 CertSentinel  100 資訊             CertSentinel 服務已啟動,檢查間隔 1 分鐘
09:56:55 CertSentinel    0 資訊             Service started successfully.
09:56:55 CertSentinel 1000 資訊             開始一輪檢查,共 6 個啟用目標
09:56:56 CertSentinel 1100 資訊             檢查 …(每個目標一筆)
09:56:59 CertSentinel 1201 警告             … 通知寄送失敗 …;下一輪重試
09:57:08 CertSentinel 1001 資訊             本輪檢查結束:檢查 6 個、無法檢查 2 個、寄出通知 0 封、寄送失敗 3 封
09:57:08 CertSentinel 1300 資訊             已清理 … 筆早於 … 的歷史紀錄
```

完整的事件內容範例：

```
--- 1100
Category: CertSentinel.Core.Checks.CheckCoordinator
EventId: 1100

檢查 127.0.0.1:9999:狀態 Error,剩餘 (null) 天,錯誤 連線或握手超過 10 秒未完成。

--- 1201
Category: CertSentinel.Core.Checks.CheckCoordinator
EventId: 1201

Unreachable 通知寄送失敗(127.0.0.1:9999):[CertSentinel] 無法檢查 — 127.0.0.1:9999;下一輪重試

--- 1001
本輪檢查結束:檢查 6 個、無法檢查 2 個、寄出通知 0 封、寄送失敗 3 封
```

> 實測中發現並修正的問題：原本把事件來源名稱放在 `appsettings.json` 的 `Logging:EventLog:SourceName`，但 `EventLogSettings` 不會從設定檔讀取這個值，服務模式下日誌因此寫不進事件檢視器。已改為在 `Program.cs` 中指定（commit `36e0bf0`），上面的輸出就是修正後錄的。

---

## #14 服務與 CLI 同時存取資料庫不丟出鎖定錯誤

服務每 1 分鐘一輪、每輪約 13 秒。90 秒內，CLI 連續執行 `status` 與 `history`，並穿插執行會寫入資料庫的 `check`：

```
讀取 160 次、寫入 8 次、失敗 0 次
同期間服務事件:開始一輪 2 次、結束 2 次
服務 Error/Critical 事件數:0
```

另外，在主控台模式下也做過一次：服務跑兩輪的 75 秒內執行 `status` 203 次，失敗 0 次。

---

## #15 停止服務時，進行中的檢查在 5 秒內結束，不留下殘缺紀錄

先等事件 1000（新一輪開始），1.5 秒後下達停止。此時 `127.0.0.1:9999` 還卡在 10 秒逾時中：

```
偵測到新一輪開始:09:59:55.848
Stop-Service 花費:0.31 秒,狀態:Stopped
127.0.0.1:9999 歷史筆數:停止前 6 → 停止後 6(應相同,被中斷的檢查不寫入)
該目標最新紀錄時間:09:59:05(上一輪)/ 本輪開始:09:59:55

時間           Id LevelDisplayName 訊息
09:59:55.848 1000 資訊             開始一輪檢查,共 6 個啟用目標
09:59:55.946 1100 資訊             檢查 github.com:443:狀態 OK,剩餘 44 天,錯誤 (null)
09:59:56.426 1100 資訊             檢查 expired.badssl.com:443:狀態 Expired,剩餘 -4205 天,錯誤 (null)
09:59:56.444 1100 資訊             檢查 self-signed.badssl.com:443:狀態 OK,剩餘 727 天,錯誤 (null)
09:59:56.509 1100 資訊             檢查 wrong.host.badssl.com:443:狀態 OK,剩餘 73 天,錯誤 (null)
09:59:58.287    0 資訊             Application is shutting down...
09:59:58.291  101 資訊             收到停止要求,已取消進行中的檢查
```

`localhost:1`（等待連線被拒）與 `127.0.0.1:9999`（等待逾時）都在進行中就被取消，沒有寫入紀錄。

---

## 驗收結束後的清理

- 已停止測試用的監聽程式。
- 已移除所有測試目標（連同歷史紀錄）。
- 已還原 `%ProgramData%\CertSentinel\appsettings.json`。
- 服務維持停止。
