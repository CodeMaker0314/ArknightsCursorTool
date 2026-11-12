using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArknightsCursorTool;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly TextBox _rootBox = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Button _autoDetectButton = new() { Text = "自動偵測", AutoSize = true };
    private readonly Button _chooseButton = new() { Text = "手動選擇…", AutoSize = true };
    private readonly Button _scanButton = new() { Text = "掃描並驗證資源", AutoSize = true, Enabled = false };
    private readonly Button _disableButton = new() { Text = "備份並停用勾選項目", AutoSize = true, Enabled = false };
    private readonly Button _restoreButton = new() { Text = "安全恢復", AutoSize = true, Enabled = false };
    private readonly Button _mapWindowsCursorButton = new() { Text = "啟動 Windows 指標映射", AutoSize = true, Enabled = false };
    private readonly Button _clearLogButton = new() { Text = "清除紀錄", AutoSize = true };
    private readonly Button _cancelScanButton = new() { Text = "取消掃描", Dock = DockStyle.Fill, Enabled = false };
    private readonly ProgressBar _scanProgress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24, Visible = false };
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = false, RowHeadersVisible = false, AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = Color.White };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label _status = new() { Text = "請先選擇遊戲資料夾。" };
    private readonly BindingSource _rowsSource = new();
    private string? _gameRoot;
    private bool _gameRunning;
    private bool _detectingGameRoot;
    private List<ResourceRow> _rows = [];
    private bool _busy;
    private CancellationTokenSource? _scanCts;
    private CursorMappingController? _cursorMapping;
    private bool _mappingHotKeyRegistered;

    public MainForm()
    {
        Text = "明日方舟 PC 系統滑鼠指標工具";
        MinimumSize = new Size(920, 640);
        Size = new Size(1120, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft JhengHei UI", 9F);

        var rootGroup = new GroupBox { Text = "1. 遊戲安裝位置", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var rootLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(4) };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        rootLayout.Controls.Add(new Label
        {
            Text = "啟動時會嘗試自動偵測；也可按「自動偵測」或手動選擇包含 Arknights_Data 的資料夾。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 2, 0, 2) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rootBox.MinimumSize = new Size(0, 28);
        _autoDetectButton.MinimumSize = new Size(112, 30);
        _chooseButton.MinimumSize = new Size(124, 30);
        _autoDetectButton.Margin = new Padding(8, 0, 0, 0);
        _chooseButton.Margin = new Padding(8, 0, 0, 0);
        pathRow.Controls.Add(_rootBox, 0, 0);
        pathRow.Controls.Add(_autoDetectButton, 1, 0);
        pathRow.Controls.Add(_chooseButton, 2, 0);
        rootLayout.Controls.Add(pathRow, 0, 1);
        rootGroup.Controls.Add(rootLayout);

        var resourceGroup = new GroupBox { Text = "2. 資源備份與恢復（停用或恢復前請關閉遊戲）", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var resourceLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(4) };
        resourceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        resourceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        resourceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var resourceActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 2, 0, 2) };
        resourceActions.Controls.AddRange([_scanButton, _disableButton, _restoreButton]);
        resourceLayout.Controls.Add(resourceActions, 0, 0);
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(2, 0, 2, 0) };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        statusPanel.Controls.Add(_status, 0, 0);
        statusPanel.Controls.Add(_scanProgress, 1, 0);
        statusPanel.Controls.Add(_cancelScanButton, 2, 0);
        resourceLayout.Controls.Add(statusPanel, 0, 1);

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "選取", DataPropertyName = nameof(ResourceRow.Selected), Width = 52 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "來源", DataPropertyName = nameof(ResourceRow.Source), Width = 102, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "狀態", DataPropertyName = nameof(ResourceRow.State), Width = 170, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SHA-256", DataPropertyName = nameof(ResourceRow.Hash), Width = 260, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "資源路徑", DataPropertyName = nameof(ResourceRow.DisplayPath), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        _grid.RowTemplate.Height = 28;
        _grid.ColumnHeadersHeight = 32;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 240, 248);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253);
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.DataSource = _rowsSource;
        _grid.CellBeginEdit += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
            if (_grid.Rows[e.RowIndex].DataBoundItem is ResourceRow row && row.State == "台服候選：PCMouseMgr（不可停用）") e.Cancel = true;
        };
        resourceLayout.Controls.Add(_grid, 0, 2);
        resourceGroup.Controls.Add(resourceLayout);

        var mappingGroup = new GroupBox { Text = "3. Windows 指標映射", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var mappingLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(4) };
        mappingLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mappingLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mappingLayout.Controls.Add(_mapWindowsCursorButton, 0, 0);
        mappingLayout.Controls.Add(new Label
        {
            Text = "只在所選遊戲視窗位於前景時生效；按 Ctrl+Shift+F12 或本按鈕可停止。此功能不修改遊戲資源。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0)
        }, 1, 0);
        mappingGroup.Controls.Add(mappingLayout);

        var logGroup = new GroupBox { Text = "操作紀錄", Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(8) };
        var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logActions = new Panel { Dock = DockStyle.Fill };
        _clearLogButton.Dock = DockStyle.Right;
        logActions.Controls.Add(_clearLogButton);
        logLayout.Controls.Add(logActions, 0, 0);
        logLayout.Controls.Add(_log, 0, 1);
        logGroup.Controls.Add(logLayout);

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        main.Controls.Add(rootGroup, 0, 0);
        main.Controls.Add(resourceGroup, 0, 1);
        main.Controls.Add(mappingGroup, 0, 2);
        Controls.Add(main);
        Controls.Add(logGroup);

        _chooseButton.Click += (_, _) => ChooseFolder();
        _autoDetectButton.Click += async (_, _) => await AutoDetectGameRootAsync();
        _scanButton.Click += async (_, _) => await RunScanAsync();
        _disableButton.Click += async (_, _) => await RunDisableAsync();
        _restoreButton.Click += async (_, _) => await RunRestoreAsync();
        _mapWindowsCursorButton.Click += (_, _) => ToggleWindowsCursorMapping();
        _clearLogButton.Click += (_, _) => _log.Clear();
        _cancelScanButton.Click += (_, _) =>
        {
            _cancelScanButton.Enabled = false;
            _status.Text = "正在取消掃描，請稍候…";
            _scanCts?.Cancel();
        };
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.ColumnIndex == 0) _disableButton.Enabled = !_busy && CanModifyResources && _rows.Any(r => r.Match && r.Selected);
        };
        _grid.DataError += (_, e) => { e.ThrowException = false; };
        FormClosing += (_, e) => { if (!StopCursorMapping("主視窗關閉")) e.Cancel = true; };
        Shown += async (_, _) => await AutoDetectGameRootAsync();
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "選取含有 Arknights_Data 的明日方舟遊戲安裝資料夾", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SetGameRoot(dialog.SelectedPath, "手動選取");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "無法識別遊戲資料夾", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task AutoDetectGameRootAsync()
    {
        if (_detectingGameRoot || _busy || _cursorMapping is not null) return;
        _detectingGameRoot = true;
        SetControls(false);
        _status.Text = "正在尋找明日方舟安裝資料夾…";
        try
        {
            var detected = await Task.Run(GameDirectoryDetector.Find);
            if (detected is null)
            {
                _status.Text = _gameRoot is null
                    ? "找不到遊戲資料夾；請按「手動選擇…」。"
                    : "未找到其他安裝位置；保留目前已選取的路徑。";
                Log(_gameRoot is null
                    ? "自動偵測未找到遊戲資料夾；可手動選擇包含 Arknights_Data 的目錄。"
                    : "自動偵測未找到其他安裝位置；保留目前路徑：" + _gameRoot);
                return;
            }
            SetGameRoot(detected.Path, "自動偵測（" + detected.Method + "）");
        }
        catch (Exception ex)
        {
            _status.Text = "自動偵測失敗；請手動選擇遊戲資料夾。";
            Log("自動偵測失敗：" + ex.Message);
        }
        finally
        {
            _detectingGameRoot = false;
            SetControls(true);
        }
    }

    private void SetGameRoot(string selectedPath, string source)
    {
        _gameRoot = GameLayout.NormalizeRoot(selectedPath);
        _rootBox.Text = _gameRoot;
        _gameRunning = false;
        _rows = [];
        _rowsSource.DataSource = new BindingList<ResourceRow>(_rows);
        _scanButton.Enabled = true;
        _disableButton.Enabled = false;
        _restoreButton.Enabled = false;
        _mapWindowsCursorButton.Enabled = true;
        _autoDetectButton.Text = "重新偵測";
        Log($"{source}遊戲路徑：{_gameRoot}");
        _status.Text = "已找到遊戲資料夾；下一步按「掃描並驗證資源」。";
    }

    private async Task RunScanAsync()
    {
        if (_gameRoot is null) return;
        await WithBusyAsync(async () =>
        {
            var root = _gameRoot;
            using var cts = new CancellationTokenSource();
            _scanCts = cts;
            _scanProgress.Visible = true;
            _cancelScanButton.Enabled = true;
            try
            {
                _status.Text = "正在建立掃描工作…";
                Log("開始掃描安裝資源與熱更新資源。");
                IProgress<string> uiProgress = new Progress<string>(message =>
                {
                    _status.Text = message;
                    if (message.StartsWith("正在列舉", StringComparison.Ordinal)) Log(message);
                });
                var result = await Task.Run(() => ResourceManager.Scan(root, uiProgress.Report, cts.Token), cts.Token);
                _rows = result.Rows;
                _rowsSource.DataSource = new BindingList<ResourceRow>(_rows);
                foreach (var message in result.Messages) Log(message);
                var matchCount = _rows.Count(r => r.Match);
                var restorableCount = _rows.Count(r => r.IsRestorable);
                var taiwanCandidateCount = _rows.Count(r => r.State == "台服候選：PCMouseMgr（不可停用）");
                _status.Text = matchCount > 0
                    ? $"掃描完成：找到 {matchCount} 個精確驗證資源；{taiwanCandidateCount} 個台服線索；{restorableCount} 個可恢復項目。"
                    : taiwanCandidateCount > 0
                        ? $"掃描完成：只有 {taiwanCandidateCount} 個未驗證台服線索，不可停用；{restorableCount} 個可恢復項目。"
                        : restorableCount > 0
                            ? $"掃描完成：找到 {restorableCount} 個已停用資源可恢復。"
                            : "掃描完成：沒有找到可精確驗證的滑鼠管理器資源。";
                _gameRunning = IsGameRunning(root, out _);
                _disableButton.Enabled = _rows.Any(r => r.Match && r.Selected) && !_gameRunning;
                _restoreButton.Enabled = _rows.Any(r => r.IsRestorable) && !_gameRunning;
                if (_gameRunning) _status.Text += " 偵測到遊戲仍在執行，資源修改按鈕已鎖定。";
            }
            catch (OperationCanceledException)
            {
                _status.Text = "掃描已取消。可重新掃描，或先恢復已存在的備份。";
                Log("掃描已由使用者取消；掃描不會修改遊戲檔案。");
            }
            finally
            {
                _scanCts = null;
                _scanProgress.Visible = false;
                _cancelScanButton.Enabled = false;
            }
        });
    }

    private async Task RunDisableAsync()
    {
        if (_gameRoot is null) return;
        _grid.EndEdit();
        var selected = _rows.Where(r => r.Selected && r.Match).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "請勾選至少一個已驗證的資源。", "沒有選取項目", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await WithBusyAsync(async () =>
        {
            var root = _gameRoot;
            await Task.Run(() => ResourceManager.Disable(root, selected, msg => BeginInvoke(() => Log(msg)), () => IsGameRunning(root, out var detail) ? detail : null));
            var result = await Task.Run(() => ResourceManager.Scan(root));
            _rows = result.Rows;
            _rowsSource.DataSource = new BindingList<ResourceRow>(_rows);
            foreach (var message in result.Messages) Log(message);
            _gameRunning = IsGameRunning(root, out _);
            _status.Text = "停用作業完成；請檢查操作記錄。";
            _disableButton.Enabled = _rows.Any(r => r.Match && r.Selected) && !_gameRunning;
            _restoreButton.Enabled = _rows.Any(r => r.IsRestorable) && !_gameRunning;
        });
    }

    private async Task RunRestoreAsync()
    {
        if (_gameRoot is null) return;
        if (MessageBox.Show(this, "工具會核對備份與停用檔案的 SHA-256。若原路徑已出現新檔案，會保留新檔並跳過該項目。要繼續嗎？", "安全恢復", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await WithBusyAsync(async () =>
        {
            var root = _gameRoot;
            await Task.Run(() => ResourceManager.Restore(root, msg => BeginInvoke(() => Log(msg)), () => IsGameRunning(root, out var detail) ? detail : null));
            var result = await Task.Run(() => ResourceManager.Scan(root));
            _rows = result.Rows;
            _rowsSource.DataSource = new BindingList<ResourceRow>(_rows);
            foreach (var message in result.Messages) Log(message);
            _gameRunning = IsGameRunning(root, out _);
            _status.Text = "恢復作業完成；請檢查操作記錄。";
            _disableButton.Enabled = _rows.Any(r => r.Match && r.Selected) && !_gameRunning;
            _restoreButton.Enabled = _rows.Any(r => r.IsRestorable) && !_gameRunning;
        });
    }

    private async Task WithBusyAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        SetControls(false);
        try { await action(); }
        catch (Exception ex)
        {
            Log("錯誤：" + ex.Message);
            MessageBox.Show(this, ex.Message, "操作失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (_gameRoot is not null) _gameRunning = IsGameRunning(_gameRoot, out _);
            if (_gameRoot is not null) { _scanButton.Enabled = true; _restoreButton.Enabled = true; }
        }
        finally { _busy = false; SetControls(true); }
    }

    private void SetControls(bool enable)
    {
        _autoDetectButton.Enabled = enable && !_detectingGameRoot && _cursorMapping is null;
        _autoDetectButton.Text = _gameRoot is null ? "自動偵測" : "重新偵測";
        _chooseButton.Enabled = enable && !_detectingGameRoot && _cursorMapping is null;
        _scanButton.Enabled = enable && _gameRoot is not null;
        var canModifyResources = enable && CanModifyResources;
        _disableButton.Enabled = canModifyResources && _rows.Any(r => r.Match && r.Selected);
        _restoreButton.Enabled = canModifyResources && _rows.Any(r => r.IsRestorable);
        _mapWindowsCursorButton.Enabled = _gameRoot is not null && (enable || _cursorMapping is not null);
        _mapWindowsCursorButton.Text = _cursorMapping is null ? "啟動 Windows 指標映射" : "停止指標映射";
        _cancelScanButton.Enabled = enable && _scanCts is not null;
    }

    private void ToggleWindowsCursorMapping()
    {
        if (_cursorMapping is not null)
        {
            StopCursorMapping("使用者停止");
            return;
        }
        if (_gameRoot is null) return;
        try
        {
            StartCursorMapping(Cursors.Arrow, ownsCursor: false, mappedSize: 0);
        }
        catch (Exception ex)
        {
            Log("無法啟動 Windows 指標映射：" + ex.Message);
            MessageBox.Show(this, ex.Message, "映射啟動失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartCursorMapping(Cursor cursor, bool ownsCursor, int mappedSize)
    {
        if (_gameRoot is null) throw new InvalidOperationException("請先選擇遊戲資料夾。");
        _cursorMapping = new CursorMappingController(_gameRoot, cursor, ownsCursor, mappedSize, MappingLog);
        _mapWindowsCursorButton.Text = "停止指標映射";
        _chooseButton.Enabled = false;
        _autoDetectButton.Enabled = false;
        _status.Text = "映射已啟動；請切回遊戲並移動指標。Ctrl+Shift+F12 可停止。";
        if (!_mappingHotKeyRegistered)
        {
            _mappingHotKeyRegistered = RegisterHotKey(Handle, MappingHotKeyId, ModControl | ModShift | ModNoRepeat, VkF12);
            if (!_mappingHotKeyRegistered) Log($"無法註冊緊急停止快捷鍵（Win32 {Marshal.GetLastWin32Error()}）；仍可按「停止映射指標」。");
        }
    }

    private bool StopCursorMapping(string reason)
    {
        var mapping = _cursorMapping;
        if (mapping is null) return true;
        if (!mapping.Stop()) return false;
        _cursorMapping = null;
        if (_mappingHotKeyRegistered)
        {
            _ = UnregisterHotKey(Handle, MappingHotKeyId);
            _mappingHotKeyRegistered = false;
        }
        if (!IsDisposed)
        {
            _mapWindowsCursorButton.Text = "啟動 Windows 指標映射";
            _chooseButton.Enabled = !_busy && !_detectingGameRoot;
            _autoDetectButton.Enabled = !_busy && !_detectingGameRoot;
            _status.Text = _gameRoot is null ? "請先選擇遊戲資料夾。" : "游標映射已停止。";
            Log("游標映射已停止：" + reason);
        }
        return true;
    }

    private void MappingLog(string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)(() => Log(message))); } catch (InvalidOperationException) { }
        }
        else Log(message);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotKey && message.WParam.ToInt32() == MappingHotKeyId)
        {
            _ = StopCursorMapping("緊急停止快捷鍵 Ctrl+Shift+F12");
            return;
        }
        base.WndProc(ref message);
    }

    private const int MappingHotKeyId = 0xA713;
    private const int WmHotKey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF12 = 0x7B;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private void Log(string message)
    {
        if (IsDisposed) return;
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    internal static bool IsGameRunning(string root, out string detail)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                var exePath = CursorMappingController.TryGetProcessPath(process);
                var pathMatches = !string.IsNullOrWhiteSpace(exePath)
                    && Path.GetFullPath(exePath).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
                var knownGameProcess = CursorMappingController.IsArknightsName(name);
                if (pathMatches || (knownGameProcess && string.IsNullOrWhiteSpace(exePath)))
                {
                    detail = $"偵測到遊戲程序 {name}（PID {process.Id}）。請完全關閉遊戲後再操作。";
                    return true;
                }
            }
            catch { }
            finally { process.Dispose(); }
        }
        detail = "";
        return false;
    }

    private bool CanModifyResources => _gameRoot is not null && !_gameRunning;
}

