# 實機驗收紀錄（原始輸出）

本文件保存實機驗收時的**實際輸出**（未經修飾），作為「實測」類驗收標準的證據。整理後的結論見 [TEST-REPORT.md](TEST-REPORT.md)。

| 項目 | 內容 |
|---|---|
| 日期 | 2026-10-16 10:33–10:38（UTC+8） |
| 受測版本 | commit `ceba38f`，以 `scripts\install-service.ps1 -PublishDir` 升級安裝（自含 Runtime） |
| 環境 | Windows 11 Pro 10.0.26200、.NET SDK 9.0.315 |
| 服務帳號 | `NT AUTHORITY\LocalService`，延遲自動啟動 |
| SMTP | 本機測試用 SMTP 接收程式 `127.0.0.1:2525`，把收到的信存成 `.eml`，不對外寄送。一開始故意**不啟動**，用來觀察寄送失敗與重試 |
| 檢查間隔 | 暫設 1 分鐘（方便在數分鐘內觀察多輪） |

測試目標如下：

| 目標 | 用途 |
|---|---|
| `github.com` | 正常 |
| `expired.badssl.com` | 已過期 |
| `wrong.host.badssl.com` | 名稱不符（憑證為 `*.badssl.com`，不涵蓋兩層子網域） |
| `self-signed.badssl.com` | 自簽憑證，不受信任 |
| `localhost:1` | 拒絕連線 |
| `127.0.0.1:9999` | 本機監聽程式，只接受連線、永不回應，用來模擬逾時與「檢查進行中」 |

> 實機驗收之後又有兩個程式修正：`b2b993d` 關閉 SMTP 撤銷檢查，`4040636` 處理寄信後目標被刪除的競態。這次驗收的 SMTP 不加密，所以前者不影響結果；後者是驗收中沒有發生的競態。兩者都有自動化測試涵蓋，並經 CI 驗證。

---

## #2 add：三種格式正規化、重複、不合法

```
> certsentinel add github.com --name "GitHub 官網"
已新增 GitHub 官網(github.com:443)。
> certsentinel add expired.badssl.com:443
已新增 expired.badssl.com:443。
> certsentinel add https://wrong.host.badssl.com/path?x=1
已新增 wrong.host.badssl.com:443。
> certsentinel add https://GITHUB.com/
錯誤:目標 github.com:443 已存在(名稱:GitHub 官網),同一主機與埠號不可重複新增。
exit=1
> certsentinel add http://example.com
錯誤:目標格式不合法:不支援的 scheme「http」:僅支援 https:// 或不帶 scheme 的主機名稱。
exit=1
> certsentinel add example.com:70000
錯誤:目標格式不合法:埠號「70000」不合法:埠號必須是 1–65535 的整數。
exit=1
> certsentinel add 10
錯誤:目標格式不合法:「10」不是完整的 IPv4 位址:IPv4 請輸入四段十進位數字(例如 10.0.0.1)。
exit=1
```

## #1 啟動後立即產生第一輪紀錄

```
Start-Service:0.83 秒 / 狀態:Running / 6 個目標全部有紀錄:11.7 秒
（慢在 127.0.0.1:9999 要等滿 10 秒逾時）

時間           Id LevelDisplayName 訊息
10:33:52.350    0 資訊   Service started successfully.
10:33:52.350  100 資訊   CertSentinel 服務已啟動,檢查間隔 1 分鐘
10:33:52.461 1000 資訊   開始一輪檢查,共 6 個啟用目標
10:33:52.944 1100 資訊   檢查 github.com:443:狀態 OK,剩餘 44 天,錯誤 (null)
10:33:53.265 1100 資訊   檢查 wrong.host.badssl.com:443:狀態 OK,剩餘 73 天,錯誤 (null)
10:33:53.331 1100 資訊   檢查 self-signed.badssl.com:443:狀態 OK,剩餘 727 天,錯誤 (null)
10:33:53.336 1100 資訊   檢查 expired.badssl.com:443:狀態 Expired,剩餘 -4205 天,錯誤 (null)
10:33:55.543 1201 警告   Deteriorated 通知寄送失敗(expired.badssl.com:443):[CertSentinel] Expired — expired.badssl.com:443;下一輪重試
10:33:56.751 1100 資訊   檢查 localhost:1:狀態 Error,剩餘 (null) 天,錯誤 連線被拒絕(該埠號沒有服務在聽)。
10:34:02.967 1100 資訊   檢查 127.0.0.1:9999:狀態 Error,剩餘 (null) 天,錯誤 連線或握手超過 10 秒未完成。
10:34:02.974 1001 資訊   本輪檢查結束:檢查 6 個、無法檢查 2 個、寄出通知 0 封、寄送失敗 1 封
10:34:02.982 1300 資訊   已清理 0 筆早於 2026-07-18T02:34:02.9758884+00:00 的歷史紀錄
```

