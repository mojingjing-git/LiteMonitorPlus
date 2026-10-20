using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteMonitor.PerformanceProbe;

/// <summary>
/// 外观夹具的固定数据。
///
/// 数据来源：<c>tests/AppearanceFixtures/appearance-fixtures.json</c>（版本控制内的唯一事实来源）。
/// 加载顺序：
///   1) 探针输出目录 <c>appearance-fixtures/appearance-fixtures.json</c>（构建时复制）
///   2) 仓库内 <c>tests/AppearanceFixtures/appearance-fixtures.json</c>（按仓库根回溯查找）
/// 两条路径指向**同一个文件**，因此不存在内容漂移的可能。
///
/// 找不到时直接抛异常 —— 夹具缺失绝不允许"编造数据继续跑"。
/// </summary>
internal sealed class AppearanceFixtureData
{
    public string SourcePath { get; private set; } = "";
    public string Sha256 { get; private set; } = "";

    public int Version { get; private set; }
    public string[] Modes { get; private set; } = Array.Empty<string>();
    public int[] GpuCounts { get; private set; } = Array.Empty<int>();
    public int[] Dpis { get; private set; } = Array.Empty<int>();
    public double[] UserScales { get; private set; } = Array.Empty<double>();
    public int PanelWidth { get; private set; } = 240;
    public int ReferenceDpi { get; private set; } = 96;
    public double ReferenceUserScale { get; private set; } = 1.0;

    public double ValueBase { get; private set; } = 40;
    public double ValueStep { get; private set; } = 0.37;
    public double ValueMax { get; private set; } = 99;
    public double ItemOffset { get; private set; } = 13.7;

    public Dictionary<string, string> DashValues { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] RequiredLegacyThemes { get; private set; } = Array.Empty<string>();
    public List<AppearanceGpuSpec> Gpus { get; private set; } = new();
    public List<AppearanceItemSpec> Items { get; private set; } = new();
    public List<AppearanceItemSpec> GpuItems { get; private set; } = new();
    public List<AppearanceEdgeSpec> EdgeScenarios { get; private set; } = new();

    public int InvalidationFrames { get; private set; } = 100;
    public RectangleI InvalidationFixedRect { get; private set; } = new(37, 43, 20, 20);
    public int ClipInflation { get; private set; } = 3;

    /// <summary>按 index 取显卡简称/全名（夹具数据，不来自真实硬件）。</summary>
    public AppearanceGpuSpec? FindGpu(int index) => Gpus.FirstOrDefault(g => g.Index == index);

    public static AppearanceFixtureData Load()
    {
        string path = LocateFixtureFile();
        var data = Parse(File.ReadAllText(path));
        data.SourcePath = path;
        data.Sha256 = ComputeSha256(path);
        return data;
    }

