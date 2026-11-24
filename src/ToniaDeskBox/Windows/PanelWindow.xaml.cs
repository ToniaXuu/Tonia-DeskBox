using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;

namespace ToniaDeskBox.Windows;

/// <summary>
/// 面板的打开方式。
/// - Browse：完整面板（标题 + 网格 + 底部提示），点盒子空白处打开；
/// - Search：只做搜索用的小面板，由折叠态盒子上的放大镜打开，搜索栏常驻。
/// </summary>
public enum PanelOpenMode
{
    Browse = 0,
    Search = 1,
}

public partial class PanelWindow : Window
{
    private class PanelItem : INotifyPropertyChanged
    {
        public string FilePath { get; init; } = "";
        public string DisplayName { get; init; } = "";

        private ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            set
            {
                if (ReferenceEquals(_icon, value)) return;
                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly BoxWindow _owner;
    private readonly BoxConfig _box;
    private readonly AppConfig _config;
    private readonly ConfigStore _store;

    /// <summary>面板排序方式。None = 保持 <c>Box.Order</c> 的收纳顺序（默认）。</summary>
    private enum SortMode { None, Name, Added }

    private List<PanelItem> _items = new();
    // 全量（按 Box.Order 排列）。_items 是它经关键字过滤后的显示集合，
    // 框选/拖拽/右键/打开都只作用在 _items 上。
    private List<PanelItem> _allItems = new();
    private string _filterText = "";
    private SortMode _sortMode = SortMode.None;
    private bool _searchOpen;
    private readonly PanelOpenMode _openMode;
    // 被别的卡片压住时把表面调成全不透明；记一个状态位，避免每 120ms 重刷笔刷
    private bool _opaqueForOverlap;
    private long _lastOverlapCheck;   // 拖动路径与定时器共用的节流时间戳
    private DispatcherTimer? _overlapTimer;
    private ContextMenu? _openMenu;
    private bool _suppressDeactivate;

    private Point _dragStart;
    private bool _draggingItem;
    private bool _actionTaken;

    // 面板内排序
    private bool _sorting;
    private int _sortIndex;
    private int _visualIndex;
    private Rect[] _origRects = Array.Empty<Rect>();
    private FrameworkElement? _sortCell;

    // 橡皮筋框选
    private bool _banding;
    private bool _bandMoved;
    private Point _bandStart;
    private HashSet<PanelItem> _bandBase = new();

    private const double DragThreshold = 10;
    private const double BandThreshold = 6;
    private const double DragOutMargin = 14;

    private readonly AppearanceTheme _theme;
    private bool _dropActive;
    private bool _openAnimated;
    // 从面板内拖出文件期间，不要把面板自己的 DragOver 当成外部拖入
    private bool _internalDrag;

    // ---------- 唤出 / 收起：仿 Windows 11 设置面板从任务栏展开的缩放动效 ----------
    // 缩放从「触发点（盒子）那一角」长出来，所以 origin 不是固定的 0.5,0.5，
    // 而是每次都按盒子相对面板的位置重算（见 ApplyScaleOrigin）。
    private const double OpenScale = 0.90;    // 起始比例：再小就像"弹窗"，不像系统面板
    private const double CloseScale = 0.90;   // 收起终点：与起始比例对称，看着是"退回触发点"
    private const double IntroShift = 6;      // 起始位移：沿远离盒子的方向预置
    private readonly ScaleTransform _scale = new(OpenScale, OpenScale);
    private readonly TranslateTransform _shift = new();
    private bool _animateOnClose;             // 只有 ClosePanel() 置位；Owner 销毁时直接关
    private bool _closeDone;
    private bool _closeAnimating;             // 收起动画已在跑，避免重复起动画
    // 展开动画期间挂起"补图标"与"重叠检测"：
    // 前者每到位一个图标就要重算一次阴影（位图缓存也会失效），后者会重建整套主题笔刷，
    // 两者都会把动画帧挤掉 —— 卡顿主要来自这里，不是动画时长。
    private readonly TaskCompletionSource<bool> _introGate = new();
    private bool _introAnimating;
    private DispatcherTimer? _closeGuard;

    public PanelWindow(BoxWindow owner, BoxConfig box, AppConfig config, ConfigStore store,
        PanelOpenMode mode = PanelOpenMode.Browse)
    {
        InitializeComponent();
        // 必须在 InitializeComponent() 之后挂：XAML 有自己的 Window.Resources，
        // 提前挂会被加载过程替换掉
        _theme = AppearanceTheme.Attach(this);
        _owner = owner;
        _box = box;
        _config = config;
        _store = store;
        _openMode = mode;

        PanelTitle.Text = box.Name;
        ApplyTheme();
        Owner = owner;
        // 首次绑定不做条目逐个淡入：此刻整个面板正在缩放展开，
        // 两套动画叠在一起会互相削弱——系统面板是内容跟着容器一起放大，
        // 不是"容器放大 + 条目自己再浮现一遍"。后续刷新列表（拖入/删除）仍然保留淡入。
        RefreshItems(animate: false);

        // 显示前预量尺寸并定位，避免窗口先在默认位置闪现一下
        if (Content is FrameworkElement fe)
        {
            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _designSize = fe.DesiredSize;
        }

        // —— 消除「打开瞬间闪一下」的关键一步 ——
        // 首帧在窗口 Show 后立刻就被 DWM 合成到屏幕上，而 ContentRendered 是在
        // 「首帧已经画完」之后才触发的。原实现把「透明化 + 定位」都放在 ContentRendered 里，
        // 于是第一帧必然是「默认位置 + 完全不透明」，看起来就是先闪一下再归位。
        // 因此这里在 Show 之前就把内容设为不可见、缩放与位移预置好。
        //
        // 顺序是 Scale → Translate：平移放在缩放之后，位移量就不会被缩放二次放大。
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_shift);
        OuterGrid.RenderTransform = group;
        OuterGrid.Opacity = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;

        SourceInitialized += (_, _) =>
        {
            // 分层窗口不显式把 DWM 背景设成透明时，首帧会先铺一层不透明白底
            // （这就是截图里那块白色矩形的来源）。
            var hwnd = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
                target.BackgroundColor = Colors.Transparent;

            // HWND 已建、窗口尚未显示：此刻定位才是真正意义上的「一次到位」。
            // 位图缓存也在这里挂上 —— 若等 ContentRendered 再挂，首帧会先无缓存渲染一次、
            // 挂上后又整树重渲染一次（还带一次 18px 模糊），等于开局白付一遍。
            // 缓存挂在内容层而非 OuterGrid，所以此刻 opacity=0 不会被固化进缓存。
            EnableRenderCache();
            UpdatePlacement();
            // 位置已经定了：立刻判一次「是否被压住」。此后动画开始，判定要被挂起到收尾，
            // 那一拍（展开 200ms + 定时器 120ms）正是用户说的"慢半拍"。
            // 现在首帧就是实心的，根本看不到"由透变实"的过程。
            UpdateOverlapOpacity(force: true);
        };

        if (_openMode == PanelOpenMode.Search)
        {
            // 搜索态面板只干一件事：找东西。放大镜与「更多」在这里是冗余的，
            // 底部那条"拖入收纳"提示也不是搜索场景的文案
            SearchButton.Visibility = Visibility.Collapsed;
            MoreButton.Visibility = Visibility.Collapsed;
            FooterHint.Visibility = Visibility.Collapsed;
            SearchBar.Visibility = Visibility.Visible;
            _searchOpen = true;
        }

        ContentRendered += (_, _) =>
        {
            // SizeToContent 下实际尺寸此时才定稿，再校一次位置（此时内容仍不可见）
            UpdatePlacement();

            // 重叠判定交给动画收尾后的定时器：它可能要重建整套主题笔刷，
            // 放在这里会让「点击 → 开始缩放」之间的等待变长，用户感受就是迟钝
            PlayOpenAnimation();

            // 键盘焦点必须等首帧画完再抢：提前 Focus 会被窗口激活流程盖掉
            if (_openMode == PanelOpenMode.Search)
            {
                SearchBox.Focus();
                QueuePlacement();
            }
        };

        UpdatePlacement();
        StartOverlapWatch();
    }