## #8 #11 寄信失敗重試、Error 連續 3 次才寄（10:34 啟動 SMTP 接收程式）

```
10:33:52 1000 開始一輪檢查(第 1 輪)
10:33:55 1201 Deteriorated 通知寄送失敗(expired.badssl.com:443);下一輪重試      ← SMTP 未啟動
10:34:02 1001 本輪檢查結束:… 寄出通知 0 封、寄送失敗 1 封
10:34:52 1000 開始一輪檢查(第 2 輪)
10:34:53 1200 已寄出 Deteriorated 通知(expired.badssl.com:443)                  ← 重試成功
10:35:02 1001 本輪檢查結束:… 寄出通知 1 封、寄送失敗 0 封
10:35:52 1000 開始一輪檢查(第 3 輪)
10:35:56 1200 已寄出 Unreachable 通知(localhost:1)                              ← 連續第 3 次 Error
10:36:02 1200 已寄出 Unreachable 通知(127.0.0.1:9999)                           ← 連續第 3 次 Error
10:36:02 1001 本輪檢查結束:… 寄出通知 2 封、寄送失敗 0 封
```

SMTP 接收程式收到的信件（共 3 封，與事件紀錄一致）：

```
=== 001-103453.eml
From: certsentinel@example.test  To: ops@example.test
Subject: [CertSentinel] Expired — expired.badssl.com:443
通知類型:狀態惡化
目標名稱:expired.badssl.com:443
主機與埠號:expired.badssl.com:443
目前狀態:Expired
檢查時間:2026-10-16 02:34 UTC(本機 2026-10-16 10:34)

到期時間:2015-04-12 23:59 UTC(本機 2015-04-13 07:59)
剩餘天數:-4205
主體:CN=*.badssl.com, OU=PositiveSSL Wildcard, OU=Domain Control Validated
簽發者:CN=COMODO RSA Domain Validation Secure Server CA, O=COMODO CA Limited, L=Salford, S=Greater Manchester, C=GB
SHA-256 指紋:BA105CE02BAC76888ECEE47CD4EB7941653E9AC993B61B2EB3DCC82014D21B4F
涵蓋名稱:*.badssl.com, badssl.com
名稱比對:相符
鏈信任:不受信任:NotTimeValid: 與目前的系統時鐘或簽署檔案的時間戳記核對時，所需的憑證不在有效日期內。

— 此信由 CertSentinel 自動寄出。

=== 002-103556.eml
Subject: [CertSentinel] 無法檢查 — localhost:1
通知類型:連續無法檢查
目前狀態:Error
錯誤類型:ConnectionRefused
錯誤原因:連線被拒絕(該埠號沒有服務在聽)。
連續失敗次數:3

=== 003-103602.eml
Subject: [CertSentinel] 無法檢查 — 127.0.0.1:9999
```

## #14 服務與 CLI 同時存取資料庫

服務跑第 2、3 輪的期間，CLI 連續執行 `status` 與 `history --json`，並每 20 次讀取插入一次會寫入資料庫的 `check github.com`：

```
#14:98 秒內讀取 196 次、寫入 9 次、失敗 0 次;期間服務完成第 2、3 輪
服務 Error/Critical 事件:0
```

## #10 服務重啟後不重複寄出已通知過的狀態

```
重啟完成:Running
重啟前信件 3 封 → 重啟後第一輪結束時 3 封

10:36:24  100 CertSentinel 服務已啟動,檢查間隔 1 分鐘
10:36:24 1000 開始一輪檢查,共 6 個啟用目標
10:36:34 1001 本輪檢查結束:檢查 6 個、無法檢查 2 個、寄出通知 0 封、寄送失敗 0 封
```

## #15 停止時取消進行中的檢查，5 秒內結束

在新一輪開始（事件 1000）後 1.5 秒下達停止：

