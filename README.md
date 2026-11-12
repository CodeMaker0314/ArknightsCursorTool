# 明日方舟 PC 系統滑鼠指標工具

Windows x64 桌面工具：在遊戲安裝資源及熱更新資源中檢查 UnityFS 封包；只有精確驗證含有 `dyn/mouse/pc_mouse_mgr.prefab` 的封包才可停用。

## 使用方式

啟動工具時 Windows 會顯示 UAC 管理員權限確認；只有按下「是」後工具才會開啟。

1. 執行 `ArknightsCursorTool.exe`，工具會先檢查執行中的遊戲程序、`D:\GRYPHLINK\games\Arknights_TC`，以及固定磁碟根目錄下的常見 Gryphlink/Gryphline 安裝路徑。也可按「自動偵測」重試；若找不到，按「手動選擇…」選取包含 `Arknights_Data` 的遊戲資料夾。
2. 在「資源備份與恢復」按「掃描並驗證資源」。掃描進度顯示在狀態列，不會每隔數秒重複寫入操作記錄。工具只列舉下列目錄第一層的 `.bin` 與 `.ab`，也會依 UnityFS 標頭辨識匿名 `.bin`：
   - `Arknights_Data/StreamingAssets/AB/Windows/anon`、`mouse`
   - `Arknights_Data/PersistentData/Bundles/anon`、`mouse`
   `mouse` 子目錄為可選；原生安裝沒有該目錄時會正常略過，仍會掃描存在的 `anon` 目錄。
3. 確認資源路徑及 SHA-256 後，勾選要停用的項目；只有勾選已驗證資源後，停用按鈕才會啟用。按「備份並停用勾選項目」後，遊戲檔案會加上 `.disabled` 後綴。
4. 要還原時按「安全恢復」。若原路徑已出現遊戲更新或修復產生的新檔，工具會保留它。

若備份記錄中的原檔仍在，掃描會重新驗證該原檔；精確命中目標的項目仍可再次停用，並重用 SHA-256 相符的既有備份。此情況不算「可恢復」項目，因為原檔目前已存在。

## 游標映射模式

選好遊戲資料夾後，在第 3 區按「啟動 Windows 指標映射」可在遊戲前景視窗中顯示 Windows 目前的箭頭游標。按「停止指標映射」或 `Ctrl+Shift+F12` 可停止映射並恢復原指標。工具不再提供 `.cur`/`.ani` 檔案映射。操作記錄區有「清除紀錄」按鈕；映射只在狀態改變時新增記錄，正常移動指標不會刷出大量訊息。

映射透過透明、可穿透點擊的置頂視窗顯示游標，並在連接遊戲輸入執行緒時隱藏原指標。記錄只在映射狀態改變時新增，例如等待遊戲、成功連接、失去前景或發生錯誤；游標正常移動時不新增記錄。若顯示無法連接，請讓遊戲與工具以相同權限執行。遊戲若以獨佔全螢幕呈現，Windows 置頂覆蓋視窗可能無法顯示；切換無邊框或視窗模式再使用。遊戲若另外繪製軟體游標，映射視窗無法移除遊戲畫面中的那層圖像。

掃描亦會辨識範本建立的 `.system-cursor-disabled-<SHA-256 前綴>` 與舊版 `.disabled-by-codex` 檔案。恢復前會重新驗證資源內容；有同名原檔時會保留原檔，不覆寫。

備份與 SHA-256 清單存於 `%LOCALAPPDATA%\ArknightsCursorTool`，不放在遊戲資料夾中。恢復前會比對備份及停用檔案的 SHA-256。遊戲執行時工具會阻止掃描以外的檔案修改操作。

掃描採逐檔列舉，不會先載入整個目錄清單；期間會顯示目錄列舉狀態、已檢查檔案數及目前處理的資源，可按「取消掃描」停止。完成摘要會列出候選檔案數、解析成功數及略過原因。掃描本身不會修改遊戲檔案。安全上限為單一封包 32 MiB、索引或單一解壓區塊 16 MiB、每包總解壓量 512 MiB；超過上限會略過，不會停用。

## 封包格式與相容性

目前驗證器支援 UnityFS 格式 6–8，以及未壓縮、LZ4、LZ4HC 和明日方舟使用的類型 4/5 LZ4 變體區塊。遇到其他版本、LZMA 或未知壓縮格式時會拒絕修改，並在操作記錄中說明。工具不會依檔名猜測封包內容或停用資源。

台服 PC 客戶端的封包可能不直接保存可搜尋的 prefab 路徑。當 `app.info` 確認是 Gryphline 台服，且安裝或熱更新 `.idx` 索引含有完整目標路徑時，工具會搜尋封包解壓內容中的長度欄位 `PCMouseMgr`，並將符合項目標示為「台服候選：PCMouseMgr（不可停用）」。這只是線索，不能證明該封包就是游標管理器；工具不允許停用此類候選。只有精確找到 prefab 路徑的項目才標為「已驗證」並可停用。

參照專案最初只在國服 PC 客戶端實測；台服候選是本機索引與封包識別字線索，尚不足以安全停用。若先前版本曾停用 `PCMouseMgr` 候選，請關閉遊戲後使用「安全恢復」還原，再提供掃描記錄以便定位其他封包。此工具透過備份並停用已精確驗證的資源來讓遊戲保留系統指標，不會注入遊戲程序或更改 Windows 指標設定。

## 參考與授權

封包掃描範圍、讀取上限及 UnityFS 驗證方式參考 [assfafa/arknights-system-cursor](https://github.com/assfafa/arknights-system-cursor)。該專案採 MIT License；授權文字見 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 建置

需要 .NET 8 SDK。在 Windows x64 建置小型單檔；執行目標電腦需安裝 .NET 8 Desktop Runtime：

```powershell
dotnet publish .\ArknightsCursorTool.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishTrimmed=false
```

輸出位置：`bin\Release\net8.0-windows\win-x64\publish\ArknightsCursorTool.exe`

此發佈模式不會把 .NET Runtime 包進 EXE，因此檔案較小。若要建立不依賴預先安裝 Runtime 的版本，將 `--self-contained false` 改為 `--self-contained true`，並加入 `-p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`；產生的 EXE 會較大。