internal sealed class ResourceRow
{
    public bool Selected { get; set; }
    public string Source { get; set; } = "";
    public string State { get; set; } = "";
    public string Hash { get; set; } = "";
    [JsonIgnore] public string Evidence { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    [JsonIgnore] public string OriginalPath { get; set; } = "";
    [JsonIgnore] public string DisabledPath { get; set; } = "";
    [JsonIgnore] public string BackupPath { get; set; } = "";
    [JsonIgnore] public bool Match { get; set; }
    [JsonIgnore] public bool IsRestorable { get; set; }
}

internal sealed record ScanResult(List<ResourceRow> Rows, List<string> Messages);

internal sealed class BackupManifest
{
    public int Version { get; set; } = 1;
    public string GameRoot { get; set; } = "";
    public List<BackupEntry> Entries { get; set; } = [];
}

internal sealed class BackupEntry
{
    public string OriginalPath { get; set; } = "";
    public string DisabledPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Evidence { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

internal static class GameLayout
{
    private static readonly byte[] TaiwanMouseAssetPath = Encoding.ASCII.GetBytes("dyn/mouse/pc_mouse_mgr.prefab");

    public static string NormalizeRoot(string selected)
    {
        var full = Path.GetFullPath(selected);
        if (Path.GetFileName(full).Equals("Arknights_Data", StringComparison.OrdinalIgnoreCase)) full = Directory.GetParent(full)?.FullName ?? full;
        if (!Directory.Exists(Path.Combine(full, "Arknights_Data")))
            throw new InvalidOperationException("所選資料夾內找不到 Arknights_Data。請選擇遊戲安裝資料夾或 Arknights_Data 資料夾。 ");
        return full;
    }

    public static IEnumerable<(string Label, string Path)> ResourceRoots(string root)
    {
        var data = Path.Combine(root, "Arknights_Data");
        var install = Path.Combine(data, "StreamingAssets", "AB", "Windows");
        var updates = Path.Combine(data, "PersistentData", "Bundles");
        yield return ("安裝資源 / anon", Path.Combine(install, "anon"));
        yield return ("安裝資源 / mouse", Path.Combine(install, "mouse"));
        yield return ("熱更新資源 / anon", Path.Combine(updates, "anon"));
        yield return ("熱更新資源 / mouse", Path.Combine(updates, "mouse"));
    }

    public static bool AllowTaiwanManagerMarker(string root)
    {
        try
        {
            var appInfo = File.ReadAllText(Path.Combine(root, "Arknights_Data", "app.info"));
            if (!appInfo.Contains("Gryphline", StringComparison.OrdinalIgnoreCase)
                || !appInfo.Contains("Arknights_TC", StringComparison.OrdinalIgnoreCase)) return false;
            return HasTaiwanMousePathInIndex(root);
        }
        catch { return false; }
    }

    private static bool HasTaiwanMousePathInIndex(string root)
    {
        foreach (var resourceRoot in new[]
        {
            Path.Combine(root, "Arknights_Data", "StreamingAssets", "AB", "Windows"),
            Path.Combine(root, "Arknights_Data", "PersistentData", "Bundles")
        })
        {
            var hotUpdateList = Path.Combine(resourceRoot, "hot_update_list.json");
            if (!File.Exists(hotUpdateList)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(hotUpdateList));
                if (!document.RootElement.TryGetProperty("abInfos", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) continue;
                    var name = nameElement.GetString();
                    if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".idx", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name) != name) continue;
                    var indexPath = Path.Combine(resourceRoot, name);
                    if (!File.Exists(indexPath)) continue;
                    var length = new FileInfo(indexPath).Length;
                    if (length <= 0 || length > 64 * 1024 * 1024) continue;
                    var bytes = File.ReadAllBytes(indexPath);
                    if (bytes.AsSpan().IndexOf(TaiwanMouseAssetPath) >= 0) return true;
                }
            }
            catch (IOException) { }
            catch (JsonException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }
}

internal sealed record GameDirectoryCandidate(string Path, string Method);

internal static class GameDirectoryDetector
{
    public static GameDirectoryCandidate? Find()
    {
        var candidates = new List<GameDirectoryCandidate>();

        // A running client is the strongest signal and also handles custom install locations.
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!CursorMappingController.IsArknightsName(process.ProcessName)) continue;
                var executable = CursorMappingController.TryGetProcessPath(process);
                if (string.IsNullOrWhiteSpace(executable)) continue;
                var directory = Path.GetDirectoryName(Path.GetFullPath(executable));
                for (var depth = 0; !string.IsNullOrWhiteSpace(directory) && depth < 8; depth++)
                {
                    candidates.Add(new GameDirectoryCandidate(directory, "執行中的遊戲程序"));
                    directory = Directory.GetParent(directory)?.FullName;
                }
            }
            catch { }
            finally { process.Dispose(); }
        }

