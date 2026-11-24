using System.IO;
using System.Text;
using ToniaDeskBox.Models;
using ToniaDeskBox.Services;

namespace ToniaDeskBox.Tests;

/// <summary>
/// 零依赖测试入口。
///
/// 为什么不用 xunit：主工程的卖点之一是「零第三方 NuGet 依赖」，
/// 而测试只需要断言 + 临时目录，为一个 Console.WriteLine 引入一整套测试基础设施不划算。
/// 因此这里自带一个极简 runner：`dotnet run --project tests/ToniaDeskBox.Tests`，
/// 退出码 0 = 全通过，1 = 有失败（可直接接进 CI）。
///
/// 覆盖范围刻意收窄到「直接操作用户真实文件、出错代价最大」的三块：
/// 1. ConfigStore    —— 配置损坏后的隔离与恢复；
/// 2. StorageMigration —— 迁移目标的拒绝路径；
/// 3. ImportService  —— 导入的回滚顺序（失败时数据必须还在原处）。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Tonia-DeskBox 单测（零依赖 runner）");
        Console.WriteLine(new string('=', 60));

        ConfigStoreTests.Run();
        StorageMigrationTests.Run();
        ImportServiceTests.Run();
        ExtraTests.Run();
        DisplayModeTests.Run();
        TidyRestoreTests.Run();
        BoxPlacementTests.Run();

        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"通过 {_passed} 个，失败 {_failed} 个");
        return _failed == 0 ? 0 : 1;
    }

    // ---------- 极简断言 ----------

    public static void Case(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {ex.Message}");
        }
    }

    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}（期望 {expected}，实际 {actual}）");
    }

    /// <summary>临时目录：用例结束即删，不往仓库或用户目录留垃圾。</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir(string tag = "tdb")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"{tag}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Combine(params string[] parts) =>
            System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

        public string MkDir(string relative)
        {
            var dir = Combine(relative);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string MkFile(string relative, string content = "x")
        {
            var file = Combine(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
            return file;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* 用例已失败时清不掉可以接受 */ }
        }
    }
}

internal static class ConfigStoreTests
{
    public static void Run()
    {
        Console.WriteLine("\n[ConfigStore] 配置损坏恢复");

        Program.Case("主文件损坏 → 隔离为 config.corrupt-* 并从 bak 恢复", () =>
        {
            using var tmp = new Program.TempDir("cfg-restore");
            var store = new ConfigStore(tmp.MkDir("cfg"), tmp.MkDir("data"));

            var cfg = new AppConfig();
            cfg.Boxes.Clear();
            cfg.Boxes.Add(new BoxConfig { Name = "工作" });
            // 存两次：第二次会把第一次的主文件复制成 bak
            store.Save(cfg);
            store.Save(cfg);

            var cfgDir = store.ConfigDir;
            var main = Path.Combine(cfgDir, "config.json");
            var bak = Path.Combine(cfgDir, "config.json.bak");
            Program.Check(File.Exists(main), "主文件应存在");
            Program.Check(File.Exists(bak), "备份文件应存在");

            File.WriteAllText(main, "{ 这不是 json");

            var loaded = store.Load();

            Program.Equal(1, loaded.Boxes.Count, "应从 bak 恢复出 1 个盒子");
            Program.Equal("工作", loaded.Boxes[0].Name, "恢复的盒子名应一致");
            Program.Check(
                Directory.EnumerateFiles(cfgDir, "config.corrupt-*.json").Any(),
                "损坏的主文件应被隔离成 config.corrupt-<时间戳>.json");
            Program.Check(IsValidJson(main), "恢复后重建的主文件应是合法配置");
        });

        Program.Case("主文件与 bak 都损坏 → 重建为 1 个空盒，不抛异常", () =>
        {
            using var tmp = new Program.TempDir("cfg-both-bad");
            var store = new ConfigStore(tmp.MkDir("cfg"), tmp.MkDir("data"));

            File.WriteAllText(Path.Combine(store.ConfigDir, "config.json"), "nope");
            File.WriteAllText(Path.Combine(store.ConfigDir, "config.json.bak"), "also nope");

            var loaded = store.Load();

            Program.Equal(1, loaded.Boxes.Count, "应退回 1 个空盒的默认配置");
            Program.Equal("新建盒子", loaded.Boxes[0].Name, "默认盒子名");
            Program.Check(IsValidJson(Path.Combine(store.ConfigDir, "config.json")), "应重建主文件");
        });

        Program.Case("保存时绝不把损坏内容写进 bak", () =>
        {
            using var tmp = new Program.TempDir("cfg-bak-guard");
            var store = new ConfigStore(tmp.MkDir("cfg"), tmp.MkDir("data"));
            var cfg = new AppConfig();
            cfg.Boxes.Clear();
            cfg.Boxes.Add(new BoxConfig { Name = "旧" });
            store.Save(cfg);
            store.Save(cfg);

            var main = Path.Combine(store.ConfigDir, "config.json");
            var bak = Path.Combine(store.ConfigDir, "config.json.bak");
            var bakBefore = File.ReadAllText(bak);

            File.WriteAllText(main, "{{{ 坏掉的主文件");
            var next = new AppConfig();
            next.Boxes.Clear();
            next.Boxes.Add(new BoxConfig { Name = "新" });
            store.Save(next);

            Program.Equal(bakBefore, File.ReadAllText(bak), "bak 必须保持为上一份合法配置");
            Program.Check(IsValidJson(main), "新主文件应合法");
        });

        Program.Case("配置里的非法盒子名被规范化（不含路径分隔符 / 保留名）", () =>
        {
            using var tmp = new Program.TempDir("cfg-name");
            var store = new ConfigStore(tmp.MkDir("cfg"), tmp.MkDir("data"));
            var cfg = new AppConfig();
            cfg.Boxes.Clear();
            cfg.Boxes.Add(new BoxConfig { Id = "aaaa1111", Name = ".." });
            cfg.Boxes.Add(new BoxConfig { Id = "bbbb2222", Name = "CON" });
            cfg.Boxes.Add(new BoxConfig { Id = "cccc3333", Name = "重名" });
            cfg.Boxes.Add(new BoxConfig { Id = "dddd4444", Name = "重名" });
            store.Save(cfg);

            var loaded = store.Load();
            var names = loaded.Boxes.Select(b => b.Name).ToList();

            Program.Check(names.All(ConfigStore.IsSafeSegment), "所有盒子名都应可安全用作目录名");
            Program.Equal(4, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "重名盒子必须被消歧，不能两个盒子共用一个目录");
        });

        Program.Case("映射盒缺失目标路径时退回普通盒，且普通盒不保留 PortalPath", () =>
        {
            using var tmp = new Program.TempDir("cfg-portal");
            var dir = tmp.MkDir("cfg");
            var store = new ConfigStore(dir, tmp.MkDir("data"));

            File.WriteAllText(Path.Combine(dir, "config.json"), """
                {
                  "Settings": { "TopmostMigrated": true },
                  "Boxes": [
                    { "Id": "aaaa1111", "Name": "无目标", "Kind": "Portal" },
                    { "Id": "bbbb2222", "Name": "有目标", "Kind": "Portal", "PortalPath": "C:\\\\Windows" },
                    { "Id": "cccc3333", "Name": "普通", "PortalPath": "C:\\\\Windows" }
                  ]
                }
                """);

            var cfg = store.Load();

            Program.Equal(BoxKind.Files, cfg.Boxes[0].Kind, "缺 PortalPath 的映射盒应退回 Files");
            Program.Check(cfg.Boxes[0].PortalPath == null, "退回后不应保留 PortalPath");
            Program.Equal(BoxKind.Portal, cfg.Boxes[1].Kind, "带绝对路径的映射盒保持 Portal");
            Program.Check(cfg.Boxes[2].PortalPath == null, "普通盒应清掉残留 PortalPath");
        });
    }

