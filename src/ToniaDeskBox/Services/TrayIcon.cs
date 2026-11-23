using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ToniaDeskBox.Services;

/// <summary>
/// 原生托盘图标（Shell_NotifyIcon），替掉原来的 <c>System.Windows.Forms.NotifyIcon</c>。
///
/// 为什么要自己写：整个 WinForms 程序集的唯一用途就是这一个托盘图标，
/// 却把 UseWindowsForms 打开、把另一套 UI 框架链路（含 System.Drawing）拖进进程，
/// 同时抬高安装体积与内存基线。这里只保留必要的一点点 P/Invoke：
/// 一个消息专用窗口接收托盘回调 + ICO 字节直接转 HICON。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 1;   // 自用消息号，只在本进程内有效
    private const uint TrayIconId = 1;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const int LR_DEFAULTCOLOR = 0x0000;

    /// <summary>右键托盘图标（含键盘唤出的上下文菜单）。</summary>
    public event Action? ContextMenuRequested;
    /// <summary>左键双击托盘图标。</summary>
    public event Action? DoubleClicked;

    private readonly HwndSource _source;
    private readonly IntPtr _hwnd;
    private readonly string _tooltip;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _hIcon;
    private bool _added;

    /// <summary>图标当前是否真的挂在任务栏上（Shell_NotifyIcon 的返回值）。</summary>
    public bool IsAttached => _added;

    public TrayIcon(string tooltip)
    {
        _tooltip = Trim(tooltip);

        // 消息专用窗口：不进任务栏、不可见，只负责收托盘回调
        var parameters = new HwndSourceParameters("ToniaDeskBox.TrayHost")
        {
            ParentWindow = new IntPtr(-3),   // HWND_MESSAGE
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _hwnd = _source.Handle;
        _source.AddHook(WndProc);

        // explorer 重启后托盘图标会被清空，需要等它广播 TaskbarCreated 再重新挂
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>装入 ICO 字节并显示图标。传 null 时退回系统默认应用图标。</summary>
    public void SetIcon(byte[]? ico)
    {
        var next = ico != null ? CreateHIcon(ico, 16) : IntPtr.Zero;
        if (next == IntPtr.Zero) next = LoadIcon(IntPtr.Zero, IDI_APPLICATION);

        var old = _hIcon;
        _hIcon = next;
        if (old != IntPtr.Zero && old != next) DestroyIcon(old);

        if (_added) Modify();
        else Add();
    }

    private void Add()
    {
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _hIcon;
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void Modify()
    {
        var data = NewData();
        data.uFlags = NIF_ICON | NIF_TIP;
        data.hIcon = _hIcon;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch (lParam.ToInt64() & 0xFFFF)
            {
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ContextMenuRequested?.Invoke();
                    handled = true;
                    break;
                case WM_LBUTTONDBLCLK:
                    DoubleClicked?.Invoke();
                    handled = true;
                    break;
            }
        }
        else if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            // 新任务栏已经把旧图标丢了，直接重新挂一次
            _added = false;
            Add();
        }
        return IntPtr.Zero;
    }

    private static string Trim(string text)
    {
        // szTip 只有 128 个字符（含结尾 \0），超长会让 Shell_NotifyIcon 直接失败
        const int max = 127;
        if (string.IsNullOrEmpty(text)) return "Tonia-DeskBox";
        return text.Length <= max ? text : text[..max];
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    // ---------- ICO → HICON ----------

    /// <summary>
    /// 从 .ico 文件字节里挑一张最接近目标尺寸的图，转成 HICON。
    /// 走 CreateIconFromResourceEx 而不是先写临时文件，避免在系统盘留垃圾。
    /// </summary>
    private static IntPtr CreateHIcon(byte[] ico, int size)
    {
        try
        {
            if (ico.Length < 6) return IntPtr.Zero;
            int count = BitConverter.ToUInt16(ico, 4);
            if (count <= 0) return IntPtr.Zero;

            int bestOffset = -1, bestLength = 0, bestScore = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int entry = 6 + i * 16;
                if (entry + 16 > ico.Length) break;

                int w = ico[entry] == 0 ? 256 : ico[entry];   // 0 表示 256
                int length = (int)BitConverter.ToUInt32(ico, entry + 8);
                int offset = (int)BitConverter.ToUInt32(ico, entry + 12);
                if (length <= 0 || offset < 0 || offset + length > ico.Length) continue;

                // 优先精确命中，其次比目标略大的（缩小比放大清楚），最后才是更大的图
                int score = w == size ? 0 : w > size ? w - size : 1000 + (size - w);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestOffset = offset;
                    bestLength = length;
                }
            }

            if (bestOffset < 0) return IntPtr.Zero;

            var bits = new byte[bestLength];
            Buffer.BlockCopy(ico, bestOffset, bits, 0, bestLength);
            return CreateIconFromResourceEx(bits, (uint)bits.Length, true, 0x00030000,
                size, size, LR_DEFAULTCOLOR);
        }
        catch
        {
            // 图标解析失败不该影响托盘可用性：调用方会退回系统默认图标
            return IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private static readonly IntPtr IDI_APPLICATION = new(32512);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(
        byte[] presbits, uint dwResSize, bool fIcon, uint dwVer,
        int cxDesired, int cyDesired, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
}
