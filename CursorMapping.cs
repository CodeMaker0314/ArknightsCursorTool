using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ArknightsCursorTool;

internal sealed class CursorMappingController : IDisposable
{
    private readonly string _gameRoot;
    private readonly Action<string> _log;
    private readonly Cursor _cursor;
    private readonly bool _ownsCursor;
    private readonly CursorOverlayWindow _overlay;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;
    private int _stopped;
    private int _cleanedUp;

    public CursorMappingController(string gameRoot, Cursor cursor, bool ownsCursor, int mappedSize, Action<string> log)
    {
        _gameRoot = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _log = log;
        _cursor = cursor;
        _ownsCursor = ownsCursor;
        _overlay = new CursorOverlayWindow(cursor, mappedSize);
        try
        {
            _ = _overlay.Handle;
            _overlay.InitializeLayeredImage();
            _overlay.Show();
            _overlay.Hide();
        }
        catch
        {
            _overlay.Dispose();
            if (_ownsCursor) _cursor.Dispose();
            _stop.Dispose();
            throw;
        }

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Arknights cursor mapper",
            Priority = ThreadPriority.Normal
        };
        _worker.Start();
    }

    public bool Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0) _stop.Cancel();
        if (_worker.IsAlive && !_worker.Join(TimeSpan.FromSeconds(3)))
        {
            _log("映射執行緒尚未結束；游標恢復程序仍在等待。可稍候再按停止。");
            return false;
        }
        if (Interlocked.Exchange(ref _cleanedUp, 1) != 0) return true;

        if (!_overlay.IsDisposed)
        {
            if (_overlay.InvokeRequired) _overlay.Invoke(_overlay.Close);
            else _overlay.Close();
            _overlay.Dispose();
        }
        if (_ownsCursor) _cursor.Dispose();
        _stop.Dispose();
        return true;
    }

    public void Dispose() => _ = Stop();

    private void WorkerLoop()
    {
        uint currentThreadId = 0;
        uint attachedGameThreadId = 0;
        var hideCalls = 0;
        var attachFailedThreadId = 0u;
        var retryAttachAfter = DateTime.MinValue;
        var overlayVisible = false;
        var lastX = int.MinValue;
        var lastY = int.MinValue;
        var lastProcessCheck = DateTime.MinValue;
        var cachedProcessId = 0u;
        var cachedIsGame = false;
        var cachedProcessDescription = "";
        var lastStateMessage = "";
        var timerResolutionRequested = TimeBeginPeriod(1) == 0;

        void LogStateIfChanged(string message)
        {
            if (string.Equals(message, lastStateMessage, StringComparison.Ordinal)) return;
            _log(message);
            lastStateMessage = message;
        }

        try
        {
            // GetCurrentThreadId is exported by Kernel32, not User32. Keep it
            // inside the worker's exception boundary so native API failures
            // cannot terminate the whole application.
            currentThreadId = GetCurrentThreadId();
            // AttachThreadInput requires this worker thread to own a message queue.
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            while (!_stop.IsCancellationRequested)
            {
                var foreground = GetForegroundWindow();
                var processId = 0u;
                var gameThreadId = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out processId);
                var now = DateTime.UtcNow;
                var isGameWindow = false;
                if (gameThreadId != 0)
                {
                    if (processId != cachedProcessId || now - lastProcessCheck >= TimeSpan.FromSeconds(1))
                    {
                        cachedProcessId = processId;
                        cachedIsGame = IsGameProcess(processId, foreground, out cachedProcessDescription);
                        lastProcessCheck = now;
                    }
                    isGameWindow = cachedIsGame;
                }
                else
                {
                    cachedProcessId = 0;
                    cachedIsGame = false;
                    cachedProcessDescription = "沒有前景視窗";
                }

                if (!isGameWindow)
                {
                    HideOverlay(ref overlayVisible);
                    DetachAndRestore(currentThreadId, ref attachedGameThreadId, ref hideCalls);
                    var title = GetWindowTitle(foreground);
                    LogStateIfChanged($"等待所選遊戲視窗：前景程序資訊「{cachedProcessDescription}」，視窗標題「{title}」。");
                    Thread.Sleep(4);
                    continue;
                }

                if (!TryGetCursorInClient(foreground, out var cursorPosition))
                {
                    HideOverlay(ref overlayVisible);
                    DetachAndRestore(currentThreadId, ref attachedGameThreadId, ref hideCalls);
                    LogStateIfChanged("遊戲視窗已在前景，但指標位於客戶區外；移入遊戲畫面後會套用映射。");
                    Thread.Sleep(4);
                    continue;
                }

                if (attachedGameThreadId != 0 && attachedGameThreadId != gameThreadId)
                {
                    HideOverlay(ref overlayVisible);
                    DetachAndRestore(currentThreadId, ref attachedGameThreadId, ref hideCalls);
                }

                if (attachedGameThreadId == 0)
                {
                    if (gameThreadId == attachFailedThreadId && now < retryAttachAfter)
                    {
                        HideOverlay(ref overlayVisible);
                        Thread.Sleep(4);
                        continue;
                    }

                    if (!AttachThreadInput(currentThreadId, gameThreadId, true))
                    {
                        attachFailedThreadId = gameThreadId;
                        retryAttachAfter = now + TimeSpan.FromSeconds(2);
                        HideOverlay(ref overlayVisible);
                        LogStateIfChanged($"無法連接遊戲輸入執行緒（Win32 {Marshal.GetLastWin32Error()}）；請確認工具與遊戲使用相同權限。");
                        Thread.Sleep(4);
                        continue;
                    }

                    attachedGameThreadId = gameThreadId;
                    attachFailedThreadId = 0;
                    hideCalls = HideOriginalCursor();
                    LogStateIfChanged($"已連接遊戲視窗（PID {processId}），Windows 指標映射已生效。");
                }

                var x = cursorPosition.X - _overlay.HotSpotX;
                var y = cursorPosition.Y - _overlay.HotSpotY;
                if (!overlayVisible || x != lastX || y != lastY)
                {
                    if (!SetWindowPos(_overlay.Handle, HwndTopMost, x, y, 0, 0, SwpNoActivate | SwpNoSize | SwpShowWindow))
                    {
                        attachFailedThreadId = gameThreadId;
                        retryAttachAfter = now + TimeSpan.FromSeconds(2);
                        LogStateIfChanged($"無法更新映射游標視窗（Win32 {Marshal.GetLastWin32Error()}）；已恢復原指標，稍後重試。");
                        HideOverlay(ref overlayVisible);
                        DetachAndRestore(currentThreadId, ref attachedGameThreadId, ref hideCalls);
                        Thread.Sleep(4);
                        continue;
                    }
                    overlayVisible = true;
                    lastX = x;
                    lastY = y;
                }

                Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            _log("映射錯誤：" + ex.Message);
        }
        finally
        {
            HideOverlay(ref overlayVisible);
            DetachAndRestore(currentThreadId, ref attachedGameThreadId, ref hideCalls);
            if (timerResolutionRequested) _ = TimeEndPeriod(1);
        }
    }

    private bool IsGameProcess(uint processId, IntPtr foreground, out string description)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var executable = TryGetProcessPath(process);
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var fullPath = Path.GetFullPath(executable);
                var matches = fullPath.StartsWith(_gameRoot, StringComparison.OrdinalIgnoreCase);
                description = $"程序 {process.ProcessName}，路徑「{fullPath}」" + (matches ? "（符合所選客戶端）" : "（不在所選客戶端目錄下）");
                return matches;
            }

            var windowTitle = GetWindowTitle(foreground);
            if (IsArknightsName(process.ProcessName) && IsArknightsTitle(windowTitle))
            {
                description = $"程序 {process.ProcessName} 的路徑無法讀取，但視窗標題「{windowTitle}」符合遊戲；以標題備援辨識。";
                return true;
            }

            description = $"程序 {process.ProcessName} 的執行檔路徑無法讀取";
            return false;
        }
        catch (Exception ex)
        {
            description = $"PID {processId} 無法檢查：{ex.Message}";
            return false;
        }
    }

    internal static string? TryGetProcessPath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch { }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(32768);
            var length = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
        }
        finally { _ = CloseHandle(handle); }
    }

    internal static bool IsArknightsName(string processName) =>
        processName.StartsWith("Arknights", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("明日方舟", StringComparison.OrdinalIgnoreCase);

    private static bool IsArknightsTitle(string title) =>
        title.Contains("明日方舟", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Arknights", StringComparison.OrdinalIgnoreCase);

    private static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "（無前景視窗）";
        var title = new StringBuilder(512);
        _ = GetWindowText(hwnd, title, title.Capacity);
        return title.Length == 0 ? "（無標題）" : title.ToString();
    }

    private static bool TryGetCursorInClient(IntPtr hwnd, out NativePoint cursorPoint)
    {
        cursorPoint = default;
        if (!GetCursorPos(out cursorPoint) || !GetClientRect(hwnd, out var client)) return false;
        var topLeft = new NativePoint(client.Left, client.Top);
        var bottomRight = new NativePoint(client.Right, client.Bottom);
        if (!ClientToScreen(hwnd, ref topLeft) || !ClientToScreen(hwnd, ref bottomRight)) return false;
        return cursorPoint.X >= topLeft.X && cursorPoint.X < bottomRight.X
            && cursorPoint.Y >= topLeft.Y && cursorPoint.Y < bottomRight.Y;
    }

    private static int HideOriginalCursor()
    {
        var calls = 0;
        while (calls < 32)
        {
            calls++;
            if (ShowCursor(false) < 0) break;
        }
        return calls;
    }

    private static void DetachAndRestore(uint currentThreadId, ref uint gameThreadId, ref int hideCalls)
    {
        if (gameThreadId == 0) return;
        for (var i = 0; i < hideCalls; i++) _ = ShowCursor(true);
        hideCalls = 0;
        _ = AttachThreadInput(currentThreadId, gameThreadId, false);
        gameThreadId = 0;
    }

    private void HideOverlay(ref bool visible)
    {
        if (!visible) return;
        _ = ShowWindow(_overlay.Handle, SwHide);
        visible = false;
    }

    private const int SwHide = 0;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopMost = new(-1);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint period);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }
}