    private static bool IsValidJson(string path)
    {
        try
        {
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class StorageMigrationTests
{
    public static void Run()
    {
        Console.WriteLine("\n[StorageMigration] 迁移目标的拒绝路径");

        Program.Case("与当前位置相同 → 拒绝", () =>
        {
            using var tmp = new Program.TempDir("mig-same");
            var current = tmp.MkDir("boxes");
            var cfg = tmp.MkDir("cfg");
            Program.Check(
                !StorageMigration.ValidateTarget(current, current, cfg, out var err),
                "同路径必须被拒绝");
            Program.Check(err.Contains("相同"), $"错误应说明原因，实际：{err}");
        });

        Program.Case("盘符根目录 → 拒绝", () =>
        {
            using var tmp = new Program.TempDir("mig-root");
            var current = tmp.MkDir("boxes");
            var cfg = tmp.MkDir("cfg");
            var driveRoot = Path.GetPathRoot(tmp.Path)!;
            Program.Check(
                !StorageMigration.ValidateTarget(driveRoot, current, cfg, out var err),
                "盘符根必须被拒绝");
            Program.Check(err.Contains("根目录"), $"错误应说明原因，实际：{err}");
        });

        Program.Case("当前根的子目录 → 拒绝（避免自我嵌套）", () =>
        {
            using var tmp = new Program.TempDir("mig-child");
            var current = tmp.MkDir("boxes");
            var cfg = tmp.MkDir("cfg");
            var child = Path.Combine(current, "inner");
            Program.Check(
                !StorageMigration.ValidateTarget(child, current, cfg, out var err),
                "子目录必须被拒绝");
            Program.Check(err.Contains("子目录"), $"错误应说明原因，实际：{err}");
        });

        Program.Case("当前根的上级目录 → 拒绝", () =>
        {
            using var tmp = new Program.TempDir("mig-parent");
            var parent = tmp.MkDir("work");
            var current = Path.Combine(parent, "boxes");
            Directory.CreateDirectory(current);
            var cfg = tmp.MkDir("cfg");
            Program.Check(
                !StorageMigration.ValidateTarget(parent, current, cfg, out var err),
                "上级目录必须被拒绝");
            Program.Check(err.Contains("上级目录"), $"错误应说明原因，实际：{err}");
        });

        Program.Case("配置目录本身 → 拒绝", () =>
        {
            using var tmp = new Program.TempDir("mig-cfg");
            var current = tmp.MkDir("boxes");
            var cfg = tmp.MkDir("cfg");
            Program.Check(
                !StorageMigration.ValidateTarget(cfg, current, cfg, out var err),
                "配置目录必须被拒绝");
            Program.Check(err.Contains("配置"), $"错误应说明原因，实际：{err}");
        });

        Program.Case("合法的兄弟目录 → 通过，并建立可写探测", () =>
        {
            using var tmp = new Program.TempDir("mig-ok");
            var current = tmp.MkDir("boxes");
            var cfg = tmp.MkDir("cfg");
            var target = Path.Combine(tmp.Path, "boxes-new");
            Program.Check(
                StorageMigration.ValidateTarget(target, current, cfg, out var err),
                $"合法目标应通过，实际：{err}");
            Program.Check(Directory.Exists(target), "校验时应创建目标目录");
            Program.Check(
                !File.Exists(Path.Combine(target, ".tonia-write-test")),
                "写探测文件必须被清理");
        });

        Program.Case("扫描时排除根目录的 config/日志，盒子内部同名文件照常计入", () =>
        {
            using var tmp = new Program.TempDir("mig-scan");
            var root = tmp.MkDir("boxes");
            File.WriteAllText(Path.Combine(root, "config.json"), "{}");
            File.WriteAllText(Path.Combine(root, "error.log"), "x");
            Directory.CreateDirectory(Path.Combine(root, "盒子"));
            File.WriteAllText(Path.Combine(root, "盒子", "config.json"), "{}");
            File.WriteAllText(Path.Combine(root, "盒子", "a.txt"), "x");

            var plan = StorageMigration.Scan(root);
            Program.Equal(2, plan.FileCount, "只应统计盒子内部的两个文件");
        });
    }
}

internal static class ImportServiceTests
{
    public static void Run()
    {
        Console.WriteLine("\n[ImportService] 导入的回滚顺序");

        Program.Case("覆盖导入：旧数据整体改名留底，新数据就位，配置另存副本", () =>
        {
            using var tmp = new Program.TempDir("imp-apply");
            var root = tmp.MkDir("boxes");
            File.WriteAllText(Path.Combine(root, "old.txt"), "old");
            var cfgDir = tmp.MkDir("cfg");
            File.WriteAllText(Path.Combine(cfgDir, "config.json"), "{}");

            // 解压目录必须与数据根同父目录，rename 才原子
            var staging = Path.Combine(tmp.Path, "boxes.importing-1");
            Directory.CreateDirectory(Path.Combine(staging, "工作"));
            File.WriteAllText(Path.Combine(staging, "工作", "new.txt"), "new");

            var result = ImportService.Apply(staging, root, cfgDir, 1, "20260101-000000");

            Program.Check(result.ReplacedDataPath != null, "原有数据应被改名留底");
            Program.Check(Directory.Exists(result.ReplacedDataPath!), "留底目录必须真实存在");
            Program.Check(File.Exists(Path.Combine(result.ReplacedDataPath!, "old.txt")),
                "旧文件应完好地留在留底目录里");
            Program.Check(File.Exists(Path.Combine(root, "工作", "new.txt")), "新数据应就位");
            Program.Check(!Directory.Exists(staging), "解压目录应已被整体改名为数据根");
            Program.Check(File.Exists(Path.Combine(cfgDir, "config.before-import-20260101-000000.json")),
                "配置应另存 before-import 副本");
        });

        Program.Case("解压目录不存在 → 抛错且不动现有数据", () =>
        {
            using var tmp = new Program.TempDir("imp-missing");
            var root = tmp.MkDir("boxes");
            File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");
            var cfgDir = tmp.MkDir("cfg");

            var threw = false;
            try
            {
                ImportService.Apply(Path.Combine(tmp.Path, "nope"), root, cfgDir, 0, "s");
            }
            catch (DirectoryNotFoundException)
            {
                threw = true;
            }

            Program.Check(threw, "应抛 DirectoryNotFoundException");
            Program.Check(File.Exists(Path.Combine(root, "keep.txt")), "现有数据必须毫发无伤");
        });

        Program.Case("解压目录与数据根不同父目录 → 拒绝（rename 不具备原子性）", () =>
        {
            using var tmp = new Program.TempDir("imp-cross");
            var root = tmp.MkDir("boxes");
            var cfgDir = tmp.MkDir("cfg");
            var elsewhere = tmp.MkDir("other");
            var staging = Path.Combine(elsewhere, "staging");
            Directory.CreateDirectory(staging);

            var threw = false;
            try
            {
                ImportService.Apply(staging, root, cfgDir, 0, "s");
            }
            catch (IOException)
            {
                threw = true;
            }

            Program.Check(threw, "跨目录（等效跨卷）必须拒绝");
            Program.Check(Directory.Exists(staging), "拒绝时不应动解压目录");
        });

        Program.Case("合并回滚：中途失败时已搬入的目录按相反顺序搬回", () =>
        {
            using var tmp = new Program.TempDir("imp-rollback");
            var root = tmp.MkDir("boxes");
            var staging = tmp.MkDir("staging");
            Directory.CreateDirectory(Path.Combine(staging, "A"));
            File.WriteAllText(Path.Combine(staging, "A", "a.txt"), "a");
            Directory.CreateDirectory(Path.Combine(staging, "B"));

            // 目标里预先占住 B，让第二个盒子搬入时失败
            Directory.CreateDirectory(Path.Combine(root, "B"));

            var plan = new List<MergeItem>
            {
                new() { SourceDir = "A", FinalName = "A", Box = new BoxConfig { Name = "A" } },
                new() { SourceDir = "B", FinalName = "B", Box = new BoxConfig { Name = "B" } },
            };

            var threw = false;
            try
            {
                ImportService.MergeStaging(staging, root, plan);
            }
            catch (IOException)
            {
                threw = true;
            }

            Program.Check(threw, "目标已存在时应抛错并停止合并");
            Program.Check(!Directory.Exists(Path.Combine(root, "A")),
                "已搬入的 A 必须被回滚，不能残留在数据根");
            Program.Check(File.Exists(Path.Combine(staging, "A", "a.txt")),
                "回滚后 A 应回到解压目录，文件不丢");
        });

        Program.Case("合并计划：重名盒子自动改名，且新 Id 不与来源 Id 相同", () =>
        {
            var sourceId = "源盒子Id";
            var manifest = new BackupManifest
            {
                Config = new AppConfig
                {
                    Boxes =
                    {
                        new BoxConfig { Id = sourceId, Name = "工作" },
                        new BoxConfig { Id = "x2", Name = "工作" },
                    },
                },
            };

            var plan = ImportService.BuildMergePlan(manifest, new[] { "工作" });

            Program.Equal(2, plan.Count, "两个来源盒子都应出现在计划里");
            Program.Equal("工作 (2)", plan[0].FinalName, "与现有盒子重名应加序号");
            Program.Equal("工作 (3)", plan[1].FinalName, "来源之间重名也要继续避让");
            Program.Check(plan.All(p => p.Box.Id != sourceId), "新盒子不能沿用备份里的 Id");
            Program.Check(plan.All(p => ConfigStore.IsSafeSegment(p.FinalName)),
                "落地名必须能当目录名");
        });
    }
}

internal static class ExtraTests
{
    public static void Run()
    {
        Console.WriteLine("\n[附加] 一键整理分组 / 热键解析");

        Program.Case("按扩展名归类，目录进「文件夹」，未知扩展名进「其他」", () =>
        {
            Program.Equal(TidyCategory.Shortcut, DesktopTidyService.Categorize("微信.lnk", false), ".lnk");
            Program.Equal(TidyCategory.Shortcut, DesktopTidyService.Categorize("a.URL", false), ".url 不区分大小写");
            Program.Equal(TidyCategory.Document, DesktopTidyService.Categorize("报价单.XLSX", false), ".xlsx");
            Program.Equal(TidyCategory.Image, DesktopTidyService.Categorize("shot.png", false), ".png");
            Program.Equal(TidyCategory.Video, DesktopTidyService.Categorize("demo.mp4", false), ".mp4");
            Program.Equal(TidyCategory.Audio, DesktopTidyService.Categorize("a.flac", false), ".flac");
            Program.Equal(TidyCategory.Archive, DesktopTidyService.Categorize("bak.7z", false), ".7z");
            Program.Equal(TidyCategory.Folder, DesktopTidyService.Categorize("项目", true), "目录");
            Program.Equal(TidyCategory.Other, DesktopTidyService.Categorize("noext", false), "无扩展名");
            Program.Equal(TidyCategory.Other, DesktopTidyService.Categorize("x.xyz", false), "未知扩展名");
        });

        Program.Case("扫描跳过 desktop.ini 与排除目录（数据根在桌面上也不会自吃）", () =>
        {
            using var tmp = new Program.TempDir("tidy-scan");
            var desktop = tmp.MkDir("desktop");
            File.WriteAllText(Path.Combine(desktop, "desktop.ini"), "x");
            File.WriteAllText(Path.Combine(desktop, "a.txt"), "hello");
            var dataRoot = Path.Combine(desktop, "boxes");
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot, "inside.txt"), "x");

            var groups = DesktopTidyService.Scan(desktop, new[] { dataRoot });
            var names = groups.SelectMany(g => g.Items).Select(i => i.Name).ToList();

            Program.Check(!names.Contains("desktop.ini"), "desktop.ini 不应出现");
            Program.Check(!names.Contains("boxes"), "数据根目录不应被整理进盒子");
            Program.Check(names.Contains("a.txt"), "普通文件应被扫到");
            Program.Equal(1, groups.Single().Items.Count, "只应剩下一个条目");
        });

        Program.Case("整理时不覆盖盒内同名条目（跳过但保留桌面文件）", () =>
        {
            using var tmp = new Program.TempDir("tidy-move");
            var src = tmp.MkFile("desktop/a.txt", "desktop");
            var target = tmp.MkDir("box");
            File.WriteAllText(Path.Combine(target, "a.txt"), "box");

            var moves = new List<TidyMove> { new(src, "a.txt", target, "文档") };
            var result = DesktopTidyService.MoveAll(moves);

            Program.Equal(0, result.Moved, "不该搬走");
            Program.Equal(1, result.Skipped, "应记为跳过");
            Program.Check(File.Exists(src), "桌面上的原文件必须还在");
            Program.Equal("box", File.ReadAllText(Path.Combine(target, "a.txt")), "盒内文件不能被覆盖");
        });

        Program.Case("热键串解析 / 格式化往返一致", () =>
        {
            Program.Check(HotkeyService.TryParse("Ctrl+Alt+D", out var mods, out var vk), "应能解析 Ctrl+Alt+D");
            Program.Equal("Ctrl+Alt+D", HotkeyService.Format(mods, vk), "格式化结果应与输入一致");

            Program.Check(HotkeyService.TryParse("control+shift+F5", out mods, out vk), "别名 control 应被接受");
            Program.Equal("Ctrl+Shift+F5", HotkeyService.Format(mods, vk), "修饰键按固定顺序输出");

            Program.Check(!HotkeyService.TryParse("D", out _, out _), "裸键不能注册为全局热键");
            Program.Check(!HotkeyService.TryParse("Ctrl+Alt", out _, out _), "只有修饰键不能注册");
            Program.Check(!HotkeyService.TryParse("Ctrl+NotAKey", out _, out _), "未知键名应失败");
        });

        Program.Case("盒子名避让：与现有名字冲突时加序号", () =>
        {
            var name = DesktopTidyService.UniqueBoxName("文档", new[] { "文档" });
            Program.Equal("文档 (2)", name, "重名应加 (2)");
            Program.Check(ConfigStore.IsSafeSegment(name), "结果必须是合法目录名");
        });
    }
}

/// <summary>展示方式：网格宽度吸附 + 老配置的按宽度迁移。</summary>
internal static class DisplayModeTests
{
    public static void Run()
    {
        Console.WriteLine("\n[展示方式] 宽度吸附 / 配置迁移");

        Program.Case("宽度吸附到整列：结果必须是 列数×80 + 26", () =>
        {
            // 26 = 左右各 1px 描边 + 12px 内边距
            Program.Equal(266.0, BoxMetrics.ContentMinWidth, "三列基准宽");
            Program.Equal(346.0, BoxMetrics.Snap(340, out var c1), "340 吸附到 4 列");
            Program.Equal(4, c1, "340 → 4 列");
            Program.Equal(346.0, BoxMetrics.Snap(350, out _), "350 同样落在 4 列");
            Program.Equal(426.0, BoxMetrics.Snap(400, out var c2), "400 吸附到 5 列");
            Program.Equal(5, c2, "400 → 5 列");
            Program.Equal(266.0, BoxMetrics.Snap(10, out _), "过窄时兜底到三列");
            Program.Equal(586.0, BoxMetrics.Snap(9999, out _), "过宽时封顶到七列");
        });

        Program.Case("老配置没有 DisplayMode → 按宽度迁移一次，且只迁一次", () =>
        {
            using var tmp = new Program.TempDir("cfg-mode");
            var store = new ConfigStore(tmp.MkDir("cfg"), tmp.MkDir("data"));

            // 模拟旧版本配置：没有 DisplayMode / DisplayModeMigrated 字段
            File.WriteAllText(Path.Combine(store.ConfigDir, "config.json"), """
            {
              "Settings": { "TopmostMigrated": true },
              "Boxes": [
                { "Name": "宽的", "W": 346 },
                { "Name": "窄的", "W": 0 }
              ]
            }
            """);

            var loaded = store.Load();
            Program.Equal(BoxDisplayMode.Content, loaded.Boxes[0].DisplayMode, "宽盒 → 显示内容");
            Program.Equal(BoxDisplayMode.Preview, loaded.Boxes[1].DisplayMode, "窄盒 → 缩略图");
            Program.Check(loaded.Settings.DisplayModeMigrated, "迁移标记应已置位");

            // 用户手动切成「仅标题」后，重新加载不能被迁移覆盖回去
            loaded.Boxes[0].DisplayMode = BoxDisplayMode.TitleOnly;
            loaded.Boxes[0].W = 346;
            store.Save(loaded);
            var again = store.Load();
            Program.Equal(BoxDisplayMode.TitleOnly, again.Boxes[0].DisplayMode, "再次加载应保留用户选择");
        });
    }
}

/// <summary>
/// 一键整理 / 一键恢复：快照、备份边界、恢复计划。
///
/// 这三块都直接操作用户的真实文件，出错代价最高，所以必须有用例兜住：
/// 尤其「只备份本次参与整理的条目」—— 用户没勾的文件被动到了，信任就没了。
/// </summary>
internal static class TidyRestoreTests
{
    public static void Run()
    {
        Console.WriteLine("\n[一键整理 / 恢复] 快照、备份边界与恢复计划");

        Program.Case("没有整理记录 → 返回 null 且不算错误（用于区分「从没整理过」）", () =>
        {
            using var tmp = new Program.TempDir("tidy-none");
            var cfg = tmp.MkDir("cfg");

            var loaded = TidyStateStore.TryLoad(cfg, out var error);
            Program.Check(loaded == null, "没有记录时应返回 null");
            Program.Equal("", error, "没有记录不是错误，不该给用户报错文案");
        });

        Program.Case("快照往返：中文名、图标坐标、备份信息都不丢", () =>
        {
            using var tmp = new Program.TempDir("tidy-state");
            var cfg = tmp.MkDir("cfg");

            var state = new TidyState
            {
                CreatedAt = new DateTime(2026, 11, 24, 10, 30, 0),
                LayoutCaptured = true,
                AutoArrange = false,
                Icons = new List<DesktopIconPos>
                {
                    new() { Name = "会议纪要.txt", X = 26, Y = 173 },
                    new() { Name = "Qoder CN IDE.lnk", X = 2610, Y = 344 },
                },
                Items = new List<TidyRestoreItem>
                {
                    new()
                    {
                        Name = "会议纪要.txt",
                        OriginalPath = @"C:\Users\a\Desktop\会议纪要.txt",
                        BoxPath = @"D:\box\文档\会议纪要.txt",
                        BoxName = "文档",
                    },
                },
                BackupDir = @"C:\cfg\tidy-backups\20261124-103000",
                BackupFiles = 1,
                BackupBytes = 1234,
            };

            TidyStateStore.Save(cfg, state);
            var loaded = TidyStateStore.TryLoad(cfg, out var error);

            Program.Check(loaded != null, "应能读回快照：" + error);
            Program.Equal(2, loaded!.Icons.Count, "图标数");
            Program.Equal("会议纪要.txt", loaded.Icons[0].Name, "中文名不应被转义或截断");
            Program.Equal(173, loaded.Icons[0].Y, "图标坐标");
            Program.Equal(1, loaded.Items.Count, "条目数");
            Program.Equal("文档", loaded.Items[0].BoxName, "目标盒子");
            Program.Equal(1, loaded.BackupFiles, "备份文件数");
            Program.Check(!loaded.AutoArrange, "自动排列状态");
            Program.Check(loaded.LayoutCaptured, "布局已记录标记");
        });

        Program.Case("快照损坏 → 返回 null 并给出可读原因", () =>
        {
            using var tmp = new Program.TempDir("tidy-bad");
            var cfg = tmp.MkDir("cfg");
            File.WriteAllText(Path.Combine(cfg, TidyStateStore.FileName), "{ 这显然不是 json");

            var loaded = TidyStateStore.TryLoad(cfg, out var error);
            Program.Check(loaded == null, "损坏的快照不能被当成有效记录");
            Program.Check(error.Length > 0, "必须说明为什么读不出来");
        });

        Program.Case("备份只复制传进来的条目，未参与整理的文件不动", () =>
        {
            using var tmp = new Program.TempDir("tidy-backup");
            var desktop = tmp.MkDir("desktop");
            var cfg = tmp.MkDir("cfg");
            var tidy = Path.Combine(desktop, "要整理的.txt");
            var keep = Path.Combine(desktop, "不动的.txt");
            File.WriteAllText(tidy, "要搬走的");
            File.WriteAllText(keep, "不能被碰");

            var result = TidyBackupService.Create(new[] { tidy }, cfg, "s1");

            Program.Equal(1, result.Files, "只备份 1 个文件");
            Program.Check(File.Exists(Path.Combine(result.Dir, "要整理的.txt")), "勾选的条目应有副本");
            Program.Check(!File.Exists(Path.Combine(result.Dir, "不动的.txt")), "没勾的文件不得被备份");
            Program.Check(File.Exists(keep), "没勾的文件仍在桌面原位");
            Program.Equal("不能被碰", File.ReadAllText(keep), "没勾的文件内容不得被改");
            Program.Equal("要搬走的", File.ReadAllText(tidy), "备份是复制，源文件不动");
        });

        Program.Case("备份目录里的子文件也一并复制（文件夹条目）", () =>
        {
            using var tmp = new Program.TempDir("tidy-backup-dir");
            var desktop = tmp.MkDir("desktop");
            var cfg = tmp.MkDir("cfg");
            var folder = tmp.MkDir(@"desktop\资料夹");
            File.WriteAllText(Path.Combine(folder, "a.txt"), "A");
            Directory.CreateDirectory(Path.Combine(folder, "子目录"));
            File.WriteAllText(Path.Combine(folder, "子目录", "b.txt"), "B");

            var result = TidyBackupService.Create(new[] { folder }, cfg, "s1");

            Program.Equal(2, result.Files, "递归计入 2 个文件");
            Program.Check(File.Exists(Path.Combine(result.Dir, "资料夹", "a.txt")), "顶层文件");
            Program.Check(File.Exists(Path.Combine(result.Dir, "资料夹", "子目录", "b.txt")), "子目录文件");
        });

        Program.Case("恢复计划：盒子 / 备份 / 找不到 三态判定", () =>
        {
            using var tmp = new Program.TempDir("tidy-plan");
            var desktop = tmp.MkDir("desktop");
            var box = tmp.MkDir("box");
            var cfg = tmp.MkDir("cfg");
            var backupDir = Path.Combine(cfg, "tidy-backups", "s1");
            Directory.CreateDirectory(backupDir);

            File.WriteAllText(Path.Combine(box, "a.txt"), "a");
            File.WriteAllText(Path.Combine(backupDir, "b.txt"), "b");

            var state = new TidyState
            {
                BackupDir = backupDir,
                LayoutCaptured = true,
                Items = new List<TidyRestoreItem>
                {
                    Item(desktop, box, "a.txt"),
                    Item(desktop, box, "b.txt"),
                    Item(desktop, box, "c.txt"),
                },
            };

            var plan = TidyRestoreService.BuildPlan(state, desktop);

            Program.Equal(2, plan.RestorableCount, "可恢复 2 项");
            Program.Equal(1, plan.FromBackupCount, "其中 1 项只能靠备份");
            Program.Equal(1, plan.MissingCount, "1 项哪里都找不到");
            Program.Equal(TidyRestoreOrigin.Box, plan.Items[0].Origin, "a 从盒子搬回");
            Program.Equal(TidyRestoreOrigin.Backup, plan.Items[1].Origin, "b 从备份复制");
            Program.Equal(TidyRestoreOrigin.Missing, plan.Items[2].Origin, "c 不可恢复");
            Program.Check(plan.Items[2].Problem.Contains("c.txt"), "缺失项要指明是哪个文件");
        });

        Program.Case("恢复执行：搬回桌面、同名跳过、缺失记失败并给出原因", () =>
        {
            using var tmp = new Program.TempDir("tidy-restore");
            var desktop = tmp.MkDir("desktop");
            var box = tmp.MkDir("box");
            var cfg = tmp.MkDir("cfg");
            var backupDir = Path.Combine(cfg, "tidy-backups", "s1");
            Directory.CreateDirectory(backupDir);

            File.WriteAllText(Path.Combine(box, "a.txt"), "a");
            File.WriteAllText(Path.Combine(backupDir, "b.txt"), "b");
            // 桌面上已经有 a.txt：恢复不得覆盖
            File.WriteAllText(Path.Combine(desktop, "a.txt"), "桌面上的");

            var state = new TidyState
            {
                BackupDir = backupDir,
                LayoutCaptured = true,
                Items = new List<TidyRestoreItem>
                {
                    Item(desktop, box, "a.txt"),
                    Item(desktop, box, "b.txt"),
                    Item(desktop, box, "c.txt"),
                },
            };

            var result = TidyRestoreService.Execute(
                TidyRestoreService.BuildPlan(state, desktop));

            Program.Equal(1, result.Restored, "b 成功放回");
            Program.Equal(1, result.Skipped, "a 因桌面同名跳过");
            Program.Equal(1, result.Failed, "c 无法恢复");
            Program.Equal("桌面上的", File.ReadAllText(Path.Combine(desktop, "a.txt")),
                "同名文件绝不能被覆盖");
            Program.Equal("b", File.ReadAllText(Path.Combine(desktop, "b.txt")),
                "从备份复制的内容应完整");
            Program.Check(File.Exists(Path.Combine(box, "a.txt")), "跳过的条目应留在盒子里");
        });

        Program.Case("没有布局记录时仍能恢复文件（不能因为布局失败就整条链路中断）", () =>
        {
            using var tmp = new Program.TempDir("tidy-nolayout");
            var desktop = tmp.MkDir("desktop");
            var box = tmp.MkDir("box");
            var cfg = tmp.MkDir("cfg");

            File.WriteAllText(Path.Combine(box, "a.txt"), "a");

            var state = new TidyState
            {
                LayoutCaptured = false,
                LayoutError = "找不到桌面图标列表",
                Items = new List<TidyRestoreItem> { Item(desktop, box, "a.txt") },
            };

            var plan = TidyRestoreService.BuildPlan(state, desktop);
            var result = TidyRestoreService.Execute(plan);

            Program.Equal(1, result.Restored, "布局失败不影响文件恢复");
            Program.Check(File.Exists(Path.Combine(desktop, "a.txt")), "文件已回桌面");
        });

        static TidyRestoreItem Item(string desktop, string box, string name) => new()
        {
            Name = name,
            OriginalPath = Path.Combine(desktop, name),
            BoxPath = Path.Combine(box, name),
            BoxName = "文档",
        };
    }
}

/// <summary>
/// 整理时的「新盒子落位」与恢复后的「空盒子清理」。
///
/// 这两块都是纯逻辑（不碰窗口、不碰真实桌面），所以能直接断言：
/// 新盒子绝不与已有元素重叠、已有盒子坐标一个都不动、
/// 只有「本次恢复涉及 + 目录确实空了」的盒子才会被删。
/// </summary>
internal static class BoxPlacementTests
{
    public static void Run()
    {
        Console.WriteLine("\n[整理落位 / 空盒子清理]");

        var work = new BoxArea(0, 0, 1280, 800);

        Program.Case("空桌面：新盒子落在工作区左上并留出边距", () =>
        {
            var (x, y) = BoxPlacementService.FindFreeSpot(work, 200, 100);
            Program.Equal(BoxPlacementService.EdgeMargin, x, "X 贴左边距");
            Program.Equal(BoxPlacementService.EdgeMargin, y, "Y 贴上边距");
        });

        Program.Case("已有盒子占住左上：新盒子另找空地且不压住它", () =>
        {
            var existing = new BoxArea(16, 16, 210, 96);
            var (x, y) = BoxPlacementService.FindFreeSpot(work, 210, 96, new[] { existing });
            var placed = new BoxArea(x, y, 210, 96);

            Program.Check(!placed.Overlaps(existing, BoxPlacementService.Gap), "不得与已有盒子重叠");
            Program.Check(x >= work.X + BoxPlacementService.EdgeMargin, "不越左界");
            Program.Check(placed.Right <= work.Right - BoxPlacementService.EdgeMargin, "不越右界");
            Program.Check(placed.Bottom <= work.Bottom - BoxPlacementService.EdgeMargin, "不越下界");
        });

        Program.Case("第一行放满后自动换到第二行", () =>
        {
            var existing = new[]
            {
                new BoxArea(16, 16, 600, 96),
                new BoxArea(628, 16, 600, 96),
            };
            var (_, y) = BoxPlacementService.FindFreeSpot(work, 210, 96, existing);
            Program.Check(y >= 124, $"应落到第一行下方（实际 y={y:0}）");
        });

        Program.Case("一次整理建 4 个盒子：彼此不重叠，也不碰已有盒子", () =>
        {
            var existing = new BoxArea(400, 300, 300, 200);
            var placed = BoxPlacementService.Arrange(work, new[] { existing },
                new (double, double)[] { (210, 96), (210, 96), (210, 96), (210, 96) });

            Program.Equal(4, placed.Count, "排了 4 个");
            for (int i = 0; i < placed.Count; i++)
            {
                Program.Check(!placed[i].Overlaps(existing, BoxPlacementService.Gap), $"第 {i + 1} 个压住了已有盒子");
                for (int k = 0; k < i; k++)
                    Program.Check(!placed[i].Overlaps(placed[k], BoxPlacementService.Gap), $"第 {i + 1} 个与第 {k + 1} 个重叠");
                Program.Check(placed[i].Right <= work.Right, $"第 {i + 1} 个越右界");
                Program.Check(placed[i].Bottom <= work.Bottom, $"第 {i + 1} 个越下界");
            }
        });

        Program.Case("排布不会改动已有盒子的坐标", () =>
        {
            var existing = new List<BoxArea> { new(500, 260, 240, 120) };
            var before = existing[0];

            BoxPlacementService.Arrange(work, existing, new (double, double)[] { (210, 96), (210, 96) });

            Program.Equal(before, existing[0], "已有盒子坐标必须原样保留");
        });

        Program.Case("盒子比工作区还大：压进可用区域，不产生越界或非数值坐标", () =>
        {
            var (x, y) = BoxPlacementService.FindFreeSpot(work, 5000, 5000);
            Program.Check(!double.IsNaN(x) && !double.IsNaN(y), "坐标不能是 NaN");
            Program.Check(x >= work.X && y >= work.Y, "不越左上界");
            Program.Check(x <= work.Right && y <= work.Bottom, "不越右下界");
        });

        Program.Case("桌面完全塞满：返回重叠面积最小的位置，而不是丢弃整块", () =>
        {
            var tiny = new BoxArea(0, 0, 300, 120);
            var blocker = new BoxArea(0, 0, 300, 120);

            var (x, y) = BoxPlacementService.FindFreeSpot(tiny, 200, 80, new[] { blocker });

            Program.Check(!double.IsNaN(x) && !double.IsNaN(y), "仍要给出一个坐标");
            Program.Check(x >= tiny.X && y >= tiny.Y && x + 200 <= tiny.Right + 1, "仍留在工作区内");
        });

        Program.Case("空目录判定：只有 desktop.ini / Thumbs.db 视为空，有子目录不算空", () =>
        {
            using var tmp = new Program.TempDir("tidy-empty");

            var empty = tmp.MkDir("empty");
            Program.Check(TidyBoxCleanupService.IsEmpty(empty), "空目录");

            var sysOnly = tmp.MkDir("sys");
            File.WriteAllText(Path.Combine(sysOnly, "desktop.ini"), "x");
            File.WriteAllText(Path.Combine(sysOnly, "Thumbs.db"), "x");
            Program.Check(TidyBoxCleanupService.IsEmpty(sysOnly), "系统占位文件不算内容");

            var withFile = tmp.MkDir("with-file");
            File.WriteAllText(Path.Combine(withFile, "a.txt"), "a");
            Program.Check(!TidyBoxCleanupService.IsEmpty(withFile), "有文件不算空");

            var withSub = tmp.MkDir("with-sub");
            Directory.CreateDirectory(Path.Combine(withSub, "子目录"));
            Program.Check(!TidyBoxCleanupService.IsEmpty(withSub), "有空子目录也保留（保守）");

            Program.Check(!TidyBoxCleanupService.IsEmpty(Path.Combine(tmp.Path, "不存在")),
                "目录不存在时不得判定为空（异常状态不该顺手删配置）");
        });

        Program.Case("恢复后清理：只删本次涉及且真的空了的盒子，映射盒与重复名都被排除", () =>
        {
            using var tmp = new Program.TempDir("tidy-cleanup");
            var empty = tmp.MkDir("box-empty");
            var full = tmp.MkDir("box-full");
            File.WriteAllText(Path.Combine(full, "留下.txt"), "还在用");

            var dirs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["文档"] = empty,
                ["图片"] = full,
                ["映射盒"] = null,          // 映射盒：dirOf 返回 null，不参与清理
            };

            var emptied = TidyBoxCleanupService.FindEmptiedBoxes(
                new[] { "文档", "图片", "映射盒", "文档", "完全不存在的盒子" },
                name => dirs.TryGetValue(name, out var d) ? d : null);

            Program.Equal(1, emptied.Count, "只应删 1 个");
            Program.Equal("文档", emptied[0], "被删的是空盒子");
        });