    private static AppearanceFixtureData Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<FixtureDto>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        }) ?? throw new InvalidOperationException("外观夹具 JSON 解析结果为空。");

        var data = new AppearanceFixtureData
        {
            Version = dto.Version,
            Modes = dto.Axes?.Modes?.ToArray() ?? Array.Empty<string>(),
            GpuCounts = dto.Axes?.GpuCounts?.ToArray() ?? Array.Empty<int>(),
            Dpis = dto.Axes?.Dpis?.ToArray() ?? Array.Empty<int>(),
            UserScales = dto.Axes?.UserScales?.ToArray() ?? Array.Empty<double>(),
            PanelWidth = dto.Axes?.PanelWidth ?? 240,
            ReferenceDpi = dto.Axes?.ReferenceDpi ?? 96,
            ReferenceUserScale = dto.Axes?.ReferenceUserScale ?? 1.0,
            ValueBase = dto.ValueModel?.Base ?? 40,
            ValueStep = dto.ValueModel?.Step ?? 0.37,
            ValueMax = dto.ValueModel?.Max ?? 99,
            ItemOffset = dto.ValueModel?.ItemOffset ?? 13.7,
            DashValues = dto.DashValues ?? new Dictionary<string, string>(),
            RequiredLegacyThemes = dto.RequiredLegacyThemes?.ToArray() ?? Array.Empty<string>(),
            Gpus = dto.Gpus ?? new List<AppearanceGpuSpec>(),
            Items = dto.Items ?? new List<AppearanceItemSpec>(),
            GpuItems = dto.GpuItems ?? new List<AppearanceItemSpec>(),
            EdgeScenarios = dto.EdgeScenarios ?? new List<AppearanceEdgeSpec>(),
            InvalidationFrames = dto.Invalidation?.Frames ?? 100,
            ClipInflation = dto.Invalidation?.ClipInflation ?? 3,
        };

        if (dto.Invalidation?.FixedRect is { } r)
            data.InvalidationFixedRect = new RectangleI(r.X, r.Y, r.Width, r.Height);

        if (data.Modes.Length == 0) throw new InvalidOperationException("外观夹具缺少 axes.modes。");
        if (data.Dpis.Length == 0) throw new InvalidOperationException("外观夹具缺少 axes.dpis。");
        if (data.UserScales.Length == 0) throw new InvalidOperationException("外观夹具缺少 axes.userScales。");
        if (data.GpuCounts.Length == 0) throw new InvalidOperationException("外观夹具缺少 axes.gpuCounts。");
        return data;
    }

    private static string LocateFixtureFile()
    {
        const string relative = "appearance-fixtures/appearance-fixtures.json";

        // 1) 探针输出目录（构建时复制）
        string fromOutput = Path.Combine(AppContext.BaseDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fromOutput)) return Path.GetFullPath(fromOutput);

        // 2) 仓库内 tests/AppearanceFixtures/appearance-fixtures.json
        string repoRoot = FindRepositoryRoot();
        if (repoRoot != null)
        {
            string inRepo = Path.Combine(repoRoot, "tests", "AppearanceFixtures", "appearance-fixtures.json");
            if (File.Exists(inRepo)) return Path.GetFullPath(inRepo);
        }

        throw new FileNotFoundException(
            "找不到外观夹具数据文件。请确认：构建时已把 tests/AppearanceFixtures/appearance-fixtures.json 复制到探针输出目录，" +
            "或探针位于仓库内（tests/LiteMonitor.PerformanceProbe/output/）。");
    }

    /// <summary>从探针输出目录向上回溯，找到含 .git 的仓库根。</summary>
    private static string? FindRepositoryRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    // ---------------------------------------------------------------- DTO

    private sealed class FixtureDto
    {
        public int Version { get; set; }
        public AxesDto? Axes { get; set; }
        public ValueModelDto? ValueModel { get; set; }
        public Dictionary<string, string>? DashValues { get; set; }
        public List<string>? RequiredLegacyThemes { get; set; }
        public List<AppearanceGpuSpec>? Gpus { get; set; }
        public List<AppearanceItemSpec>? Items { get; set; }
        public List<AppearanceItemSpec>? GpuItems { get; set; }
        public List<AppearanceEdgeSpec>? EdgeScenarios { get; set; }
        public InvalidationDto? Invalidation { get; set; }
    }

    private sealed class AxesDto
    {
        public List<string>? Modes { get; set; }
        public List<int>? GpuCounts { get; set; }
        public List<int>? Dpis { get; set; }
        public List<double>? UserScales { get; set; }
        public int PanelWidth { get; set; } = 240;
        public int ReferenceDpi { get; set; } = 96;
        public double ReferenceUserScale { get; set; } = 1.0;
    }

    private sealed class ValueModelDto
    {
        public double Base { get; set; } = 40;
        public double Step { get; set; } = 0.37;
        public double Max { get; set; } = 99;
        public double ItemOffset { get; set; } = 13.7;
    }

    private sealed class InvalidationDto
    {
        public int Frames { get; set; } = 100;
        public int ClipInflation { get; set; } = 3;
        public RectDto? FixedRect { get; set; }
    }

    private sealed class RectDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}

/// <summary>夹具里的一个显卡条目（纯夹具数据，不来自真实硬件）。</summary>
internal sealed class AppearanceGpuSpec
{
    public int Index { get; set; }
    public string ShortName { get; set; } = "";
    public string FullName { get; set; } = "";
}

/// <summary>夹具里的一个监控项定义。</summary>
internal sealed class AppearanceItemSpec
{
    public string Key { get; set; } = "";
    public int SortIndex { get; set; }
    public bool VisibleInPanel { get; set; } = true;
    public bool VisibleInTaskbar { get; set; }
}

