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