        Program.Case("图标与移动清单的名字匹配：显示名可能没有扩展名", () =>
        {
            var moving = new List<string> { "报告.pdf", "Qoder CN IDE.lnk", "项目资料" };

            Program.Check(DesktopTidyService.IconWillMove("报告", moving),
                "Explorer 隐藏扩展名时显示名是「报告」，也要认出来");
            Program.Check(DesktopTidyService.IconWillMove("报告.pdf", moving),
                "没隐藏扩展名时显示名就是全名");
            Program.Check(DesktopTidyService.IconWillMove("qoder cn ide", moving),
                "大小写不敏感");
            Program.Check(DesktopTidyService.IconWillMove("项目资料", moving),
                "文件夹名原样匹配");
            Program.Check(!DesktopTidyService.IconWillMove("回收站", moving),
                "不在清单里的图标必须保留");
            Program.Check(!DesktopTidyService.IconWillMove("报告2", moving),
                "前缀相同但名字不同，不能误判");
        });

        Program.Case("端到端：恢复把文件搬回后盒子判空；恢复被跳过的盒子仍有货，不删", () =>
        {
            using var tmp = new Program.TempDir("tidy-e2e");
            var desktop = tmp.MkDir("desktop");
            var boxDoc = tmp.MkDir("box-doc");
            var boxImg = tmp.MkDir("box-img");

            File.WriteAllText(Path.Combine(boxDoc, "已恢复.txt"), "a");
            File.WriteAllText(Path.Combine(boxImg, "会被跳过.txt"), "b");
            // 桌面上预先放一份同名：恢复会跳过，盒子那份必须留着
            File.WriteAllText(Path.Combine(desktop, "会被跳过.txt"), "桌面上的");

            var state = new TidyState
            {
                Items = new List<TidyRestoreItem>
                {
                    new()
                    {
                        Name = "已恢复.txt",
                        OriginalPath = Path.Combine(desktop, "已恢复.txt"),
                        BoxPath = Path.Combine(boxDoc, "已恢复.txt"),
                        BoxName = "文档",
                    },
                    new()
                    {
                        Name = "会被跳过.txt",
                        OriginalPath = Path.Combine(desktop, "会被跳过.txt"),
                        BoxPath = Path.Combine(boxImg, "会被跳过.txt"),
                        BoxName = "图片",
                    },
                },
            };

            var result = TidyRestoreService.Execute(TidyRestoreService.BuildPlan(state, desktop));
            Program.Equal(1, result.Restored, "只有 1 项成功放回");
            Program.Equal(1, result.Skipped, "同名的那项跳过");

            var dirs = new Dictionary<string, string?>
            {
                ["文档"] = boxDoc,
                ["图片"] = boxImg,
            };
            var emptied = TidyBoxCleanupService.FindEmptiedBoxes(new[] { "文档", "图片" }, n => dirs[n]);

            Program.Equal(1, emptied.Count, "只有搬空的「文档」该被清理");
            Program.Equal("文档", emptied[0], "被清理的盒子名");
            Program.Check(File.Exists(Path.Combine(boxImg, "会被跳过.txt")),
                "恢复被跳过的盒子必须原样保留");
        });
    }
}