        // The user's Taiwan PC client commonly uses this Gryphlink directory.
        candidates.Add(new GameDirectoryCandidate(@"D:\GRYPHLINK\games\Arknights_TC", "台服常用安裝路徑"));

        // Check only standard top-level install folders on fixed drives; do not recursively crawl disks.
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            {
                var driveRoot = drive.RootDirectory.FullName;
                candidates.Add(new GameDirectoryCandidate(Path.Combine(driveRoot, "GRYPHLINK", "games", "Arknights_TC"), "固定磁碟上的 GRYPHLINK 資料夾"));
                candidates.Add(new GameDirectoryCandidate(Path.Combine(driveRoot, "Gryphline", "games", "Arknights_TC"), "固定磁碟上的 Gryphline 資料夾"));
            }
        }
        catch { }

        foreach (var candidate in candidates.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var root = GameLayout.NormalizeRoot(candidate.Path);
                return candidate with { Path = root };
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { }
        }
        return null;
    }
}

internal static class ResourceManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private const string ReferenceDisabledSuffix = ".system-cursor-disabled-";
    private const string LegacyReferenceDisabledSuffix = ".disabled-by-codex";

    public static ScanResult Scan(string root, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var rows = new List<ResourceRow>();
        var messages = new List<string>();
        var allowTaiwanManagerMarker = GameLayout.AllowTaiwanManagerMarker(root);
        if (allowTaiwanManagerMarker)
            messages.Add("已確認 Gryphline 台服索引包含 dyn/mouse/pc_mouse_mgr.prefab；另以 PCMouseMgr 序列化類型名稱辨識台服候選封包。");
        var manifest = LoadManifest(root);
        var checkedFiles = 0;
        var parsedUnityFsBundles = 0;
        var unityFsWithoutTarget = 0;
        var referenceDisabledCount = 0;
        var skippedReasons = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        var lastProgress = Stopwatch.StartNew();
        foreach (var (label, folder) in GameLayout.ResourceRoots(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder))
            {
                if (label.EndsWith("/ mouse", StringComparison.Ordinal))
                    messages.Add($"可選 mouse 目錄不存在，正常略過：{folder}");
                else
                    messages.Add($"未找到{label}目錄：{folder}");
                continue;
            }
            messages.Add($"掃描{label}：{folder}");
            progress?.Invoke($"正在列舉{label}資源目錄…");
            try
            {
                foreach (var path in EnumerateFilesSafely(folder, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checkedFiles++;
                    if (checkedFiles == 1 || lastProgress.ElapsedMilliseconds >= 180)
                    {
                        progress?.Invoke($"已檢查 {checkedFiles:N0} 個檔案｜{label}：{Path.GetFileName(path)}");
                        lastProgress.Restart();
                    }
                    if (TryGetReferenceDisabledInfo(path, out var referenceOriginalPath, out var expectedHashPrefix))
                    {
                        if (!UnityFsScanner.TryFindTarget(path, out var disabledContainsTarget, out var disabledReason, out var disabledEvidence, allowTaiwanManagerMarker, cancellationToken) || !disabledContainsTarget)
                        {
                            var skipReason = disabledReason.Length > 0 ? disabledReason : "停用檔未包含目標 prefab";
                            if (skippedReasons.TryGetValue(skipReason, out var previous))
                                skippedReasons[skipReason] = (previous.Count + 1, previous.Example);
                            else
                                skippedReasons.Add(skipReason, (1, path));
                            continue;
                        }
                        parsedUnityFsBundles++;
                        var disabledHash = HashFile(path);
                        if (expectedHashPrefix.Length > 0 && !disabledHash.StartsWith(expectedHashPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            const string skipReason = "範本停用檔的 SHA-256 尾碼不符";
                            if (skippedReasons.TryGetValue(skipReason, out var previous))
                                skippedReasons[skipReason] = (previous.Count + 1, previous.Example);
                            else
                                skippedReasons.Add(skipReason, (1, path));
                            continue;
                        }
                        var originalExists = File.Exists(referenceOriginalPath);
                        rows.Add(new ResourceRow
                        {
                            Source = label,
                            State = originalExists ? "原路徑已有檔案（保留）" : "範本已停用，可恢復",
                            Hash = disabledHash,
                            Evidence = disabledEvidence,
                            DisplayPath = Path.GetRelativePath(root, referenceOriginalPath),
                            OriginalPath = referenceOriginalPath,
                            DisabledPath = path,
                            Match = false,
                            IsRestorable = !originalExists
                        });
                        referenceDisabledCount++;
                        messages.Add($"已驗證範本停用的滑鼠資源：{path} (SHA-256 {disabledHash})");
                        continue;
                    }
                    if (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!UnityFsScanner.TryFindTarget(path, out var result, out var reason, out var evidence, allowTaiwanManagerMarker, cancellationToken))
                    {
                        reason = reason.Length > 0 ? reason : "無法識別封包格式";
                        if (skippedReasons.TryGetValue(reason, out var previous))
                            skippedReasons[reason] = (previous.Count + 1, previous.Example);
                        else
                            skippedReasons.Add(reason, (1, path));
                        continue;
                    }
                    parsedUnityFsBundles++;
                    if (!result) { unityFsWithoutTarget++; continue; }
                    var hash = HashFile(path);
                    var isTaiwanCandidate = evidence == "PCMouseMgr";
                    rows.Add(new ResourceRow
                    {
                        Selected = false,
                        Source = label,
                        State = isTaiwanCandidate ? "台服候選：PCMouseMgr（不可停用）" : "已驗證",
                        Hash = hash,
                        Evidence = evidence,
                        DisplayPath = Path.GetRelativePath(root, path),
                        OriginalPath = path,
                        Match = !isTaiwanCandidate
                    });
                    messages.Add(isTaiwanCandidate
                        ? $"發現未驗證台服線索 PCMouseMgr：{path} (SHA-256 {hash})；這不足以證明封包是游標管理器，工具不允許停用。"
                        : $"已驗證目標 prefab：{path} (SHA-256 {hash})");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                messages.Add($"無法完整列舉{label}目錄，該目錄掃描可能不完整：{ex.Message}");
            }
        }

        foreach (var entry in manifest.Entries)
        {
            var existingRow = rows.FirstOrDefault(r => Path.GetFullPath(r.OriginalPath).Equals(Path.GetFullPath(entry.OriginalPath), StringComparison.OrdinalIgnoreCase));
            if (existingRow is not null)
            {
                existingRow.DisabledPath = entry.DisabledPath;
                existingRow.BackupPath = entry.BackupPath;
                existingRow.IsRestorable = !File.Exists(entry.OriginalPath) && File.Exists(entry.BackupPath);
                continue;
            }
            var source = SourceName(root, entry.OriginalPath);
            string state;
            if (File.Exists(entry.OriginalPath)) state = "原檔仍在（未停用）";
            else if (File.Exists(entry.DisabledPath)) state = "已停用，可恢復";
            else state = "原檔與停用檔均缺失";
            rows.Add(new ResourceRow
            {
                Source = source,
                State = state,
                Hash = entry.Sha256,
                Evidence = entry.Evidence,
                DisplayPath = Path.GetRelativePath(root, entry.OriginalPath),
                OriginalPath = entry.OriginalPath,
                DisabledPath = entry.DisabledPath,
                BackupPath = entry.BackupPath,
                Match = false,
                IsRestorable = !File.Exists(entry.OriginalPath) && File.Exists(entry.BackupPath)
            });
        }
        var verifiedCount = rows.Count(r => r.Match && r.State == "已驗證");
        var taiwanCandidateCount = rows.Count(r => r.State == "台服候選：PCMouseMgr（不可停用）");
        messages.Add($"掃描完成：檢查 {checkedFiles:N0} 個候選檔案，成功解析 {parsedUnityFsBundles:N0} 個 UnityFS；其中 {unityFsWithoutTarget:N0} 個未含辨識標記，找到 {verifiedCount:N0} 個精確路徑資源、{taiwanCandidateCount:N0} 個台服 PCMouseMgr 候選，另識別 {referenceDisabledCount:N0} 個範本停用封包。");
        if (skippedReasons.Count > 0)
        {
            messages.Add($"另有 {skippedReasons.Values.Sum(v => v.Count):N0} 個候選檔案略過或無法驗證：");
            foreach (var (reason, details) in skippedReasons.OrderByDescending(item => item.Value.Count).Take(8))
                messages.Add($"  {details.Count:N0} 個：{reason}（例：{details.Example}）");
            if (skippedReasons.Count > 8) messages.Add($"  另有 {skippedReasons.Count - 8} 種略過原因未列出。");
        }
        if (verifiedCount == 0 && taiwanCandidateCount > 0)
            messages.Add("找到 PCMouseMgr 字串，但未能將它與目標 prefab 建立足夠關聯；不會提供停用操作，以免破壞遊戲資源。請先安全恢復任何曾停用的候選封包。");
        else if (verifiedCount == 0)
        {
            if (referenceDisabledCount > 0)
                messages.Add($"已辨識 {referenceDisabledCount:N0} 個由範本停用的滑鼠資源；如需恢復，可使用「安全恢復」。");
            else if (checkedFiles > 0 && parsedUnityFsBundles == checkedFiles && skippedReasons.Count == 0)
                messages.Add("已解析所有候選封包，但內容均未包含目標路徑。參照範本僅在國服 PC 客戶端實測；目前客戶端或版本尚未確認相容。工具不會停用未驗證的封包。");
            else
                messages.Add("若預期應找到目標，請確認所選客戶端目錄正確，並查看上方候選封包略過原因；原生安裝沒有 mouse 目錄屬正常情況。工具不會停用未驗證的封包。");
        }
        return new ScanResult(rows, messages);
    }

    public static void Disable(string root, List<ResourceRow> selected, Action<string> log, Func<string?> running)
    {
        var allowTaiwanManagerMarker = GameLayout.AllowTaiwanManagerMarker(root);
        var manifest = LoadManifest(root);
        var backupRoot = GetBackupRoot(root);
        Directory.CreateDirectory(backupRoot);
        foreach (var row in selected)
        {
            var process = running();
            if (process is not null) throw new InvalidOperationException(process);
            var path = Path.GetFullPath(row.OriginalPath);
            if (!File.Exists(path)) { log($"跳過：原資源不存在 {path}"); continue; }
            if (!UnityFsScanner.TryFindTarget(path, out var contains, out var reason, out var evidence, allowTaiwanManagerMarker) || !contains)
            {
                log($"跳過：資源重新驗證失敗 {path} {reason}");
                continue;
            }
            if (!evidence.Equals("dyn/mouse/pc_mouse_mgr.prefab", StringComparison.Ordinal))
            {
                log($"拒絕停用未精確驗證的台服候選（{evidence}）：{path}");
                continue;
            }
            var existingEntry = manifest.Entries.FirstOrDefault(e => Path.GetFullPath(e.OriginalPath).Equals(path, StringComparison.OrdinalIgnoreCase));
            var hash = HashFile(path);
            var disabledPath = path + ".disabled";
            if (File.Exists(disabledPath)) { log($"跳過：停用目標已存在 {disabledPath}"); continue; }
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p == "..")) throw new InvalidOperationException("資源路徑超出遊戲資料夾，已停止操作。");
            var backupPath = existingEntry?.BackupPath ?? Path.Combine(backupRoot, relative + ".arkmouse.bak");
            var reuseBackup = existingEntry is not null
                && FixedHashEquals(existingEntry.Sha256, hash)
                && File.Exists(backupPath)
                && FixedHashEquals(hash, HashFile(backupPath));
            if (!reuseBackup)
            {
                backupPath = Path.Combine(backupRoot, relative + ".arkmouse.bak");
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                if (File.Exists(backupPath)) backupPath += "." + DateTime.UtcNow.Ticks.ToString();
                File.Copy(path, backupPath, false);
                var backupHash = HashFile(backupPath);
                if (!FixedHashEquals(hash, backupHash)) throw new IOException($"備份 SHA-256 不一致，原檔未更動：{path}");
            }
            var entry = existingEntry ?? new BackupEntry();
            entry.OriginalPath = path;
            entry.DisabledPath = disabledPath;
            entry.BackupPath = backupPath;
            entry.Sha256 = hash;
            entry.Evidence = evidence;
            entry.CreatedUtc = DateTime.UtcNow;
            if (existingEntry is null) manifest.Entries.Add(entry);
            SaveManifest(root, manifest);
            process = running();
            if (process is not null) throw new InvalidOperationException(process);
            if (File.Exists(path)) File.Move(path, disabledPath);
            if (!File.Exists(disabledPath) || !FixedHashEquals(hash, HashFile(disabledPath)))
                throw new IOException($"停用後 SHA-256 驗證失敗，備份仍保留於：{backupPath}");
            log($"已停用並驗證（{evidence}）：{path}；備份：{backupPath}；SHA-256：{hash}；{(reuseBackup ? "重用雜湊相符的既有備份" : "建立新備份")}");
        }
    }

    public static void Restore(string root, Action<string> log, Func<string?> running)
    {
        var allowTaiwanManagerMarker = GameLayout.AllowTaiwanManagerMarker(root);
        var manifest = LoadManifest(root);
        foreach (var entry in manifest.Entries)
        {
            var process = running();
            if (process is not null) throw new InvalidOperationException(process);
            // Keep Taiwan fallback validation available after an update changes its index;
            // the manifest evidence is still bound to the backed-up bytes by SHA-256.
            var allowEntryTaiwanMarker = allowTaiwanManagerMarker || entry.Evidence == "PCMouseMgr";
            if (!File.Exists(entry.BackupPath)) { log($"跳過：備份遺失 {entry.BackupPath}"); continue; }
            var backupHash = HashFile(entry.BackupPath);
            if (!FixedHashEquals(entry.Sha256, backupHash)) { log($"拒絕恢復：備份 SHA-256 不符 {entry.BackupPath}"); continue; }
            if (!UnityFsScanner.TryFindTarget(entry.BackupPath, out var backupContainsTarget, out var backupReason, out _, allowEntryTaiwanMarker) || !backupContainsTarget)
            {
                log($"拒絕恢復：備份內容未通過滑鼠資源驗證 {entry.BackupPath} {backupReason}");
                continue;
            }
            var disabledValid = false;
            if (File.Exists(entry.DisabledPath))
            {
                disabledValid = FixedHashEquals(entry.Sha256, HashFile(entry.DisabledPath));
                if (!disabledValid) log($"停用檔 SHA-256 不符，保留原狀：{entry.DisabledPath}");
                else if (!UnityFsScanner.TryFindTarget(entry.DisabledPath, out var disabledContainsTarget, out var disabledReason, out _, allowEntryTaiwanMarker) || !disabledContainsTarget)
                {
                    disabledValid = false;
                    log($"停用檔內容未通過滑鼠資源驗證，保留原狀：{entry.DisabledPath} {disabledReason}");
                }
            }
            if (File.Exists(entry.OriginalPath))
            {
                log($"保留原路徑現有檔案（可能是遊戲更新或修復產生的新檔）：{entry.OriginalPath}");
                continue;
            }
            if (File.Exists(entry.DisabledPath))
            {
                if (!disabledValid) continue;
                File.Move(entry.DisabledPath, entry.OriginalPath);
                if (!FixedHashEquals(entry.Sha256, HashFile(entry.OriginalPath))) throw new IOException($"恢復後 SHA-256 不符：{entry.OriginalPath}");
                log($"已安全恢復：{entry.OriginalPath}");
                continue;
            }
            File.Copy(entry.BackupPath, entry.OriginalPath, false);
            if (!FixedHashEquals(entry.Sha256, HashFile(entry.OriginalPath))) throw new IOException($"從已驗證備份恢復後 SHA-256 不符：{entry.OriginalPath}");
            log($"停用檔不存在，已從已驗證備份安全恢復：{entry.OriginalPath}");
        }

        var manifestDisabledPaths = new HashSet<string>(manifest.Entries.Select(e => Path.GetFullPath(e.DisabledPath)), StringComparer.OrdinalIgnoreCase);
        foreach (var (label, folder) in GameLayout.ResourceRoots(root))
        {
            foreach (var disabledPath in EnumerateFilesSafely(folder, CancellationToken.None))
            {
                if (!TryGetReferenceDisabledInfo(disabledPath, out var originalPath, out var expectedHashPrefix)) continue;
                if (manifestDisabledPaths.Contains(Path.GetFullPath(disabledPath))) continue;
                var process = running();
                if (process is not null) throw new InvalidOperationException(process);
                if (File.Exists(originalPath))
                {
                    log($"保留原路徑現有檔案（可能是遊戲更新或修復產生的新檔）：{originalPath}；範本停用檔仍保留：{disabledPath}");
                    continue;
                }
                if (!UnityFsScanner.TryFindTarget(disabledPath, out var containsTarget, out var reason, out _, allowTaiwanManagerMarker) || !containsTarget)
                {
                    log($"拒絕恢復：範本停用檔內容未通過滑鼠資源驗證 {disabledPath} {reason}");
                    continue;
                }
                var hash = HashFile(disabledPath);
                if (expectedHashPrefix.Length > 0 && !hash.StartsWith(expectedHashPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    log($"拒絕恢復：範本停用檔 SHA-256 尾碼不符 {disabledPath}");
                    continue;
                }
                process = running();
                if (process is not null) throw new InvalidOperationException(process);
                File.Move(disabledPath, originalPath, false);
                if (!FixedHashEquals(hash, HashFile(originalPath))) throw new IOException($"範本停用檔恢復後 SHA-256 不符：{originalPath}");
                log($"已恢復範本停用的資源（{label}）：{originalPath}；SHA-256：{hash}");
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rootIsOrdinary = false;
        try { rootIsOrdinary = (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0; } catch { }
        if (!rootIsOrdinary) yield break;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileIsOrdinary = false;
            try { fileIsOrdinary = (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0; } catch { }
            if (!fileIsOrdinary) continue;
            var extension = Path.GetExtension(file);
            if (extension.Equals(".bin", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ab", StringComparison.OrdinalIgnoreCase)
                || TryGetReferenceDisabledInfo(file, out _, out _))
                yield return file;
        }
    }

    private static bool TryGetReferenceDisabledInfo(string path, out string originalPath, out string expectedHashPrefix)
    {
        originalPath = "";
        expectedHashPrefix = "";
        var fullPath = Path.GetFullPath(path);
        var fileName = Path.GetFileName(fullPath);
        var suffixIndex = fileName.LastIndexOf(ReferenceDisabledSuffix, StringComparison.OrdinalIgnoreCase);
        if (suffixIndex >= 0)
        {
            var prefix = fileName[(suffixIndex + ReferenceDisabledSuffix.Length)..];
            if (prefix.Length != 12 || prefix.Any(c => !Uri.IsHexDigit(c))) return false;
            var originalName = fileName[..suffixIndex];
            if (originalName.Length == 0) return false;
            originalPath = Path.Combine(Path.GetDirectoryName(fullPath)!, originalName);
            expectedHashPrefix = prefix;
            return true;
        }
        if (fileName.EndsWith(LegacyReferenceDisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var originalName = fileName[..^LegacyReferenceDisabledSuffix.Length];
            if (originalName.Length == 0) return false;
            originalPath = Path.Combine(Path.GetDirectoryName(fullPath)!, originalName);
            return true;
        }
        return false;
    }

    private static string SourceName(string root, string path)
    {
        foreach (var (label, folder) in GameLayout.ResourceRoots(root))
            if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return label;
        return "已備份";
    }

    private static string GetBackupRoot(string root)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant()))).ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArknightsCursorTool", id);
    }

    private static string ManifestPath(string root) => Path.Combine(GetBackupRoot(root), "manifest.json");

    private static BackupManifest LoadManifest(string root)
    {
        var path = ManifestPath(root);
        if (!File.Exists(path)) return new BackupManifest { GameRoot = Path.GetFullPath(root) };
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), JsonOptions) ?? new BackupManifest();
        var fullRoot = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(manifest.GameRoot) || !Path.GetFullPath(manifest.GameRoot).Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("備份清單中的遊戲路徑不符，停止修改。 ");
        var backupRoot = Path.GetFullPath(GetBackupRoot(root)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in manifest.Entries)
        {
            var original = Path.GetFullPath(entry.OriginalPath);
            var disabled = Path.GetFullPath(entry.DisabledPath);
            var backup = Path.GetFullPath(entry.BackupPath);
            var insideResourceTree = GameLayout.ResourceRoots(root).Any(r =>
            {
                var folder = Path.GetFullPath(r.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return original.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
            });
            if (!insideResourceTree || !disabled.Equals(original + ".disabled", StringComparison.OrdinalIgnoreCase) || !backup.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase) || entry.Sha256.Length != 64)
                throw new InvalidDataException("備份清單中的檔案路徑或 SHA-256 欄位不合法，停止修改。 ");
        }
        return manifest;
    }

    private static void SaveManifest(string root, BackupManifest manifest)
    {
        manifest.GameRoot = Path.GetFullPath(root);
        var path = ManifestPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path);
    }

    internal static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static bool FixedHashEquals(string a, string b)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b)); }
        catch { return false; }
    }
}