```
本輪開始 10:37:24.241;Stop-Service 花費 0.30 秒,狀態 Stopped
127.0.0.1:9999 歷史:4 → 4;localhost:1 歷史:4 → 4

10:37:24.241 1000 開始一輪檢查,共 6 個啟用目標
10:37:24.334 1100 檢查 github.com:443:狀態 OK,剩餘 44 天,錯誤 (null)
10:37:25.971 1100 檢查 wrong.host.badssl.com:443:狀態 OK,剩餘 73 天,錯誤 (null)
10:37:26.745    0 Application is shutting down...
10:37:26.751  101 收到停止要求,已取消進行中的檢查
10:37:26.753    0 Service stopped successfully.
```

停止當下仍有 4 個目標在進行中：`expired.badssl.com`、`self-signed.badssl.com`、`localhost:1`、`127.0.0.1:9999`。它們都沒有寫入紀錄，歷史筆數不變。

## #12 status / check / history 輸出與 JSON

```
> certsentinel status
名稱                        主機:埠號                   狀態     剩餘天數  到期日      最後檢查          備註
GitHub 官網                 github.com:443              OK       44        2026-11-30  2026-10-16 10:37
expired.badssl.com:443      expired.badssl.com:443      Expired  -4205     2015-04-13  2026-10-16 10:36  不受信任
localhost:1                 localhost:1                 Error    -         -           2026-10-16 10:36  連線被拒絕(該埠號沒有服務在聽)。
self-signed.badssl.com:443  self-signed.badssl.com:443  OK       727       2028-10-13  2026-10-16 10:36  不受信任
wrong.host.badssl.com:443   wrong.host.badssl.com:443   OK       73        2026-12-29  2026-10-16 10:37  名稱不符
靜默伺服器                  127.0.0.1:9999              Error    -         -           2026-10-16 10:36  連線或握手超過 10 秒未完成。

> certsentinel check wrong.host.badssl.com
目標          wrong.host.badssl.com:443(wrong.host.badssl.com:443)
狀態          OK
檢查時間      2026-10-16 10:37
剩餘天數      73
到期日        2026-12-29 04:02(UTC 2026-12-28 20:02)
生效日        2026-09-30 04:02
主體          CN=*.badssl.com
簽發者        CN=YR1, O=Let's Encrypt, C=US
SHA-256 指紋  101154813E8FDAB6BA4FAB575185C1A13944BCEC1D849AE2CD94A073494997E3
涵蓋名稱      *.badssl.com, badssl.com
名稱比對      名稱不符(憑證未涵蓋 wrong.host.badssl.com)
鏈信任        受系統信任

> certsentinel history expired.badssl.com --days 1
檢查時間          來源     狀態     剩餘天數  到期日      指紋               備註
2026-10-16 10:36  Service  Expired  -4205     2015-04-13  BA105CE02BAC7688…
2026-10-16 10:35  Service  Expired  -4205     2015-04-13  BA105CE02BAC7688…
2026-10-16 10:34  Service  Expired  -4205     2015-04-13  BA105CE02BAC7688…
2026-10-16 10:33  Service  Expired  -4205     2015-04-13  BA105CE02BAC7688…
```

以 `System.Text.Json.JsonDocument.Parse` 驗證各指令的 `--json` 輸出：

```
status --json → 合法 JSON(2116 字元)
check github.com --json → 合法 JSON(743 字元)
history github.com --days 1 --json → 合法 JSON(5451 字元)
list --json → 合法 JSON(1031 字元)
```

實機上沒有換憑證的目標，所以「憑證已更新」標示由自動化測試驗證（`CliTests.history_指紋變動時標示憑證已更新_並依天數篩選`）。

## 驗收結束後的清理

- 已停止 SMTP 接收程式與靜默監聽程式，2525、9999 埠都已不在監聽。
- 已移除所有測試目標（連同歷史紀錄）。
- 已還原 `%ProgramData%\CertSentinel\appsettings.json`。
- 服務維持停止。

---

## 附錄：第一次實機驗收（commit `36e0bf0`）

第一次實機驗收在 commit `36e0bf0`（外部審查的修正之前）進行，結果與這次一致：

| 驗收項目 | 結果 |
|---|---|
| #1 啟動 | 0.9 秒，第一輪 11.5 秒內全部完成 |
| #14 併發 | 讀取 160 次、寫入 8 次，失敗 0 次 |
| #15 停止 | 0.31 秒 |

那次也發現並修正了「事件檢視器來源名稱沒有生效」的問題。
