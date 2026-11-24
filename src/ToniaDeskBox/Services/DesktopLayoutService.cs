using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace ToniaDeskBox.Services;

/// <summary>
/// 一个桌面图标在屏幕上占的矩形（DIP，虚拟桌面坐标）。
/// 只用来「别把新盒子压在图标上」，不参与快照、不需要持久化。
/// </summary>
public readonly record struct DesktopIconBounds(string Name, double X, double Y, double W, double H);

/// <summary>一个桌面图标的位置（ListView 客户区像素坐标）。</summary>
public sealed class DesktopIconPos
{
    /// <summary>**显示名**（Explorer 可能隐藏已知扩展名，所以它不等于文件名）。</summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// 对应的完整路径。系统图标（回收站/此电脑）没有路径，留空。
    /// 定位时优先用它：显示名拿去给 Shell 解析并不可靠（"Stretchly" 未必能解析回 "Stretchly.lnk"）。
    /// </summary>
    public string Path { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>桌面图标的「显示样式」+ 逐个位置。</summary>
public sealed class DesktopLayout
{
    public List<DesktopIconPos> Icons { get; set; } = new();
    /// <summary>「自动排列图标」是否开启。开着的时候设位置是无效的，必须先关掉。</summary>
    public bool AutoArrange { get; set; }

    /// <summary>
    /// 每个图标在屏幕上占的矩形（DIP）。采集时顺手算好，供「整理时新盒子避让」使用。
    /// 不进快照：它是当前这一刻的屏幕布局，恢复时用的是 <see cref="Icons"/> 里的坐标。
    /// </summary>
    [JsonIgnore]
    public List<DesktopIconBounds> ScreenBounds { get; set; } = new();
}

/// <summary>
/// 读写桌面图标的位置。
///
/// 为什么要动 Win32：Windows 没有给「桌面图标布局」提供公开 API，位置只活在 Explorer 的
/// 桌面 ListView 里（注册表 Bags\1\Desktop\ItemPos* 是私有二进制格式、且要重启 explorer 才生效）。
/// 所以这里走 `Progman → SHELLDLL_DefView → SysListView32` 这条老路，用 ListView 消息
/// `LVM_GETITEMPOSITION` / `LVM_SETITEMPOSITION` 直接读写，改完立刻可见。
///
/// 跨进程：ListView 属于 explorer.exe，`LVM_GETITEMPOSITION` 的 lParam 必须指向**目标进程**里的
/// POINT；`LVM_GETITEMTEXTW` 的 LVITEMW 和文本缓冲同理。所以要先 `OpenProcess` +
/// `VirtualAllocEx` 在自己够得着的地方开一块共享内存，再读回来。
///
/// 安全边界：只读不写、只写位置，不碰文件；任何一步失败都返回具体原因，
/// 让上层能如实告诉用户「是没找到桌面、还是没权限、还是图标被自动排列锁住了」。
/// </summary>
public static class DesktopLayoutService
{
    // ---- ListView 消息 ----
    private const int LVM_GETITEMCOUNT = 0x1004;
    private const int LVM_SETITEMPOSITION = 0x100F;
    private const int LVM_SETITEMPOSITION32 = 0x1031;
    private const int LVM_GETITEMPOSITION = 0x1010;
    private const int LVM_GETITEMTEXTW = 0x1073;
    private const int LVM_REDRAWITEMS = 0x1015;
    private const int LVM_GETITEMRECT = 0x100E;

    /// <summary>LVM_GETITEMRECT 的「要整块区域」标志（图标 + 文字标签）。</summary>
    private const int LVIR_BOUNDS = 0;

    /// <summary>拿不到图标矩形时的保守格子尺寸（物理像素）：桌面默认图标 75×75 上下。</summary>
    private const int FallbackIconSize = 84;

    // ---- 窗口样式 ----
    private const int GWL_STYLE = -16;
    private const long LVS_AUTOARRANGE = 0x0100;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_FRAMECHANGED = 0x0020;

    // ---- 进程内存 ----
    private const int PROCESS_VM_OPERATION = 0x0008;
    private const int PROCESS_VM_READ = 0x0010;
    private const int PROCESS_VM_WRITE = 0x0020;
    private const int PROCESS_QUERY_INFORMATION = 0x0400;
    private const int MEM_COMMIT = 0x1000;
    private const int MEM_RESERVE = 0x2000;
    private const int MEM_RELEASE = 0x8000;
    private const int PAGE_READWRITE = 0x04;

    private const int MaxNameChars = 260;

    /// <summary>
    /// 未公开但长期稳定的消息：发给桌面的 DefView 父窗口，返回 `IShellBrowser*`。
    /// 用它绕开 `IShellWindows` + VARIANT 的一整套麻烦，直接拿到桌面视图。
    /// </summary>
    private const int WM_GETISHELLBROWSER = 0x0407;

    /// <summary>`IFolderView::SelectAndPositionItems` 的「只定位、不改选中」标志。</summary>
    private const uint SVSI_POSITIONITEM = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, int flags);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    /// <summary>把 ListView 客户区坐标换算成屏幕坐标（桌面图标位置只在客户区里）。</summary>
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    /// <summary>窗口所在屏的 DPI（Win10 1607+）。用于把物理像素换成 WPF 的 DIP。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address,
        IntPtr size, int allocationType, int protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, int freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
        byte[] buffer, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address,
        byte[] buffer, IntPtr size, out IntPtr written);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(
        [MarshalAs(UnmanagedType.Interface)] out IShellFolder shellFolder);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    /// <summary>取绝对 PIDL 里最后一个 SHITEMID 的地址，也就是相对该文件夹的「子 PIDL」。</summary>
    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder path);

    private const uint SHGFI_PIDL = 0x00000008;
    private const uint SvgioAllView = 0x00000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes,
        ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint attributes,
        ref SHFILEINFO info, uint size, uint flags);

    private const uint SHGFI_DISPLAYNAME = 0x00000200;

    // ---- Shell COM 接口 ----
    // 只声明用到的方法，但**顺序必须与原生 vtable 完全一致** —— 少一个/错一位就是野指针，
    // 表现为 AccessViolation 而不是编译错误，所以宁可把用不到的方法也按序写出来。

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellBrowser
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
        // IShellBrowser
        [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string pszStatusText);
        [PreserveSig] int EnableModelessSB(bool fEnable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
        [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
        [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object ppshv);
        [PreserveSig] int OnViewWindowActive([MarshalAs(UnmanagedType.Interface)] object pshv);
        [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
        [PreserveSig] int SetCurrentViewMode(uint viewMode);
        [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
        [PreserveSig] int Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int piItem);
        [PreserveSig] int GetFocusedItem(out int piItem);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
        [PreserveSig] int GetSpacing(out POINT ppt);
        [PreserveSig] int GetDefaultSpacing(out POINT ppt);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int iItem, uint dwFlags);
        [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
    }

    private const uint FwfAutoArrange = 0x00000001;

    /// <summary>
    /// IShellView：`SaveViewState` 是把当前布局写回注册表 Bags 的唯一入口。
    /// 不调它的话，`SelectAndPositionItems` 改的位置只在内存里存在一瞬，
    /// Explorer 下一次异步整理就会按 Bags 里的旧坐标全部摆回去
    /// —— 实测位置在 80～160ms 之间被回滚，就是这个原因。
    /// </summary>
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellView
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
        // IShellView
        [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
        [PreserveSig] int EnableModeless(bool fEnable);
        [PreserveSig] int UIActivate(uint uState);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(IntPtr previousView, IntPtr shellBrowser,
            IntPtr viewRect, out IntPtr phwnd);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(out IntPtr folderSettings);
        [PreserveSig] int AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr lparam);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(IntPtr pidl, uint flags);
        [PreserveSig] int GetItemObject(uint aspect, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
    }

    /// <summary>
    /// IFolderView2：比 IFolderView 多了文件夹视图标志位的读写。
    /// 「自动排列图标」的权威状态在这里（`FWF_AUTOARRANGE`），
    /// ListView 的 `LVS_AUTOARRANGE` 可能与它不一致 —— 实测本机 LVS 说没开、Shell 说开着，
    /// 这种时候定位会被 Shell 立刻重排回去，表现就是"调用了成功、位置没变"。
    /// 所以必须重新声明 IFolderView 的全部方法（vtable 不能断）。
    /// </summary>
    [ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFolderView2
    {
        // ---- IFolderView ----
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
        [PreserveSig] int SetCurrentViewMode(uint viewMode);
        [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
        [PreserveSig] int Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int piItem);
        [PreserveSig] int GetFocusedItem(out int piItem);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
        [PreserveSig] int GetSpacing(out POINT ppt);
        [PreserveSig] int GetDefaultSpacing(out POINT ppt);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int iItem, uint dwFlags);
        [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
        // ---- IFolderView2 ----
        [PreserveSig] int SetGroupBy(IntPtr key, bool ascending);
        [PreserveSig] int GetGroupBy(IntPtr pkey, out bool ascending);
        [PreserveSig] int SetViewProperty(IntPtr pidl, IntPtr propkey, IntPtr propvalue);
        [PreserveSig] int GetViewProperty(IntPtr pidl, IntPtr propkey, IntPtr ppropvalue);
        [PreserveSig] int SetTileViewProperties(IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string propList);
        [PreserveSig] int SetExtendedTileViewProperties(IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string propList);
        [PreserveSig] int SetText(int iType, [MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int SetCurrentFolderFlags(uint mask, uint flags);
        [PreserveSig] int GetCurrentFolderFlags(out uint flags);
    }

    /// <summary>与 <see cref="System.IServiceProvider"/> 同名的 COM 接口，特意改名避免歧义。</summary>
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    /// <summary>
    /// IShellWindows 是 dual interface：vtable 里 IDispatch 的 4 个方法排在自定义方法之前。
    /// 用 InterfaceIsIUnknown 声明时必须把这 4 个占位写出来，否则后面全部错位。
    /// </summary>
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellWindows
    {
        // ---- IDispatch（占位，调用不到）----
        [PreserveSig] int GetTypeInfoCount(out int pctinfo);
        [PreserveSig] int GetTypeInfo(int iTInfo, int lcid, out IntPtr ppTInfo);
        [PreserveSig] int GetIDsOfNames(ref Guid riid, IntPtr rgszNames, int cNames,
            int lcid, IntPtr rgDispId);
        [PreserveSig] int Invoke(int dispIdMember, ref Guid riid, int lcid, ushort wFlags,
            IntPtr pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
        // ---- IShellWindows ----
        [PreserveSig] int get_Count(out int count);
        [PreserveSig] int Item(object index, [MarshalAs(UnmanagedType.Interface)] out object folder);
        [PreserveSig] int _NewEnum(out IntPtr enumerator);
        [PreserveSig] int Register([MarshalAs(UnmanagedType.Interface)] object pid,
            int hwnd, int swClass, out int cookie);
        [PreserveSig] int RegisterPending(int threadId, ref object loc, int hwnd,
            int swClass, int requestId, out int cookie);
        [PreserveSig] int Revoke(int cookie);
        [PreserveSig] int OnNavigate(int cookie, ref object loc);
        [PreserveSig] int OnActivated(int cookie, ref object loc);
        [PreserveSig] int FindWindowSW(ref object loc, ref object locRoot, int swClass,
            out int phwnd, int swfwOptions,
            [MarshalAs(UnmanagedType.Interface)] out object disp);
        [PreserveSig] int OnCreated(int cookie);
        [PreserveSig] int ProcessAttachDetach(bool attach);
    }

    /// <summary>桌面视图（SHELLDLL_DefView）。它是图标列表的父窗口，也是取 ShellBrowser 的入口。</summary>
    private static IntPtr FindDesktopDefView()
    {
        // 常规路径：Progman 下挂着 SHELLDLL_DefView
        var progman = FindWindow("Progman", null);
        var defView = progman == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

        // 壁纸轮播 / 动态壁纸时，DefView 会搬家到某个 WorkerW 下 —— 枚举找一遍
        if (defView == IntPtr.Zero)
        {
            EnumWindows((hwnd, _) =>
            {
                var child = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (child == IntPtr.Zero) return true;
                defView = child;
                return false;
            }, IntPtr.Zero);
        }

        return defView;
    }

    /// <summary>
    /// 「显示名 → 完整路径」映射。
    ///
    /// 为什么需要：桌面 ListView 给出的是**显示名**（`Stretchly`），而 Explorer 默认隐藏已知扩展名，
    /// 拿这个显示名去让 Shell 解析并不一定成功。枚举桌面目录、用 `SHGetFileInfo` 取每个条目的
    /// 显示名来配对，才能确定它到底是 `Stretchly.lnk` 还是别的。
    /// 系统图标（回收站等）不在文件系统里，配不上就留空，定位时退回按显示名解析。
    /// </summary>
    private static Dictionary<string, string> BuildDesktopDisplayNameMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 桌面上的图标来自两个目录：用户桌面 + 公用桌面（安装程序常把快捷方式放这里）。
        // 只扫用户桌面的话，`Stretchly`/`图吧工具箱` 这类公用快捷方式就拿不到路径。
        foreach (var desktop in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                 })
        {
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) continue;

            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(desktop))
                {
                    var info = new SHFILEINFO();
                    var size = (uint)Marshal.SizeOf<SHFILEINFO>();
                    if (SHGetFileInfo(path, 0, ref info, size, SHGFI_DISPLAYNAME) == IntPtr.Zero) continue;
                    if (string.IsNullOrEmpty(info.szDisplayName)) continue;
                    // 同名条目以用户桌面为准（先加入者优先）
                    map.TryAdd(info.szDisplayName, path);
                }
            }
            catch
            {
                // 某个桌面目录读不了就跳过，不影响另一个
            }
        }

        return map;
    }

    /// <summary>桌面图标所在的 ListView。找不到返回 <see cref="IntPtr.Zero"/>。</summary>
    private static IntPtr FindDesktopListView()
    {
        var defView = FindDesktopDefView();
        return defView == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>
    /// 等记录里的图标都出现在桌面上，最多 1.5 秒。
    /// 恢复文件后 Explorer 是异步把这些图标建出来的，太早定位会漏掉刚回来的那批。
    /// </summary>
    private static void WaitForIcons(DesktopLayout layout)
    {
        if (layout.Icons.Count == 0) return;
        if (!TryCapture(out var current, out _)) return;

        var names = new HashSet<string>(current.Icons.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        for (int waited = 0; waited < 1500 && layout.Icons.Any(i => !names.Contains(i.Name)); waited += 150)
        {
            Thread.Sleep(150);
            if (!TryCapture(out current, out _)) return;
            names = new HashSet<string>(current.Icons.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool ReadAutoArrange(IntPtr listView) =>
        (GetWindowLongPtr(listView, GWL_STYLE).ToInt64() & LVS_AUTOARRANGE) != 0;

    /// <summary>切换「自动排列图标」。开着时设置位置会被立刻重排，所以恢复前必须先关。</summary>
    private static void SetAutoArrange(IntPtr listView, bool on)
    {
        long style = GetWindowLongPtr(listView, GWL_STYLE).ToInt64();
        long next = on ? style | LVS_AUTOARRANGE : style & ~LVS_AUTOARRANGE;
        if (next == style) return;

        SetWindowLongPtr(listView, GWL_STYLE, new IntPtr(next));
        // 样式改动要重新走一遍非客户区计算，否则 Explorer 内部的排布逻辑还按旧值走
        SetWindowPos(listView, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// 读取当前桌面图标的名称与位置。
    /// </summary>
    /// <param name="error">失败原因（成功时为空串）。</param>
    public static bool TryCapture(out DesktopLayout layout, out string error)
    {
        layout = new DesktopLayout();
        error = "";

        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            error = "找不到桌面图标列表（Explorer 可能没有运行，或者桌面被第三方工具接管了）";
            return false;
        }

        GetWindowThreadProcessId(listView, out uint pid);
        if (pid == 0)
        {
            error = "无法确定 Explorer 进程";
            return false;
        }

        var process = OpenProcess(
            PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION,
            false, pid);
        if (process == IntPtr.Zero)
        {
            error = $"无法打开 Explorer 进程（错误码 {Marshal.GetLastWin32Error()}），可能需要以管理员身份运行";
            return false;
        }

        int itemSize = Marshal.SizeOf<LVITEMW>();
        int textBytes = MaxNameChars * 2;
        int total = itemSize + textBytes;

        IntPtr remote = IntPtr.Zero;
        try
        {
            remote = VirtualAllocEx(process, IntPtr.Zero, new IntPtr(total),
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero)
            {
                error = $"无法在 Explorer 进程里分配内存（错误码 {Marshal.GetLastWin32Error()}）";
                return false;
            }

            int count = (int)SendMessage(listView, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            if (count < 0)
            {
                error = "读取桌面图标数量失败";
                return false;
            }

            layout.AutoArrange = ReadAutoArrange(listView);
            // 以 Shell 的 folder flag 为准：它才是"自动排列"的真实状态，
            // ListView 的样式位可能和它不一致（本机实测就是不一致）
            if (ReadFolderAutoArrange() is bool fromShell) layout.AutoArrange = fromShell;

            // 图标间距（LVM_GETITEMSPACING）在桌面 ListView 上返回的不是有效间距
            // （实测是垃圾值），拿它去 LVM_SETITEMSPACING 会把桌面排布改坏 —— 不采、不设。

            var textBuffer = new byte[textBytes];
            var pointBuffer = new byte[8];
            var rectBuffer = new byte[16];
            // ListView 给的是显示名，定位要用路径：先摸清「显示名 → 完整路径」
            var pathByName = BuildDesktopDisplayNameMap();

            // 图标坐标是「ListView 客户区物理像素」，盒子的 Left/Top 是「屏幕 DIP」。
            // 两者必须换算到同一坐标系，否则避让就是拿两把尺子量。
            var clientOrigin = default(POINT);
            ClientToScreen(listView, ref clientOrigin);
            double dipScale = GetDpiScale(listView);

            for (int i = 0; i < count; i++)
            {
                var name = ReadItemName(process, listView, remote, itemSize, i, textBuffer);
                if (string.IsNullOrEmpty(name)) continue;

                // LVM_GETITEMPOSITION 的 lParam 指向远程 POINT，消息返回后再读回来
                if (SendMessage(listView, LVM_GETITEMPOSITION, new IntPtr(i), remote) == IntPtr.Zero)
                    continue;
                if (!ReadProcessMemory(process, remote, pointBuffer, new IntPtr(8), out _))
                    continue;

                int px = BitConverter.ToInt32(pointBuffer, 0);
                int py = BitConverter.ToInt32(pointBuffer, 4);

                layout.Icons.Add(new DesktopIconPos
                {
                    Name = name,
                    Path = pathByName.TryGetValue(name, out var full) ? full : "",
                    X = px,
                    Y = py,
                });

                // 整块区域（图标 + 文字标签）：整理时新盒子要避开它，
                // 否则会压在"没被整理、还要留在桌面"的快捷方式上面
                if (TryReadItemRect(process, listView, remote, i, rectBuffer,
                        out int rl, out int rt, out int rr, out int rb))
                {
                    layout.ScreenBounds.Add(new DesktopIconBounds(
                        name,
                        (clientOrigin.X + rl) / dipScale,
                        (clientOrigin.Y + rt) / dipScale,
                        (rr - rl) / dipScale,
                        (rb - rt) / dipScale));
                }
                else
                {
                    // 拿不到精确矩形时用保守格子：宁可多让一点，也不能压住图标
                    layout.ScreenBounds.Add(new DesktopIconBounds(
                        name,
                        (clientOrigin.X + px) / dipScale,
                        (clientOrigin.Y + py) / dipScale,
                        FallbackIconSize / dipScale,
                        FallbackIconSize / dipScale));
                }
            }

            if (layout.Icons.Count == 0)
            {
                // 空桌面不是错误：用户可能刚清空，恢复时就是"没有图标要恢复位置"
                return true;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (remote != IntPtr.Zero) VirtualFreeEx(process, remote, IntPtr.Zero, MEM_RELEASE);
            CloseHandle(process);
        }
    }

    private static string ReadItemName(IntPtr process, IntPtr listView,
        IntPtr remote, int itemSize, int index, byte[] textBuffer)
    {
        var item = new LVITEMW
        {
            iSubItem = 0,
            // 文本缓冲紧跟在 LVITEMW 后面：一次分配、两处使用
            pszText = IntPtr.Add(remote, itemSize),
            cchTextMax = MaxNameChars,
        };

        var itemBytes = new byte[itemSize];
        IntPtr offset = IntPtr.Zero;
        unsafeCopy(item, itemBytes);
        if (!WriteProcessMemory(process, remote, itemBytes, new IntPtr(itemSize), out _))
            return "";

        int chars = (int)SendMessage(listView, LVM_GETITEMTEXTW, new IntPtr(index), remote);
        if (chars <= 0) return "";

        chars = Math.Min(chars, MaxNameChars - 1);
        if (!ReadProcessMemory(process, IntPtr.Add(remote, itemSize), textBuffer,
                new IntPtr(chars * 2), out _))
            return "";

        return Encoding.Unicode.GetString(textBuffer, 0, chars * 2);

        // 用 Marshal 把结构体写进托管数组，避免 unsafe 上下文
        static void unsafeCopy(LVITEMW value, byte[] destination)
        {
            IntPtr buffer = Marshal.AllocHGlobal(destination.Length);
            try
            {
                Marshal.StructureToPtr(value, buffer, false);
                Marshal.Copy(buffer, destination, 0, destination.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// 读第 <paramref name="index"/> 项的整块矩形（图标 + 文字标签）。
    ///
    /// 跨进程：RECT 必须写在 Explorer 的内存里，且第一个字段要预置成
    /// <see cref="LVIR_BOUNDS"/>，告诉 ListView 要的是整块区域而不是图标本身。
    /// </summary>
    private static bool TryReadItemRect(IntPtr process, IntPtr listView, IntPtr remote, int index,
        byte[] buffer, out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        Array.Clear(buffer, 0, 16);   // Left = LVIR_BOUNDS = 0

        if (!WriteProcessMemory(process, remote, buffer, new IntPtr(16), out _)) return false;
        if (SendMessage(listView, LVM_GETITEMRECT, new IntPtr(index), remote) == IntPtr.Zero) return false;
        if (!ReadProcessMemory(process, remote, buffer, new IntPtr(16), out _)) return false;

        left = BitConverter.ToInt32(buffer, 0);
        top = BitConverter.ToInt32(buffer, 4);
        right = BitConverter.ToInt32(buffer, 8);
        bottom = BitConverter.ToInt32(buffer, 12);
        return right > left && bottom > top;
    }

    /// <summary>桌面窗口所在屏的缩放比例（96 DPI = 1.0）。取不到就按 100% 算。</summary>
    private static double GetDpiScale(IntPtr hwnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        catch (EntryPointNotFoundException)
        {
            // Win10 1607 之前没有这个 API：退回 100%，比猜错更安全
        }
        catch (DllNotFoundException) { }
        return 1.0;
    }

    /// <summary>
    /// 按名称把图标摆回记录的位置。名称对不上的（整理后被删/改名）计入 <paramref name="missing"/>。
    /// </summary>
    /// <param name="applied">实际设置成功的位置数。</param>
    /// <param name="missing">记录里有、桌面当前没有的图标数。</param>
    public static bool TryRestore(DesktopLayout layout, out string error,
        out int applied, out int missing)
    {
        applied = 0;
        missing = 0;
        error = "";

        if (layout.Icons.Count == 0) return true;

        var defView = FindDesktopDefView();
        if (defView == IntPtr.Zero)
        {
            error = "找不到桌面视图（Explorer 可能没有运行）";
            return false;
        }

        // 文件刚回到桌面时 Explorer 要过一会儿才把图标建出来，此刻定位是对着空气定位
        WaitForIcons(layout);

        if (!TryGetDesktopFolderView(out object? shellViewObject,
                out IFolderView? view, out string viewError) || view == null)
        {
            error = viewError;
            return false;
        }

        try
        {
            var listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            bool wasAutoArrange = listView != IntPtr.Zero && ReadAutoArrange(listView);
            // 自动排列开着时定位会被立刻重排回去：临时关掉，结束后按原样还原。
            // 两个地方都要关 —— LVS 样式位和 Shell folder flag 是两套状态。
            if (wasAutoArrange) SetAutoArrange(listView, false);

            var view2 = view as IFolderView2;
            var firstProblem = "";
            bool shellAutoArrange = false;
            uint originalFlags = 0;
            if (view2 != null && view2.GetCurrentFolderFlags(out originalFlags) == 0)
            {
                shellAutoArrange = (originalFlags & FwfAutoArrange) != 0;
                if (shellAutoArrange) view2.SetCurrentFolderFlags(FwfAutoArrange, 0);
            }

            try
            {
                // 定位必须走 IFolderView::SelectAndPositionItems。
                // 实测 Win11 桌面完全不响应 LVM_SETITEMPOSITION / …32（消息发出去、位置纹丝不动），
                // 因为图标位置由 Shell 的 folder view 持有，ListView 只是它的呈现层。
                int itemCount = 0;
                view.ItemCount(SvgioAllView, out itemCount);

                // 一次性把所有图标交给 Shell（和"拖动多个选中项"走同一条内部路径），
                // 逐个调用会被当成临时定位，下一次布局就被丢掉
                var found = new List<(DesktopIconPos Icon, IntPtr Pidl)>();
                foreach (var icon in layout.Icons)
                {
                    if (TryFindItem(view, itemCount, icon, out IntPtr pidl))
                    {
                        found.Add((icon, pidl));
                    }
                    else
                    {
                        missing++;
                        if (firstProblem.Length == 0) firstProblem = $"{icon.Name}：桌面上找不到该图标";
                    }
                }

                try
                {
                    if (found.Count > 0)
                    {
                        // 手写两块非托管内存（PIDL 数组 + POINT 数组），不依赖 marshaler 对
                        // 「双数组 + SizeParamIndex」的推断 —— 那套在双数组参数上会悄悄传错指针，
                        // 表现是返回 S_OK 却什么都没变，或者直接 AccessViolation。
                        IntPtr pidlArray = Marshal.AllocHGlobal(found.Count * IntPtr.Size);
                        IntPtr pointArray = Marshal.AllocHGlobal(found.Count * 8);
                        try
                        {
                            for (int i = 0; i < found.Count; i++)
                            {
                                Marshal.WriteIntPtr(pidlArray, i * IntPtr.Size, found[i].Pidl);
                                Marshal.WriteInt32(pointArray, i * 8, found[i].Icon.X);
                                Marshal.WriteInt32(pointArray, i * 8 + 4, found[i].Icon.Y);
                            }

                            int result = view.SelectAndPositionItems(
                                (uint)found.Count, pidlArray, pointArray, SVSI_POSITIONITEM);

                            if (result != 0)
                            {
                                if (firstProblem.Length == 0)
                                    firstProblem = $"设置图标位置失败（HRESULT 0x{result:X8}）";
                            }
                            else
                            {
                                // Shell 有 100～200ms 的异步布局周期：位置刚设完是新的，
                                // 但它会在之后把"它认为不对"的项重新排一遍。
                                // 所以隔一会儿再核对，才对得上用户最终看到的画面 ——
                                // 立刻读会得到"全部成功"的假象（实测 160ms 后就被还原）。
                                Thread.Sleep(300);
                                foreach (var (icon, pidl) in found)
                                {
                                    POINT now = default;
                                    int readHr = view.GetItemPosition(pidl, out now);
                                    if (readHr == 0
                                        && Math.Abs(now.X - icon.X) <= 2
                                        && Math.Abs(now.Y - icon.Y) <= 2)
                                    {
                                        applied++;
                                    }
                                    else
                                    {
                                        missing++;
                                        if (firstProblem.Length == 0)
                                            firstProblem = readHr != 0
                                                ? $"{icon.Name}：回读位置失败 0x{readHr:X8}"
                                                : $"{icon.Name}：定位后读回 ({now.X},{now.Y})，期望 ({icon.X},{icon.Y})";
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pidlArray);
                            Marshal.FreeHGlobal(pointArray);
                        }
                    }
                }
                finally
                {
                    foreach (var (_, pidl) in found) Marshal.FreeCoTaskMem(pidl);
                }
            }
            finally
            {
                if (shellAutoArrange && view2 != null)
                    view2.SetCurrentFolderFlags(FwfAutoArrange, FwfAutoArrange);
                if (wasAutoArrange && listView != IntPtr.Zero) SetAutoArrange(listView, true);
                if (listView != IntPtr.Zero) InvalidateRect(listView, IntPtr.Zero, true);
            }

            // 位置已经写进 Shell，但还没落到 Bags：必须 SaveViewState，
            // 否则 Explorer 的下一次异步整理会按旧坐标把它们摆回去
            int saveHr = -1;
            if (shellViewObject is IShellView shellView) saveHr = shellView.SaveViewState();
            if (saveHr != 0 && firstProblem.Length == 0)
                firstProblem = $"保存桌面布局失败（HRESULT 0x{saveHr:X8}）";

            if (applied == 0)
            {
                // 最常见的失败是"系统根本不接受程序设置的图标位置"（部分 Windows 版本会
                // 在一百多毫秒后整体重排回去）。这条要说人话，别把 HRESULT 甩给用户。
                error = layout.Icons.Count > 0 && missing >= layout.Icons.Count
                    ? "当前系统不允许程序设置桌面图标位置（Windows 会立刻重排），文件已放回桌面，但图标位置无法还原"
                    : firstProblem.Length > 0
                        ? $"图标位置未能还原：{firstProblem}"
                        : "没有可恢复的图标位置";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(view);
        }
    }

    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;

    /// <summary>
    /// 拿到桌面的 <see cref="IFolderView"/>。两条路：
    /// 1. `WM_GETISHELLBROWSER`（老系统可用，Win11 实测不响应）；
    /// 2. `IShellWindows.FindWindowSW` 找桌面窗口，再 `QueryService(SID_STopLevelBrowser)`。
    /// 第二条虽绕但稳，是官方公开的 COM 通路。
    /// </summary>
    private static bool TryGetDesktopFolderView(out object? shellViewObject,
        out IFolderView? view, out string error)
    {
        shellViewObject = null;
        view = null;
        error = "";

        var defView = FindDesktopDefView();
        if (defView != IntPtr.Zero)
        {
            var browserPtr = IntPtr.Zero;
            var parent = GetParent(defView);
            if (parent != IntPtr.Zero)
                browserPtr = SendMessage(parent, WM_GETISHELLBROWSER, IntPtr.Zero, IntPtr.Zero);
            if (browserPtr == IntPtr.Zero)
                browserPtr = SendMessage(defView, WM_GETISHELLBROWSER, IntPtr.Zero, IntPtr.Zero);

            if (browserPtr != IntPtr.Zero)
            {
                object? comObject = null;
                try
                {
                    comObject = Marshal.GetObjectForIUnknown(browserPtr);
                    if (TryQueryActiveView(comObject, out shellViewObject, out view, out _)) return true;
                }
                catch
                {
                    // 这条路不通就走下面，不把它当成最终错误
                }
                finally
                {
                    if (comObject != null) Marshal.ReleaseComObject(comObject);
                }
            }
        }

        return TryShellWindows(out shellViewObject, out view, out error);
    }

    private static bool TryShellWindows(out object? shellViewObject,
        out IFolderView? view, out string error)
    {
        shellViewObject = null;
        view = null;
        error = "";

        object? windowsObject = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidShellWindows);
            if (type == null)
            {
                error = "系统里找不到 ShellWindows 组件";
                return false;
            }

            windowsObject = Activator.CreateInstance(type);
            if (windowsObject is not IShellWindows windows)
            {
                error = "ShellWindows 接口不匹配";
                return false;
            }

            object loc = null!, root = null!;
            int hr = windows.FindWindowSW(ref loc, ref root, SwcDesktop,
                out _, SwfoNeedDispatch, out object dispatch);
            if (hr != 0 || dispatch == null)
            {
                error = $"找不到桌面 Shell 窗口（HRESULT 0x{hr:X8}）";
                return false;
            }

            return TryQueryActiveView(dispatch, out shellViewObject, out view, out error);
        }
        catch (Exception ex)
        {
            error = $"访问 ShellWindows 失败：{ex.Message}";
            return false;
        }
        finally
        {
            if (windowsObject != null) Marshal.ReleaseComObject(windowsObject);
        }
    }

    /// <summary>从「桌面窗口的 IDispatch / IShellBrowser」顺着 QueryService 走到 IFolderView。</summary>
    private static bool TryQueryActiveView(object source, out object? shellViewObject,
        out IFolderView? view, out string error)
    {
        shellViewObject = null;
        view = null;
        error = "";

        try
        {
            IShellBrowser? browser = source as IShellBrowser;
            if (browser == null && source is IShellServiceProvider provider)
            {
                Guid service = SidTopLevelBrowser;
                Guid iid = IidShellBrowser;
                int hr = provider.QueryService(ref service, ref iid, out object browserObject);
                if (hr != 0 || browserObject == null)
                {
                    error = $"QueryService 失败（HRESULT 0x{hr:X8}）";
                    return false;
                }
                browser = browserObject as IShellBrowser;
            }

            if (browser == null)
            {
                error = "桌面的 Shell 视图不提供 IShellBrowser 接口";
                return false;
            }

            int query = browser.QueryActiveShellView(out object viewObject);
            if (query != 0 || viewObject == null)
            {
                error = $"获取活动视图失败（HRESULT 0x{query:X8}）";
                return false;
            }

            view = viewObject as IFolderView;
            if (view == null)
            {
                error = "当前桌面视图不支持 IFolderView 接口";
                return false;
            }

            shellViewObject = viewObject;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>读「自动排列图标」的真实状态（Shell folder flags）。拿不到返回 null。</summary>
    private static bool? ReadFolderAutoArrange()
    {
        if (!TryGetDesktopFolderView(out _, out IFolderView? view, out _) || view == null) return null;
        try
        {
            if (view is IFolderView2 view2 && view2.GetCurrentFolderFlags(out uint flags) == 0)
                return (flags & FwfAutoArrange) != 0;
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(view);
        }
    }

    /// <summary>把记录里的图标解析成 PIDL。优先用**完整路径**（能精确区分 `a.lnk` 和 `a.txt`），
    /// 拿不到路径的系统图标（回收站/此电脑）退回按显示名解析。
    /// </summary>
    /// <summary>
    /// 在 folder view 里找到记录的那个图标，返回它的**子 PIDL**（调用方负责 CoTaskMemFree）。
    ///
    /// 为什么不自己用 `SHParseDisplayName` 解析：它给的是**绝对** PIDL，而 IFolderView 系列 API
    /// 要的是相对该文件夹的子 PIDL。传绝对 PIDL 时 `SelectAndPositionItems` 返回 S_OK 却毫无效果、
    /// `GetItemPosition` 直接 E_FAIL —— 这才是"调用成功但位置不动"的真正原因。
    /// 从 `IFolderView.Item(i)` 拿到的才是 Shell 自己认的那个 PIDL。
    /// </summary>
    private static bool TryFindItem(IFolderView view, int count, DesktopIconPos icon,
        out IntPtr childPidl)
    {
        childPidl = IntPtr.Zero;
        var buffer = new StringBuilder(260);

        for (int i = 0; i < count; i++)
        {
            if (view.Item(i, out IntPtr item) != 0 || item == IntPtr.Zero) continue;

            bool hit = false;
            buffer.Clear();

            if (SHGetPathFromIDList(item, buffer) && buffer.Length > 0)
            {
                // 文件系统条目：按完整路径比对（能区分同名不同扩展名）
                hit = !string.IsNullOrEmpty(icon.Path)
                      && string.Equals(buffer.ToString(), icon.Path, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // 系统图标（回收站/此电脑）：没有路径，退回比对显示名
                var info = new SHFILEINFO();
                var size = (uint)Marshal.SizeOf<SHFILEINFO>();
                if (SHGetFileInfoPidl(item, 0, ref info, size, SHGFI_DISPLAYNAME | SHGFI_PIDL) != IntPtr.Zero)
                    hit = string.Equals(info.szDisplayName, icon.Name, StringComparison.OrdinalIgnoreCase);
            }

            if (hit)
            {
                childPidl = item;
                return true;
            }

            Marshal.FreeCoTaskMem(item);
        }

        return false;
    }

    /// <summary>不建窗口、仅探测桌面 ListView 是否可访问（供冒烟检查用）。</summary>
    public static bool CanAccess(out string error)
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            error = "找不到桌面图标列表";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>诊断用：两套「自动排列」状态各读到什么。定位失效时先看这个。</summary>
    public static string DescribeState()
    {
        var listView = FindDesktopListView();
        var lvs = listView == IntPtr.Zero ? "找不到列表" : ReadAutoArrange(listView).ToString();
        var flag = ReadFolderAutoArrange();
        var shell = flag.HasValue ? flag.Value.ToString() : "读取失败";
        return $"LVS_AUTOARRANGE={lvs}、FWF_AUTOARRANGE={shell}";
    }
}