internal static class UnityFsScanner
{
    private const int MaximumBundleSize = 32 * 1024 * 1024;
    private const int MaximumDecodedSize = 16 * 1024 * 1024;
    private const long MaximumTotalDecodedSize = 512L * 1024 * 1024;
    private static readonly byte[] TargetPath = Encoding.ASCII.GetBytes("dyn/mouse/pc_mouse_mgr.prefab");
    private static readonly byte[] SerializedMarker = CreateSerializedMarker(TargetPath);
    private static readonly byte[] TaiwanManagerTypeMarker = CreateSerializedMarker(Encoding.ASCII.GetBytes("PCMouseMgr"));

    public static bool TryFindTarget(string path, out bool found, out string reason, CancellationToken cancellationToken = default)
        => TryFindTarget(path, out found, out reason, out _, false, cancellationToken);

    public static bool TryFindTarget(string path, out bool found, out string reason, out string evidence, bool allowTaiwanManagerMarker, CancellationToken cancellationToken = default)
    {
        found = false;
        reason = "";
        evidence = "";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileLength = new FileInfo(path).Length;
            if (fileLength <= 0) { reason = "空封包"; return false; }
            if (fileLength > MaximumBundleSize) { reason = "超過 32 MiB 檢查上限"; return false; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 64, FileOptions.SequentialScan);
            if (stream.Length < 7) { reason = "檔案短於 UnityFS 標頭"; return false; }
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            var signature = ReadNullString(reader, 32);
            if (!signature.Equals("UnityFS", StringComparison.Ordinal)) { reason = "不是 UnityFS 封包"; return false; }
            var format = ReadInt32BE(reader);
            if (format is < 6 or > 8) { reason = $"不支援 UnityFS 格式版本 {format}（參照解析器支援 6–8）"; return false; }
            _ = ReadNullString(reader, 256); // player version
            _ = ReadNullString(reader, 256); // engine version
            var declaredSize = ReadInt64BE(reader);
            var infoCompressedSize = ReadUInt32BE(reader);
            var infoUncompressedSize = ReadUInt32BE(reader);
            var dataFlags = ReadUInt32BE(reader);
            if (declaredSize != stream.Length || declaredSize < stream.Position || infoCompressedSize > MaximumBundleSize || infoUncompressedSize > MaximumDecodedSize)
            { reason = "UnityFS 標頭大小無效"; return false; }

            var headerEnd = stream.Position;
            if (format >= 7) headerEnd = Align(headerEnd, 16);
            var infoPosition = (dataFlags & 0x80) != 0 ? declaredSize - infoCompressedSize : headerEnd;
            if (infoPosition < headerEnd || infoPosition + infoCompressedSize > declaredSize) { reason = "UnityFS 區塊索引位置無效"; return false; }
            stream.Position = infoPosition;
            var compressedInfo = ReadExact(reader, checked((int)infoCompressedSize));
            var infoCompression = (int)(dataFlags & 0x3F);
            var info = Decompress(compressedInfo, checked((int)infoUncompressedSize), infoCompression, cancellationToken);
            var parsed = ParseBlockInfo(info, format);
            var totalDecodedSize = parsed.Blocks.Sum(block => (long)block.UncompressedSize);
            if (totalDecodedSize > MaximumTotalDecodedSize)
            { reason = "封包總解壓量超過 512 MiB 檢查上限"; return false; }
            var dataPosition = (dataFlags & 0x80) != 0 ? headerEnd : infoPosition + infoCompressedSize;
            if ((dataFlags & 0x200) != 0) dataPosition = Align(dataPosition, 16);
            stream.Position = dataPosition;
            var search = new ByteSearch([SerializedMarker]);
            var taiwanSearch = allowTaiwanManagerMarker ? new ByteSearch([TaiwanManagerTypeMarker]) : null;
            foreach (var block in parsed.Blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (block.UncompressedSize > MaximumDecodedSize || block.CompressedSize > stream.Length - stream.Position)
                { reason = "區塊大小超出安全限制或封包被截斷"; return false; }
                var input = ReadExact(reader, checked((int)block.CompressedSize));
                var decoded = Decompress(input, checked((int)block.UncompressedSize), block.Flags & 0x3F, cancellationToken);
                if (search.Feed(decoded)) { found = true; evidence = "dyn/mouse/pc_mouse_mgr.prefab"; return true; }
                if (taiwanSearch?.Feed(decoded) == true) { found = true; evidence = "PCMouseMgr"; return true; }
            }
            return true;
        }
        catch (NotSupportedException ex) { reason = ex.Message; return false; }
        catch (InvalidDataException ex) { reason = ex.Message; return false; }
        catch (EndOfStreamException) { reason = "封包資料不完整"; return false; }
        catch (IOException ex) { reason = ex.Message; return false; }
        catch (OverflowException) { reason = "封包大小欄位超出範圍"; return false; }
        catch (ArgumentException ex) { reason = ex.Message; return false; }
        catch (UnauthorizedAccessException ex) { reason = $"無法讀取封包：{ex.Message}"; return false; }
        catch (System.Security.SecurityException ex) { reason = $"無法讀取封包：{ex.Message}"; return false; }
    }

    private static BlockInfo ParseBlockInfo(byte[] data, int format)
    {
        using var ms = new MemoryStream(data, false);
        using var reader = new BinaryReader(ms);
        if (ms.Length < 20) throw new InvalidDataException("UnityFS 區塊索引太短");
        _ = ReadExact(reader, 16); // uncompressed data hash
        var count = ReadInt32BE(reader);
        if (count < 0 || count > 4096) throw new InvalidDataException("UnityFS 區塊數量超出 4096 檢查上限");
        var blocks = new List<DataBlock>(count);
        for (var i = 0; i < count; i++)
        {
            var unpacked = ReadUInt32BE(reader);
            var packed = ReadUInt32BE(reader);
            var flags = ReadUInt16BE(reader);
            blocks.Add(new DataBlock(unpacked, packed, flags));
        }
        var fileCount = ReadInt32BE(reader);
        if (fileCount < 0 || fileCount > 4096) throw new InvalidDataException("UnityFS 內部檔案數量超出 4096 檢查上限");
        for (var i = 0; i < fileCount; i++)
        {
            _ = ReadInt64BE(reader);
            _ = ReadInt64BE(reader);
            _ = ReadUInt32BE(reader);
            _ = ReadNullString(reader, 32 * 1024);
        }
        return new BlockInfo(blocks);
    }

    private static byte[] Decompress(byte[] input, int expected, int method, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expected < 0 || expected > MaximumDecodedSize) throw new InvalidDataException("解壓後區塊超出 16 MiB 安全上限");
        switch (method)
        {
            case 0:
                if (input.Length != expected) throw new InvalidDataException("未壓縮區塊長度不符");
                return input;
            case 2:
            case 3:
                return Lz4Decode(input, expected, cancellationToken);
            case 4:
            case 5:
                return ArknightsLz4VariantDecode(input, expected, cancellationToken);
            case 1:
                throw new NotSupportedException("此 UnityFS 封包使用 LZMA 壓縮；目前工具尚無法驗證此壓縮格式，因此不會修改它。");
            default:
                throw new NotSupportedException($"不支援 UnityFS 壓縮方法 {method}");
        }
    }

    private static byte[] Lz4Decode(byte[] input, int expected, CancellationToken cancellationToken)
    {
        var output = new byte[expected];
        var ip = 0;
        var op = 0;
        while (ip < input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = input[ip++];
            var literals = token >> 4;
            if (literals == 15) { byte x; do { if (ip >= input.Length) throw new InvalidDataException("LZ4 literals 長度欄位損毀"); x = input[ip++]; literals = checked(literals + x); } while (x == 255); }
            if (literals > input.Length - ip || literals > output.Length - op) throw new InvalidDataException("LZ4 literals 區域超出範圍");
            Buffer.BlockCopy(input, ip, output, op, literals); ip += literals; op += literals;
            if (ip == input.Length) break;
            if (input.Length - ip < 2) throw new InvalidDataException("LZ4 match offset 不完整");
            var offset = input[ip] | input[ip + 1] << 8; ip += 2;
            if (offset == 0 || offset > op) throw new InvalidDataException("LZ4 match offset 無效");
            var match = (token & 0x0F) + 4;
            if ((token & 0x0F) == 15) { byte x; do { if (ip >= input.Length) throw new InvalidDataException("LZ4 match 長度欄位損毀"); x = input[ip++]; match = checked(match + x); } while (x == 255); }
            if (match > output.Length - op) throw new InvalidDataException("LZ4 match 超出輸出範圍");
            for (var i = 0; i < match; i++)
            {
                if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                output[op + i] = output[op - offset + i];
            }
            op += match;
        }
        if (op != expected) throw new InvalidDataException($"LZ4 解壓大小不符（{op}/{expected}）");
        return output;
    }

    // Arknights' UnityPlayer routes bundle methods 4/5 through its own LZ4-like
    // decoder: literal length is the low nibble, match length the high nibble,
    // and the 16-bit match offset is big-endian (unlike standard LZ4 blocks).
    private static byte[] ArknightsLz4VariantDecode(byte[] input, int expected, CancellationToken cancellationToken)
    {
        var output = new byte[expected];
        var ip = 0;
        var op = 0;
        while (ip < input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = input[ip++];
            var literalNibble = token & 0x0F;
            var literals = literalNibble;
            if (literalNibble == 15)
            {
                byte extra;
                do
                {
                    if (ip >= input.Length) throw new InvalidDataException("Arknights LZ4 literals 長度欄位不完整");
                    extra = input[ip++];
                    literals = checked(literals + extra);
                } while (extra == 255);
            }
            if (literals > input.Length - ip || literals > output.Length - op) throw new InvalidDataException("Arknights LZ4 literals 區域超出範圍");
            Buffer.BlockCopy(input, ip, output, op, literals);
            ip += literals;
            op += literals;

            // A final literal-only sequence may end the stream without a match.
            if (ip == input.Length) break;
            if (input.Length - ip < 2) throw new InvalidDataException("Arknights LZ4 match offset 不完整");
            var offset = (input[ip] << 8) | input[ip + 1];
            ip += 2;
            if (offset == 0 || offset > op) throw new InvalidDataException("Arknights LZ4 match offset 無效");

            var matchNibble = token >> 4;
            var match = matchNibble + 4;
            if (matchNibble == 15)
            {
                byte extra;
                do
                {
                    if (ip >= input.Length) throw new InvalidDataException("Arknights LZ4 match 長度欄位不完整");
                    extra = input[ip++];
                    match = checked(match + extra);
                } while (extra == 255);
            }
            if (match > output.Length - op) throw new InvalidDataException("Arknights LZ4 match 超出輸出範圍");
            for (var i = 0; i < match; i++)
            {
                if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                output[op + i] = output[op - offset + i];
            }
            op += match;
        }
        if (op != expected) throw new InvalidDataException($"Arknights LZ4 解壓大小不符（{op}/{expected}）");
        return output;
    }

    private static int Align(long position, int alignment) => checked((int)((position + alignment - 1) & ~(alignment - 1)));

    private static byte[] CreateSerializedMarker(byte[] path)
    {
        var marker = new byte[path.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(marker.AsSpan(0, 4), path.Length);
        Buffer.BlockCopy(path, 0, marker, 4, path.Length);
        return marker;
    }
    private static string ReadNullString(BinaryReader reader, int max)
    {
        using var ms = new MemoryStream();
        for (var i = 0; i < max; i++) { var b = reader.ReadByte(); if (b == 0) return Encoding.UTF8.GetString(ms.ToArray()); ms.WriteByte(b); }
        throw new InvalidDataException("UnityFS 字串欄位過長或沒有結尾");
    }
    private static int ReadInt32BE(BinaryReader r) => unchecked((int)ReadUInt32BE(r));
    private static uint ReadUInt32BE(BinaryReader r) => BinaryPrimitives.ReadUInt32BigEndian(ReadExact(r, 4));
    private static ushort ReadUInt16BE(BinaryReader r) => BinaryPrimitives.ReadUInt16BigEndian(ReadExact(r, 2));
    private static long ReadInt64BE(BinaryReader r) => BinaryPrimitives.ReadInt64BigEndian(ReadExact(r, 8));
    private static byte[] ReadExact(BinaryReader r, int size)
    {
        var bytes = r.ReadBytes(size);
        if (bytes.Length != size) throw new EndOfStreamException();
        return bytes;
    }
    private sealed record DataBlock(uint UncompressedSize, uint CompressedSize, int Flags);
    private sealed record BlockInfo(List<DataBlock> Blocks);

    private sealed class ByteSearch
    {
        private readonly byte[][] _needles;
        private byte[] _tail = [];
        private readonly int _max;
        public ByteSearch(byte[][] needles) { _needles = needles; _max = needles.Max(n => n.Length); }
        public bool Feed(byte[] data)
        {
            var combined = new byte[_tail.Length + data.Length];
            Buffer.BlockCopy(_tail, 0, combined, 0, _tail.Length);
            Buffer.BlockCopy(data, 0, combined, _tail.Length, data.Length);
            foreach (var needle in _needles)
                if (IndexOfAsciiIgnoreCase(combined, needle) >= 0) return true;
            var keep = Math.Min(_max - 1, combined.Length);
            _tail = new byte[keep];
            Buffer.BlockCopy(combined, combined.Length - keep, _tail, 0, keep);
            return false;
        }
        private static int IndexOfAsciiIgnoreCase(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var j = 0;
                for (; j < needle.Length; j++)
                {
                    var a = haystack[i + j]; var b = needle[j];
                    if (a is >= (byte)'A' and <= (byte)'Z') a += 32;
                    if (b is >= (byte)'A' and <= (byte)'Z') b += 32;
                    if (a != b) break;
                }
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }
}