internal sealed class CursorOverlayWindow : Form
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;

    private readonly Cursor _cursor;
    public int HotSpotX { get; }
    public int HotSpotY { get; }

    public CursorOverlayWindow(Cursor cursor, int mappedSize)
    {
        _cursor = cursor;
        var source = cursor.Size;
        if (source.Width <= 0 || source.Height <= 0) source = new Size(32, 32);
        var hotspot = cursor.HotSpot;
        var width = mappedSize > 0 ? mappedSize : source.Width;
        var height = mappedSize > 0 ? Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width)) : source.Height;
        HotSpotX = (int)Math.Round(hotspot.X * (double)width / source.Width);
        HotSpotY = (int)Math.Round(hotspot.Y * (double)height / source.Height);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        ClientSize = new Size(width, height);
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
            return parameters;
        }
    }

    public void InitializeLayeredImage()
    {
        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            _cursor.DrawStretched(graphics, new Rectangle(Point.Empty, ClientSize));
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "無法取得映射游標畫面。");
        try
        {
            var memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "無法建立映射游標記憶體畫面。");
            try
            {
                var bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
                if (bitmapHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "無法建立映射游標點陣圖。");
                var oldBitmap = SelectObject(memoryDc, bitmapHandle);
                if (oldBitmap == IntPtr.Zero || oldBitmap == new IntPtr(-1))
                {
                    _ = DeleteObject(bitmapHandle);
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "無法選取映射游標點陣圖。");
                }
                try
                {
                    var destination = new NativePoint(-32000, -32000);
                    var source = new NativePoint(0, 0);
                    var size = new NativeSize(ClientSize.Width, ClientSize.Height);
                    var blend = new BlendFunction { Operation = AcSrcOver, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
                    if (!UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "無法更新透明映射游標。");
                }
                finally
                {
                    _ = SelectObject(memoryDc, oldBitmap);
                    _ = DeleteObject(bitmapHandle);
                }
            }
            finally { _ = DeleteDC(memoryDc); }
        }
        finally { _ = ReleaseDC(IntPtr.Zero, screenDc); }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest) { message.Result = new IntPtr(HtTransparent); return; }
        if (message.Msg == WmMouseActivate) { message.Result = new IntPtr(MaNoActivate); return; }
        base.WndProc(ref message);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc, ref NativePoint destination, ref NativeSize size, IntPtr sourceDc, ref NativePoint source, int colorKey, ref BlendFunction blend, int flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteDC(IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte Operation;
        public byte Flags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }
}
