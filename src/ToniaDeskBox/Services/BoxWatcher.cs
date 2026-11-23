using System.IO;
using System.Windows.Threading;

namespace ToniaDeskBox.Services;

/// <summary>
/// 监听单个盒子目录的外部变化。
///
/// 解决的是「信任」问题而不是功能问题：以前往盒目录里拷文件、或在资源管理器里删掉盒内文件，
/// 角标计数要等下次自己操作或重启才准 —— 用户会怀疑"是不是丢文件了"。
///
/// 三条约束：
/// 1) 300ms 防抖：解压/批量粘贴会在几十毫秒内打出上百条事件，不合并会把 UI 线程打满；
/// 2) 自身操作要抑制：拖入 / 拖出 / 放回桌面 / 一键整理期间文件变动是自己造成的，
///    此时刷新列表会打断正在进行的拖拽；
/// 3) 目录可能还不存在（新盒子第一次拖入前），也可能被外部删掉 —— 所以监听是"按需补挂"的。
/// </summary>
public sealed class BoxWatcher : IDisposable
{
    private readonly string _dir;
    private readonly Action _onChanged;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private FileSystemWatcher? _watcher;
    private int _suspend;
    private bool _disposed;

    public BoxWatcher(string dir, Action onChanged)
    {
        _dir = dir;
        _onChanged = onChanged;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>目录存在且尚未监听时挂上。文件操作后顺手调一次即可。</summary>
    public void EnsureStarted()
    {
        if (_disposed || _watcher != null) return;
        if (!Directory.Exists(_dir)) return;

        try
        {
            var w = new FileSystemWatcher(_dir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 32 * 1024,
            };
            w.Created += OnFileSystemEvent;
            w.Deleted += OnFileSystemEvent;
            w.Changed += OnFileSystemEvent;
            w.Renamed += OnFileSystemEvent;
            w.Error += OnWatcherError;
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch
        {
            // 目录不可访问（权限 / 断连的网络盘）时静默放弃：角标退化为"操作后刷新"，不影响使用
            _watcher = null;
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => Queue();

    private void Queue()
    {
        if (_disposed) return;
        // 事件在 IO 线程上到，必须回到 UI 线程才能动控件
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _suspend > 0) return;
            // 每次事件都重置计时器：最后一次变动后静默 300ms 才真正刷新
            _timer.Stop();
            _timer.Start();
        }));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_disposed || _suspend > 0) return;
        try { _onChanged(); }
        catch
        {
            // 刷新失败不该影响监听本身：下次变动还会再来一次
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // 缓冲区溢出 / 目录被移除：扔掉这个 watcher，等下次 EnsureStarted 重建
        _dispatcher.BeginInvoke(new Action(ResetWatcher));
    }

    private void ResetWatcher()
    {
        var w = _watcher;
        _watcher = null;
        if (w == null) return;
        try
        {
            w.EnableRaisingEvents = false;
            w.Created -= OnFileSystemEvent;
            w.Deleted -= OnFileSystemEvent;
            w.Changed -= OnFileSystemEvent;
            w.Renamed -= OnFileSystemEvent;
            w.Error -= OnWatcherError;
            w.Dispose();
        }
        catch { }
    }

    /// <summary>
    /// 在自身操作期间抑制回调。用 using 包住文件操作：
    /// <code>using (watcher.Suspend()) { FileMover.MoveTo(...); }</code>
    /// </summary>
    public IDisposable Suspend()
    {
        _suspend++;
        _timer.Stop();
        return new Scope(this);
    }

    private void Resume()
    {
        if (_suspend > 0) _suspend--;
        if (_suspend == 0) _timer.Stop();   // 丢掉抑制期间堆积的那一拍
    }

    private sealed class Scope : IDisposable
    {
        private BoxWatcher? _owner;
        public Scope(BoxWatcher owner) => _owner = owner;
        public void Dispose()
        {
            _owner?.Resume();
            _owner = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        ResetWatcher();
    }
}
