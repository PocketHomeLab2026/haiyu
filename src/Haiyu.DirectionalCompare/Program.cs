using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Haiyu.DirectionalCompare;

internal static class Program
{
    internal const string ProductVersion = "0.7.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            var options = CliOptions.Parse(args.Skip(1).ToArray());
            return command switch
            {
                "scan" => RunScan(options),
                "compare" => RunCompare(options),
                "measure" => RunMeasure(options),
                "ab-benchmark" => RunAbBenchmark(options),
                "robustness-audit" => RunRobustnessAudit(options),
                "runtime-compare" => RunRuntimeCompare(options),
                "self-test" => SelfTest.Run(JsonOptions),
                "version" => PrintVersion(),
                _ => Fail($"未知命令：{args[0]}", 2),
            };
        }
        catch (Exception exception)
        {
            WriteJson(new
            {
                ok = false,
                error = exception.GetType().Name,
                message = exception.Message,
                ownerTaiji = Receipt.OwnerTaiji(),
            });
            return 1;
        }
    }

    private static int RunScan(CliOptions options)
    {
        var roots = options.GetMany("root");
        if (roots.Count == 0)
        {
            roots = ModelDiscovery.DefaultRoots().Where(Directory.Exists).ToList();
        }

        if (roots.Count == 0)
        {
            return Fail("没有找到默认模型目录。请使用 --root 指定模型根目录。", 3);
        }

        var output = Path.GetFullPath(options.GetOne("output") ?? Path.Combine(Environment.CurrentDirectory, "haiyu-directional-map-output"));
        var maxDepth = options.GetInt("max-depth", 6, 1, 20);
        var maxModels = options.GetInt("max-models", 256, 1, 4096);
        Directory.CreateDirectory(output);

        var stopwatch = Stopwatch.StartNew();
        var discoveries = ModelDiscovery.Find(roots, maxDepth, maxModels);
        var modelMaps = new List<ModelMap>();
        foreach (var discovery in discoveries)
        {
            var modelMap = ModelScanner.Scan(discovery);
            modelMaps.Add(modelMap);
            var safeName = FileNames.Safe(modelMap.ModelId) + ".causal-position-map.json";
            WriteJsonFile(Path.Combine(output, safeName), modelMap);
        }
        stopwatch.Stop();

        var manifest = new ScanManifest
        {
            Schema = "haiyu-directional-model-scan/v1",
            ProductVersion = Program.ProductVersion,
            GeneratedAt = DateTimeOffset.Now,
            OwnerTaiji = Receipt.OwnerTaiji(),
            Roots = roots.Select(Path.GetFullPath).ToList(),
            OutputDirectory = output,
            ModelCount = modelMaps.Count,
            SupportedModelCount = modelMaps.Count(item => item.Adapter.Supported),
            FailClosedModelCount = modelMaps.Count(item => !item.Adapter.Supported),
            TensorCount = modelMaps.Sum(item => item.Tensors.Count),
            StandardRoleMappedTensorCount = modelMaps.Sum(item => item.Tensors.Count(tensor => !tensor.StandardRole.StartsWith("native_unmapped.", StringComparison.Ordinal))),
            HeaderBytesRead = modelMaps.Sum(item => item.Evidence.HeaderBytesRead),
            WeightPayloadBytesRead = 0,
            FullWeightLoaded = false,
            GpuUsed = false,
            NetworkUsed = false,
            OriginalWeightsModified = false,
            ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            Models = modelMaps.Select(ModelSummary.From).ToList(),
            Authority = Receipt.Authority,
        };

        var manifestPath = Path.Combine(output, "scan-manifest.json");
        WriteJsonFile(manifestPath, manifest);
        WriteJson(new
        {
            ok = true,
            command = "scan",
            manifest = manifestPath,
            manifest.ModelCount,
            manifest.SupportedModelCount,
            manifest.FailClosedModelCount,
            manifest.TensorCount,
            manifest.StandardRoleMappedTensorCount,
            manifest.HeaderBytesRead,
            manifest.WeightPayloadBytesRead,
            manifest.FullWeightLoaded,
            manifest.GpuUsed,
            manifest.NetworkUsed,
            manifest.OriginalWeightsModified,
            elapsedMilliseconds = Math.Round(manifest.ElapsedMilliseconds, 3),
            manifest.Authority,
        });
        return modelMaps.Count == 0 ? 4 : 0;
    }

    private static int RunCompare(CliOptions options)
    {
        var mapPath = RequireFile(options, "map");
        var from = options.GetOne("from") ?? throw new ArgumentException("缺少 --from");
        var to = options.GetOne("to") ?? throw new ArgumentException("缺少 --to");
        var output = options.GetOne("output");
        var modelMap = JsonSerializer.Deserialize<ModelMap>(File.ReadAllText(mapPath), JsonOptions)
            ?? throw new InvalidDataException("因果位图反序列化失败");
        var result = DirectionalComparer.Compare(modelMap, from, to, options.GetInt("max-paths", 32, 1, 4096));
        if (!string.IsNullOrWhiteSpace(output))
        {
            WriteJsonFile(Path.GetFullPath(output), result);
        }
        WriteJson(result);
        return result.Supported ? 0 : 5;
    }

    private static int RunMeasure(CliOptions options)
    {
        var mapPath = RequireFile(options, "map");
        var from = options.GetOne("from") ?? throw new ArgumentException("缺少 --from");
        var to = options.GetOne("to") ?? throw new ArgumentException("缺少 --to");
        var output = options.GetOne("output");
        var modelMap = JsonSerializer.Deserialize<ModelMap>(File.ReadAllText(mapPath), JsonOptions)
            ?? throw new InvalidDataException("因果位图反序列化失败");
        var result = DirectedPayloadComparer.Measure(
            modelMap,
            from,
            to,
            options.GetInt("max-pairs", 8, 1, 128),
            options.GetInt("sample-elements", 32768, 128, 1048576));
        if (!string.IsNullOrWhiteSpace(output))
        {
            WriteJsonFile(Path.GetFullPath(output), result);
        }
        WriteJson(result);
        return result.Supported ? 0 : 6;
    }

    private static int RunAbBenchmark(CliOptions options)
    {
        var mapPath = RequireFile(options, "map");
        var from = options.GetOne("from") ?? throw new ArgumentException("缺少 --from");
        var to = options.GetOne("to") ?? throw new ArgumentException("缺少 --to");
        var output = options.GetOne("output");
        var modelMap = JsonSerializer.Deserialize<ModelMap>(File.ReadAllText(mapPath), JsonOptions)
            ?? throw new InvalidDataException("因果位图反序列化失败");
        var result = DirectionalAbBenchmark.Run(
            modelMap,
            from,
            to,
            options.GetInt("max-pairs", 1, 1, 32),
            options.GetInt("sample-elements", 4096, 128, 1048576),
            options.GetInt("iterations", 3, 1, 20));
        if (!string.IsNullOrWhiteSpace(output))
        {
            WriteJsonFile(Path.GetFullPath(output), result);
        }
        WriteJson(result);
        return result.Supported && result.AllOutputsEquivalent ? 0 : 7;
    }

    private static int RunRobustnessAudit(CliOptions options)
    {
        var mapPath = RequireFile(options, "map");
        var from = options.GetOne("from") ?? throw new ArgumentException("缺少 --from");
        var to = options.GetOne("to") ?? throw new ArgumentException("缺少 --to");
        var output = options.GetOne("output");
        var modelMap = JsonSerializer.Deserialize<ModelMap>(File.ReadAllText(mapPath), JsonOptions)
            ?? throw new InvalidDataException("因果位图反序列化失败");
        var result = RobustnessAudit.Run(
            modelMap,
            from,
            to,
            options.GetInt("max-pairs", 1, 1, 16),
            options.GetCsvInts("sample-scales", new[] { 128, 1024, 4096, 16384 }, 128, 1048576),
            options.GetCsvDoubles("noise-levels", new[] { 0.000001, 0.0001, 0.01 }, 0.0, 0.1),
            options.GetInt("seed", 20260917, 0, int.MaxValue));
        if (!string.IsNullOrWhiteSpace(output))
        {
            WriteJsonFile(Path.GetFullPath(output), result);
        }
        WriteJson(result);
        return result.Passed ? 0 : 8;
    }

    private static int RunRuntimeCompare(CliOptions options)
    {
        var mapPath = RequireFile(options, "map");
        var readinessPath = RequireFile(options, "readiness");
        var from = options.GetOne("from") ?? throw new ArgumentException("缺少 --from");
        var to = options.GetOne("to") ?? throw new ArgumentException("缺少 --to");
        var output = options.GetOne("output");
        var modelMap = JsonSerializer.Deserialize<ModelMap>(File.ReadAllText(mapPath), JsonOptions)
            ?? throw new InvalidDataException("因果位图反序列化失败");
        var result = RuntimeComparisonOrgan.Run(
            modelMap,
            readinessPath,
            from,
            to,
            options.GetInt("max-pairs", 1, 1, 32),
            options.GetInt("sample-elements", 4096, 128, 1048576),
            options.GetBool("verify-baseline", false),
            options.GetBool("allow-baseline-fallback", true),
            options.GetOne("request-id") ?? Guid.NewGuid().ToString("N"),
            options.GetOne("inject-fault") ?? "none");
        if (!string.IsNullOrWhiteSpace(output))
        {
            WriteJsonFile(Path.GetFullPath(output), result);
        }
        WriteJson(result);
        return result.Supported ? 0 : 9;
    }

    private static string RequireFile(CliOptions options, string name)
    {
        var value = options.GetOne(name) ?? throw new ArgumentException($"缺少 --{name}");
        var path = Path.GetFullPath(value);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"文件不存在：{path}", path);
        }
        return path;
    }

    private static int PrintVersion()
    {
        Console.WriteLine(ProductVersion);
        return 0;
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private static int Fail(string message, int code)
    {
        WriteJson(new { ok = false, message, ownerTaiji = Receipt.OwnerTaiji() });
        return code;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
海域模型定向对比器 0.7.0

用途：只读识别本地模型原生结构，生成结构因果位图，并按原生方向做定向比较。
边界：结构方向与定向浮点采样分层留证；不宣称语义正确率，不加载完整权重，不使用显卡，不修改权重。

命令：
  scan [--root <目录>]... [--output <目录>] [--max-depth 6] [--max-models 256]
  compare --map <模型图.json> --from <张量名|标准角色[@层]> --to <张量名|标准角色[@层]> [--max-paths 32]
  measure --map <模型图.json> --from <张量名|标准角色[@层]> --to <张量名|标准角色[@层]> [--max-pairs 8] [--sample-elements 32768]
  ab-benchmark --map <模型图.json> --from <张量名|标准角色[@层]> --to <张量名|标准角色[@层]> [--max-pairs 1] [--sample-elements 4096] [--iterations 3]
  robustness-audit --map <模型图.json> --from <标准角色> --to <标准角色> [--sample-scales 128,1024,4096,16384] [--noise-levels 0.000001,0.0001,0.01]
  runtime-compare --map <模型图.json> --readiness <就绪报告.json> --from <张量名|标准角色[@层]> --to <张量名|标准角色[@层]> [--verify-baseline false] [--allow-baseline-fallback true] [--request-id <ID>]
  self-test
  version

示例：
  haiyu-directional-compare scan --root D:\Models --output D:\HaiyuMaps
  haiyu-directional-compare compare --map D:\HaiyuMaps\model.causal-position-map.json --from token_embedding --to language_head
  haiyu-directional-compare compare --map D:\HaiyuMaps\model.causal-position-map.json --from attention.query_projection@0 --to attention.output_projection@0
  haiyu-directional-compare measure --map D:\HaiyuMaps\model.causal-position-map.json --from attention.query_projection@0 --to attention.output_projection@0
  haiyu-directional-compare ab-benchmark --map D:\HaiyuMaps\model.causal-position-map.json --from attention.query_projection@0 --to attention.output_projection@0 --iterations 3
  haiyu-directional-compare robustness-audit --map D:\HaiyuMaps\model.causal-position-map.json --from attention.query_projection --to attention.output_projection --output D:\HaiyuMaps\robustness-audit.json
  haiyu-directional-compare runtime-compare --map D:\HaiyuMaps\model.causal-position-map.json --readiness D:\Haiyu\customer-deployment-readiness.json --from attention.query_projection@0 --to attention.output_projection@0 --verify-baseline true
""");
    }

    internal static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    internal static void WriteJsonFile(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"无法识别的参数：{token}");
            }
            var name = token[2..];
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options.Add(name, "true");
                continue;
            }
            options.Add(name, args[++index]);
        }
        return options;
    }

    private void Add(string name, string value)
    {
        if (!_values.TryGetValue(name, out var values))
        {
            values = new List<string>();
            _values[name] = values;
        }
        values.Add(value);
    }

    public string? GetOne(string name) => _values.TryGetValue(name, out var values) ? values.LastOrDefault() : null;
    public List<string> GetMany(string name) => _values.TryGetValue(name, out var values) ? values.ToList() : new List<string>();

    public bool GetBool(string name, bool fallback)
    {
        var text = GetOne(name);
        if (text is null) return fallback;
        if (bool.TryParse(text, out var value)) return value;
        if (text is "1" or "yes" or "on") return true;
        if (text is "0" or "no" or "off") return false;
        throw new ArgumentException($"--{name} 必须是 true 或 false");
    }

    public int GetInt(string name, int fallback, int minimum, int maximum)
    {
        var text = GetOne(name);
        if (text is null)
        {
            return fallback;
        }
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"--{name} 必须在 {minimum} 到 {maximum} 之间");
        }
        return value;
    }

    public List<int> GetCsvInts(string name, IEnumerable<int> fallback, int minimum, int maximum)
    {
        var text = GetOne(name);
        var values = text is null
            ? fallback.ToList()
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => int.TryParse(item, out var value)
                    ? value
                    : throw new ArgumentException($"--{name} 包含无效整数：{item}"))
                .ToList();
        if (values.Count == 0 || values.Any(value => value < minimum || value > maximum))
        {
            throw new ArgumentOutOfRangeException(name, $"--{name} 每项必须在 {minimum} 到 {maximum} 之间");
        }
        return values.Distinct().OrderBy(value => value).ToList();
    }

    public List<double> GetCsvDoubles(string name, IEnumerable<double> fallback, double minimum, double maximum)
    {
        var text = GetOne(name);
        var values = text is null
            ? fallback.ToList()
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => double.TryParse(item, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw new ArgumentException($"--{name} 包含无效浮点数：{item}"))
                .ToList();
        if (values.Count == 0 || values.Any(value => !double.IsFinite(value) || value < minimum || value > maximum))
        {
            throw new ArgumentOutOfRangeException(name, $"--{name} 每项必须在 {minimum} 到 {maximum} 之间");
        }
        return values.Distinct().OrderBy(value => value).ToList();
    }
}

internal static class Receipt
{
    public const string Authority = "structural-direction-only; semantic meaning and answer correctness remain unproven";

    public static OwnerTaijiAnchor OwnerTaiji() => new()
    {
        Present = true,
        Kind = "local-owner-consent-boundary",
        Rule = "read-only metadata scan; original model bytes are never rewritten",
    };
}

internal sealed class OwnerTaijiAnchor
{
    public bool Present { get; set; }
    public string Kind { get; set; } = "";
    public string Rule { get; set; } = "";
}

internal sealed class ModelDiscovery
{
    public string ConfigPath { get; init; } = "";
    public string ModelRoot { get; init; } = "";
    public string DiscoveryRoot { get; init; } = "";
    public string ArtifactPath { get; init; } = "";
    public string DiscoveryId { get; init; } = "";

    public static List<string> DefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string>
        {
            Path.Combine(home, ".cache", "huggingface", "hub"),
            Path.Combine(home, ".cache", "modelscope", "hub"),
            Path.Combine(home, ".ollama", "models"),
            Path.Combine(home, ".lmstudio", "models"),
            Path.Combine(home, ".cache", "lm-studio", "models"),
            Path.Combine(local, "nomic.ai", "GPT4All"),
            Path.Combine(local, "LM Studio", "models"),
        };

        AddEnvironmentRoot(roots, "HF_HOME", "hub");
        AddEnvironmentRoot(roots, "HUGGINGFACE_HUB_CACHE");
        AddEnvironmentRoot(roots, "TRANSFORMERS_CACHE");
        AddEnvironmentRoot(roots, "MODELSCOPE_CACHE");
        AddEnvironmentRoot(roots, "OLLAMA_MODELS");

        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddEnvironmentRoot(ICollection<string> roots, string variable, string? child = null)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return;
        roots.Add(string.IsNullOrWhiteSpace(child) ? value : Path.Combine(value, child));
    }

    public static List<ModelDiscovery> Find(IReadOnlyList<string> roots, int maxDepth, int maxModels)
    {
        var found = new Dictionary<string, ModelDiscovery>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootText in roots)
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rootText));
            if (!Directory.Exists(root))
            {
                continue;
            }

            AddOllamaModels(root, found, maxModels);

            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0 && found.Count < maxModels)
            {
                var current = queue.Dequeue();
                if (ShouldSkip(current.Path))
                {
                    continue;
                }
                var config = Path.Combine(current.Path, "config.json");
                if (File.Exists(config))
                {
                    found.TryAdd(Path.GetFullPath(config), new ModelDiscovery
                    {
                        ConfigPath = Path.GetFullPath(config),
                        ModelRoot = Path.GetFullPath(current.Path),
                        DiscoveryRoot = root,
                    });
                    continue;
                }

                try
                {
                    foreach (var gguf in Directory.EnumerateFiles(current.Path, "*.gguf", SearchOption.TopDirectoryOnly))
                    {
                        if (found.Count >= maxModels) break;
                        found.TryAdd(Path.GetFullPath(gguf), new ModelDiscovery
                        {
                            ArtifactPath = Path.GetFullPath(gguf),
                            ModelRoot = Path.GetFullPath(current.Path),
                            DiscoveryRoot = root,
                            DiscoveryId = Path.GetFileNameWithoutExtension(gguf),
                        });
                    }
                }
                catch (IOException)
                {
                }

                if (current.Depth >= maxDepth)
                {
                    continue;
                }
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(current.Path))
                    {
                        queue.Enqueue((child, current.Depth + 1));
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }
        }
        return found.Values.OrderBy(item => item.ModelRoot, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddOllamaModels(string root, IDictionary<string, ModelDiscovery> found, int maxModels)
    {
        var manifests = Path.Combine(root, "manifests");
        var blobs = Path.Combine(root, "blobs");
        if (!Directory.Exists(manifests) || !Directory.Exists(blobs)) return;
        foreach (var manifest in Directory.EnumerateFiles(manifests, "*", SearchOption.AllDirectories))
        {
            if (found.Count >= maxModels) break;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (!document.RootElement.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array) continue;
                foreach (var layer in layers.EnumerateArray())
                {
                    var mediaType = JsonRead.String(layer, "mediaType") ?? "";
                    if (!mediaType.Contains(".model", StringComparison.OrdinalIgnoreCase)) continue;
                    var digest = JsonRead.String(layer, "digest") ?? "";
                    var blob = Path.Combine(blobs, digest.Replace(':', '-'));
                    if (!File.Exists(blob) || !GgufReader.HasMagic(blob)) continue;
                    var id = "ollama:" + Path.GetRelativePath(manifests, manifest).Replace('\\', '/');
                    found.TryAdd(Path.GetFullPath(blob), new ModelDiscovery
                    {
                        ArtifactPath = Path.GetFullPath(blob),
                        ModelRoot = Path.GetDirectoryName(Path.GetFullPath(blob))!,
                        DiscoveryRoot = root,
                        DiscoveryId = id,
                    });
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool ShouldSkip(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals(".locks", StringComparison.OrdinalIgnoreCase)
            || name.Equals("blobs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".git", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ModelScanner
{
    public static ModelMap Scan(ModelDiscovery discovery)
    {
        if (!string.IsNullOrWhiteSpace(discovery.ArtifactPath))
        {
            return ScanGguf(discovery);
        }
        using var document = JsonDocument.Parse(File.ReadAllText(discovery.ConfigPath));
        var config = document.RootElement.Clone();
        var modelType = JsonRead.String(config, "model_type") ?? "unknown";
        var architectureName = JsonRead.StringArray(config, "architectures").FirstOrDefault() ?? "unknown";
        var layerCount = JsonRead.Int(config, "num_hidden_layers")
            ?? JsonRead.Int(config, "n_layer")
            ?? JsonRead.Int(config, "num_layers")
            ?? JsonRead.Int(config, "n_layers")
            ?? 0;
        var profile = ArchitectureRegistry.Resolve(modelType, architectureName, config);
        var catalog = TensorCatalog.Read(discovery.ModelRoot, profile, layerCount);
        var modelId = ModelIds.FromPath(discovery.ModelRoot, config);

        return new ModelMap
        {
            Schema = "haiyu-local-causal-position-map/v1",
            ProductVersion = Program.ProductVersion,
            GeneratedAt = DateTimeOffset.Now,
            OwnerTaiji = Receipt.OwnerTaiji(),
            ModelId = modelId,
            ModelRoot = discovery.ModelRoot,
            ConfigPath = discovery.ConfigPath,
            ConfigSha256 = Hashing.Sha256(discovery.ConfigPath),
            ArtifactPath = "",
            MetadataFingerprint = Hashing.Sha256(discovery.ConfigPath),
            ModelType = modelType,
            ArchitectureName = architectureName,
            LayerCount = layerCount,
            HiddenSize = JsonRead.Int(config, "hidden_size") ?? JsonRead.Int(config, "n_embd") ?? JsonRead.Int(config, "d_model") ?? 0,
            Adapter = profile,
            Tensors = catalog.Tensors,
            Evidence = new ScanEvidence
            {
                TensorFiles = catalog.TensorFiles,
                HeaderBytesRead = catalog.HeaderBytesRead,
                WeightPayloadBytesRead = 0,
                FullWeightLoaded = false,
                GpuUsed = false,
                NetworkUsed = false,
                OriginalWeightsModified = false,
                UnknownTensorRoles = catalog.Tensors.Count(tensor => tensor.StandardRole.StartsWith("native_unmapped.", StringComparison.Ordinal)),
                Authority = Receipt.Authority,
                Notes = catalog.Notes,
            },
        };
    }

    private static ModelMap ScanGguf(ModelDiscovery discovery)
    {
        var gguf = GgufReader.Read(discovery.ArtifactPath);
        using var empty = JsonDocument.Parse("{}");
        var profile = ArchitectureRegistry.Resolve(gguf.Architecture, gguf.Architecture + "-gguf", empty.RootElement);
        var tensors = gguf.Tensors
            .Select(tensor => TensorRoleMapper.Map(tensor.Name, tensor.DType, tensor.Shape, profile, gguf.LayerCount, Path.GetFileName(discovery.ArtifactPath)))
            .OrderBy(tensor => tensor.Name, StringComparer.Ordinal)
            .ToList();
        var metadataFingerprint = Hashing.Sha256Text(JsonSerializer.Serialize(gguf.MetadataForFingerprint));
        return new ModelMap
        {
            Schema = "haiyu-local-causal-position-map/v1",
            ProductVersion = Program.ProductVersion,
            GeneratedAt = DateTimeOffset.Now,
            OwnerTaiji = Receipt.OwnerTaiji(),
            ModelId = string.IsNullOrWhiteSpace(discovery.DiscoveryId) ? gguf.ModelName : discovery.DiscoveryId,
            ModelRoot = discovery.ModelRoot,
            ConfigPath = "",
            ConfigSha256 = "",
            ArtifactPath = discovery.ArtifactPath,
            MetadataFingerprint = metadataFingerprint,
            ModelType = gguf.Architecture,
            ArchitectureName = gguf.Architecture + "-gguf",
            LayerCount = gguf.LayerCount,
            HiddenSize = gguf.HiddenSize,
            Adapter = profile,
            Tensors = tensors,
            Evidence = new ScanEvidence
            {
                TensorFiles = new List<string> { discovery.ArtifactPath },
                HeaderBytesRead = gguf.HeaderBytesRead,
                WeightPayloadBytesRead = 0,
                FullWeightLoaded = false,
                GpuUsed = false,
                NetworkUsed = false,
                OriginalWeightsModified = false,
                UnknownTensorRoles = tensors.Count(tensor => tensor.StandardRole.StartsWith("native_unmapped.", StringComparison.Ordinal)),
                Authority = Receipt.Authority,
                Notes = new List<string> { "GGUF metadata and tensor descriptors were read; quantized tensor payload was not loaded." },
            },
        };
    }
}

internal sealed class TensorCatalogResult
{
    public List<TensorPosition> Tensors { get; init; } = new();
    public List<string> TensorFiles { get; init; } = new();
    public long HeaderBytesRead { get; set; }
    public List<string> Notes { get; init; } = new();
}

internal sealed class GgufTensor
{
    public string Name { get; init; } = "";
    public string DType { get; init; } = "";
    public long[] Shape { get; init; } = Array.Empty<long>();
}

internal sealed class GgufInfo
{
    public string Architecture { get; init; } = "unknown";
    public string ModelName { get; init; } = "unknown-gguf";
    public int LayerCount { get; init; }
    public int HiddenSize { get; init; }
    public long HeaderBytesRead { get; init; }
    public List<GgufTensor> Tensors { get; init; } = new();
    public Dictionary<string, object?> MetadataForFingerprint { get; init; } = new();
}

internal static class GgufReader
{
    private const uint UInt8 = 0;
    private const uint Int8 = 1;
    private const uint UInt16 = 2;
    private const uint Int16 = 3;
    private const uint UInt32 = 4;
    private const uint Int32 = 5;
    private const uint Float32 = 6;
    private const uint Bool = 7;
    private const uint String = 8;
    private const uint Array = 9;
    private const uint UInt64 = 10;
    private const uint Int64 = 11;
    private const uint Float64 = 12;

    public static bool HasMagic(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 && magic.SequenceEqual("GGUF"u8);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static GgufInfo Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("GGUF"u8)) throw new InvalidDataException("GGUF magic missing");
        var version = reader.ReadUInt32();
        if (version is < 2 or > 3) throw new InvalidDataException($"Unsupported GGUF version: {version}");
        var tensorCount = reader.ReadUInt64();
        var metadataCount = reader.ReadUInt64();
        if (tensorCount > 10_000_000 || metadataCount > 1_000_000) throw new InvalidDataException("GGUF header count exceeds safety limit");

        var selected = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (ulong index = 0; index < metadataCount; index++)
        {
            var key = ReadString(reader);
            var type = reader.ReadUInt32();
            var capture = key == "general.architecture" || key == "general.name" || key.EndsWith(".block_count", StringComparison.Ordinal) || key.EndsWith(".embedding_length", StringComparison.Ordinal);
            var value = ReadOrSkipValue(reader, type, capture);
            if (capture) selected[key] = value;
        }

        var tensors = new List<GgufTensor>(checked((int)Math.Min(tensorCount, int.MaxValue)));
        for (ulong index = 0; index < tensorCount; index++)
        {
            var name = ReadString(reader);
            var dimensions = reader.ReadUInt32();
            if (dimensions > 16) throw new InvalidDataException("GGUF tensor dimension exceeds safety limit");
            var shape = new long[dimensions];
            for (var dimension = 0; dimension < dimensions; dimension++) shape[dimension] = checked((long)reader.ReadUInt64());
            var type = reader.ReadUInt32();
            _ = reader.ReadUInt64();
            tensors.Add(new GgufTensor { Name = name, DType = GgmlType(type), Shape = shape });
        }

        var architecture = selected.TryGetValue("general.architecture", out var architectureValue) ? Convert.ToString(architectureValue) ?? "unknown" : "unknown";
        var modelName = selected.TryGetValue("general.name", out var nameValue) ? Convert.ToString(nameValue) ?? Path.GetFileNameWithoutExtension(path) : Path.GetFileNameWithoutExtension(path);
        var layerCount = ConvertToInt(selected.FirstOrDefault(pair => pair.Key.EndsWith(".block_count", StringComparison.Ordinal)).Value);
        var hiddenSize = ConvertToInt(selected.FirstOrDefault(pair => pair.Key.EndsWith(".embedding_length", StringComparison.Ordinal)).Value);
        return new GgufInfo
        {
            Architecture = NormalizeArchitecture(architecture),
            ModelName = modelName,
            LayerCount = layerCount,
            HiddenSize = hiddenSize,
            HeaderBytesRead = stream.Position,
            Tensors = tensors,
            MetadataForFingerprint = selected,
        };
    }

    private static string NormalizeArchitecture(string value) => value.ToLowerInvariant() switch
    {
        "qwen2moe" => "qwen2",
        "llama" => "llama",
        "mistral" => "mistral",
        "qwen2" => "qwen2",
        "qwen3" => "qwen3",
        "phi2" => "phi",
        "phi3" => "phi3",
        "phi3small" => "phi3small",
        "gptneox" => "gpt_neox",
        "mamba" => "mamba",
        var other => other,
    };

    private static int ConvertToInt(object? value)
    {
        if (value is null) return 0;
        try { return checked(Convert.ToInt32(value)); }
        catch (Exception exception) when (exception is OverflowException or FormatException or InvalidCastException) { return 0; }
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > 1024UL * 1024UL * 1024UL) throw new InvalidDataException("GGUF string exceeds safety limit");
        var bytes = reader.ReadBytes(checked((int)length));
        if ((ulong)bytes.Length != length) throw new EndOfStreamException("GGUF string truncated");
        return Encoding.UTF8.GetString(bytes);
    }

    private static object? ReadOrSkipValue(BinaryReader reader, uint type, bool capture)
    {
        if (capture)
        {
            return type switch
            {
                UInt8 => reader.ReadByte(),
                Int8 => reader.ReadSByte(),
                UInt16 => reader.ReadUInt16(),
                Int16 => reader.ReadInt16(),
                UInt32 => reader.ReadUInt32(),
                Int32 => reader.ReadInt32(),
                Float32 => reader.ReadSingle(),
                Bool => reader.ReadByte() != 0,
                String => ReadString(reader),
                UInt64 => reader.ReadUInt64(),
                Int64 => reader.ReadInt64(),
                Float64 => reader.ReadDouble(),
                Array => ReadCapturedArray(reader),
                _ => throw new InvalidDataException($"Unknown GGUF metadata type: {type}"),
            };
        }
        SkipValue(reader, type);
        return null;
    }

    private static object ReadCapturedArray(BinaryReader reader)
    {
        var elementType = reader.ReadUInt32();
        var count = reader.ReadUInt64();
        if (count > 1024) throw new InvalidDataException("Selected GGUF metadata array exceeds capture limit");
        var values = new List<object?>((int)count);
        for (ulong index = 0; index < count; index++) values.Add(ReadOrSkipValue(reader, elementType, true));
        return values;
    }

    private static void SkipValue(BinaryReader reader, uint type)
    {
        var fixedSize = type switch
        {
            UInt8 or Int8 or Bool => 1,
            UInt16 or Int16 => 2,
            UInt32 or Int32 or Float32 => 4,
            UInt64 or Int64 or Float64 => 8,
            _ => 0,
        };
        if (fixedSize > 0)
        {
            reader.BaseStream.Seek(fixedSize, SeekOrigin.Current);
            return;
        }
        if (type == String)
        {
            var length = reader.ReadUInt64();
            reader.BaseStream.Seek(checked((long)length), SeekOrigin.Current);
            return;
        }
        if (type == Array)
        {
            var elementType = reader.ReadUInt32();
            var count = reader.ReadUInt64();
            var elementSize = elementType switch
            {
                UInt8 or Int8 or Bool => 1,
                UInt16 or Int16 => 2,
                UInt32 or Int32 or Float32 => 4,
                UInt64 or Int64 or Float64 => 8,
                _ => 0,
            };
            if (elementSize > 0)
            {
                reader.BaseStream.Seek(checked((long)count * elementSize), SeekOrigin.Current);
                return;
            }
            for (ulong index = 0; index < count; index++) SkipValue(reader, elementType);
            return;
        }
        throw new InvalidDataException($"Unknown GGUF metadata type: {type}");
    }

    private static string GgmlType(uint type) => type switch
    {
        0 => "F32",
        1 => "F16",
        2 => "Q4_0",
        3 => "Q4_1",
        6 => "Q5_0",
        7 => "Q5_1",
        8 => "Q8_0",
        9 => "Q8_1",
        10 => "Q2_K",
        11 => "Q3_K",
        12 => "Q4_K",
        13 => "Q5_K",
        14 => "Q6_K",
        15 => "Q8_K",
        16 => "IQ2_XXS",
        17 => "IQ2_XS",
        18 => "IQ3_XXS",
        22 => "IQ1_S",
        30 => "BF16",
        _ => "GGML_TYPE_" + type,
    };
}

internal static class TensorCatalog
{
    public static TensorCatalogResult Read(string modelRoot, ArchitectureProfile profile, int layerCount)
    {
        var result = new TensorCatalogResult();
        var safetensors = Directory.EnumerateFiles(modelRoot, "*.safetensors", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var path in safetensors)
        {
            try
            {
                var header = ReadSafeTensorHeader(path);
                result.HeaderBytesRead += 8 + header.HeaderLength;
                result.TensorFiles.Add(path);
                foreach (var tensor in header.Tensors)
                {
                    result.Tensors.Add(TensorRoleMapper.Map(
                        tensor.Name,
                        tensor.DType,
                        tensor.Shape,
                        profile,
                        layerCount,
                        Path.GetFileName(path),
                        tensor.PayloadOffset,
                        tensor.PayloadBytes,
                        "safetensors"));
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                result.Notes.Add($"safetensors-header-failed:{Path.GetFileName(path)}:{exception.Message}");
            }
        }

        if (result.Tensors.Count == 0)
        {
            var indexPath = Directory.EnumerateFiles(modelRoot, "*.index.json", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (indexPath is not null)
            {
                ReadIndexNames(indexPath, profile, layerCount, result);
            }
        }

        if (result.Tensors.Count == 0)
        {
            var gguf = Directory.EnumerateFiles(modelRoot, "*.gguf", SearchOption.TopDirectoryOnly).ToList();
            var bins = Directory.EnumerateFiles(modelRoot, "*.bin", SearchOption.TopDirectoryOnly).ToList();
            if (gguf.Count > 0)
            {
                result.TensorFiles.AddRange(gguf);
                result.Notes.Add("GGUF quantized payload decoding is not enabled in v0.3, so numeric comparison fails closed while structural discovery remains available.");
            }
            else if (bins.Count > 0)
            {
                result.TensorFiles.AddRange(bins);
                result.Notes.Add("PyTorch BIN payload detected without readable index; comparison fails closed.");
            }
            else
            {
                result.Notes.Add("No supported tensor metadata source was found.");
            }
        }

        result.Tensors.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return result;
    }

    private static SafeTensorHeader ReadSafeTensorHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        Span<byte> prefix = stackalloc byte[8];
        if (stream.Read(prefix) != 8)
        {
            throw new InvalidDataException("missing safetensors header length");
        }
        var headerLengthUnsigned = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
        if (headerLengthUnsigned == 0 || headerLengthUnsigned > 256UL * 1024UL * 1024UL)
        {
            throw new InvalidDataException($"invalid safetensors header length: {headerLengthUnsigned}");
        }
        var headerLength = checked((int)headerLengthUnsigned);
        var buffer = new byte[headerLength];
        stream.ReadExactly(buffer);
        using var document = JsonDocument.Parse(buffer);
        var tensors = new List<RawTensor>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("__metadata__") || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var dtype = JsonRead.String(property.Value, "dtype") ?? "unknown";
            var shape = JsonRead.Int64Array(property.Value, "shape");
            var offsets = JsonRead.Int64Array(property.Value, "data_offsets");
            if (offsets.Length != 2 || offsets[0] < 0 || offsets[1] < offsets[0])
            {
                throw new InvalidDataException($"invalid data_offsets for tensor {property.Name}");
            }
            var payloadOffset = checked(8L + headerLength + offsets[0]);
            var payloadBytes = checked(offsets[1] - offsets[0]);
            if (payloadOffset + payloadBytes > stream.Length)
            {
                throw new InvalidDataException($"tensor payload exceeds file length: {property.Name}");
            }
            tensors.Add(new RawTensor(property.Name, dtype, shape, payloadOffset, payloadBytes));
        }
        return new SafeTensorHeader(headerLength, tensors);
    }

    private static void ReadIndexNames(string indexPath, ArchitectureProfile profile, int layerCount, TensorCatalogResult result)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
        if (!document.RootElement.TryGetProperty("weight_map", out var weightMap) || weightMap.ValueKind != JsonValueKind.Object)
        {
            result.Notes.Add("index-without-weight-map:" + Path.GetFileName(indexPath));
            return;
        }
        foreach (var property in weightMap.EnumerateObject())
        {
            var fileName = property.Value.GetString() ?? "index-only";
            result.Tensors.Add(TensorRoleMapper.Map(property.Name, "unknown", Array.Empty<long>(), profile, layerCount, fileName));
            if (!result.TensorFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                result.TensorFiles.Add(fileName);
            }
        }
        result.HeaderBytesRead += new FileInfo(indexPath).Length;
        result.Notes.Add("Tensor names came from an index only; dtype and shape are unknown.");
    }

    private sealed record RawTensor(string Name, string DType, long[] Shape, long PayloadOffset, long PayloadBytes);
    private sealed record SafeTensorHeader(int HeaderLength, List<RawTensor> Tensors);
}

internal static class TensorRoleMapper
{
    private static readonly Regex[] LayerPatterns =
    {
        new(@"(?:^|\.)layers\.(\d+)(?:\.|$)", RegexOptions.Compiled),
        new(@"(?:^|\.)h\.(\d+)(?:\.|$)", RegexOptions.Compiled),
        new(@"(?:^|\.)blocks?\.(\d+)(?:\.|$)", RegexOptions.Compiled),
        new(@"(?:^|\.)blk\.(\d+)(?:\.|$)", RegexOptions.Compiled),
        new(@"(?:^|\.)encoder\.layer\.(\d+)(?:\.|$)", RegexOptions.Compiled),
        new(@"(?:^|\.)decoder\.layer\.(\d+)(?:\.|$)", RegexOptions.Compiled),
    };

    public static TensorPosition Map(
        string name,
        string dtype,
        long[] shape,
        ArchitectureProfile profile,
        int layerCount,
        string sourceFile,
        long payloadOffset = -1,
        long payloadBytes = 0,
        string storageFormat = "metadata-only")
    {
        var (layer, nativeRole) = ExtractLayerAndRole(name);
        var standardRole = Standardize(name, nativeRole);
        var stage = profile.StageFor(standardRole, layer, layerCount);
        return new TensorPosition
        {
            Name = name,
            SourceFile = sourceFile,
            DType = dtype,
            Shape = shape,
            Layer = layer,
            NativeRole = nativeRole,
            StandardRole = standardRole,
            Stage = stage,
            DirectionRank = DirectionRank(layer, stage, standardRole, layerCount),
            PayloadOffset = payloadOffset,
            PayloadBytes = payloadBytes,
            StorageFormat = storageFormat,
        };
    }

    private static (int Layer, string NativeRole) ExtractLayerAndRole(string name)
    {
        foreach (var pattern in LayerPatterns)
        {
            var match = pattern.Match(name);
            if (!match.Success)
            {
                continue;
            }
            var role = name[(match.Index + match.Length)..].TrimStart('.');
            return (int.Parse(match.Groups[1].Value), string.IsNullOrWhiteSpace(role) ? "layer-root" : role);
        }
        return (-1, name);
    }

    private static string Standardize(string fullName, string nativeRole)
    {
        var lower = nativeRole.ToLowerInvariant();
        var full = fullName.ToLowerInvariant();
        if (lower.Contains("attn_qkv")) return Suffix("attention.qkv_combined_projection", nativeRole);
        if (lower.Contains("query_key_value") || lower.Contains("qkv_proj") || lower.Contains("wqkv")) return Suffix("attention.qkv_combined_projection", nativeRole);
        if (Regex.IsMatch(lower, @"(?:^|\.)attn_q(?:\.|$)")) return Suffix("attention.query_projection", nativeRole);
        if (Regex.IsMatch(lower, @"(?:^|\.)attn_k(?:\.|$)")) return Suffix("attention.key_projection", nativeRole);
        if (Regex.IsMatch(lower, @"(?:^|\.)attn_v(?:\.|$)")) return Suffix("attention.value_projection", nativeRole);
        if (lower.Contains("attn_output")) return Suffix("attention.output_projection", nativeRole);
        if (EndsComponent(lower, "q_proj") || lower.Contains("query.")) return Suffix("attention.query_projection", nativeRole);
        if (EndsComponent(lower, "k_proj") || lower.Contains("key.")) return Suffix("attention.key_projection", nativeRole);
        if (EndsComponent(lower, "v_proj") || lower.Contains("value.")) return Suffix("attention.value_projection", nativeRole);
        if (EndsComponent(lower, "o_proj") || lower.Contains("self_attention.dense") || lower.Contains("self_attn.dense") || lower.StartsWith("attention.dense")) return Suffix("attention.output_projection", nativeRole);
        if (lower.Contains("q_norm")) return Suffix("attention.query_normalization", nativeRole);
        if (lower.Contains("k_norm")) return Suffix("attention.key_normalization", nativeRole);
        if (lower.Contains("gate_proj")) return Suffix("feedforward.gate_projection", nativeRole);
        if (lower.Contains("ffn_gate")) return Suffix("feedforward.gate_projection", nativeRole);
        if (lower.Contains("up_proj")) return Suffix("feedforward.up_projection", nativeRole);
        if (lower.Contains("ffn_up")) return Suffix("feedforward.up_projection", nativeRole);
        if (lower.Contains("down_proj")) return Suffix("feedforward.down_projection", nativeRole);
        if (lower.Contains("ffn_down")) return Suffix("feedforward.down_projection", nativeRole);
        if (lower.Contains("dense_h_to_4h") || lower.Contains("mlp.fc1") || lower.StartsWith("fc1")) return Suffix("feedforward.input_projection", nativeRole);
        if (lower.Contains("dense_4h_to_h") || lower.Contains("mlp.fc2") || lower.StartsWith("fc2")) return Suffix("feedforward.output_projection", nativeRole);
        if (lower.Contains("input_layernorm") || lower.Contains("ln_1")) return Suffix("normalization.attention_input", nativeRole);
        if (lower.Contains("attn_norm")) return Suffix("normalization.attention_input", nativeRole);
        if (lower.Contains("post_attention_layernorm") || lower.Contains("ln_2")) return Suffix("normalization.feedforward_input", nativeRole);
        if (lower.Contains("ffn_norm")) return Suffix("normalization.feedforward_input", nativeRole);
        if (lower.Contains("post_feedforward_layernorm")) return Suffix("normalization.layer_output", nativeRole);
        if (lower.Contains("mixer.a_log")) return Suffix("state_space.transition_log", nativeRole);
        if (Regex.IsMatch(lower, @"(?:^|\.)mixer\.d(?:\.|$)")) return Suffix("state_space.skip_coefficient", nativeRole);
        if (lower.Contains("mixer.conv1d")) return Suffix("state_space.local_convolution", nativeRole);
        if (lower.Contains("mixer.dt_proj")) return Suffix("state_space.time_step_projection", nativeRole);
        if (lower.Contains("mixer.in_proj")) return Suffix("state_space.input_projection", nativeRole);
        if (lower.Contains("mixer.out_proj")) return Suffix("state_space.output_projection", nativeRole);
        if (lower.Contains("mixer.x_proj")) return Suffix("state_space.state_parameter_projection", nativeRole);
        if ((lower == "norm.weight" || lower.StartsWith("norm.")) && full.Contains("layers.")) return Suffix("normalization.block_input", nativeRole);
        if (full.Contains("embed_tokens") || full.Contains("embed_in") || full.Contains("word_embeddings") || full.Contains("wte.weight") || full.Contains("backbone.embeddings")) return Suffix("token_embedding", nativeRole);
        if (full.Contains("token_embd")) return Suffix("token_embedding", nativeRole);
        if (full.Contains("lm_head") || full.Contains("embed_out")) return Suffix("language_head", nativeRole);
        if (full == "output.weight" || full == "output.bias") return Suffix("language_head", nativeRole);
        if (full.Contains("final_layer_norm") || full.Contains("final_layernorm") || full.Contains("ln_f") || full.Contains("norm_f") || (full.EndsWith("model.norm.weight") || full.EndsWith("model.norm.bias"))) return Suffix("normalization.final", nativeRole);
        if (full.Contains("output_norm")) return Suffix("normalization.final", nativeRole);
        if (full.Contains("word_embeddings_layernorm") || full.Contains("emb_layer_norm")) return Suffix("normalization.embedding", nativeRole);
        if (lower.Contains("attention.bias")) return Suffix("attention.causal_mask.buffer", nativeRole);
        if (lower.Contains("attention.masked_bias")) return Suffix("attention.mask_fill.buffer", nativeRole);
        if (lower.Contains("rotary_emb.inv_freq")) return Suffix("position.rotary.inverse_frequency", nativeRole);
        return "native_unmapped." + nativeRole;
    }

    private static bool EndsComponent(string value, string component) => value.Contains(component, StringComparison.Ordinal);

    private static string Suffix(string prefix, string nativeRole)
    {
        if (nativeRole.EndsWith(".weight", StringComparison.OrdinalIgnoreCase)) return prefix + ".weight";
        if (nativeRole.EndsWith(".bias", StringComparison.OrdinalIgnoreCase)) return prefix + ".bias";
        return prefix;
    }

    private static long DirectionRank(int layer, int stage, string role, int layerCount)
    {
        if (role.StartsWith("token_embedding", StringComparison.Ordinal)) return 0;
        if (role.StartsWith("normalization.embedding", StringComparison.Ordinal)) return 10;
        if (layer >= 0 && stage >= 0) return 1000L + layer * 100L + stage;
        if (role.StartsWith("normalization.final", StringComparison.Ordinal)) return 1000L + layerCount * 100L + 80;
        if (role.StartsWith("language_head", StringComparison.Ordinal)) return 1000L + layerCount * 100L + 90;
        return -1;
    }
}

internal static class ArchitectureRegistry
{
    public static ArchitectureProfile Resolve(string modelType, string architectureName, JsonElement config)
    {
        var type = modelType.ToLowerInvariant();
        var parallel = JsonRead.Bool(config, "use_parallel_residual") ?? false;
        return type switch
        {
            "llama" or "mistral" or "mixtral" or "qwen2" or "qwen3" or "gemma" or "gemma2" or "deepseek_v2" or "deepseek_v3" => Transformer(type + "-decoder", "RMSNorm", "pre-sublayer", "sequential-attention-then-mlp"),
            "granite" => Transformer("granite-decoder", "RMSNorm", "pre-sublayer", "sequential-scaled-residual"),
            "gpt_neox" => Transformer("gpt-neox-decoder", "LayerNorm", "pre-sublayer", parallel ? "parallel-attention-plus-mlp" : "sequential-attention-then-mlp"),
            "phi" => Transformer("phi2-decoder", "LayerNorm", "pre-sublayer", "parallel-attention-plus-mlp"),
             "phi3" => Transformer("phi3-decoder", "RMSNorm", "pre-sublayer", "sequential-attention-then-mlp"),
             "phi3small" => Transformer("phi3small-blocksparse-decoder", "LayerNorm", "pre-sublayer", "sequential-attention-then-mlp"),
            "stablelm" => Transformer("stablelm-decoder", "LayerNorm", "pre-sublayer", parallel ? "parallel-attention-plus-mlp" : "sequential-attention-then-mlp"),
            "olmo2" => Transformer("olmo2-decoder", "RMSNorm", "post-sublayer-before-residual-add", "sequential-attention-then-mlp"),
            "bloom" => Transformer("bloom-decoder", "LayerNorm", "pre-sublayer", "sequential-attention-then-mlp"),
            "opt" => Transformer("opt-decoder", "LayerNorm", "mixed-by-config", "sequential-attention-then-mlp"),
            "falcon" => Transformer("falcon-decoder", "LayerNorm", "pre-sublayer", parallel ? "parallel-attention-plus-mlp" : "sequential-attention-then-mlp"),
            "bert" or "roberta" => Transformer(type + "-encoder", "LayerNorm", "post-sublayer", "sequential-attention-then-mlp", "encoder"),
            "t5" or "mt5" => Transformer(type + "-encoder-decoder", "RMSNorm", "pre-sublayer", "encoder-decoder-with-cross-attention", "encoder-decoder"),
            "mamba" or "falcon_mamba" => StateSpace(type + "-state-space"),
            _ => ArchitectureProfile.Unsupported(type, architectureName),
        };
    }

    private static ArchitectureProfile Transformer(string id, string norm, string placement, string residual, string topology = "decoder") => new()
    {
        Id = id,
        Supported = true,
        Topology = topology,
        NormalizationType = norm,
        NormalizationPlacement = placement,
        ResidualTopology = residual,
        StateTopology = "stateless-layer-stack",
        DirectionPolicy = residual.Contains("parallel", StringComparison.Ordinal) ? "parallel-transformer" : "sequential-transformer",
        Evidence = "adapter-family-reviewed",
    };

    private static ArchitectureProfile StateSpace(string id) => new()
    {
        Id = id,
        Supported = true,
        Topology = "decoder",
        NormalizationType = "RMSNorm",
        NormalizationPlacement = "pre-mixer",
        ResidualTopology = "sequential-state-space-residual",
        StateTopology = "stateful-recurrent",
        DirectionPolicy = "state-space",
        Evidence = "adapter-family-reviewed",
    };
}

internal sealed class ArchitectureProfile
{
    public string Id { get; set; } = "unsupported";
    public bool Supported { get; set; }
    public string Topology { get; set; } = "unknown";
    public string NormalizationType { get; set; } = "unverified";
    public string NormalizationPlacement { get; set; } = "unverified";
    public string ResidualTopology { get; set; } = "unverified";
    public string StateTopology { get; set; } = "unverified";
    public string DirectionPolicy { get; set; } = "fail-closed";
    public string Evidence { get; set; } = "unverified";
    public string? UnsupportedReason { get; set; }

    public static ArchitectureProfile Unsupported(string modelType, string architectureName) => new()
    {
        Id = "unsupported-" + modelType,
        Supported = false,
        Topology = "unknown",
        UnsupportedReason = $"No reviewed adapter for model_type={modelType}, architecture={architectureName}",
    };

    public int StageFor(string role, int layer, int layerCount)
    {
        if (role.StartsWith("token_embedding", StringComparison.Ordinal)) return 0;
        if (role.StartsWith("normalization.embedding", StringComparison.Ordinal)) return 5;
        if (role.StartsWith("normalization.final", StringComparison.Ordinal)) return 80;
        if (role.StartsWith("language_head", StringComparison.Ordinal)) return 90;
        if (layer < 0 || !Supported) return -1;
        if (DirectionPolicy == "state-space")
        {
            if (role.StartsWith("normalization.block_input", StringComparison.Ordinal)) return 10;
            if (role.StartsWith("state_space.input_projection", StringComparison.Ordinal)) return 20;
            if (role.StartsWith("state_space.local_convolution", StringComparison.Ordinal)) return 30;
            if (role.StartsWith("state_space.time_step_projection", StringComparison.Ordinal) || role.StartsWith("state_space.state_parameter_projection", StringComparison.Ordinal) || role.StartsWith("state_space.transition_log", StringComparison.Ordinal) || role.StartsWith("state_space.skip_coefficient", StringComparison.Ordinal)) return 40;
            if (role.StartsWith("state_space.output_projection", StringComparison.Ordinal)) return 50;
            return -1;
        }

        var parallel = DirectionPolicy == "parallel-transformer";
        if (role.StartsWith("normalization.attention_input", StringComparison.Ordinal) || role.StartsWith("normalization.block_input", StringComparison.Ordinal)) return 10;
        if (role.StartsWith("attention.query", StringComparison.Ordinal) || role.StartsWith("attention.key", StringComparison.Ordinal) || role.StartsWith("attention.value", StringComparison.Ordinal) || role.StartsWith("attention.qkv", StringComparison.Ordinal) || role.StartsWith("position.rotary", StringComparison.Ordinal)) return 20;
        if (role.StartsWith("attention.output_projection", StringComparison.Ordinal)) return 30;
        if (role.StartsWith("normalization.feedforward_input", StringComparison.Ordinal)) return parallel ? 10 : 40;
        if (role.StartsWith("feedforward.gate_projection", StringComparison.Ordinal) || role.StartsWith("feedforward.up_projection", StringComparison.Ordinal) || role.StartsWith("feedforward.input_projection", StringComparison.Ordinal)) return parallel ? 20 : 50;
        if (role.StartsWith("feedforward.down_projection", StringComparison.Ordinal) || role.StartsWith("feedforward.output_projection", StringComparison.Ordinal)) return parallel ? 30 : 60;
        if (role.StartsWith("normalization.layer_output", StringComparison.Ordinal)) return 70;
        return -1;
    }
}

internal static class DirectionalComparer
{
    public static DirectionalCompareResult Compare(ModelMap map, string fromQuery, string toQuery, int maxPaths)
    {
        var baseResult = new DirectionalCompareResult
        {
            Schema = "haiyu-directional-compare-result/v1",
            GeneratedAt = DateTimeOffset.Now,
            OwnerTaiji = Receipt.OwnerTaiji(),
            ModelId = map.ModelId,
            AdapterId = map.Adapter.Id,
            FromQuery = fromQuery,
            ToQuery = toQuery,
            ReadOnly = true,
            FullWeightLoaded = false,
            GpuUsed = false,
            NetworkUsed = false,
            OriginalWeightsModified = false,
            Authority = Receipt.Authority,
        };

        if (!map.Adapter.Supported)
        {
            baseResult.Supported = false;
            baseResult.Status = "fail-closed-unsupported-architecture";
            baseResult.Notes.Add(map.Adapter.UnsupportedReason ?? "unsupported architecture");
            return baseResult;
        }
        if (map.Tensors.Count == 0)
        {
            baseResult.Supported = false;
            baseResult.Status = "fail-closed-no-tensor-metadata";
            return baseResult;
        }

        var from = Resolve(map.Tensors, fromQuery);
        var to = Resolve(map.Tensors, toQuery);
        baseResult.FromCandidateCount = from.Count;
        baseResult.ToCandidateCount = to.Count;
        if (from.Count == 0 || to.Count == 0)
        {
            baseResult.Supported = false;
            baseResult.Status = "fail-closed-endpoint-not-found";
            if (from.Count == 0) baseResult.Notes.Add("from endpoint not found");
            if (to.Count == 0) baseResult.Notes.Add("to endpoint not found");
            return baseResult;
        }

        var forward = new List<DirectionalPath>();
        var reverse = new List<DirectionalPath>();
        var ties = 0;
        foreach (var left in from)
        {
            foreach (var right in to)
            {
                if (left.DirectionRank < 0 || right.DirectionRank < 0)
                {
                    ties++;
                    continue;
                }
                if (left.DirectionRank < right.DirectionRank)
                {
                    forward.Add(Path(left, right, right.DirectionRank - left.DirectionRank, "native-forward"));
                }
                else if (left.DirectionRank > right.DirectionRank)
                {
                    reverse.Add(Path(left, right, left.DirectionRank - right.DirectionRank, "native-reverse"));
                }
                else
                {
                    ties++;
                }
            }
        }

        var comparable = forward.Count + reverse.Count;
        baseResult.Supported = comparable > 0;
        baseResult.Status = comparable == 0 ? "undetermined-same-stage-or-unmapped" : "ok";
        baseResult.ForwardPairCount = forward.Count;
        baseResult.ReversePairCount = reverse.Count;
        baseResult.UndeterminedPairCount = ties;
        baseResult.DirectionScore = comparable == 0 ? 0 : (double)forward.Count / comparable;
        baseResult.ReverseScore = comparable == 0 ? 0 : (double)reverse.Count / comparable;
        baseResult.Decision = baseResult.DirectionScore > baseResult.ReverseScore ? "from-to" : baseResult.ReverseScore > baseResult.DirectionScore ? "to-from" : "undetermined";
        baseResult.Paths = (baseResult.Decision == "from-to" ? forward : reverse)
            .OrderBy(path => path.Distance)
            .ThenBy(path => path.FromTensor, StringComparer.Ordinal)
            .Take(maxPaths)
            .ToList();
        baseResult.Notes.Add("directionScore is structural candidate-pair support, not semantic accuracy");
        return baseResult;
    }

    private static List<TensorPosition> Resolve(List<TensorPosition> tensors, string query)
    {
        var (baseQuery, layer) = ParseLayer(query);
        var exact = tensors.Where(tensor => tensor.Name.Equals(baseQuery, StringComparison.Ordinal)).ToList();
        var source = exact.Count > 0 ? exact : tensors.Where(tensor => RoleMatches(tensor.StandardRole, baseQuery)).ToList();
        if (layer.HasValue)
        {
            source = source.Where(tensor => tensor.Layer == layer.Value).ToList();
        }
        return source;
    }

    private static (string Query, int? Layer) ParseLayer(string query)
    {
        var match = Regex.Match(query, @"^(.*)@(\d+)$");
        return match.Success ? (match.Groups[1].Value, int.Parse(match.Groups[2].Value)) : (query, null);
    }

    private static bool RoleMatches(string role, string query) => role.Equals(query, StringComparison.OrdinalIgnoreCase)
        || role.StartsWith(query + ".", StringComparison.OrdinalIgnoreCase);

    private static DirectionalPath Path(TensorPosition left, TensorPosition right, long distance, string direction) => new()
    {
        FromTensor = left.Name,
        ToTensor = right.Name,
        FromRole = left.StandardRole,
        ToRole = right.StandardRole,
        FromLayer = left.Layer,
        ToLayer = right.Layer,
        Direction = direction,
        Distance = distance,
    };
}

internal static class DirectedPayloadComparer
{
    public const string Authority = "directional-payload-sample-only; structural route and sampled numeric relation are observed, semantic meaning and answer correctness remain unproven";

    public static DirectedPayloadCompareResult Measure(ModelMap map, string fromQuery, string toQuery, int maxPairs, int sampleElements)
    {
        var stopwatch = Stopwatch.StartNew();
        var structural = DirectionalComparer.Compare(map, fromQuery, toQuery, maxPairs);
        var result = new DirectedPayloadCompareResult
        {
            Schema = "haiyu-directed-payload-compare/v1",
            ProductVersion = Program.ProductVersion,
            GeneratedAt = DateTimeOffset.Now,
            OwnerTaiji = Receipt.OwnerTaiji(),
            ModelId = map.ModelId,
            AdapterId = map.Adapter.Id,
            FromQuery = fromQuery,
            ToQuery = toQuery,
            StructuralDecision = structural.Decision,
            StructuralStatus = structural.Status,
            RequestedSampleElementsPerTensor = sampleElements,
            ReadOnly = true,
            FullWeightLoaded = false,
            GpuUsed = false,
            NetworkUsed = false,
            OriginalWeightsModified = false,
            Authority = Authority,
        };

        if (!structural.Supported)
        {
            result.Status = "fail-closed-structural-route-unavailable";
            result.Notes.AddRange(structural.Notes);
            result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        var payloadTensors = map.Tensors.Where(tensor => tensor.PayloadBytes > 0).ToList();
        result.TotalModelPayloadBytes = payloadTensors
            .GroupBy(tensor => (tensor.SourceFile, tensor.PayloadOffset, tensor.PayloadBytes))
            .Sum(group => group.Key.PayloadBytes);
        if (payloadTensors.Count == 0)
        {
            result.Status = map.ArtifactPath.Length > 0
                ? "fail-closed-quantized-or-opaque-payload"
                : "fail-closed-payload-offsets-unavailable-rescan-with-v0.3";
            result.Notes.Add("结构方向仍可用；真实浮点测量需要 v0.3 重新扫描得到 safetensors 数据偏移。GGUF 量化块本版不做伪浮点解码。");
            result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        var selected = new HashSet<(string SourceFile, long PayloadOffset, long PayloadBytes)>();
        foreach (var path in structural.Paths.Take(maxPairs))
        {
            var left = map.Tensors.FirstOrDefault(tensor => tensor.Name.Equals(path.FromTensor, StringComparison.Ordinal));
            var right = map.Tensors.FirstOrDefault(tensor => tensor.Name.Equals(path.ToTensor, StringComparison.Ordinal));
            if (left is null || right is null)
            {
                result.Notes.Add($"路径张量未回找到：{path.FromTensor} -> {path.ToTensor}");
                continue;
            }
            var leftReadable = CanRead(left, out var leftReason);
            var rightReadable = CanRead(right, out var rightReason);
            if (!leftReadable || !rightReadable)
            {
                result.Notes.Add($"跳过不可数值采样路径：{path.FromTensor} ({leftReason}) -> {path.ToTensor} ({rightReason})");
                continue;
            }

            try
            {
                var leftSample = ReadDirectedSample(map, left, sampleElements);
                var rightSample = ReadDirectedSample(map, right, sampleElements);
                var pair = CompareSamples(path, left, right, leftSample, rightSample);
                result.Pairs.Add(pair);
                result.ActualPayloadBytesRead += leftSample.BytesRead + rightSample.BytesRead;
                selected.Add((left.SourceFile, left.PayloadOffset, left.PayloadBytes));
                selected.Add((right.SourceFile, right.PayloadOffset, right.PayloadBytes));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
            {
                result.Notes.Add($"定向采样失败：{path.FromTensor} -> {path.ToTensor}: {exception.Message}");
            }
        }

        result.SelectedRoutePayloadBytes = selected.Sum(item => item.PayloadBytes);
        result.RoutePruningRatio = RatioAvoided(result.SelectedRoutePayloadBytes, result.TotalModelPayloadBytes);
        result.SampleAvoidanceWithinRoute = RatioAvoided(result.ActualPayloadBytesRead, result.SelectedRoutePayloadBytes);
        result.EndToEndByteAvoidance = RatioAvoided(result.ActualPayloadBytesRead, result.TotalModelPayloadBytes);
        result.PairCount = result.Pairs.Count;
        result.Supported = result.PairCount > 0;
        result.Status = result.Supported ? "ok-directed-payload-sampled" : "fail-closed-no-readable-directed-pair";
        if (result.Supported)
        {
            result.MeanCosineSimilarity = result.Pairs.Average(pair => pair.CosineSimilarity);
            result.MeanSignAgreement = result.Pairs.Average(pair => pair.SignAgreement);
            result.Notes.Add("字节规避率描述本次读取范围，不等于端到端速度倍数；性能需在客户真实任务上做同口径 A/B。 ");
        }
        stopwatch.Stop();
        result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        return result;
    }

    internal static bool CanRead(TensorPosition tensor, out string reason)
    {
        if (!tensor.StorageFormat.Equals("safetensors", StringComparison.OrdinalIgnoreCase))
        {
            reason = "storage-format-not-readable:" + tensor.StorageFormat;
            return false;
        }
        if (tensor.PayloadOffset < 0 || tensor.PayloadBytes <= 0)
        {
            reason = "payload-offset-unavailable";
            return false;
        }
        if (ElementSize(tensor.DType) == 0)
        {
            reason = "dtype-not-supported:" + tensor.DType;
            return false;
        }
        reason = "ok";
        return true;
    }

    internal static NumericSample ReadDirectedSample(ModelMap map, TensorPosition tensor, int requestedElements)
    {
        var elementSize = ElementSize(tensor.DType);
        var totalElements = tensor.PayloadBytes / elementSize;
        if (totalElements <= 0 || tensor.PayloadBytes % elementSize != 0)
        {
            throw new InvalidDataException($"张量字节数与 dtype 不匹配：{tensor.Name}");
        }
        var plan = BuildSamplePlan(totalElements, requestedElements);
        var values = new List<double>(plan.Sum(window => window.ElementCount));
        long bytesRead = 0;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(ResolveTensorSource(map, tensor), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
        foreach (var window in plan)
        {
            var byteCount = checked(window.ElementCount * elementSize);
            var buffer = new byte[byteCount];
            stream.Position = checked(tensor.PayloadOffset + window.StartElement * elementSize);
            stream.ReadExactly(buffer);
            digest.AppendData(buffer);
            bytesRead += buffer.Length;
            Decode(buffer, tensor.DType, values);
        }
        return new NumericSample(values, bytesRead, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), plan.Count);
    }

    internal static NumericSample ReadSequentialSample(ModelMap map, TensorPosition tensor, int requestedElements)
    {
        var elementSize = ElementSize(tensor.DType);
        var totalElements = tensor.PayloadBytes / elementSize;
        if (totalElements <= 0 || tensor.PayloadBytes % elementSize != 0)
        {
            throw new InvalidDataException($"张量字节数与 dtype 不匹配：{tensor.Name}");
        }
        var plan = BuildSamplePlan(totalElements, requestedElements);
        var values = new List<double>(plan.Sum(window => window.ElementCount));
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(ResolveTensorSource(map, tensor), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        stream.Position = tensor.PayloadOffset;
        long traversed = 0;
        var discard = new byte[1024 * 1024];
        foreach (var window in plan)
        {
            var windowOffset = checked(window.StartElement * elementSize);
            ReadAndDiscard(stream, checked(windowOffset - traversed), discard);
            traversed = windowOffset;
            var byteCount = checked(window.ElementCount * elementSize);
            var buffer = new byte[byteCount];
            stream.ReadExactly(buffer);
            traversed += buffer.Length;
            digest.AppendData(buffer);
            Decode(buffer, tensor.DType, values);
        }
        ReadAndDiscard(stream, checked(tensor.PayloadBytes - traversed), discard);
        return new NumericSample(values, tensor.PayloadBytes, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), plan.Count);
    }

    private static List<SampleWindow> BuildSamplePlan(long totalElements, int requestedElements)
    {
        var target = checked((int)Math.Min(totalElements, requestedElements));
        var windowCount = Math.Min(32, target);
        var windowElements = Math.Max(1, (int)Math.Ceiling(target / (double)windowCount));
        var plan = new List<SampleWindow>(windowCount);
        var planned = 0;
        for (var window = 0; window < windowCount && planned < target; window++)
        {
            var count = Math.Min(windowElements, target - planned);
            var maxStart = Math.Max(0, totalElements - count);
            var startElement = windowCount == 1 ? 0 : (long)Math.Round(window * maxStart / (double)(windowCount - 1));
            plan.Add(new SampleWindow(startElement, count));
            planned += count;
        }
        return plan;
    }

    internal static string ResolveTensorSource(ModelMap map, TensorPosition tensor)
    {
        var root = Path.GetFullPath(map.ModelRoot);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var source = Path.GetFullPath(Path.Combine(root, tensor.SourceFile));
        if (!source.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("张量源文件越出模型根目录");
        }
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("张量源文件不存在", source);
        }
        return source;
    }

    private static void ReadAndDiscard(Stream stream, long byteCount, byte[] buffer)
    {
        if (byteCount < 0) throw new InvalidDataException("顺序读取窗口发生重叠或倒序");
        var remaining = byteCount;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(remaining, buffer.Length);
            var read = stream.Read(buffer, 0, requested);
            if (read == 0) throw new EndOfStreamException();
            remaining -= read;
        }
    }

    private static DirectedPayloadPair CompareSamples(
        DirectionalPath path,
        TensorPosition left,
        TensorPosition right,
        NumericSample leftSample,
        NumericSample rightSample)
    {
        var count = Math.Min(leftSample.Values.Count, rightSample.Values.Count);
        double dot = 0;
        double leftSquared = 0;
        double rightSquared = 0;
        double absoluteDelta = 0;
        double squaredDelta = 0;
        var signs = 0;
        var finite = 0;
        for (var index = 0; index < count; index++)
        {
            var a = leftSample.Values[index];
            var b = rightSample.Values[index];
            if (!double.IsFinite(a) || !double.IsFinite(b)) continue;
            finite++;
            dot += a * b;
            leftSquared += a * a;
            rightSquared += b * b;
            var delta = a - b;
            absoluteDelta += Math.Abs(delta);
            squaredDelta += delta * delta;
            if (Math.Sign(a) == Math.Sign(b)) signs++;
        }
        var denominator = Math.Sqrt(leftSquared) * Math.Sqrt(rightSquared);
        return new DirectedPayloadPair
        {
            FromTensor = left.Name,
            ToTensor = right.Name,
            FromRole = left.StandardRole,
            ToRole = right.StandardRole,
            Direction = path.Direction,
            Distance = path.Distance,
            FromDType = left.DType,
            ToDType = right.DType,
            FromPayloadBytes = left.PayloadBytes,
            ToPayloadBytes = right.PayloadBytes,
            SampledElementPairs = finite,
            CosineSimilarity = finite == 0 || denominator == 0 ? 0 : dot / denominator,
            MeanAbsoluteDelta = finite == 0 ? 0 : absoluteDelta / finite,
            RootMeanSquareDelta = finite == 0 ? 0 : Math.Sqrt(squaredDelta / finite),
            SignAgreement = finite == 0 ? 0 : signs / (double)finite,
            FromSampleSha256 = leftSample.Sha256,
            ToSampleSha256 = rightSample.Sha256,
            BytesRead = leftSample.BytesRead + rightSample.BytesRead,
        };
    }

    internal static int ElementSize(string dtype) => dtype.ToUpperInvariant() switch
    {
        "F64" or "I64" or "U64" => 8,
        "F32" or "I32" or "U32" => 4,
        "F16" or "BF16" or "I16" or "U16" => 2,
        "I8" or "U8" or "BOOL" => 1,
        _ => 0,
    };

    private static void Decode(byte[] bytes, string dtype, List<double> output)
    {
        var upper = dtype.ToUpperInvariant();
        var size = ElementSize(upper);
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            var span = bytes.AsSpan(offset, size);
            output.Add(upper switch
            {
                "F64" => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(span)),
                "F32" => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span)),
                "F16" => (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(span)),
                "BF16" => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadUInt16LittleEndian(span) << 16),
                "I64" => BinaryPrimitives.ReadInt64LittleEndian(span),
                "U64" => BinaryPrimitives.ReadUInt64LittleEndian(span),
                "I32" => BinaryPrimitives.ReadInt32LittleEndian(span),
                "U32" => BinaryPrimitives.ReadUInt32LittleEndian(span),
                "I16" => BinaryPrimitives.ReadInt16LittleEndian(span),
                "U16" => BinaryPrimitives.ReadUInt16LittleEndian(span),
                "I8" => unchecked((sbyte)span[0]),
                "U8" => span[0],
                "BOOL" => span[0] == 0 ? 0 : 1,
                _ => throw new InvalidDataException("不支持的 dtype：" + dtype),
            });
        }
    }

    internal static double RatioAvoided(long selected, long total) => total <= 0 ? 0 : Math.Clamp(1.0 - selected / (double)total, 0, 1);

    internal sealed record NumericSample(List<double> Values, long BytesRead, string Sha256, int Windows);
    private sealed record SampleWindow(long StartElement, int ElementCount);
}

internal static class RobustnessAudit
{
    public const string Authority = "local directional-extraction robustness only; scale determinism, dtype decoding, in-memory perturbation response, layer coverage, and fail-closed behavior are observed; semantic correctness and full-model inference replacement remain unproven";

    public static RobustnessAuditResult Run(
        ModelMap map,
        string fromQuery,
        string toQuery,
        int maxPairs,
        IReadOnlyList<int> sampleScales,
        IReadOnlyList<double> noiseLevels,
        int seed)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new RobustnessAuditResult
        {
            Schema = "haiyu-directional-robustness-audit/v1",
            ProductVersion = Program.ProductVersion,
            GeneratedAt = DateTimeOffset.Now,
            OwnerTaiji = Receipt.OwnerTaiji(),
            ModelId = map.ModelId,
            AdapterId = map.Adapter.Id,
            FromQuery = fromQuery,
            ToQuery = toQuery,
            ReadOnly = true,
            FullWeightLoaded = false,
            GpuUsed = false,
            NetworkUsed = false,
            OriginalWeightsModified = false,
            Seed = seed,
            Authority = Authority,
        };

        var structural = DirectionalComparer.Compare(map, fromQuery, toQuery, Math.Max(32, maxPairs));
        result.StructuralStatus = structural.Status;
        result.StructuralDecision = structural.Decision;
        if (!structural.Supported)
        {
            result.Status = "fail-closed-structural-route-unavailable";
            result.Notes.AddRange(structural.Notes);
            result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        var pairs = new List<(DirectionalPath Path, TensorPosition Left, TensorPosition Right)>();
        foreach (var path in structural.Paths.Take(maxPairs))
        {
            var left = map.Tensors.FirstOrDefault(tensor => tensor.Name.Equals(path.FromTensor, StringComparison.Ordinal));
            var right = map.Tensors.FirstOrDefault(tensor => tensor.Name.Equals(path.ToTensor, StringComparison.Ordinal));
            if (left is null || right is null) continue;
            var leftReadable = DirectedPayloadComparer.CanRead(left, out var leftReason);
            var rightReadable = DirectedPayloadComparer.CanRead(right, out var rightReason);
            if (!leftReadable || !rightReadable)
            {
                result.Notes.Add($"跳过不可读取路线：{left.Name} ({leftReason}) -> {right.Name} ({rightReason})");
                continue;
            }
            pairs.Add((path, left, right));
        }

        if (pairs.Count == 0)
        {
            result.Status = "fail-closed-no-readable-directed-pair";
            result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        var precisionTensors = map.Tensors
            .Where(tensor => DirectedPayloadComparer.CanRead(tensor, out _))
            .GroupBy(tensor => tensor.DType, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var watchedSources = pairs
            .SelectMany(pair => new[] { pair.Left, pair.Right })
            .Concat(precisionTensors)
            .Select(tensor => DirectedPayloadComparer.ResolveTensorSource(map, tensor))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, SourceSnapshot.Capture, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pairs)
        {
            foreach (var scale in sampleScales)
            {
                var firstLeft = DirectedPayloadComparer.ReadDirectedSample(map, pair.Left, scale);
                var secondLeft = DirectedPayloadComparer.ReadDirectedSample(map, pair.Left, scale);
                var firstRight = DirectedPayloadComparer.ReadDirectedSample(map, pair.Right, scale);
                var secondRight = DirectedPayloadComparer.ReadDirectedSample(map, pair.Right, scale);
                result.ScaleCases.Add(new RobustnessScaleCase
                {
                    FromTensor = pair.Left.Name,
                    ToTensor = pair.Right.Name,
                    FromDType = pair.Left.DType,
                    ToDType = pair.Right.DType,
                    RequestedElementsPerTensor = scale,
                    ActualFromElements = firstLeft.Values.Count,
                    ActualToElements = firstRight.Values.Count,
                    BytesRead = firstLeft.BytesRead + secondLeft.BytesRead + firstRight.BytesRead + secondRight.BytesRead,
                    FromDigest = firstLeft.Sha256,
                    ToDigest = firstRight.Sha256,
                    Deterministic = firstLeft.Sha256 == secondLeft.Sha256
                        && firstRight.Sha256 == secondRight.Sha256
                        && firstLeft.Values.Count == secondLeft.Values.Count
                        && firstRight.Values.Count == secondRight.Values.Count,
                });
            }
        }

        var maxScale = sampleScales.Max();
        foreach (var tensor in precisionTensors)
        {
            try
            {
                var sample = DirectedPayloadComparer.ReadDirectedSample(map, tensor, Math.Min(maxScale, 4096));
                result.PrecisionCases.Add(new RobustnessPrecisionCase
                {
                    DType = tensor.DType,
                    Tensor = tensor.Name,
                    SampledElements = sample.Values.Count,
                    BytesRead = sample.BytesRead,
                    FiniteElements = sample.Values.Count(double.IsFinite),
                    DecodeSucceeded = sample.Values.Count > 0 && sample.Values.All(double.IsFinite),
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
            {
                result.PrecisionCases.Add(new RobustnessPrecisionCase
                {
                    DType = tensor.DType,
                    Tensor = tensor.Name,
                    DecodeSucceeded = false,
                    Error = exception.Message,
                });
            }
        }

        var noisePairIndex = 0;
        foreach (var pair in pairs)
        {
            var tensors = new[] { pair.Left, pair.Right };
            foreach (var tensor in tensors)
            {
                var sample = DirectedPayloadComparer.ReadDirectedSample(map, tensor, Math.Min(maxScale, 4096));
                var finite = sample.Values.Where(double.IsFinite).ToArray();
                if (finite.Length == 0) continue;
                var random = new Random(unchecked(seed + noisePairIndex * 7919));
                var direction = finite.Select(_ => random.NextDouble() * 2.0 - 1.0).ToArray();
                foreach (var level in noiseLevels)
                {
                    result.NoiseCases.Add(MeasureNoise(tensor, finite, direction, level));
                }
                noisePairIndex++;
            }
        }

        var rolePair = pairs[0];
        var commonLayers = map.Tensors
            .Where(tensor => RoleMatches(tensor, rolePair.Left.StandardRole))
            .Select(tensor => tensor.Layer)
            .Intersect(map.Tensors.Where(tensor => RoleMatches(tensor, rolePair.Right.StandardRole)).Select(tensor => tensor.Layer))
            .Distinct()
            .OrderBy(layer => layer)
            .ToArray();
        foreach (var layer in PickBoundaryLayers(commonLayers))
        {
            var layerResult = DirectionalComparer.Compare(
                map,
                rolePair.Left.StandardRole + "@" + layer,
                rolePair.Right.StandardRole + "@" + layer,
                8);
            result.LayerCoverage.Add(new RobustnessLayerCase
            {
                Layer = layer,
                FromRole = rolePair.Left.StandardRole,
                ToRole = rolePair.Right.StandardRole,
                Supported = layerResult.Supported,
                Decision = layerResult.Decision,
                Status = layerResult.Status,
            });
        }

        result.FaultCases.Add(FaultCase("malformed-map-json", () =>
        {
            _ = JsonSerializer.Deserialize<ModelMap>("{", new JsonSerializerOptions());
        }, typeof(JsonException)));
        result.FaultCases.Add(FaultCase("out-of-root-source", () =>
        {
            var invalid = CloneTensor(pairs[0].Left);
            invalid.SourceFile = ".." + Path.DirectorySeparatorChar + "outside.safetensors";
            _ = DirectedPayloadComparer.ReadDirectedSample(map, invalid, 128);
        }, typeof(InvalidDataException)));
        result.FaultCases.Add(new RobustnessFaultCase
        {
            Name = "unsupported-dtype",
            Rejected = !DirectedPayloadComparer.CanRead(new TensorPosition
            {
                StorageFormat = "safetensors",
                PayloadOffset = 0,
                PayloadBytes = 64,
                DType = "Q4_K",
            }, out _),
            ExpectedError = nameof(InvalidDataException),
            ActualError = "fail-closed-can-read-false",
        });
        result.FaultCases.Add(FaultCase("payload-past-end", () =>
        {
            var invalid = CloneTensor(pairs[0].Left);
            var sourceLength = new FileInfo(DirectedPayloadComparer.ResolveTensorSource(map, invalid)).Length;
            invalid.PayloadOffset = sourceLength + 1;
            invalid.PayloadBytes = Math.Max(invalid.PayloadBytes, 256);
            _ = DirectedPayloadComparer.ReadDirectedSample(map, invalid, 128);
        }, typeof(EndOfStreamException)));

        result.SourceSnapshotsUnchanged = watchedSources.All(entry => entry.Value.Matches(SourceSnapshot.Capture(entry.Key)));
        result.ScaleDeterminismPassed = result.ScaleCases.Count > 0 && result.ScaleCases.All(item => item.Deterministic);
        result.PrecisionCoveragePassed = result.PrecisionCases.Count > 0 && result.PrecisionCases.All(item => item.DecodeSucceeded);
        result.NoiseResponsePassed = NoiseResponsePassed(result.NoiseCases);
        result.LayerCoveragePassed = result.LayerCoverage.Count > 0 && result.LayerCoverage.All(item => item.Supported);
        result.FaultClosedPassed = result.FaultCases.Count >= 4 && result.FaultCases.All(item => item.Rejected);
        result.Passed = result.ScaleDeterminismPassed
            && result.PrecisionCoveragePassed
            && result.NoiseResponsePassed
            && result.LayerCoveragePassed
            && result.FaultClosedPassed
            && result.SourceSnapshotsUnchanged;
        result.Status = result.Passed ? "ok-local-robustness-gate" : "fail-closed-robustness-gate";
        result.Notes.Add("噪声仅施加于内存副本，用于观察数值比较响应；原始权重从未写入。 ");
        result.Notes.Add("本门不把数值稳定性等同于语义正确率，也不宣称已替代完整模型前向。 ");
        stopwatch.Stop();
        result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        return result;
    }

    private static RobustnessNoiseCase MeasureNoise(TensorPosition tensor, IReadOnlyList<double> values, IReadOnlyList<double> direction, double level)
    {
        double dot = 0;
        double sourceSquared = 0;
        double perturbedSquared = 0;
        double squaredDelta = 0;
        var signs = 0;
        for (var index = 0; index < values.Count; index++)
        {
            var source = values[index];
            var scale = Math.Max(Math.Abs(source), 1e-12);
            var perturbed = source + direction[index] * level * scale;
            dot += source * perturbed;
            sourceSquared += source * source;
            perturbedSquared += perturbed * perturbed;
            var delta = source - perturbed;
            squaredDelta += delta * delta;
            if (Math.Sign(source) == Math.Sign(perturbed)) signs++;
        }
        var denominator = Math.Sqrt(sourceSquared) * Math.Sqrt(perturbedSquared);
        var cosineSimilarity = denominator == 0 ? 1 : dot / denominator;
        var rootMeanSquareDelta = Math.Sqrt(squaredDelta / values.Count);
        var signAgreement = signs / (double)values.Count;
        return new RobustnessNoiseCase
        {
            Tensor = tensor.Name,
            DType = tensor.DType,
            NoiseLevel = level,
            Elements = values.Count,
            CosineSimilarity = cosineSimilarity,
            RootMeanSquareDelta = rootMeanSquareDelta,
            SignAgreement = signAgreement,
            Finite = double.IsFinite(dot)
                && double.IsFinite(squaredDelta)
                && double.IsFinite(cosineSimilarity)
                && double.IsFinite(rootMeanSquareDelta)
                && double.IsFinite(signAgreement),
        };
    }

    private static bool NoiseResponsePassed(IEnumerable<RobustnessNoiseCase> cases)
    {
        var groups = cases.GroupBy(item => item.Tensor, StringComparer.Ordinal);
        var any = false;
        foreach (var group in groups)
        {
            any = true;
            var ordered = group.OrderBy(item => item.NoiseLevel).ToArray();
            if (ordered.Any(item => !item.Finite)) return false;
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index].RootMeanSquareDelta + 1e-18 < ordered[index - 1].RootMeanSquareDelta) return false;
            }
        }
        return any;
    }

    private static IEnumerable<int> PickBoundaryLayers(IReadOnlyList<int> layers)
    {
        if (layers.Count == 0) return Array.Empty<int>();
        return new[] { layers[0], layers[layers.Count / 2], layers[^1] }.Distinct();
    }

    private static bool RoleMatches(TensorPosition tensor, string role) => tensor.StandardRole.Equals(role, StringComparison.OrdinalIgnoreCase)
        || tensor.StandardRole.StartsWith(role + ".", StringComparison.OrdinalIgnoreCase);

    private static TensorPosition CloneTensor(TensorPosition source) => new()
    {
        Name = source.Name,
        SourceFile = source.SourceFile,
        DType = source.DType,
        Shape = source.Shape.ToArray(),
        Layer = source.Layer,
        NativeRole = source.NativeRole,
        StandardRole = source.StandardRole,
        Stage = source.Stage,
        DirectionRank = source.DirectionRank,
        PayloadOffset = source.PayloadOffset,
        PayloadBytes = source.PayloadBytes,
        StorageFormat = source.StorageFormat,
    };

    private static RobustnessFaultCase FaultCase(string name, Action action, Type expected)
    {
        try
        {
            action();
            return new RobustnessFaultCase { Name = name, ExpectedError = expected.Name, ActualError = "none", Rejected = false };
        }
        catch (Exception exception)
        {
            return new RobustnessFaultCase
            {
                Name = name,
                ExpectedError = expected.Name,
                ActualError = exception.GetType().Name,
                Rejected = expected.IsAssignableFrom(exception.GetType()),
            };
        }
    }

    private sealed record SourceSnapshot(long Length, DateTime LastWriteTimeUtc)
    {
        public static SourceSnapshot Capture(string path)
        {
            var info = new FileInfo(path);
            return new SourceSnapshot(info.Length, info.LastWriteTimeUtc);
        }

        public bool Matches(SourceSnapshot other) => Length == other.Length && LastWriteTimeUtc == other.LastWriteTimeUtc;
    }
}

internal static class RuntimeComparisonOrgan
{
    public const string Authority = "runtime selected-causal-position comparison organ only; readiness identity, sampled-byte equivalence and failover are observed, while semantic correctness, generation and full-model inference replacement remain unproven";

    public static RuntimeComparisonResult Run(
        ModelMap map,
        string readinessPath,
        string fromQuery,
        string toQuery,
        int maxPairs,
        int sampleElements,
        bool verifyBaseline,
        bool allowBaselineFallback,
        string requestId,
        string injectFault)
    {
        var result = new RuntimeComparisonResult
        {
            Schema = "haiyu-directional-runtime-receipt/v1",
            ProductVersion = Program.ProductVersion,
            GeneratedAt = DateTimeOffset.Now,
            RequestId = requestId,
            OwnerTaiji = Receipt.OwnerTaiji(),
            ModelId = map.ModelId,
            AdapterId = map.Adapter.Id,
            FromQuery = fromQuery,
            ToQuery = toQuery,
            VerifyBaselineRequested = verifyBaseline,
            BaselineFallbackAllowed = allowBaselineFallback,
            ReadOnly = true,
            FullWeightLoaded = false,
            GpuUsed = false,
            NetworkUsed = false,
            OriginalWeightsModified = false,
            Authority = Authority,
        };

        var normalizedFault = injectFault.Trim().ToLowerInvariant();
        if (normalizedFault is not ("none" or "directional-error" or "digest-mismatch"))
        {
            result.Mode = "fail-closed";
            result.Status = "fail-closed-invalid-fault-mode";
            result.Notes.Add("inject-fault 仅用于本地验收，允许值为 none、directional-error、digest-mismatch。");
            return result;
        }

        if (!RuntimeReadinessGate.Validate(readinessPath, map, out var readinessReason))
        {
            result.Mode = "fail-closed";
            result.Status = "fail-closed-readiness-gate";
            result.ReadinessGatePassed = false;
            result.Notes.Add(readinessReason);
            return result;
        }
        result.ReadinessGatePassed = true;

        var structural = DirectionalComparer.Compare(map, fromQuery, toQuery, maxPairs);
        result.StructuralStatus = structural.Status;
        result.StructuralDecision = structural.Decision;
        if (!structural.Supported)
        {
            result.Mode = "fail-closed";
            result.Status = "fail-closed-structural-route-unavailable";
            result.Notes.AddRange(structural.Notes);
            return result;
        }

        var pairs = new List<RuntimePair>();
        foreach (var path in structural.Paths.Take(maxPairs))
        {
            var left = map.Tensors.FirstOrDefault(tensor => tensor.Name.Equals(path.FromTensor, StringComparison.Ordinal));
            var right = map.Tensors.FirstOrDefault(tensor => tensor.Name.Equals(path.ToTensor, StringComparison.Ordinal));
            if (left is null || right is null) continue;
            var leftReadable = DirectedPayloadComparer.CanRead(left, out var leftReason);
            var rightReadable = DirectedPayloadComparer.CanRead(right, out var rightReason);
            if (!leftReadable || !rightReadable)
            {
                result.Notes.Add($"跳过不可读路径：{path.FromTensor} ({leftReason}) -> {path.ToTensor} ({rightReason})");
                continue;
            }
            pairs.Add(new RuntimePair(path, left, right));
        }
        result.PairCount = pairs.Count;
        if (pairs.Count == 0)
        {
            result.Mode = "fail-closed";
            result.Status = "fail-closed-no-readable-directed-pair";
            return result;
        }

        var sources = pairs
            .SelectMany(pair => new[]
            {
                DirectedPayloadComparer.ResolveTensorSource(map, pair.Left),
                DirectedPayloadComparer.ResolveTensorSource(map, pair.Right),
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var before = sources.ToDictionary(path => path, RuntimeSourceSnapshot.Capture, StringComparer.OrdinalIgnoreCase);
        RuntimeExtractionEvidence? directed = null;
        RuntimeExtractionEvidence? baseline = null;
        string? directionalFailure = null;

        try
        {
            try
            {
                if (normalizedFault == "directional-error")
                {
                    throw new IOException("injected-directional-read-error");
                }
                (directed, result.DirectionalMilliseconds) = Time(() => Extract(map, pairs, sampleElements, sequential: false));
                result.DirectionalBytesRead = directed.BytesRead;
                result.DirectionalOutputSha256 = normalizedFault == "digest-mismatch"
                    ? Hashing.Sha256Text(directed.OutputDigestSha256 + ":injected-mismatch")
                    : directed.OutputDigestSha256;
                result.SampledValues = directed.SampledValues;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
            {
                directionalFailure = exception.Message;
                result.Notes.Add("定向读取失败：" + exception.Message);
            }

            var baselineNeeded = verifyBaseline || directionalFailure is not null || normalizedFault == "digest-mismatch";
            if (baselineNeeded)
            {
                (baseline, result.BaselineMilliseconds) = Time(() => Extract(map, pairs, sampleElements, sequential: true));
                result.BaselineBytesRead = baseline.BytesRead;
                result.BaselineOutputSha256 = baseline.OutputDigestSha256;
            }

            if (directed is not null && baseline is not null)
            {
                result.BaselineVerifiedThisRequest = result.DirectionalOutputSha256.Equals(result.BaselineOutputSha256, StringComparison.Ordinal)
                    && directed.SampledValues == baseline.SampledValues;
                if (!result.BaselineVerifiedThisRequest)
                {
                    directionalFailure = "directional-baseline-digest-mismatch";
                    result.Notes.Add("本次定向摘要与原顺序读取不一致。 ");
                }
            }

            if (directionalFailure is null)
            {
                result.Mode = "directional";
                result.Status = result.BaselineVerifiedThisRequest
                    ? "ok-directional-baseline-verified"
                    : "ok-directional-readiness-gate";
                result.ResultOutputSha256 = result.DirectionalOutputSha256;
                result.Supported = true;
            }
            else if (allowBaselineFallback)
            {
                baseline ??= Extract(map, pairs, sampleElements, sequential: true);
                if (result.BaselineBytesRead == 0)
                {
                    result.BaselineBytesRead = baseline.BytesRead;
                    result.BaselineOutputSha256 = baseline.OutputDigestSha256;
                }
                result.Mode = "baseline-fallback";
                result.Status = "ok-baseline-fallback";
                result.FallbackTriggered = true;
                result.FallbackReason = directionalFailure;
                result.ResultOutputSha256 = baseline.OutputDigestSha256;
                result.SampledValues = baseline.SampledValues;
                result.Supported = true;
            }
            else
            {
                result.Mode = "fail-closed";
                result.Status = "fail-closed-directional-read";
                result.FallbackReason = directionalFailure;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        {
            result.Mode = "fail-closed";
            result.Status = "fail-closed-baseline-unavailable";
            result.FallbackReason = exception.Message;
            result.Notes.Add("原顺序读取回退失败：" + exception.Message);
            result.Supported = false;
        }

        var after = sources.ToDictionary(path => path, RuntimeSourceSnapshot.Capture, StringComparer.OrdinalIgnoreCase);
        result.SourceSnapshotsUnchanged = before.All(item => after.TryGetValue(item.Key, out var snapshot) && item.Value.Matches(snapshot));
        if (!result.SourceSnapshotsUnchanged)
        {
            result.Supported = false;
            result.Mode = "fail-closed";
            result.Status = "fail-closed-source-snapshot-changed";
            result.Notes.Add("读取前后源模型文件长度或修改时间发生变化。 ");
        }
        result.ByteAvoidanceWithinSelectedRoute = result.BaselineBytesRead > 0
            ? DirectedPayloadComparer.RatioAvoided(result.DirectionalBytesRead, result.BaselineBytesRead)
            : 0;
        result.Notes.Add("运行器官只替代已验证所选因果位的读取与对比路径；完整模型前向、开放域语义和生成未被替代。 ");
        return result;
    }

    private static RuntimeExtractionEvidence Extract(ModelMap map, List<RuntimePair> pairs, int sampleElements, bool sequential)
    {
        long bytesRead = 0;
        var sampledValues = 0;
        var digestText = new StringBuilder();
        foreach (var pair in pairs)
        {
            var left = sequential
                ? DirectedPayloadComparer.ReadSequentialSample(map, pair.Left, sampleElements)
                : DirectedPayloadComparer.ReadDirectedSample(map, pair.Left, sampleElements);
            var right = sequential
                ? DirectedPayloadComparer.ReadSequentialSample(map, pair.Right, sampleElements)
                : DirectedPayloadComparer.ReadDirectedSample(map, pair.Right, sampleElements);
            bytesRead += left.BytesRead + right.BytesRead;
            sampledValues += left.Values.Count + right.Values.Count;
            digestText.Append(pair.Path.FromTensor).Append('\n')
                .Append(pair.Path.ToTensor).Append('\n')
                .Append(left.Sha256).Append('\n')
                .Append(right.Sha256).Append('\n')
                .Append(left.Values.Count).Append('\n')
                .Append(right.Values.Count).Append('\n');
        }
        return new RuntimeExtractionEvidence(
            bytesRead,
            sampledValues,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestText.ToString()))).ToLowerInvariant());
    }

    private static (RuntimeExtractionEvidence Evidence, double Milliseconds) Time(Func<RuntimeExtractionEvidence> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var evidence = action();
        stopwatch.Stop();
        return (evidence, stopwatch.Elapsed.TotalMilliseconds);
    }

    private sealed record RuntimePair(DirectionalPath Path, TensorPosition Left, TensorPosition Right);
    private sealed record RuntimeExtractionEvidence(long BytesRead, int SampledValues, string OutputDigestSha256);
    private sealed record RuntimeSourceSnapshot(long Length, DateTime LastWriteTimeUtc)
    {
        public static RuntimeSourceSnapshot Capture(string path)
        {
            var info = new FileInfo(path);
            return new RuntimeSourceSnapshot(info.Length, info.LastWriteTimeUtc);
        }

        public bool Matches(RuntimeSourceSnapshot other) => Length == other.Length && LastWriteTimeUtc == other.LastWriteTimeUtc;
    }
}

internal static class RuntimeReadinessGate
{
    public static bool Validate(string readinessPath, ModelMap map, out string reason)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(readinessPath));
            var root = document.RootElement;
            if (!ReadBool(root, "ownerTaiji", "present")) return Fail("主人太极锚点缺失", out reason);
            if (!ReadBool(root, "safeToEnableDirectionalComparison")) return Fail("客户就绪门未授权定向比较", out reason);
            if (!ReadString(root, "binding", "modelId").Equals(map.ModelId, StringComparison.Ordinal)) return Fail("就绪报告模型与因果位图不一致", out reason);
            if (!ReadString(root, "binding", "adapterId").Equals(map.Adapter.Id, StringComparison.Ordinal)) return Fail("就绪报告适配器与因果位图不一致", out reason);
            if (!ReadBool(root, "selectedPositionAb", "outputsEquivalent")) return Fail("既有 A/B 输出等价门未通过", out reason);
            if (!ReadBool(root, "selectedPositionAb", "accelerationObserved")) return Fail("既有 A/B 未观测到定向加速", out reason);
            if (!ReadBool(root, "readOnly")) return Fail("就绪报告不是只读模式", out reason);
            if (ReadBool(root, "fullWeightLoaded")) return Fail("就绪报告显示加载了完整权重", out reason);
            if (ReadBool(root, "gpuUsed")) return Fail("就绪报告显示使用了 GPU", out reason);
            if (ReadBool(root, "networkUsed")) return Fail("就绪报告显示使用了网络", out reason);
            if (ReadBool(root, "originalWeightsModified")) return Fail("就绪报告显示原始权重被修改", out reason);
            reason = "ok";
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            reason = "就绪报告无效：" + exception.Message;
            return false;
        }
    }

    private static string ReadString(JsonElement root, params string[] path)
    {
        var value = Follow(root, path);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static bool ReadBool(JsonElement root, params string[] path)
    {
        var value = Follow(root, path);
        return (value.ValueKind is JsonValueKind.True or JsonValueKind.False) && value.GetBoolean();
    }

    private static JsonElement Follow(JsonElement root, IEnumerable<string> path)
    {
        var current = root;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return default;
        }
        return current;
    }

    private static bool Fail(string message, out string reason)
    {
        reason = message;
        return false;
    }
}

internal sealed class RuntimeComparisonResult
{
    public string Schema { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public string RequestId { get; set; } = "";
    public OwnerTaijiAnchor OwnerTaiji { get; set; } = new();
    public string ModelId { get; set; } = "";
    public string AdapterId { get; set; } = "";
    public string FromQuery { get; set; } = "";
    public string ToQuery { get; set; } = "";
    public string StructuralStatus { get; set; } = "";
    public string StructuralDecision { get; set; } = "";
    public bool ReadinessGatePassed { get; set; }
    public bool Supported { get; set; }
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "";
    public bool VerifyBaselineRequested { get; set; }
    public bool BaselineFallbackAllowed { get; set; }
    public bool BaselineVerifiedThisRequest { get; set; }
    public bool FallbackTriggered { get; set; }
    public string FallbackReason { get; set; } = "";
    public int PairCount { get; set; }
    public int SampledValues { get; set; }
    public long DirectionalBytesRead { get; set; }
    public long BaselineBytesRead { get; set; }
    public double ByteAvoidanceWithinSelectedRoute { get; set; }
    public double DirectionalMilliseconds { get; set; }
    public double BaselineMilliseconds { get; set; }
    public string DirectionalOutputSha256 { get; set; } = "";
    public string BaselineOutputSha256 { get; set; } = "";
    public string ResultOutputSha256 { get; set; } = "";
    public bool SourceSnapshotsUnchanged { get; set; }
    public bool ReadOnly { get; set; }
    public bool FullWeightLoaded { get; set; }
    public bool GpuUsed { get; set; }
    public bool NetworkUsed { get; set; }
    public bool OriginalWeightsModified { get; set; }
    public string Authority { get; set; } = RuntimeComparisonOrgan.Authority;
    public List<string> Notes { get; set; } = new();
}

internal static class DirectionalAbBenchmark
{
    public const string Authority = "selected-causal-position extraction A/B only; identical sampled bytes are proven, semantic correctness, generation quality, and full-model inference replacement remain unproven";

    public static DirectionalAbBenchmarkResult Run(ModelMap map, string fromQuery, string toQuery, int maxPairs, int sampleElements, int iterations)
    {
        var result = new DirectionalAbBenchmarkResult
        {
            Schema = "haiyu-directional-extraction-ab/v1",
            ProductVersion = Program.ProductVersion,
            GeneratedAt = DateTimeOffset.Now,
            OwnerTaiji = Receipt.OwnerTaiji(),
            ModelId = map.ModelId,
            AdapterId = map.Adapter.Id,
            FromQuery = fromQuery,
            ToQuery = toQuery,
            RequestedSampleElementsPerTensor = sampleElements,
            RequestedIterations = iterations,
            ReadOnly = true,
            FullWeightLoaded = false,
            FullSelectedTensorTraversedByBaseline = true,
            GpuUsed = false,
            NetworkUsed = false,
            OriginalWeightsModified = false,
            Authority = Authority,
        };

        var structural = DirectionalComparer.Compare(map, fromQuery, toQuery, maxPairs);
        result.StructuralDecision = structural.Decision;
        result.StructuralStatus = structural.Status;
        var payloadTensors = map.Tensors.Where(tensor => tensor.PayloadBytes > 0).ToList();
        result.TotalModelPayloadBytes = payloadTensors
            .GroupBy(tensor => (tensor.SourceFile, tensor.PayloadOffset, tensor.PayloadBytes))
            .Sum(group => group.Key.PayloadBytes);
        if (!structural.Supported)
        {
            result.Status = "fail-closed-structural-route-unavailable";
            result.Notes.AddRange(structural.Notes);
            return result;
        }

        var pairs = new List<BenchmarkPair>();
        var selected = new HashSet<(string SourceFile, long PayloadOffset, long PayloadBytes)>();
        foreach (var path in structural.Paths.Take(maxPairs))
        {
            var left = map.Tensors.FirstOrDefault(tensor => tensor.Name.Equals(path.FromTensor, StringComparison.Ordinal));
            var right = map.Tensors.FirstOrDefault(tensor => tensor.Name.Equals(path.ToTensor, StringComparison.Ordinal));
            if (left is null || right is null) continue;
            var leftReadable = DirectedPayloadComparer.CanRead(left, out var leftReason);
            var rightReadable = DirectedPayloadComparer.CanRead(right, out var rightReason);
            if (!leftReadable || !rightReadable)
            {
                result.Notes.Add($"跳过不可读路径：{path.FromTensor} ({leftReason}) -> {path.ToTensor} ({rightReason})");
                continue;
            }
            pairs.Add(new BenchmarkPair(path, left, right));
            selected.Add((left.SourceFile, left.PayloadOffset, left.PayloadBytes));
            selected.Add((right.SourceFile, right.PayloadOffset, right.PayloadBytes));
        }
        result.PairCount = pairs.Count;
        result.SelectedRoutePayloadBytes = selected.Sum(item => item.PayloadBytes);
        if (pairs.Count == 0)
        {
            result.Status = "fail-closed-no-readable-directed-pair";
            return result;
        }

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var baselineFirst = iteration % 2 == 0;
            ModeEvidence baseline;
            ModeEvidence directed;
            double baselineMilliseconds;
            double directedMilliseconds;
            if (baselineFirst)
            {
                (baseline, baselineMilliseconds) = Time(() => Execute(map, pairs, sampleElements, sequential: true));
                (directed, directedMilliseconds) = Time(() => Execute(map, pairs, sampleElements, sequential: false));
            }
            else
            {
                (directed, directedMilliseconds) = Time(() => Execute(map, pairs, sampleElements, sequential: false));
                (baseline, baselineMilliseconds) = Time(() => Execute(map, pairs, sampleElements, sequential: true));
            }

            var equivalent = baseline.OutputDigestSha256.Equals(directed.OutputDigestSha256, StringComparison.Ordinal)
                && baseline.SampledValues == directed.SampledValues;
            result.Iterations.Add(new DirectionalAbIteration
            {
                Iteration = iteration + 1,
                Order = baselineFirst ? "baseline-then-directional" : "directional-then-baseline",
                BaselineMilliseconds = baselineMilliseconds,
                DirectionalMilliseconds = directedMilliseconds,
                BaselineBytesRead = baseline.BytesRead,
                DirectionalBytesRead = directed.BytesRead,
                BaselineOutputSha256 = baseline.OutputDigestSha256,
                DirectionalOutputSha256 = directed.OutputDigestSha256,
                SampledValues = baseline.SampledValues,
                OutputsEquivalent = equivalent,
            });
        }

        result.CompletedIterations = result.Iterations.Count;
        result.AllOutputsEquivalent = result.Iterations.Count == iterations && result.Iterations.All(item => item.OutputsEquivalent);
        result.BaselineBytesRead = result.Iterations.Sum(item => item.BaselineBytesRead);
        result.DirectionalBytesRead = result.Iterations.Sum(item => item.DirectionalBytesRead);
        result.ByteAvoidanceWithinSelectedRoute = DirectedPayloadComparer.RatioAvoided(result.DirectionalBytesRead, result.BaselineBytesRead);
        result.MedianBaselineMilliseconds = Median(result.Iterations.Select(item => item.BaselineMilliseconds));
        result.MedianDirectionalMilliseconds = Median(result.Iterations.Select(item => item.DirectionalMilliseconds));
        result.MedianExtractionSpeedRatio = result.MedianDirectionalMilliseconds <= 0
            ? 0
            : result.MedianBaselineMilliseconds / result.MedianDirectionalMilliseconds;
        result.ObservedDirectionalExtractionSpeedup = result.AllOutputsEquivalent && result.MedianExtractionSpeedRatio > 1.0;
        result.Supported = result.AllOutputsEquivalent;
        result.Status = result.Supported ? "ok-equivalent-selected-position-ab" : "fail-closed-output-mismatch";
        result.Notes.Add("基线顺序遍历所选张量完整载荷，只保留与定向读取完全相同的采样窗口；两路输出哈希不一致即拒绝加速结论。");
        result.Notes.Add("本结果只证明所选因果位提取的等价性、读取量和墙钟时间，不代表完整模型推理或开放域语义已被替代。");
        return result;
    }

    private static ModeEvidence Execute(ModelMap map, List<BenchmarkPair> pairs, int sampleElements, bool sequential)
    {
        long bytesRead = 0;
        var sampledValues = 0;
        var digestText = new StringBuilder();
        foreach (var pair in pairs)
        {
            var left = sequential
                ? DirectedPayloadComparer.ReadSequentialSample(map, pair.Left, sampleElements)
                : DirectedPayloadComparer.ReadDirectedSample(map, pair.Left, sampleElements);
            var right = sequential
                ? DirectedPayloadComparer.ReadSequentialSample(map, pair.Right, sampleElements)
                : DirectedPayloadComparer.ReadDirectedSample(map, pair.Right, sampleElements);
            bytesRead += left.BytesRead + right.BytesRead;
            sampledValues += left.Values.Count + right.Values.Count;
            digestText.Append(pair.Path.FromTensor).Append('\n')
                .Append(pair.Path.ToTensor).Append('\n')
                .Append(left.Sha256).Append('\n')
                .Append(right.Sha256).Append('\n')
                .Append(left.Values.Count).Append('\n')
                .Append(right.Values.Count).Append('\n');
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestText.ToString()))).ToLowerInvariant();
        return new ModeEvidence(bytesRead, sampledValues, digest);
    }

    private static (ModeEvidence Evidence, double Milliseconds) Time(Func<ModeEvidence> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var evidence = action();
        stopwatch.Stop();
        return (evidence, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return 0;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2.0 : ordered[middle];
    }

    private sealed record BenchmarkPair(DirectionalPath Path, TensorPosition Left, TensorPosition Right);
    private sealed record ModeEvidence(long BytesRead, int SampledValues, string OutputDigestSha256);
}

internal static class SelfTest
{
    public static int Run(JsonSerializerOptions options)
    {
        var tests = new List<SelfTestCase>();
        void Test(string name, Action action)
        {
            try
            {
                action();
                tests.Add(new SelfTestCase(name, true, null));
            }
            catch (Exception exception)
            {
                tests.Add(new SelfTestCase(name, false, exception.Message));
            }
        }

        Test("qwen3-forward-direction", () =>
        {
            var map = Fixture("qwen3", false);
            var result = DirectionalComparer.Compare(map, "attention.query_projection@0", "attention.output_projection@0", 8);
            Ensure(result.Supported && result.Decision == "from-to" && result.DirectionScore == 1.0, "qwen3 forward route failed");
        });
        Test("parallel-branch-is-not-invented", () =>
        {
            var map = Fixture("phi", true);
            var result = DirectionalComparer.Compare(map, "attention.query_projection@0", "feedforward.input_projection@0", 8);
            Ensure(!result.Supported && result.Decision == "undetermined", "parallel branches must be undetermined");
        });
        Test("mamba-native-order", () =>
        {
            var profile = ArchitectureRegistry.Resolve("mamba", "MambaForCausalLM", JsonDocument.Parse("{}").RootElement);
            var tensors = new[]
            {
                TensorRoleMapper.Map("backbone.layers.0.mixer.in_proj.weight", "F16", new long[] { 8, 8 }, profile, 2, "x"),
                TensorRoleMapper.Map("backbone.layers.0.mixer.out_proj.weight", "F16", new long[] { 8, 8 }, profile, 2, "x"),
            };
            var map = BasicMap(profile, tensors);
            var result = DirectionalComparer.Compare(map, "state_space.input_projection@0", "state_space.output_projection@0", 8);
            Ensure(result.Supported && result.Decision == "from-to", "mamba order failed");
        });
        Test("phi3small-blocksparse-native-order", () =>
        {
            using var config = JsonDocument.Parse("{\"dense_attention_every_n_layers\":2,\"blocksparse_num_local_blocks\":16}");
            var profile = ArchitectureRegistry.Resolve("phi3small", "Phi3SmallForCausalLM", config.RootElement);
            var tensors = new[]
            {
                TensorRoleMapper.Map("model.layers.0.self_attn.query_key_value.weight", "F16", new long[] { 8, 8 }, profile, 32, "x"),
                TensorRoleMapper.Map("model.layers.0.self_attn.dense.weight", "F16", new long[] { 8, 8 }, profile, 32, "x"),
                TensorRoleMapper.Map("model.layers.0.post_attention_layernorm.weight", "F16", new long[] { 8 }, profile, 32, "x"),
                TensorRoleMapper.Map("model.layers.0.mlp.up_proj.weight", "F16", new long[] { 8, 8 }, profile, 32, "x"),
                TensorRoleMapper.Map("model.layers.0.mlp.down_proj.weight", "F16", new long[] { 8, 8 }, profile, 32, "x"),
            };
            var map = BasicMap(profile, tensors);
            var result = DirectionalComparer.Compare(map, "attention.qkv_combined_projection@0", "attention.output_projection@0", 8);
            Ensure(profile.Id == "phi3small-blocksparse-decoder", "phi3small adapter id mismatch");
            Ensure(profile.NormalizationType == "LayerNorm", "phi3small must not inherit phi3 RMSNorm");
            Ensure(result.Supported && result.Decision == "from-to", "phi3small native order failed");
        });
        Test("unknown-architecture-fails-closed", () =>
        {
            var profile = ArchitectureRegistry.Resolve("unknown_model", "Unknown", JsonDocument.Parse("{}").RootElement);
            var map = BasicMap(profile, Array.Empty<TensorPosition>());
            var result = DirectionalComparer.Compare(map, "a", "b", 8);
            Ensure(!result.Supported && result.Status.Contains("fail-closed", StringComparison.Ordinal), "unknown model did not fail closed");
        });
        Test("owner-taiji-always-present", () => Ensure(Receipt.OwnerTaiji().Present, "owner taiji missing"));
        Test("long-model-id-has-bounded-stable-map-name", () =>
        {
            var prefix = "/fsx/ckpts/7b_tok=neox_data=pilev2-recontam_lower-code_bs=8m_tp=4_pp=1_init=wang-small-init/";
            var first = FileNames.Safe(prefix + "global_step69000_hf");
            var repeated = FileNames.Safe(prefix + "global_step69000_hf");
            var different = FileNames.Safe(prefix + "global_step69001_hf");
            Ensure(first == repeated, "long model filename must be deterministic");
            Ensure(first != different, "different long model ids must not collide in the bounded name");
            Ensure(first.Length <= FileNames.MaximumSafeLength, "bounded map filename is too long");
            Ensure(first.All(character => !Path.GetInvalidFileNameChars().Contains(character)), "bounded map filename contains invalid characters");
        });
        Test("gguf-header-only-adapter", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "haiyu-gguf-self-test-" + Guid.NewGuid().ToString("N") + ".gguf");
            try
            {
                CreateMinimalGguf(path);
                var info = GgufReader.Read(path);
                Ensure(info.Architecture == "llama" && info.LayerCount == 1 && info.Tensors.Count == 2, "GGUF header parse failed");
                Ensure(info.HeaderBytesRead < new FileInfo(path).Length, "GGUF parser crossed into payload");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });
        Test("directed-payload-sample-is-read-only-and-pruned", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "haiyu-payload-self-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var configPath = Path.Combine(root, "config.json");
                var tensorPath = Path.Combine(root, "model.safetensors");
                File.WriteAllText(configPath, "{\"model_type\":\"llama\",\"architectures\":[\"LlamaForCausalLM\"],\"num_hidden_layers\":1,\"hidden_size\":4}", new UTF8Encoding(false));
                CreateMinimalSafeTensors(tensorPath);
                var before = Hashing.Sha256(tensorPath);
                var map = ModelScanner.Scan(new ModelDiscovery
                {
                    ConfigPath = configPath,
                    ModelRoot = root,
                    DiscoveryRoot = root,
                });
                var result = DirectedPayloadComparer.Measure(
                    map,
                    "attention.query_projection@0",
                    "attention.output_projection@0",
                    1,
                    128);
                var after = Hashing.Sha256(tensorPath);
                Ensure(result.Supported && result.PairCount == 1, "directed payload route was not measured");
                Ensure(Math.Abs(result.Pairs[0].CosineSimilarity - 1.0) < 1e-12, "identical tensor sample cosine mismatch");
                Ensure(result.ActualPayloadBytesRead == 32 && result.TotalModelPayloadBytes == 48, "payload byte accounting mismatch");
                Ensure(result.EndToEndByteAvoidance > 0 && before == after, "directed sampling did not stay read-only/pruned");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        Test("directional-ab-uses-identical-sample-windows", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "haiyu-ab-self-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var configPath = Path.Combine(root, "config.json");
                var tensorPath = Path.Combine(root, "model.safetensors");
                File.WriteAllText(configPath, "{\"model_type\":\"llama\",\"architectures\":[\"LlamaForCausalLM\"],\"num_hidden_layers\":1,\"hidden_size\":4}", new UTF8Encoding(false));
                CreateMinimalSafeTensors(tensorPath);
                var map = ModelScanner.Scan(new ModelDiscovery
                {
                    ConfigPath = configPath,
                    ModelRoot = root,
                    DiscoveryRoot = root,
                });
                var result = DirectionalAbBenchmark.Run(
                    map,
                    "attention.query_projection@0",
                    "attention.output_projection@0",
                    1,
                    128,
                    2);
                Ensure(result.Supported && result.AllOutputsEquivalent, "A/B sample outputs diverged");
                Ensure(result.Iterations.All(item => item.BaselineOutputSha256 == item.DirectionalOutputSha256), "A/B sample digest mismatch");
                Ensure(result.BaselineBytesRead == result.DirectionalBytesRead && result.BaselineBytesRead == 64, "tiny fixture A/B byte accounting mismatch");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        Test("runtime-organ-verifies-bound-directional-route", () =>
        {
            var fixture = CreateRuntimeFixture();
            try
            {
                var result = RuntimeComparisonOrgan.Run(
                    fixture.Map,
                    fixture.ReadinessPath,
                    "attention.query_projection@0",
                    "attention.output_projection@0",
                    1,
                    128,
                    verifyBaseline: true,
                    allowBaselineFallback: true,
                    requestId: "self-test-directional",
                    injectFault: "none");
                Ensure(result.Supported && result.Mode == "directional", "runtime organ did not use the directional route");
                Ensure(result.BaselineVerifiedThisRequest && result.DirectionalOutputSha256 == result.BaselineOutputSha256, "runtime organ did not verify equivalent output");
                Ensure(result.SourceSnapshotsUnchanged && result.OwnerTaiji.Present, "runtime organ lost the source snapshot or owner anchor");
            }
            finally
            {
                if (Directory.Exists(fixture.Root)) Directory.Delete(fixture.Root, true);
            }
        });
        Test("runtime-organ-falls-back-on-directional-read-error", () =>
        {
            var fixture = CreateRuntimeFixture();
            try
            {
                var result = RuntimeComparisonOrgan.Run(
                    fixture.Map, fixture.ReadinessPath,
                    "attention.query_projection@0", "attention.output_projection@0",
                    1, 128, false, true, "self-test-fallback-read", "directional-error");
                Ensure(result.Supported && result.Mode == "baseline-fallback" && result.FallbackTriggered, "runtime organ did not fall back after a directional read error");
                Ensure(!string.IsNullOrWhiteSpace(result.ResultOutputSha256) && result.SourceSnapshotsUnchanged, "runtime fallback did not preserve a verified read-only result");
            }
            finally
            {
                if (Directory.Exists(fixture.Root)) Directory.Delete(fixture.Root, true);
            }
        });
        Test("runtime-organ-falls-back-on-digest-mismatch", () =>
        {
            var fixture = CreateRuntimeFixture();
            try
            {
                var result = RuntimeComparisonOrgan.Run(
                    fixture.Map, fixture.ReadinessPath,
                    "attention.query_projection@0", "attention.output_projection@0",
                    1, 128, false, true, "self-test-fallback-digest", "digest-mismatch");
                Ensure(result.Supported && result.Mode == "baseline-fallback" && result.FallbackReason == "directional-baseline-digest-mismatch", "runtime organ accepted a mismatched directional digest");
                Ensure(result.ResultOutputSha256 == result.BaselineOutputSha256, "runtime fallback did not return the baseline digest");
            }
            finally
            {
                if (Directory.Exists(fixture.Root)) Directory.Delete(fixture.Root, true);
            }
        });
        Test("runtime-organ-fails-closed-on-readiness-identity-mismatch", () =>
        {
            var fixture = CreateRuntimeFixture("wrong-model-id");
            try
            {
                var before = Hashing.Sha256(fixture.TensorPath);
                var result = RuntimeComparisonOrgan.Run(
                    fixture.Map, fixture.ReadinessPath,
                    "attention.query_projection@0", "attention.output_projection@0",
                    1, 128, true, true, "self-test-identity", "none");
                Ensure(!result.Supported && result.Mode == "fail-closed" && !result.ReadinessGatePassed, "runtime organ did not fail closed on a model identity mismatch");
                Ensure(before == Hashing.Sha256(fixture.TensorPath), "identity failure modified the source tensor");
            }
            finally
            {
                if (Directory.Exists(fixture.Root)) Directory.Delete(fixture.Root, true);
            }
        });
        Test("write-json-file-cleans-temporary-files", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "haiyu-json-self-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var target = Path.Combine(root, "receipt.json");
                Program.WriteJsonFile(target, new { generation = 1, ownerTaiji = Receipt.OwnerTaiji() });
                Program.WriteJsonFile(target, new { generation = 2, ownerTaiji = Receipt.OwnerTaiji() });
                using var document = JsonDocument.Parse(File.ReadAllText(target));
                Ensure(document.RootElement.GetProperty("generation").GetInt32() == 2, "atomic JSON replacement did not persist the last generation");
                Ensure(Directory.GetFiles(root, "*.tmp-*").Length == 0, "temporary JSON files were not cleaned");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        Test("robustness-audit-covers-scale-noise-and-faults", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "haiyu-robustness-self-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var configPath = Path.Combine(root, "config.json");
                var tensorPath = Path.Combine(root, "model.safetensors");
                File.WriteAllText(configPath, "{\"model_type\":\"llama\",\"architectures\":[\"LlamaForCausalLM\"],\"num_hidden_layers\":1,\"hidden_size\":4}", new UTF8Encoding(false));
                CreateMinimalSafeTensors(tensorPath);
                var before = Hashing.Sha256(tensorPath);
                var map = ModelScanner.Scan(new ModelDiscovery
                {
                    ConfigPath = configPath,
                    ModelRoot = root,
                    DiscoveryRoot = root,
                });
                var result = RobustnessAudit.Run(
                    map,
                    "attention.query_projection",
                    "attention.output_projection",
                    1,
                    new[] { 128, 1024 },
                    new[] { 0.000001, 0.0001, 0.01 },
                    20260917);
                Ensure(result.Passed, "robustness audit did not pass the fixture");
                Ensure(result.OwnerTaiji.Present && result.FaultCases.Count >= 4 && result.FaultCases.All(item => item.Rejected), "robustness audit did not preserve owner anchor or fail closed");
                Ensure(result.ScaleCases.Count == 2 && result.ScaleCases.All(item => item.Deterministic), "multi-scale determinism gate failed");
                Ensure(result.NoiseCases.Count == 6 && result.NoiseResponsePassed, "noise response gate failed");
                Ensure(result.SourceSnapshotsUnchanged && before == Hashing.Sha256(tensorPath), "robustness audit modified a source tensor");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });

        var passed = tests.Count(test => test.Passed);
        Program.WriteJson(new
        {
            ok = passed == tests.Count,
            schema = "haiyu-directional-self-test/v1",
            passed,
            total = tests.Count,
            tests,
            ownerTaiji = Receipt.OwnerTaiji(),
            fullWeightLoaded = false,
            gpuUsed = false,
            networkUsed = false,
            originalWeightsModified = false,
            authority = Receipt.Authority,
        });
        return passed == tests.Count ? 0 : 1;
    }

    private static RuntimeFixture CreateRuntimeFixture(string? readinessModelId = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "haiyu-runtime-self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "config.json");
        var tensorPath = Path.Combine(root, "model.safetensors");
        var readinessPath = Path.Combine(root, "customer-deployment-readiness.json");
        File.WriteAllText(configPath, "{\"model_type\":\"llama\",\"architectures\":[\"LlamaForCausalLM\"],\"num_hidden_layers\":1,\"hidden_size\":4}", new UTF8Encoding(false));
        CreateMinimalSafeTensors(tensorPath);
        var map = ModelScanner.Scan(new ModelDiscovery
        {
            ConfigPath = configPath,
            ModelRoot = root,
            DiscoveryRoot = root,
        });
        Program.WriteJsonFile(readinessPath, new
        {
            ownerTaiji = Receipt.OwnerTaiji(),
            safeToEnableDirectionalComparison = true,
            binding = new
            {
                modelId = readinessModelId ?? map.ModelId,
                adapterId = map.Adapter.Id,
            },
            selectedPositionAb = new
            {
                outputsEquivalent = true,
                accelerationObserved = true,
            },
            readOnly = true,
            fullWeightLoaded = false,
            gpuUsed = false,
            networkUsed = false,
            originalWeightsModified = false,
        });
        return new RuntimeFixture(root, tensorPath, readinessPath, map);
    }

    private static ModelMap Fixture(string modelType, bool parallel)
    {
        using var config = JsonDocument.Parse(parallel ? "{\"use_parallel_residual\":true}" : "{}");
        var profile = ArchitectureRegistry.Resolve(modelType, modelType, config.RootElement);
        var tensors = new[]
        {
            TensorRoleMapper.Map("model.layers.0.self_attn.q_proj.weight", "F16", new long[] { 8, 8 }, profile, 2, "x"),
            TensorRoleMapper.Map("model.layers.0.self_attn.o_proj.weight", "F16", new long[] { 8, 8 }, profile, 2, "x"),
            TensorRoleMapper.Map("model.layers.0.mlp.fc1.weight", "F16", new long[] { 8, 8 }, profile, 2, "x"),
        };
        return BasicMap(profile, tensors);
    }

    private static ModelMap BasicMap(ArchitectureProfile profile, IEnumerable<TensorPosition> tensors) => new()
    {
        ModelId = "fixture",
        Adapter = profile,
        Tensors = tensors.ToList(),
        OwnerTaiji = Receipt.OwnerTaiji(),
    };

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CreateMinimalGguf(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("GGUF"u8);
        writer.Write((uint)3);
        writer.Write((ulong)2);
        writer.Write((ulong)4);
        WriteMetaString(writer, "general.architecture", "llama");
        WriteMetaString(writer, "general.name", "self-test");
        WriteMetaUInt32(writer, "llama.block_count", 1);
        WriteMetaUInt32(writer, "llama.embedding_length", 4);
        WriteTensorInfo(writer, "blk.0.attn_q.weight");
        WriteTensorInfo(writer, "blk.0.attn_output.weight");
        writer.Write(new byte[64]);
    }

    private static void CreateMinimalSafeTensors(string path)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["model.layers.0.self_attn.q_proj.weight"] = new { dtype = "F32", shape = new[] { 4 }, data_offsets = new[] { 0, 16 } },
            ["model.layers.0.self_attn.o_proj.weight"] = new { dtype = "F32", shape = new[] { 4 }, data_offsets = new[] { 16, 32 } },
            ["model.layers.0.mlp.down_proj.weight"] = new { dtype = "F32", shape = new[] { 4 }, data_offsets = new[] { 32, 48 } },
        });
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((ulong)header.Length);
        writer.Write(header);
        foreach (var value in new[] { 1f, 2f, 3f, 4f, 1f, 2f, 3f, 4f, -1f, -2f, -3f, -4f })
        {
            writer.Write(value);
        }
    }

    private static void WriteMetaString(BinaryWriter writer, string key, string value)
    {
        WriteGgufString(writer, key);
        writer.Write((uint)8);
        WriteGgufString(writer, value);
    }

    private static void WriteMetaUInt32(BinaryWriter writer, string key, uint value)
    {
        WriteGgufString(writer, key);
        writer.Write((uint)4);
        writer.Write(value);
    }

    private static void WriteTensorInfo(BinaryWriter writer, string name)
    {
        WriteGgufString(writer, name);
        writer.Write((uint)2);
        writer.Write((ulong)4);
        writer.Write((ulong)4);
        writer.Write((uint)1);
        writer.Write((ulong)0);
    }

    private static void WriteGgufString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }

    private sealed record SelfTestCase(string Name, bool Passed, string? Error);
    private sealed record RuntimeFixture(string Root, string TensorPath, string ReadinessPath, ModelMap Map);
}

internal static class JsonRead
{
    public static string? String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static int? Int(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    public static bool? Bool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    public static List<string> StringArray(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList() : new List<string>();
    public static long[] Int64Array(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(item => item.GetInt64()).ToArray() : Array.Empty<long>();
}

internal static class Hashing
{
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }


    public static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal static class FileNames
{
    // Keep the basename short enough that Windows PowerShell 5.1 can still
    // open the generated map when customers choose a deeply nested install
    // directory. The fixed suffix adds another 25 characters.
    public const int MaximumSafeLength = 40;

    public static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(result)) result = "unknown-model";
        if (result.Length <= MaximumSafeLength) return result;

        const int digestLength = 16;
        var digest = Hashing.Sha256Text(value)[..digestLength];
        var prefixLength = MaximumSafeLength - digestLength - 2;
        return result[..prefixLength] + "--" + digest;
    }
}

internal static class ModelIds
{
    public static string FromPath(string modelRoot, JsonElement config)
    {
        var configured = JsonRead.String(config, "_name_or_path");
        if (IsMeaningful(configured)) return configured!.Replace('\\', '/');
        var name = Path.GetFileName(modelRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "unknown-model" : name;
    }

    private static bool IsMeaningful(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().Replace('\\', '/');
        return normalized is not "." and not "./" and not ".." and not "../";
    }
}

internal sealed class ModelMap
{
    public string Schema { get; set; } = "haiyu-local-causal-position-map/v1";
    public string ProductVersion { get; set; } = Program.ProductVersion;
    public DateTimeOffset GeneratedAt { get; set; }
    public OwnerTaijiAnchor OwnerTaiji { get; set; } = new();
    public string ModelId { get; set; } = "";
    public string ModelRoot { get; set; } = "";
    public string ConfigPath { get; set; } = "";
    public string ConfigSha256 { get; set; } = "";
    public string ArtifactPath { get; set; } = "";
    public string MetadataFingerprint { get; set; } = "";
    public string ModelType { get; set; } = "";
    public string ArchitectureName { get; set; } = "";
    public int LayerCount { get; set; }
    public int HiddenSize { get; set; }
    public ArchitectureProfile Adapter { get; set; } = new();
    public List<TensorPosition> Tensors { get; set; } = new();
    public ScanEvidence Evidence { get; set; } = new();
}

internal sealed class TensorPosition
{
    public string Name { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string DType { get; set; } = "";
    public long[] Shape { get; set; } = Array.Empty<long>();
    public int Layer { get; set; }
    public string NativeRole { get; set; } = "";
    public string StandardRole { get; set; } = "";
    public int Stage { get; set; }
    public long DirectionRank { get; set; }
    public long PayloadOffset { get; set; } = -1;
    public long PayloadBytes { get; set; }
    public string StorageFormat { get; set; } = "metadata-only";
}

internal sealed class ScanEvidence
{
    public List<string> TensorFiles { get; set; } = new();
    public long HeaderBytesRead { get; set; }
    public long WeightPayloadBytesRead { get; set; }
    public bool FullWeightLoaded { get; set; }
    public bool GpuUsed { get; set; }
    public bool NetworkUsed { get; set; }
    public bool OriginalWeightsModified { get; set; }
    public int UnknownTensorRoles { get; set; }
    public string Authority { get; set; } = Receipt.Authority;
    public List<string> Notes { get; set; } = new();
}

internal sealed class ScanManifest
{
    public string Schema { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public OwnerTaijiAnchor OwnerTaiji { get; set; } = new();
    public List<string> Roots { get; set; } = new();
    public string OutputDirectory { get; set; } = "";
    public int ModelCount { get; set; }
    public int SupportedModelCount { get; set; }
    public int FailClosedModelCount { get; set; }
    public int TensorCount { get; set; }
    public int StandardRoleMappedTensorCount { get; set; }
    public long HeaderBytesRead { get; set; }
    public long WeightPayloadBytesRead { get; set; }
    public bool FullWeightLoaded { get; set; }
    public bool GpuUsed { get; set; }
    public bool NetworkUsed { get; set; }
    public bool OriginalWeightsModified { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public List<ModelSummary> Models { get; set; } = new();
    public string Authority { get; set; } = Receipt.Authority;
}

internal sealed class ModelSummary
{
    public string ModelId { get; set; } = "";
    public string ModelType { get; set; } = "";
    public string AdapterId { get; set; } = "";
    public bool Supported { get; set; }
    public int TensorCount { get; set; }
    public int StandardRoleMappedTensorCount { get; set; }
    public int UnknownTensorRoleCount { get; set; }
    public long HeaderBytesRead { get; set; }
    public string MapFileName { get; set; } = "";

    public static ModelSummary From(ModelMap map) => new()
    {
        ModelId = map.ModelId,
        ModelType = map.ModelType,
        AdapterId = map.Adapter.Id,
        Supported = map.Adapter.Supported,
        TensorCount = map.Tensors.Count,
        StandardRoleMappedTensorCount = map.Tensors.Count(tensor => !tensor.StandardRole.StartsWith("native_unmapped.", StringComparison.Ordinal)),
        UnknownTensorRoleCount = map.Tensors.Count(tensor => tensor.StandardRole.StartsWith("native_unmapped.", StringComparison.Ordinal)),
        HeaderBytesRead = map.Evidence.HeaderBytesRead,
        MapFileName = FileNames.Safe(map.ModelId) + ".causal-position-map.json",
    };
}

internal sealed class DirectionalCompareResult
{
    public string Schema { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public OwnerTaijiAnchor OwnerTaiji { get; set; } = new();
    public string ModelId { get; set; } = "";
    public string AdapterId { get; set; } = "";
    public string FromQuery { get; set; } = "";
    public string ToQuery { get; set; } = "";
    public bool Supported { get; set; }
    public string Status { get; set; } = "";
    public string Decision { get; set; } = "";
    public int FromCandidateCount { get; set; }
    public int ToCandidateCount { get; set; }
    public int ForwardPairCount { get; set; }
    public int ReversePairCount { get; set; }
    public int UndeterminedPairCount { get; set; }
    public double DirectionScore { get; set; }
    public double ReverseScore { get; set; }
    public List<DirectionalPath> Paths { get; set; } = new();
    public bool ReadOnly { get; set; }
    public bool FullWeightLoaded { get; set; }
    public bool GpuUsed { get; set; }
    public bool NetworkUsed { get; set; }
    public bool OriginalWeightsModified { get; set; }
    public string Authority { get; set; } = Receipt.Authority;
    public List<string> Notes { get; set; } = new();
}

internal sealed class DirectionalPath
{
    public string FromTensor { get; set; } = "";
    public string ToTensor { get; set; } = "";
    public string FromRole { get; set; } = "";
    public string ToRole { get; set; } = "";
    public int FromLayer { get; set; }
    public int ToLayer { get; set; }
    public string Direction { get; set; } = "";
    public long Distance { get; set; }
}

internal sealed class DirectedPayloadCompareResult
{
    public string Schema { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public OwnerTaijiAnchor OwnerTaiji { get; set; } = new();
    public string ModelId { get; set; } = "";
    public string AdapterId { get; set; } = "";
    public string FromQuery { get; set; } = "";
    public string ToQuery { get; set; } = "";
    public string StructuralDecision { get; set; } = "";
    public string StructuralStatus { get; set; } = "";
    public bool Supported { get; set; }
    public string Status { get; set; } = "";
    public int RequestedSampleElementsPerTensor { get; set; }
    public int PairCount { get; set; }
    public long TotalModelPayloadBytes { get; set; }
    public long SelectedRoutePayloadBytes { get; set; }
    public long ActualPayloadBytesRead { get; set; }
    public double RoutePruningRatio { get; set; }
    public double SampleAvoidanceWithinRoute { get; set; }
    public double EndToEndByteAvoidance { get; set; }
    public double MeanCosineSimilarity { get; set; }
    public double MeanSignAgreement { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public bool ReadOnly { get; set; }
    public bool FullWeightLoaded { get; set; }
    public bool GpuUsed { get; set; }
    public bool NetworkUsed { get; set; }
    public bool OriginalWeightsModified { get; set; }
    public string Authority { get; set; } = DirectedPayloadComparer.Authority;
    public List<DirectedPayloadPair> Pairs { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

internal sealed class DirectedPayloadPair
{
    public string FromTensor { get; set; } = "";
    public string ToTensor { get; set; } = "";
    public string FromRole { get; set; } = "";
    public string ToRole { get; set; } = "";
    public string Direction { get; set; } = "";
    public long Distance { get; set; }
    public string FromDType { get; set; } = "";
    public string ToDType { get; set; } = "";
    public long FromPayloadBytes { get; set; }
    public long ToPayloadBytes { get; set; }
    public int SampledElementPairs { get; set; }
    public double CosineSimilarity { get; set; }
    public double MeanAbsoluteDelta { get; set; }
    public double RootMeanSquareDelta { get; set; }
    public double SignAgreement { get; set; }
    public string FromSampleSha256 { get; set; } = "";
    public string ToSampleSha256 { get; set; } = "";
    public long BytesRead { get; set; }
}

internal sealed class RobustnessAuditResult
{
    public string Schema { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public OwnerTaijiAnchor OwnerTaiji { get; set; } = new();
    public string ModelId { get; set; } = "";
    public string AdapterId { get; set; } = "";
    public string FromQuery { get; set; } = "";
    public string ToQuery { get; set; } = "";
    public string StructuralStatus { get; set; } = "";
    public string StructuralDecision { get; set; } = "";
    public bool Passed { get; set; }
    public string Status { get; set; } = "";
    public int Seed { get; set; }
    public bool ScaleDeterminismPassed { get; set; }
    public bool PrecisionCoveragePassed { get; set; }
    public bool NoiseResponsePassed { get; set; }
    public bool LayerCoveragePassed { get; set; }
    public bool FaultClosedPassed { get; set; }
    public bool SourceSnapshotsUnchanged { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public bool ReadOnly { get; set; }
    public bool FullWeightLoaded { get; set; }
    public bool GpuUsed { get; set; }
    public bool NetworkUsed { get; set; }
    public bool OriginalWeightsModified { get; set; }
    public string Authority { get; set; } = RobustnessAudit.Authority;
    public List<RobustnessScaleCase> ScaleCases { get; set; } = new();
    public List<RobustnessPrecisionCase> PrecisionCases { get; set; } = new();
    public List<RobustnessNoiseCase> NoiseCases { get; set; } = new();
    public List<RobustnessLayerCase> LayerCoverage { get; set; } = new();
    public List<RobustnessFaultCase> FaultCases { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

internal sealed class RobustnessScaleCase
{
    public string FromTensor { get; set; } = "";
    public string ToTensor { get; set; } = "";
    public string FromDType { get; set; } = "";
    public string ToDType { get; set; } = "";
    public int RequestedElementsPerTensor { get; set; }
    public int ActualFromElements { get; set; }
    public int ActualToElements { get; set; }
    public long BytesRead { get; set; }
    public string FromDigest { get; set; } = "";
    public string ToDigest { get; set; } = "";
    public bool Deterministic { get; set; }
}

internal sealed class RobustnessPrecisionCase
{
    public string DType { get; set; } = "";
    public string Tensor { get; set; } = "";
    public int SampledElements { get; set; }
    public int FiniteElements { get; set; }
    public long BytesRead { get; set; }
    public bool DecodeSucceeded { get; set; }
    public string Error { get; set; } = "";
}

internal sealed class RobustnessNoiseCase
{
    public string Tensor { get; set; } = "";
    public string DType { get; set; } = "";
    public double NoiseLevel { get; set; }
    public int Elements { get; set; }
    public double CosineSimilarity { get; set; }
    public double RootMeanSquareDelta { get; set; }
    public double SignAgreement { get; set; }
    public bool Finite { get; set; }
}

internal sealed class RobustnessLayerCase
{
    public int Layer { get; set; }
    public string FromRole { get; set; } = "";
    public string ToRole { get; set; } = "";
    public bool Supported { get; set; }
    public string Decision { get; set; } = "";
    public string Status { get; set; } = "";
}

internal sealed class RobustnessFaultCase
{
    public string Name { get; set; } = "";
    public string ExpectedError { get; set; } = "";
    public string ActualError { get; set; } = "";
    public bool Rejected { get; set; }
}

internal sealed class DirectionalAbBenchmarkResult
{
    public string Schema { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public OwnerTaijiAnchor OwnerTaiji { get; set; } = new();
    public string ModelId { get; set; } = "";
    public string AdapterId { get; set; } = "";
    public string FromQuery { get; set; } = "";
    public string ToQuery { get; set; } = "";
    public string StructuralDecision { get; set; } = "";
    public string StructuralStatus { get; set; } = "";
    public bool Supported { get; set; }
    public string Status { get; set; } = "";
    public int RequestedSampleElementsPerTensor { get; set; }
    public int RequestedIterations { get; set; }
    public int CompletedIterations { get; set; }
    public int PairCount { get; set; }
    public long TotalModelPayloadBytes { get; set; }
    public long SelectedRoutePayloadBytes { get; set; }
    public long BaselineBytesRead { get; set; }
    public long DirectionalBytesRead { get; set; }
    public double ByteAvoidanceWithinSelectedRoute { get; set; }
    public double MedianBaselineMilliseconds { get; set; }
    public double MedianDirectionalMilliseconds { get; set; }
    public double MedianExtractionSpeedRatio { get; set; }
    public bool ObservedDirectionalExtractionSpeedup { get; set; }
    public bool AllOutputsEquivalent { get; set; }
    public bool ReadOnly { get; set; }
    public bool FullWeightLoaded { get; set; }
    public bool FullSelectedTensorTraversedByBaseline { get; set; }
    public bool GpuUsed { get; set; }
    public bool NetworkUsed { get; set; }
    public bool OriginalWeightsModified { get; set; }
    public string Authority { get; set; } = DirectionalAbBenchmark.Authority;
    public List<DirectionalAbIteration> Iterations { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

internal sealed class DirectionalAbIteration
{
    public int Iteration { get; set; }
    public string Order { get; set; } = "";
    public double BaselineMilliseconds { get; set; }
    public double DirectionalMilliseconds { get; set; }
    public long BaselineBytesRead { get; set; }
    public long DirectionalBytesRead { get; set; }
    public string BaselineOutputSha256 { get; set; } = "";
    public string DirectionalOutputSha256 { get; set; } = "";
    public int SampledValues { get; set; }
    public bool OutputsEquivalent { get; set; }
}