/// <summary>边界场景定义（长组名 / 自定义标签 / 零项 / 窄窗）。</summary>
internal sealed class AppearanceEdgeSpec
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public int? PanelWidth { get; set; }
    public bool ClearAllItems { get; set; }
    public Dictionary<string, string>? GroupAliases { get; set; }
    public Dictionary<string, string>? GpuShortNames { get; set; }
    public Dictionary<string, string>? UserLabels { get; set; }
    public Dictionary<string, string>? TaskbarLabels { get; set; }
}

/// <summary>一个待渲染的外观夹具。</summary>
internal sealed class AppearanceFixtureSpec
{
    public string Theme { get; set; } = "";
    public string Mode { get; set; } = "vertical";           // vertical | horizontal
    public int GpuCount { get; set; } = 1;
    public int Dpi { get; set; } = 96;
    public double UserScale { get; set; } = 1.0;
    public int PanelWidth { get; set; } = 240;
    public string EdgeId { get; set; } = "";                  // 空 = 核心矩阵
    public string EdgeKind { get; set; } = "";
    public string EdgeDescription { get; set; } = "";

    public bool Horizontal => string.Equals(Mode, "horizontal", StringComparison.OrdinalIgnoreCase);

    /// <summary>稳定、可排序、可作文件名的夹具标识。</summary>
    public string Id
    {
        get
        {
            string id = string.Join("__", new[]
            {
                Slug(Theme),
                Mode,
                "gpu" + GpuCount.ToString(CultureInfo.InvariantCulture),
                "dpi" + Dpi.ToString(CultureInfo.InvariantCulture),
                "scale" + UserScale.ToString("0.0", CultureInfo.InvariantCulture),
                "w" + PanelWidth.ToString(CultureInfo.InvariantCulture),
            });
            if (!string.IsNullOrEmpty(EdgeId)) id += "__" + EdgeId;
            return id;
        }
    }

    private static string Slug(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString();
    }
}

/// <summary>轻量整数矩形（避免在夹具数据层引入 System.Drawing 依赖）。</summary>
internal readonly struct RectangleI
{
    public RectangleI(int x, int y, int width, int height)
    {
        X = x; Y = y; Width = width; Height = height;
    }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public override string ToString() => $"({X},{Y},{Width},{Height})";
}

/// <summary>夹具矩阵构建器。</summary>
internal static class AppearanceFixtureMatrix
{
    /// <summary>
    /// 构建夹具列表。
    /// set = "full"  : 主题 × 模式 × 卡数 × DPI × 用户缩放 的完整叉乘 + 全部边界场景
    /// set = "core"  : 完整叉乘，但只取 96/144 两个 DPI
    /// set = "edge"  : 只跑边界场景
    /// set = "quick" : 完整叉乘，但只取 96 DPI + 1.0 缩放（冒烟，不能当基线）
    /// </summary>
    public static List<AppearanceFixtureSpec> Build(AppearanceFixtureData data, string set, IReadOnlyList<string> themes)
    {
        var list = new List<AppearanceFixtureSpec>();

        bool wantCore = set is "full" or "core" or "quick";
        bool wantEdge = set is "full" or "core" or "edge";

        if (wantCore)
        {
            foreach (string theme in themes)
            foreach (string mode in data.Modes)
            foreach (int gpu in data.GpuCounts)
            foreach (int dpi in data.Dpis)
            foreach (double us in data.UserScales)
            {
                if (set == "core" && dpi is not (96 or 144)) continue;
                if (set == "quick" && (dpi != 96 || Math.Abs(us - 1.0) > 1e-9)) continue;
                list.Add(new AppearanceFixtureSpec
                {
                    Theme = theme,
                    Mode = mode,
                    GpuCount = gpu,
                    Dpi = dpi,
                    UserScale = us,
                    PanelWidth = data.PanelWidth,
                });
            }
        }

        if (wantEdge)
        {
            foreach (var edge in data.EdgeScenarios)
            foreach (string mode in data.Modes)
            foreach (int gpu in data.GpuCounts)
            {
                list.Add(new AppearanceFixtureSpec
                {
                    Theme = themes.Count > 0 ? themes[0] : "DarkFlat_Classic",
                    Mode = mode,
                    GpuCount = gpu,
                    Dpi = data.ReferenceDpi,
                    UserScale = data.ReferenceUserScale,
                    PanelWidth = edge.PanelWidth ?? data.PanelWidth,
                    EdgeId = edge.Id,
                    EdgeKind = edge.Kind,
                    EdgeDescription = edge.Description,
                });
            }
        }

        return list;
    }
}