    /// <summary>
    /// 面板与别的卡片重叠时转成不透明。
    ///
    /// 用定时器而不是事件：盒子可以被拖到任意位置、别的面板也可能挪过来，
    /// 逐个订阅各窗口的 LocationChanged 覆盖面还不如每 200ms 量一次矩形来得可靠，
    /// 而且这里只是几次 Rect 求交，代价可以忽略。
    /// </summary>
    private void StartOverlapWatch()
    {
        // 120ms：拖动/挪动期间要跟手，量几次矩形求交的成本可以忽略
        _overlapTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _overlapTimer.Tick += (_, _) => UpdateOverlapOpacity();
        _overlapTimer.Start();
        // 定时器的第一次 tick 要等一整个 interval：面板刚出来那 120ms 又是"慢半拍"。
        // 先在本轮消息循环末尾补一次（首帧前 SourceInitialized 里还会 force 判一次）
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => UpdateOverlapOpacity()));
        Closed += (_, _) => _overlapTimer?.Stop();
    }

    private void UpdateOverlapOpacity(bool force = false)
    {
        // 展开动画在跑：此刻重建主题笔刷会抢走渲染帧，等动画收尾再恢复。
        // force 只给「首帧之前」用 —— 那时还没有动画在跑，不存在抢帧。
        if (_introAnimating && !force) return;
        _lastOverlapCheck = Environment.TickCount64;

        bool overlap = OverlapsOtherWindow();
        if (overlap == _opaqueForOverlap) return;
        _opaqueForOverlap = overlap;
        ApplyTheme();
    }

    private bool OverlapsOtherWindow()
    {
        // 首帧之前也要能判：SourceInitialized 里定位完就判一次，这样面板「一出生就是
        // 该有的实心程度」。等 ActualWidth 出来再判，就只能等动画收尾 + 定时器那一拍，
        // 用户看到的是先透明地铺开、过半秒才变实心 —— 就是"重叠时反应慢半拍"。
        // 此刻 ActualWidth 还是 0，用渲染前预量的设计尺寸顶上。
        const double margin = 18;
        double w = (ActualWidth > 0 ? ActualWidth : _designSize.Width) - margin * 2;
        double h = (ActualHeight > 0 ? ActualHeight : _designSize.Height) - margin * 2;
        if (w <= 0 || h <= 0) return false;
        // Left/Top 在定位之前是 NaN（"交给系统决定"），拿去构造 Rect 没有意义
        if (double.IsNaN(Left) || double.IsNaN(Top)) return false;
        var self = new Rect(Left + margin, Top + margin, w, h);

        // Application.Current 在某些宿主里可能不存在；拿不到就没有"别的窗口"可比
        var app = Application.Current;
        if (app == null) return false;

        double area = w * h;
        foreach (Window win in app.Windows)
        {
            if (ReferenceEquals(win, this) || !win.IsVisible) continue;
            if (win.WindowState == WindowState.Minimized) continue;

            // 正在缩回的旧面板不算"压住"：快速切换盒子时新旧面板必然短暂叠在一起，
            // 把它算进来会让新面板一出生就转成实心 —— 这就是"透明效果消失"的来源
            if (win is PanelWindow { IsClosing: true }) continue;

            var other = new Rect(win.Left, win.Top, win.ActualWidth, win.ActualHeight);
            var hit = Rect.Intersect(self, other);
            if (hit.IsEmpty) continue;
            // 门槛按面积算而不是按边长：圆角、投影贴边只擦到几像素；
            // 而"真的被盖住"至少不该低于整个面板的 15%，否则只是从边上路过
            if (hit.Width * hit.Height >= area * 0.15) return true;
        }
        return false;
    }

    private void ApplyTheme()
    {
        // 与盒子共用同一套 Theme.* 笔刷：白底自动切深色前景。
        // 分类色也走 Theme.Accent（每次 Update 换新笔刷对象），
        // 不再原地改 XAML 资源里的 brush —— 它可能已被 WPF 冻结。
        // 被压住时抬到全不透明：两层半透明叠在一起，谁的字都读不清
        _theme.Update(_box, _opaqueForOverlap ? 1.0 : 0);

        double r = _box.Rounded ? Math.Clamp(_box.CornerSize + 2, 10, 18) : 2;
        Root.CornerRadius = new CornerRadius(r);
        ShadowCaster.CornerRadius = new CornerRadius(r);

        // 圆角变了，箭头能贴的范围也跟着变；构造期还没布局，跳过
        if (IsLoaded) UpdatePlacement();
    }

    /// <summary>盒子设置改动后让已打开的面板跟随刷新（底色 / 圆角 / 前景对比色）。</summary>
    public void RefreshTheme() => ApplyTheme();

    // 失焦 / Esc / × / 点盒子切换可能在关闭过程中再次触发关闭；
    // WPF 在 Closing 期间再调 Close() 会抛 InvalidOperationException（会弹全局错误框）。
    private bool _closing;

    /// <summary>正在播收起动画（窗口还没销毁）。重叠判定要跳过这种窗口。</summary>
    internal bool IsClosing => _closing;

    /// <summary>是否因为被别的卡片压住而转成了不透明（供验证工具断言）。</summary>
    internal bool IsOpaqueForOverlap => _opaqueForOverlap;

    public void ClosePanel()
    {
        if (_closing) return;
        _closing = true;
        // 用户主动收起（Esc / × / 失焦 / 再点盒子）才播缩回动画；
        // Owner 销毁时走的是 Close()，那条路径不该为了动画拖慢退出
        _animateOnClose = true;

        // 以前是「先 Close() 再在 OnClosing 里 e.Cancel 掉」：那条路必须先跑完
        // WPF 整套关闭流程（去激活、重新激活 Owner、一串窗口消息），才轮得到我们播动画。
        // 展开时没有这段开销，于是同样的曲线，关闭就是比打开"迟半拍"。
        // 现在动画直接开跑，窗口等动画放完再真的关。
        if (_openAnimated && !_closeDone)
        {
            PlayCloseAnimation();
            return;
        }
        try { Close(); }
        catch (InvalidOperationException) { }
    }

    /// <summary>正在播收起动画（关闭请求已经受理，窗口还没销毁）。</summary>
    internal bool IsCloseAnimating => _closeAnimating;

    /// <summary>
    /// 兜底：仍有人直接 <c>Close()</c>（Owner 销毁等）时，先取消一次把动画播完。
    /// 正常路径已由 <see cref="ClosePanel"/> 自己起动画，走到这里时 <see cref="_closeAnimating"/>
    /// 已置位，<see cref="PlayCloseAnimation"/> 是幂等的。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_animateOnClose && !_closeDone && _openAnimated && !_closeAnimating)
        {
            e.Cancel = true;
            PlayCloseAnimation();
            return;
        }
        base.OnClosing(e);
    }

    // ---------- 打开动效 ----------

    private void PlayOpenAnimation()
    {
        if (_openAnimated) return;
        _openAnimated = true;

        // 初始的「透明 + 缩放 + 位移」已在构造函数里预置好了（首帧就必须是不可见的），
        // 这里只负责定锚点与播放。
        ApplyScaleOrigin();
        ComputeIntroOffset(out double dx, out double dy);
        _scale.ScaleX = _scale.ScaleY = OpenScale;
        _shift.X = dx;
        _shift.Y = dy;
        OuterGrid.Opacity = 0;

        // 200ms / CubicEase：前段立刻起步（跟手），末段自己收住。
        // 之前 280ms + Quadratic 偏"温和"，配合掉帧就显得又慢又顿。
        _introAnimating = true;
        _overlapTimer?.Stop();
        ShadowCaster.Opacity = 1;   // 收起时置空过，重新打开必须恢复
        EnableRenderCache();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(200);

        // 缩放和淡入同一时长、同时收尾：两条曲线错开会在结尾分出"第二段"，
        // 观感就是先动一下再补一下，不连贯。
        BeginScaled(dur, ease, OpenScale, 1, dx, 0, dy, 0);
        OuterGrid.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, dur) { EasingFunction = ease });

        // 动画走完立刻还回矢量渲染，静止时文字不能是缓存位图
        ScheduleIntroEnd(dur + TimeSpan.FromMilliseconds(60));
    }

    /// <summary>缩放 + 位移一起动：两条曲线的时长与缓动必须一致，否则会看出"两段"。</summary>
    private void BeginScaled(TimeSpan dur, IEasingFunction ease,
        double fromScale, double toScale,
        double fromX, double toX, double fromY, double toY)
    {
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(fromScale, toScale, dur) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(fromScale, toScale, dur) { EasingFunction = ease });
        _shift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromX, toX, dur) { EasingFunction = ease });
        _shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromY, toY, dur) { EasingFunction = ease });
    }

    /// <summary>
    /// 动画期间把整棵子树缓存成一张位图。
    ///
    /// 为什么要这么做：面板是 <c>AllowsTransparency</c> 的分层窗口，而 WPF 的分层窗口
    /// **强制软件渲染**（跟 <c>RenderCapability.Tier</c> 无关，本机实测 Tier=2 也一样）。
    /// 直接对视觉树做缩放，等于每帧把圆角、文字、图标、投影重新 CPU 栅格化一遍 ——
    /// 200ms 里掉几帧，看起来就是"顿"。缓存之后每帧只是对位图做变换，成本几乎为零。
    ///
    /// 建立缓存那一次仍旧要完整渲染（含 BlurRadius=18 的模糊），但首帧 opacity 还是 0，
    /// 用户看不见那一下，所以不需要再单独摘投影。
    ///
    /// 缓存里图标会不会失效？用 <c>PrintWindow</c>（走 DWM 真实合成）验证过：图标、文字都正常。
    /// 之前判成"图标变白块"是用 <c>RenderTargetBitmap</c> 抓的，它对带 <c>CacheMode</c> 的
    /// 元素本来就渲染不出内容 —— 那是抓图工具的局限，不是显示问题。
    /// </summary>
    private void EnableRenderCache()
    {
        if (Root.CacheMode != null) return;
        double dpi = 1;
        try { dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        var cache = new BitmapCache(Math.Max(1.0, dpi));   // RenderAtScale 取 DPI 比例，文字才不发虚

        // 关键：缓存挂在**内容层与投影层**，不能挂在 OuterGrid 上 ——
        // OuterGrid 身上跑着 opacity 动画，缓存位图会把当时（opacity=0）的合成结果固化下来，
        // 之后淡入就再也看不见内容了。分开之后透明度在外层照常生效。
        Root.CacheMode = cache;
        ShadowCaster.CacheMode = cache;   // 投影也缓存：BlurRadius=18 只算这一次，不是每帧
    }

    /// <summary>动画结束必须撤掉：静止态走矢量渲染，缓存位图上的小字会略微发糊。</summary>
    private void ReleaseRenderCache()
    {
        Root.CacheMode = null;
        ShadowCaster.CacheMode = null;
    }

    /// <summary>
    /// 按时间收尾（还投影 / 放行补图标 / 接回重叠检测）。
    /// 不用动画的 Completed：动画被下一次调用替换时它不会回来，漏掉就一直等着。
    /// </summary>
    private void ScheduleIntroEnd(TimeSpan after)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = after };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // 动画收尾。缓存**故意不撤**：拽着窗口移动、关闭时的收缩都能直接复用它，
            // 撤掉再建等于把那次带模糊的全量重绘又付一遍（用户感受就是"动画末尾卡一下"）。
            // RenderAtScale 取的是 DPI 比例，静止态位图与屏幕像素 1:1，清晰度没有可察觉的损失。
            _introAnimating = false;
            _introGate.TrySetResult(true);
            // 动画一收尾立刻校正一次：等下一个 tick 还要多付一个 interval
            UpdateOverlapOpacity();
            _overlapTimer?.Start();
        };
        timer.Start();
    }

    private void PlayCloseAnimation()
    {
        if (_closeAnimating) return;
        _closeAnimating = true;

        // 收起期间别让残留的透明区域接住点击
        OuterGrid.IsHitTestVisible = false;

        // 投影层比内容大一圈（BlurRadius 18 + 外扩），每帧都要整块重采样。
        // 缩到 0.9 时阴影本来就几乎不可辨，收起期间省掉这一层能明显稳住首帧。
        // 注意用 Opacity 而不是 Visibility：Collapsed 会让 Grid 重新测量，
        // SizeToContent 的窗口会在动画第一帧跳一下尺寸。
        ShadowCaster.Opacity = 0;
        _overlapTimer?.Stop();   // 收缩过程中不需要再判定重叠，否则会重建主题笔刷抢帧
        EnableRenderCache();

        // 收起必须"从第一帧起就一直在变"，而且每一帧的变化量要均匀。
        // 两个极端都不行：EaseIn 前 60% 几乎不褪 → "点了没反应，然后突然没了"；
        // CubicEaseOut 前 20% 就走完 80% → "闪一下就没了"。
        // 实测帧间隔就是 10-16ms，150ms 只有 9 帧左右，曲线一陡就只剩 2-3 个可见台阶。
        // 所以：淡出用**线性**（每帧均匀掉一格），缩放仍用 EaseOut（末段自然收住）。
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        // 150ms：展开 200ms 的 3/4，收的动作读起来更快；太短会显得"闪一下"
        var dur = TimeSpan.FromMilliseconds(150);

        // 只动缩放，不再起位移：打开结束时 _shift 已经停在 0，再补一条 0→0 的动画
        // 等于每帧多算两层变换矩阵，还让 WPF 认为有新的变换需要重组 —— 白付的开销。
        // （快速连点导致 _shift 还没归位时，它自己的动画会继续跑完，位移照样自然收住。）
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(_scale.ScaleX, CloseScale, dur) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(_scale.ScaleY, CloseScale, dur) { EasingFunction = ease });

        var fadeAnim = new DoubleAnimation(OuterGrid.Opacity, 0, dur);
        // 动画一结束就立刻收尾：不要只靠定时器。
        // DispatcherTimer 的精度与排队都不可控，动画明明停了窗口还挂在合成里，
        // 那几十毫秒正是"缩完了、桌面才反应过来"的那一下顿。
        // 收尾排到 Background：Completed 在动画时钟 tick 里触发，此刻最后一帧可能还没提交。
        // 同步 Hide 会让面板停在"还差一点透明度"的画面上硬切，观感就是最后一下顿住。
        // Background 排在该帧渲染之后，相当于"先把 0 的那一帧显示出来，再摘窗口"。
        fadeAnim.Completed += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FinalizeClose));
        OuterGrid.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

        // 兜底仍旧保留（Completed 在极端情况下可能不来），但优先级提到 Normal：
        // Background 是最后一级，主线程一忙它就一直往后排，窗口会以全透明状态蹭着 DWM
        _closeGuard = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _closeGuard.Tick += (_, _) => FinalizeClose();
        _closeGuard.Start();
    }

    /// <summary>
    /// 收起收尾时的实测数据，供 <c>--closeprof</c> 回归使用。
    /// 把"动画本身掉帧"和"Hide/Close 的同步开销"分开量化，才知道该优化哪一段。
    /// </summary>
    internal double ProfOpacityAtHide { get; private set; } = -1;
    internal double ProfHideMs { get; private set; } = -1;
    internal double ProfCloseMs { get; private set; } = -1;
    internal DateTime ProfFinalizeAt { get; private set; }
    /// <summary>当前画面状态（透明度 / 缩放），供逐帧采样。</summary>
    internal double ProfOpacity => OuterGrid.Opacity;
    internal double ProfScale => _scale.ScaleX;

    private void FinalizeClose()
    {
        if (_closeDone) return;
        _closeDone = true;
        _closeGuard?.Stop();
        _introGate.TrySetResult(true);

        ProfFinalizeAt = DateTime.UtcNow;
        ProfOpacityAtHide = OuterGrid.Opacity;

        // 先 Hide 再从合成里摘掉。Close() 不是免费的：销毁 HwndSource、释放图标位图、
        // 把激活交还 Owner，这些是几十毫秒的同步开销。紧接在缩放动画后面，用户看到的就是
        // "缩完 → 顿一下 → 才消失"。Hide 只是一次 ShowWindow(SW_HIDE)，立刻生效；
        // 真正的销毁推到下一轮 Normal 优先级的空闲里，那一下已经看不见任何变化了。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { Hide(); }
        catch (InvalidOperationException) { }
        ProfHideMs = sw.Elapsed.TotalMilliseconds;

        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            var closeSw = System.Diagnostics.Stopwatch.StartNew();
            try { Close(); }
            catch (InvalidOperationException) { }
            ProfCloseMs = closeSw.Elapsed.TotalMilliseconds;
        }));
    }

    /// <summary>
    /// 缩放锚点 = 盒子中心在面板里的相对位置。
    /// 面板默认开在盒子右侧，此时盒子中心落在面板左边界之外 → 横向夹到 0（从左边缘长出来），
    /// 纵向夹到盒子中心的高度 → 视觉上就是"从盒子那个位置向四周撑开"。
    /// </summary>
    private void ApplyScaleOrigin()
    {
        double w = ActualWidth > 0 ? ActualWidth : _designSize.Width;
        double h = ActualHeight > 0 ? ActualHeight : _designSize.Height;
        if (w <= 0 || h <= 0 || !_owner.IsLoaded)
        {
            OuterGrid.RenderTransformOrigin = new Point(0.5, 0.5);
            return;
        }

        // OuterGrid 外面还套了 18px 的投影留白，窗口坐标换成内容坐标要扣掉
        const double M = 18;
        double cx = _owner.Left + _owner.ActualWidth / 2 - Left - M;
        double cy = _owner.Top + _owner.ActualHeight / 2 - Top - M;
        double iw = Math.Max(1, w - M * 2);
        double ih = Math.Max(1, h - M * 2);

        OuterGrid.RenderTransformOrigin = new Point(
            Math.Clamp(cx / iw, 0, 1),
            Math.Clamp(cy / ih, 0, 1));
    }

    /// <summary>起始位移：沿「远离盒子」的方向预置几像素，展开时像是从盒子那侧推出来。</summary>
    private void ComputeIntroOffset(out double dx, out double dy)
    {
        dx = 0;
        dy = 0;
        if (!_owner.IsLoaded) { dx = -IntroShift; return; }

        const double M = 18;
        double w = ActualWidth > 0 ? ActualWidth : _designSize.Width;
        double h = ActualHeight > 0 ? ActualHeight : _designSize.Height;
        double boxL = _owner.Left, boxT = _owner.Top;
        double boxR = boxL + _owner.ActualWidth, boxB = boxT + _owner.ActualHeight;

        // 用**可见边**判定，不能直接比 Left/Top：它俩含 18px 投影留白，
        // 而面板可见边到盒子只有 15px（ArrowLen + ArrowGap），差这 3px 会判反。
        bool atRight = Left + M >= boxR - 1;
        bool atLeft = Left + w - M <= boxL + 1;
        bool atBelow = Top + M >= boxB - 1;
        bool atAbove = Top + h - M <= boxT + 1;

        if (atRight) dx = -IntroShift;
        else if (atLeft) dx = IntroShift;
        if (atBelow) dy = -IntroShift;
        else if (atAbove) dy = IntroShift;
        // 对角避让时两个轴都会赋值，位移自然变成斜向 —— 展开像从盒子那个角撑开
        // 与盒子重叠（极端窄屏）时给个默认方向，避免"原地放大"缺少方向感
        if (dx == 0 && dy == 0) dx = -IntroShift;
    }

    private void AnimateItems()
    {
        if (_items.Count == 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (Items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe)
                    continue;
                fe.Opacity = 0;
                fe.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(i * 40),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    });
            }
        }));
    }

    // ---------- 拖入高亮与收纳 ----------

    private void ApplyDropVisual(bool active)
    {
        _dropActive = active;
        if (active)
        {
            Root.BorderBrush = _theme.BrushOf(ThemeKeys.BorderDrop);
            FooterHint.Text = "松开鼠标即可收纳";
            FooterHint.Foreground = _theme.BrushOf(ThemeKeys.Accent);
        }
        else
        {
            Root.BorderBrush = _theme.BrushOf(ThemeKeys.Border);
            FooterHint.Text = "拖入收纳 · 拖出放回桌面";
            FooterHint.Foreground = _theme.BrushOf(ThemeKeys.TextMuted);
        }
    }

    private void OnPanelDragEnter(object sender, DragEventArgs e)
    {
        if (_internalDrag || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        ApplyDropVisual(true);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnPanelDragOver(object sender, DragEventArgs e)
    {
        if (_internalDrag || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        // DragLeave 穿过子元素时会反复触发，DragOver 幂等地把高亮补回来
        if (!_dropActive) ApplyDropVisual(true);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnPanelDragLeave(object sender, DragEventArgs e) => ApplyDropVisual(false);

    private void OnPanelDrop(object sender, DragEventArgs e)
    {
        ApplyDropVisual(false);
        if (_internalDrag) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var dir = _store.SourceDir(_box);
        if (ConfigStore.IsPortal(_box))
        {
            // 映射盒写的是别人的目录：不替它创建，路径不在就直说
            if (!Directory.Exists(dir))
            {
                ShowMsg($"映射的文件夹当前不可用：\n{dir}");
                e.Handled = true;
                return;
            }
        }
        else
        {
            Directory.CreateDirectory(dir);
        }

        // 自己搬自己：watcher 收到的事件不该反过来重建列表
        using (_owner.SuppressWatcher())
        {
            foreach (var src in paths)
            {
                var name = Path.GetFileName(src);
                if (string.IsNullOrEmpty(name)) continue;
                var target = Path.Combine(dir, name);
                if (FileMover.ExistsConflict(target)) continue;
                try { FileMover.MoveTo(src, target); }
                catch { }
            }
        }
        _store.Save(_config);
        RefreshItems();
        // 拖入后条数变了，这条必须无条件刷
        _owner.RefreshBadge();
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => ClosePanel();

    private Size _designSize;

    // 气泡箭头朝哪边：指向盒子。面板在上 → 朝下；在下 → 朝上；在左 → 朝右；在右 → 朝左
    private enum PointerSide { Down, Up, Left, Right }

    /// <summary>面板相对盒子的横向摆放：左侧 / 居中 / 右侧。</summary>
    private enum AxisX { Left, Center, Right }

    /// <summary>面板相对盒子的纵向摆放：上方 / 居中 / 下方。</summary>
    private enum AxisY { Above, Center, Below }

    private static AxisX Flip(AxisX v) =>
        v == AxisX.Right ? AxisX.Left : v == AxisX.Left ? AxisX.Right : AxisX.Center;

    private static AxisY Flip(AxisY v) =>
        v == AxisY.Below ? AxisY.Above : v == AxisY.Above ? AxisY.Below : AxisY.Center;

    /// <summary>「盒子靠近边缘就朝反方向让开」的判定阈值（占工作区的比例）。</summary>
    private const double EdgeZone = 0.34;

    /// <summary>
    /// 定位规则：气泡式避让。
    ///
    /// 横向三档 × 纵向三档，拼出三类位置：
    /// - **侧放**（一个轴居中）：水平方向居中、纵向居中、只剩一侧时贴在盒子左/右并垂直居中；
    /// - **上下放**（另一轴居中）：水平居中贴在盒子上/下；
    /// - **对角避让**（两个轴都不居中）：四个角正好落在这里 —— 盒子在左上，面板就去右下，
    ///   即面板的左上角贴住盒子的右下角，其余三角同理。
    ///
    /// 方位先按「盒子中心在工作区里的相对位置」定：靠左 → 面板放右，靠右 → 放左，
    /// 靠上 → 放下，靠下 → 放上；正中保持原来「优先上方」的习惯。
    /// 首选方向装不下时按候选序列退让（先丢一个轴的偏置变成侧放，再换到另一侧），
    /// 全都装不下才挑余量最大的一侧硬放 —— 那时宁可压住盒子一角，也不能让面板出屏。
    /// </summary>
    private void UpdatePlacement()
    {
        // SizeToContent 在 Loaded 时可能还未量出尺寸：用渲染前预量的尺寸兜底
        double w = ActualWidth > 0 ? ActualWidth : _designSize.Width;
        double h = ActualHeight > 0 ? ActualHeight : _designSize.Height;
        if (w <= 0 || h <= 0 || !_owner.IsLoaded) return;

        var hwnd = new WindowInteropHelper(_owner).Handle;
        var wa = ScreenUtil.WorkAreaPx(hwnd);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double waL = wa.X / dpi, waT = wa.Y / dpi;
        double waR = wa.Right / dpi, waB = wa.Bottom / dpi;

        const double M = 18;        // 窗口外圈投影留白
        const double ArrowLen = 12;
        const double ArrowGap = 3;  // 箭头尖到盒子的缝隙
        const double Seam = ArrowLen + ArrowGap;   // 可见边到盒子的距离

        double boxL = _owner.Left, boxT = _owner.Top;
        double boxR = boxL + _owner.ActualWidth, boxB = boxT + _owner.ActualHeight;
        double boxCx = (boxL + boxR) / 2;
        double boxCy = (boxT + boxB) / 2;

        // 可见面板（去掉投影留白）的尺寸
        double visW = Math.Max(1, w - M * 2);
        double visH = Math.Max(1, h - M * 2);
        double needX = visW + Seam;
        double needY = visH + Seam;

        double roomLeft = boxL - waL, roomRight = waR - boxR;
        double roomAbove = boxT - waT, roomBelow = waB - boxB;

        // 1) 盒子在工作区里的相对位置 → 直觉方位
        double relX = (boxCx - waL) / Math.Max(1, waR - waL);
        double relY = (boxCy - waT) / Math.Max(1, waB - waT);

        AxisX prefX = relX <= EdgeZone ? AxisX.Right
                    : relX >= 1 - EdgeZone ? AxisX.Left
                    : AxisX.Center;
        AxisY prefY = relY <= EdgeZone ? AxisY.Below
                    : relY >= 1 - EdgeZone ? AxisY.Above
                    : AxisY.Center;
        // 正中央：保持「优先上方」的老习惯（盒子贴任务栏是最常见的场景）
        if (prefX == AxisX.Center && prefY == AxisY.Center) prefY = AxisY.Above;

        bool FitX(AxisX v) => v switch
        {
            AxisX.Right => roomRight >= needX,
            AxisX.Left => roomLeft >= needX,
            _ => true,      // 居中不额外占空间（纵向已经避开盒子）
        };
        bool FitY(AxisY v) => v switch
        {
            AxisY.Below => roomBelow >= needY,
            AxisY.Above => roomAbove >= needY,
            _ => true,
        };

        // 2) 候选序列：首选 → 退成侧放/上下放 → 换到另一侧
        var candidates = new (AxisX X, AxisY Y)[]
        {
            (prefX, prefY),
            (prefX, AxisY.Center),
            (AxisX.Center, prefY),
            (Flip(prefX), prefY),
            (Flip(prefX), AxisY.Center),
            (AxisX.Center, Flip(prefY)),
            (AxisX.Center, AxisY.Above),
            (AxisX.Center, AxisY.Below),
        };

        AxisX px = AxisX.Center;
        AxisY py = AxisY.Above;
        bool placed = false;
        foreach (var (cx, cy) in candidates)
        {
            if (cx == AxisX.Center && cy == AxisY.Center) continue;  // 完全重叠，无意义
            if (!FitX(cx) || !FitY(cy)) continue;
            (px, py) = (cx, cy);
            placed = true;
            break;
        }
        if (!placed)
        {
            // 两边都比面板窄：挑余量最大的一侧硬放，宁可压住盒子一角也不出屏
            double hMax = Math.Max(roomLeft, roomRight);
            double vMax = Math.Max(roomAbove, roomBelow);
            if (hMax >= vMax)
            {
                px = roomRight >= roomLeft ? AxisX.Right : AxisX.Left;
                py = AxisY.Center;
            }
            else
            {
                px = AxisX.Center;
                py = roomAbove >= roomBelow ? AxisY.Above : AxisY.Below;
            }
        }

        // 3) 落位。除了方位，还有一条硬约束：**箭头必须真的指得到盒子** ——
        //    上下箭头要求面板横向覆盖盒子中心，左右箭头要求纵向覆盖。
        //    没有这条约束时，盒子在角落会被对角避让推到横向错开很远的位置，
        //    箭头只能夹在面板边角上，指向一片空白（"不知道这个箭头在指什么"）。
        //    做法：先按方位算理想位置，再夹进「能对准」的区间，最后夹进工作区。
        //    这一步就是在需要时**平移面板**去配合箭头，指向关系优先于避让的观感。
        double r = Root.CornerRadius.TopLeft;
        double pad = r + 11 + 4;   // 箭头半宽 11 + 离圆角的余量，保证箭头落在直边上

        double x = px switch
        {
            AxisX.Right => boxR + Seam - M,                 // 可见左边贴在盒子右边 15px 外
            AxisX.Left => boxL - Seam - visW - M,           // 可见右边贴在盒子左边 15px 外
            _ => boxCx - visW / 2 - M,                      // 水平居中于盒子
        };
        double y = py switch
        {
            AxisY.Below => boxB + Seam - M,
            AxisY.Above => boxT - Seam - visH - M,
            _ => boxCy - visH / 2 - M,                      // 垂直居中于盒子
        };

        // 约束只作用在**箭头实际挂的那条轴**上：
        // 上下箭头（py != Center）要横向覆盖盒子中心，此时纵向必须保持"在上方/下方"，
        // 绝不能再为了对准去动 y —— 那会把面板拖到和盒子重叠的位置上。
        if (py != AxisY.Center)
        {
            double lo = boxCx - M - visW + pad;
            double hi = boxCx - M - pad;
            x = lo <= hi ? Math.Clamp(x, lo, hi) : hi;
        }
        else if (px != AxisX.Center)
        {
            double lo = boxCy - M - visH + pad;
            double hi = boxCy - M - pad;
            y = lo <= hi ? Math.Clamp(y, lo, hi) : hi;
        }

        x = ClampSafe(x, waL - M, waR - w + M);
        y = ClampSafe(y, waT - M, waB - h + M);

        Left = x;
        Top = y;

        // 工作区实在放不下时锚点会被夹住：与其画一个指错方向的箭头，不如不画
        double anchorX = boxCx - (x + M);
        double anchorY = boxCy - (y + M);
        bool tight = py != AxisY.Center
            ? (anchorX < pad - 0.5 || anchorX > visW - pad + 0.5)
            : (px != AxisX.Center && (anchorY < pad - 0.5 || anchorY > visH - pad + 0.5));

        // 4) 箭头：挂在朝向盒子的那条边上。
        //   纵向有偏置时（上下放）挂上下边；只有纯侧放才挂左右边。
        PointerSide side = py != AxisY.Center
            ? (py == AxisY.Above ? PointerSide.Down : PointerSide.Up)
            : (px == AxisX.Right ? PointerSide.Left : PointerSide.Right);

        UpdatePointer(side, tight, x + M, y + M, visW, visH, boxCx, boxCy);
    }

    /// <summary>
    /// 把箭头摆到「盒子中心正对的那条边」上，并夹在圆角内侧 ——
    /// 盒子被拖到屏幕边缘、面板被迫横移时，箭头仍然指着盒子而不是面板正中。
    /// </summary>
    private void UpdatePointer(PointerSide side, bool hidden,
        double contentLeft, double contentTop, double visW, double visH,
        double boxCx, double boxCy)
    {
        if (hidden || visH < 60 || visW < 80)
        {
            PointerFill.Visibility = Visibility.Collapsed;
            PointerEdge.Visibility = Visibility.Collapsed;
            return;
        }

        const double half = 11;  // 箭头半宽
        const double len = 12;   // 箭头长
        double r = Root.CornerRadius.TopLeft;
        // 圆角 + 箭头半宽 + 余量：箭头必须整条落在直边上，压到弧线就像面板破了口。
        // 这个值和 UpdatePlacement 里的 pad 必须一致，否则"能对准"的判定会和实际画出来的位置对不上
        double pad = r + half + 4;

        if (side is PointerSide.Down or PointerSide.Up)
        {
            double ax = Math.Clamp(boxCx - contentLeft, pad, Math.Max(pad, visW - pad));
            if (side == PointerSide.Down)
            {
                PointerFill.Data = Geometry.Parse("M 0,0 L 22,0 L 11,12 Z");
                PointerEdge.Data = Geometry.Parse("M 0,0 L 11,12 M 22,0 L 11,12");
                // 抬高 1px 让填充盖住面板自己的 1px 描边，接缝处才不会出现一条横线
                Canvas.SetLeft(PointerFill, ax - half);
                Canvas.SetTop(PointerFill, visH - 1);
                Canvas.SetLeft(PointerEdge, ax - half);
                Canvas.SetTop(PointerEdge, visH - 1);
            }
            else
            {
                PointerFill.Data = Geometry.Parse("M 0,12 L 22,12 L 11,0 Z");
                PointerEdge.Data = Geometry.Parse("M 0,12 L 11,0 M 22,12 L 11,0");
                Canvas.SetLeft(PointerFill, ax - half);
                Canvas.SetTop(PointerFill, -len + 1);
                Canvas.SetLeft(PointerEdge, ax - half);
                Canvas.SetTop(PointerEdge, -len + 1);
            }
        }
        else
        {
            // 侧放：箭头挂在左/右边，纵向对准盒子中心（盒子是横长条时也能指准）
            double ay = Math.Clamp(boxCy - contentTop, pad, Math.Max(pad, visH - pad));
            if (side == PointerSide.Right)
            {
                // 面板在盒子左侧，箭头从右边伸出去朝右
                PointerFill.Data = Geometry.Parse("M 0,0 L 12,11 L 0,22 Z");
                PointerEdge.Data = Geometry.Parse("M 0,0 L 12,11 M 0,22 L 12,11");
                Canvas.SetLeft(PointerFill, visW - 1);
                Canvas.SetTop(PointerFill, ay - half);
                Canvas.SetLeft(PointerEdge, visW - 1);
                Canvas.SetTop(PointerEdge, ay - half);
            }
            else
            {
                // 面板在盒子右侧，箭头从左边伸出去朝左
                PointerFill.Data = Geometry.Parse("M 12,0 L 0,11 L 12,22 Z");
                PointerEdge.Data = Geometry.Parse("M 12,0 L 0,11 M 12,22 L 0,11");
                Canvas.SetLeft(PointerFill, -len + 1);
                Canvas.SetTop(PointerFill, ay - half);
                Canvas.SetLeft(PointerEdge, -len + 1);
                Canvas.SetTop(PointerEdge, ay - half);
            }
        }

        PointerFill.Visibility = Visibility.Visible;
        PointerEdge.Visibility = Visibility.Visible;
    }

    // 面板比工作区还宽/高时 min>max 会让 Math.Clamp 抛异常，这里退化为贴左/贴上
    private static double ClampSafe(double v, double min, double max) =>
        min > max ? min : Math.Clamp(v, min, max);

    public void FollowOwner()
    {
        UpdatePlacement();

        // 盒子被拖动时面板跟着走，随时可能撞上别的卡片。定时器那 120ms 在这条路径上
        // 明显跟不上（拖动是 16ms 粒度的），这里按 80ms 节流复判：
        // 状态没变就只做几次 Rect 求交，变了才重建笔刷
        long now = Environment.TickCount64;
        if (now - _lastOverlapCheck < 80) return;
        UpdateOverlapOpacity();
    }

    public void RefreshItems(bool animate = true)
    {
        var dir = _store.SourceDir(_box);
        var names = Directory.Exists(dir)
            ? Directory.EnumerateFileSystemEntries(dir)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var orderedNames = _box.Order
            .Where(names.Contains)
            .Concat(names.Where(n => !_box.Order.Contains(n)))
            .ToList();

        // .lnk 去掉扩展名显示（会被挤成两行），重名时自动保留全名
        var display = DisplayNames.Build(orderedNames);

        _allItems = orderedNames
            .Select(n =>
            {
                var path = Path.Combine(dir, n);
                return new PanelItem
                {
                    FilePath = path,
                    DisplayName = display[n],
                    Icon = IconService.GetCached(path),
                };
            })
            .ToList();

        // 宽度基线只在「重新扫描目录」时确定。搜索过滤时刻意不动它，
        // 否则每敲一个字面板就变宽变窄一次，位置也跟着重算，视觉上很晃。
        // 宽度必须是「列数 × 单元格宽(96)」的整数倍：206/300/400 这类整数
        // 会多出十几像素全堆在右侧，最后一行看起来就是左对齐、右边空一块。
        BandHost.Width = ColumnsFor(_allItems.Count) * 96;

        BindVisible(animate);
    }

    /// <summary>按数量选一个不出现「3+1」这种残行的列数。</summary>
    private static int ColumnsFor(int count) => count switch
    {
        <= 1 => 1,
        2 => 2,
        3 => 3,
        4 => 2,      // 2×2 比 3+1 稳
        <= 6 => 3,
        _ => 4,
    };

    /// <summary>
    /// 按当前关键字把 <see cref="_allItems"/> 过滤成 <see cref="_items"/> 并绑定。
    /// 只动「显示集合」，不碰文件、不碰 <c>Box.Order</c>：
    /// 框选、拖拽、右键、打开都基于 <see cref="_items"/>，所以筛选后自动只作用于可见项。
    /// </summary>
    private void BindVisible(bool animate)
    {
        var key = _filterText.Trim();

        _items = key.Length == 0
            ? _allItems.ToList()
            : _allItems
                .Where(i => i.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase))
                .ToList();

        Items.ItemsSource = _items;

        // 搜索时显示「命中/总数」，一眼能看出被筛掉了多少
        PanelCount.Text = key.Length == 0
            ? _allItems.Count.ToString()
            : $"{_items.Count}/{_allItems.Count}";

        bool empty = _items.Count == 0;
        EmptyCard.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ItemScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Text = key.Length == 0
            ? "把桌面图标拖进来收纳"
            : $"没有匹配「{key}」的图标";

        // 虚线拖入位只在「整盒恰好 1 项」时补位；搜索结果不补，否则会挤乱网格
        bool showSlot = _items.Count == 1 && key.Length == 0;
        DropSlot.Visibility = showSlot ? Visibility.Visible : Visibility.Collapsed;
        if (showSlot) DropSlot.Margin = new Thickness(96, 4, 0, 0);

        // 只有「没在筛选」时才刷盒子：搜索时总条数没变，
        // 每敲一个字都去重扫目录、重建缩略图没有意义（大目录会明显卡）
        if (key.Length == 0) _owner.RefreshBadge();

        // 图标提取放到后台，面板先出来，图标逐个补齐
        _ = LoadIconsAsync(_items);
        if (animate) AnimateItems();
    }

    private async Task LoadIconsAsync(List<PanelItem> items)
    {
        // 已缓存过的图标在 RefreshItems 里同步填好了；这里补的是没命中的那些。
        // 等展开动画走完再补 —— 动画期间每补一个都要重绘一次，正是掉帧的来源。
        try { await _introGate.Task; } catch { }

        // 先全部取完再统一赋值：每个 item.Icon 赋值都会让位图缓存失效，
        // 逐个"取一个赋一个"等于重建 N 次（每次都要重算投影模糊），大目录会明显卡。
        var loaded = new List<(PanelItem Item, ImageSource Icon)>();
        foreach (var item in items)
        {
            try
            {
                var icon = await IconService.GetAsync(item.FilePath);
                if (icon != null) loaded.Add((item, icon));
            }
            catch { }
        }
        if (loaded.Count == 0) return;

        Root.CacheMode = null;
        ShadowCaster.CacheMode = null;
        foreach (var (item, icon) in loaded) item.Icon = icon;
        EnableRenderCache();
    }

    // ---------- 搜索 ----------

    /// <summary>盒子标题栏上的放大镜：面板已开时就地展开搜索栏。</summary>
    public void ShowSearchBar()
    {
        if (_searchOpen)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }
        OpenSearch();
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (_searchOpen) CloseSearch();
        else OpenSearch();
    }

    private void OpenSearch()
    {
        if (_searchOpen) return;
        _searchOpen = true;
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Text = _filterText;   // 再次打开时保留上次的关键字
        SyncSearchPlaceholder();
        SearchBox.Focus();
        SearchBox.SelectAll();
        QueuePlacement();
    }

    private void CloseSearch()
    {
        if (!_searchOpen) return;
        _searchOpen = false;
        _filterText = "";
        SearchBox.Text = "";
        SearchBar.Visibility = Visibility.Collapsed;
        SyncSearchPlaceholder();
        BindVisible(animate: false);
        QueuePlacement();
        // 焦点必须离开输入框，否则 Ctrl+A / Esc 会被 TextBox 吃掉
        Keyboard.ClearFocus();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _filterText = SearchBox.Text;
        SyncSearchPlaceholder();
        // 关搜索时也会触发一次 TextChanged，那次由 CloseSearch 统一处理
        if (_searchOpen) BindVisible(animate: false);
    }

    private void OnSearchClearClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void SyncSearchPlaceholder() =>
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    // 收起/展开搜索栏会改变窗口高度（SizeToContent），要等布局定稿再重新贴靠盒子，
    // 否则面板会先按旧高度定位、再跳一下
    private void QueuePlacement() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePlacement));

    // ---------- 全局键盘 / 失焦 ----------

    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 分层：先退搜索 → 再关菜单 → 再清选中 → 最后收面板
            if (_searchOpen)
                CloseSearch();
            else if (_openMenu?.IsOpen == true)
                _openMenu.IsOpen = false;
            else if (_items.Any(i => i.IsSelected))
                ClearSelection();
            else
                ClosePanel();
            e.Handled = true;
            return;
        }

        // 焦点在搜索框里时，其余快捷键（含 Ctrl+A 全选文字）都交给输入框
        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OpenSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            foreach (var i in _items) i.IsSelected = true;
            e.Handled = true;
        }
    }

    private void OnPanelDeactivated(object? sender, EventArgs e)
    {
        if (_draggingItem || _sorting || _banding || _dropActive || _suppressDeactivate || _openMenu?.IsOpen == true)
            return;

        // 光标还在面板/盒子上 → 可能是点击盒子切换面板，交给盒子处理
        var p = ScreenUtil.CursorPosition();
        if (IsCursorOver(this, p) || IsCursorOver(_owner, p))
            return;

        ClosePanel();
    }

    private static bool IsCursorOver(Window w, PixelPoint screenPt)
    {
        if (!w.IsLoaded || w.Visibility != Visibility.Visible) return false;
        try
        {
            var tl = w.PointToScreen(new Point(0, 0));
            var br = w.PointToScreen(new Point(w.ActualWidth, w.ActualHeight));
            return screenPt.X >= tl.X && screenPt.X <= br.X &&
                   screenPt.Y >= tl.Y && screenPt.Y <= br.Y;
        }
        catch
        {
            return false;
        }
    }

    private void ShowMsg(string text, MessageBoxImage icon = MessageBoxImage.Warning)
    {
        _suppressDeactivate = true;
        try
        {
            MessageBox.Show(this, text, "Tonia-DeskBox", MessageBoxButton.OK, icon);
        }
        finally
        {
            _suppressDeactivate = false;
        }
    }

    // ---------- 面板"⋯"菜单 ----------

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AppearanceTheme.AttachMenu(menu, _box, _config.Settings.MenuOpacity, this);

        // 映射盒的"解散"语义不成立（文件不在本程序手里），干脆不提供这一项
        if (!ConfigStore.IsPortal(_box))
        {
            var miDissolve = new MenuItem { Header = "解散收纳盒（文件放回桌面）" };
            miDissolve.Click += (_, _) => DissolveBox();
            menu.Items.Add(miDissolve);

            menu.Items.Add(new Separator());
        }

        var miSortName = new MenuItem
        {
            Header = "按名称排序",
            IsCheckable = true,
            IsChecked = _sortMode == SortMode.Name,
        };
        miSortName.Click += (_, _) => ApplySort(SortMode.Name);
        menu.Items.Add(miSortName);

        var miSortAdded = new MenuItem
        {
            Header = "按放入时间排序",
            IsCheckable = true,
            IsChecked = _sortMode == SortMode.Added,
        };
        miSortAdded.Click += (_, _) => ApplySort(SortMode.Added);
        menu.Items.Add(miSortAdded);

        _openMenu = menu;
        menu.Closed += (_, _) => _openMenu = null;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void DissolveBox()
    {
        // 映射盒没有"把文件放回桌面"这一步：文件本来就不在本程序手里，
        // 强行搬到桌面等于替用户搬走别人的文件夹内容
        if (ConfigStore.IsPortal(_box))
        {
            MessageBox.Show(this,
                $"这个盒子映射的是已有文件夹，不会把里面的文件搬到桌面。\n\n" +
                $"如需移除，请在盒子右键菜单里选「移除这个映射盒子」。\n\n{_store.SourceDir(_box)}",
                "映射盒子", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dir = _store.BoxDir(_box);
        if (!Directory.Exists(dir)) return;

        var files = Directory.EnumerateFileSystemEntries(dir).ToList();
        if (files.Count == 0)
        {
            ShowMsg("盒子已经是空的。", MessageBoxImage.Information);
            return;
        }

        _suppressDeactivate = true;
        var result = MessageBox.Show(this,
            $"将把 {files.Count} 个文件全部放回桌面，盒子保留并清空。\n\n确定解散？",
            "解散收纳盒", MessageBoxButton.YesNo, MessageBoxImage.Question);
        _suppressDeactivate = false;
        if (result != MessageBoxResult.Yes) return;

        List<string> skipped;
        using (_owner.SuppressWatcher())
        {
            FileMover.MoveBackToDesktop(files, out skipped);
        }
        _box.Order.Clear();
        _store.Save(_config);
        RefreshItems();

        if (skipped.Count > 0)
            ShowMsg($"以下文件因桌面同名/占用未能放回，已保留在盒子目录：\n{string.Join("\n", skipped)}");
    }

    /// <summary>
    /// 重排并写回 <c>Box.Order</c>。
    /// - <see cref="SortMode.Name"/>：按显示名（已去掉 .lnk）的当前区域文化顺序；
    /// - <see cref="SortMode.Added"/>：按放入时间，**新放入的在前**。
    ///   <c>Box.Order</c> 本身就是放入顺序（拖入时追加到末尾），所以越靠后越新；
    ///   不在 Order 里的（直接拷进盒子目录的文件）一律视为最新。
    /// </summary>
    private void ApplySort(SortMode mode)
    {
        var dir = _store.SourceDir(_box);
        if (!Directory.Exists(dir)) return;

        var names = Directory.EnumerateFileSystemEntries(dir)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .ToList();

        List<string> ordered;
        if (mode == SortMode.Name)
        {
            var display = DisplayNames.Build(names);
            ordered = names.OrderBy(n => display[n], StringComparer.CurrentCulture).ToList();
        }
        else
        {
            var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _box.Order.Count; i++)
                pos[_box.Order[i]] = i;

            ordered = names
                .OrderByDescending(n => pos.TryGetValue(n, out var i) ? i : int.MaxValue)
                .ThenBy(n => n, StringComparer.CurrentCulture)
                .ToList();
        }

        _sortMode = mode;
        _box.Order = ordered;
        _store.Save(_config);
        RefreshItems(animate: false);
    }

    // ---------- 选中 / 框选 ----------

    private void ClearSelection()
    {
        foreach (var i in _items) i.IsSelected = false;
    }

    private List<PanelItem> SelectedItems() => _items.Where(i => i.IsSelected).ToList();

    private Rect[] ContainerRectsRelativeTo(UIElement anchor)
    {
        var rects = new Rect[_items.Count];
        for (int i = 0; i < _items.Count; i++)
        {
            rects[i] = Rect.Empty;
            if (Items.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe &&
                fe.IsLoaded)
            {
                var p = fe.TranslatePoint(new Point(0, 0), anchor);
                rects[i] = new Rect(p, fe.RenderSize);
            }
        }
        return rects;
    }

    private void OnBandMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_sorting || _draggingItem) return;
        _bandBase = _items.Where(i => i.IsSelected).ToHashSet();
        _bandStart = e.GetPosition(BandHost);
        _banding = true;
        _bandMoved = false;
        BandHost.CaptureMouse();
    }

    private void OnBandMouseMove(object sender, MouseEventArgs e)
    {
        if (!_banding || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(BandHost);
        if (!_bandMoved)
        {
            if (Dist(pos, _bandStart) <= BandThreshold) return;
            _bandMoved = true;
        }

        var rect = RectFromPoints(_bandStart, pos);
        Band.Margin = new Thickness(rect.Left, rect.Top, 0, 0);
        Band.Width = rect.Width;
        Band.Height = rect.Height;
        Band.Visibility = Visibility.Visible;

        bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var rects = ContainerRectsRelativeTo(BandHost);
        for (int i = 0; i < _items.Count; i++)
        {
            bool hit = !rects[i].IsEmpty && rects[i].IntersectsWith(rect);
            _items[i].IsSelected = additive ? (_bandBase.Contains(_items[i]) || hit) : hit;
        }
    }

    private void OnBandMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_banding) return;
        _banding = false;
        BandHost.ReleaseMouseCapture();
        Band.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (_bandMoved) return;
        // 空白处单击 = 取消全选
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ClearSelection();
    }

    // ---------- 图标：单击 / 拖动排序 / 拖出 ----------

    private PanelItem? ItemFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as PanelItem;

    private static double Dist(Point a, Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static Rect RectFromPoints(Point a, Point b)
    {
        return new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private void OnItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _dragStart = e.GetPosition(this);
        _draggingItem = false;
        _actionTaken = false;
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggingItem || _banding) return;

        var cell = (FrameworkElement)sender;
        var pos = e.GetPosition(this);

        if (_sorting)
        {
            UpdateSort(pos);
            return;
        }

        if (Dist(pos, _dragStart) <= DragThreshold) return;

        // 越出面板窗口 → 切换为拖出到桌面/资源管理器
        if (pos.X < -DragOutMargin || pos.X > ActualWidth + DragOutMargin ||
            pos.Y < -DragOutMargin || pos.Y > ActualHeight + DragOutMargin)
        {
            cell.ReleaseMouseCapture();
            StartDragOut(sender);
            return;
        }

        if (_items.Count > 1)
            StartSort(cell);
        else
            cell.CaptureMouse();
    }

    private void StartSort(FrameworkElement cell)
    {
        if (ItemFrom(cell) is not { } item) return;
        // 搜索过滤期间只显示命中子集，拖拽重排无法安全写回全量顺序，直接不进入排序
        if (_filterText.Trim().Length > 0) return;
        _sorting = true;
        _sortIndex = _items.IndexOf(item);
        _visualIndex = _sortIndex;
        _sortCell = cell;
        _origRects = ContainerRectsRelativeTo(this);
        cell.CaptureMouse();
        cell.LostMouseCapture += OnSortLostCapture;
    }

    private void OnSortLostCapture(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe) fe.LostMouseCapture -= OnSortLostCapture;
        if (!_sorting) return;
        _sorting = false;
        ClearSortTransforms();
    }

    private void UpdateSort(Point pos)
    {
        if (_sortCell == null || _sortIndex < 0 || _sortIndex >= _origRects.Length) return;

        // 拖出面板 → 取消排序并转为拖出
        if (pos.X < -DragOutMargin || pos.X > ActualWidth + DragOutMargin ||
            pos.Y < -DragOutMargin || pos.Y > ActualHeight + DragOutMargin)
        {
            CancelSort();
            if (_sortCell != null)
            {
                var cell = _sortCell;
                _sortCell = null;
                cell.ReleaseMouseCapture();
                StartDragOut(cell);
            }
            return;
        }

        double dx = pos.X - _dragStart.X;
        double dy = pos.Y - _dragStart.Y;
        SetTransform(_sortCell, dx, dy);

        // 被拖格中心落在哪一格 → 目标位
        var src = _origRects[_sortIndex];
        var center = new Point(
            src.Left + src.Width / 2 + dx,
            src.Top + src.Height / 2 + dy);

        int target = _visualIndex;
        for (int i = 0; i < _origRects.Length; i++)
        {
            if (!_origRects[i].IsEmpty && _origRects[i].Contains(center))
            {
                target = i;
                break;
            }
        }
        if (target == _visualIndex) return;

        // 让位：之间的格子整体平移一格（按原始布局矩形差值，天然支持换行）
        if (target > _visualIndex)
        {
            for (int j = _visualIndex + 1; j <= target; j++)
                ShiftContainer(j, _origRects[j - 1].TopLeft - _origRects[j].TopLeft);
        }
        else
        {
            for (int j = target; j < _visualIndex; j++)
                ShiftContainer(j, _origRects[j + 1].TopLeft - _origRects[j].TopLeft);
        }
        SetTransform(_sortCell, dx, dy);
        _visualIndex = target;
    }

    private void ShiftContainer(int index, Vector offset)
    {
        if (Items.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement fe)
            SetTransform(fe, offset.X, offset.Y);
    }

    private static void SetTransform(FrameworkElement fe, double dx, double dy)
    {
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
        {
            fe.RenderTransform = null;
            return;
        }
        fe.RenderTransform = new TranslateTransform(dx, dy);
    }

    private void ClearSortTransforms()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (Items.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
                fe.RenderTransform = null;
        }
    }

    private void CancelSort()
    {
        _sorting = false;
        ClearSortTransforms();
    }

    private void CommitSort()
    {
        // 筛选状态下 _items 只是子集，用它重建全量顺序会丢掉未匹配的项
        if (_filterText.Trim().Length > 0) return;
        if (_visualIndex != _sortIndex && _sortIndex >= 0 && _sortIndex < _items.Count)
        {
            var item = _items[_sortIndex];
            _items.RemoveAt(_sortIndex);
            _items.Insert(Math.Clamp(_visualIndex, 0, _items.Count), item);

            // Order 存的是真实文件名；DisplayName 可能是去掉 .lnk 的显示名，不能写进去
            _box.Order = _items.Select(i => Path.GetFileName(i.FilePath)).ToList();
            _store.Save(_config);
        }
    }

    private void OnItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (_sorting)
        {
            CommitSort();
            _actionTaken = true;
            CancelSort();
            _sortCell?.ReleaseMouseCapture();
            _sortCell = null;
            RefreshItems(animate: false);
            return;
        }

        if (_draggingItem || _actionTaken) return;

        if (ItemFrom(sender) is not { } item) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            item.IsSelected = !item.IsSelected;
            _actionTaken = true;
            return;
        }

        ClearSelection();
        ShortcutService.Open(item.FilePath);
    }

    private void StartDragOut(object sender)
    {
        if (ItemFrom(sender) is not { } item) return;
        _draggingItem = true;
        _actionTaken = true;
        _internalDrag = true;

        // 拖动的是选中集内一项 → 整批拖出
        var sel = SelectedItems();
        var paths = (sel.Count > 0 && sel.Contains(item))
            ? sel.Select(i => i.FilePath).ToList()
            : new List<string> { item.FilePath };

        var data = new DataObject();
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        data.SetFileDropList(list);
        using var ms = new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Move));
        data.SetData("Preferred DropEffect", ms);

        _suppressDeactivate = true;
        using (_owner.SuppressWatcher())
        {
            try
            {
                DragDrop.DoDragDrop((DependencyObject)sender, data,
                    DragDropEffects.Move | DragDropEffects.Copy);
            }
            finally
            {
                _suppressDeactivate = false;
                _draggingItem = false;
                _internalDrag = false;
            }
        }

        RefreshItems();
    }

    // ---------- 右键菜单 ----------

    private void OnItemRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;

        // 点到未选中的图标 → 只选它；点到已选中 → 保持整批
        if (!item.IsSelected)
        {
            ClearSelection();
            item.IsSelected = true;
        }
        var sel = SelectedItems();
        bool multi = sel.Count > 1;

        var menu = new ContextMenu();
        AppearanceTheme.AttachMenu(menu, _box, _config.Settings.MenuOpacity, this);

        var miOpen = new MenuItem { Header = multi ? $"打开（{sel.Count}）" : "打开" };
        miOpen.Click += (_, _) =>
        {
            foreach (var i in sel) ShortcutService.Open(i.FilePath);
        };
        menu.Items.Add(miOpen);

        if (!multi)
        {
            var miLoc = new MenuItem { Header = "打开所在位置" };
            miLoc.Click += (_, _) => ShortcutService.OpenContainingFolder(item.FilePath);
            menu.Items.Add(miLoc);
        }

        if (!multi)
        {
            var miProp = new MenuItem { Header = "属性" };
            miProp.Click += (_, _) => ShortcutService.ShowProperties(item.FilePath);
            menu.Items.Add(miProp);
        }

        menu.Items.Add(new Separator());

        var miBack = new MenuItem { Header = multi ? $"放回桌面（{sel.Count}）" : "放回桌面" };
        miBack.Click += (_, _) => MoveBackToDesktop(sel);
        menu.Items.Add(miBack);

        _openMenu = menu;
        menu.Closed += (_, _) => _openMenu = null;
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void MoveBackToDesktop(List<PanelItem> items)
    {
        if (items.Count == 0) return;
        List<string> skipped;
        using (_owner.SuppressWatcher())
        {
            FileMover.MoveBackToDesktop(items.Select(i => i.FilePath), out skipped);
        }

        foreach (var i in items)
            _box.Order.Remove(Path.GetFileName(i.FilePath));
        _store.Save(_config);
        RefreshItems();

        if (skipped.Count > 0)
            ShowMsg($"桌面已存在同名文件或文件被占用，未能放回：\n{string.Join("\n", skipped)}");
    }
}
