using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BetterDesktop.Kernel.Core;
using BetterDesktop.Shell.Convert.Contracts;
using BetterDesktop.Shell.Convert.Services.Markdown;
using BetterDesktop.Shell.Settings.Contracts;
using ReverseMarkdown;
using YamlDotNet.Serialization;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// C# 纯托管引擎（红线 9 能力诚实：md→html/txt、txt/log→md/html、html→md；
/// Markdig/ReverseMarkdown 纯库，零外部进程——纯托管项必须独立可用，计划 §6.3）。
/// 输出统一 UTF-8 无 BOM 落盘。
/// </summary>
public sealed class ManagedEngine : IConversionEngine
{
    private readonly ISettingsService? _settings;

    public ManagedEngine(ISettingsService? settings = null) => _settings = settings;

    public EngineKind Kind => EngineKind.Managed;

    public string Name => "managed";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && (target.Prefer == EngineKind.Managed || target.Fallback == EngineKind.Managed);

    public EngineAvailability Probe() => EngineAvailability.Ok("Markdig/ReverseMarkdown 纯托管");

    public Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var input = job.PrimarySource;
        var ext = Path.GetExtension(input).ToLowerInvariant();
        var format = job.Target.Format;
        var name = Path.GetFileNameWithoutExtension(input);
        var product = Path.Combine(job.TempDir, name + "." + format);

        var text = File.ReadAllText(input); // UTF-8 默认 + BOM 探测
        var content = (ext, format) switch
        {
            (".md", "html") => ToStandaloneHtml(text, name, Path.GetDirectoryName(input)),
            (".md", "txt") => MarkdownTransformer.HtmlToText(MarkdownTransformer.ToHtmlBody(
                MarkdownTransformer.ApplyFrontMatter(text, GetFrontMatterMode()))),
            (".txt", "md") or (".log", "md") => text, // 轻包装：内容原样（纯托管，无语法解释）
            (".txt", "html") or (".log", "html") => MarkdownTransformer.BuildStandaloneHtml(
                name, MarkdownTransformer.PlainTextToHtml(text)),
            // JSON/XML（2026-09-07 补全）：结构化文本轻包装，同 txt/log 档
            (".json", "md") or (".xml", "md") => text,
            (".json", "html") or (".xml", "html") => MarkdownTransformer.BuildStandaloneHtml(
                name, MarkdownTransformer.PlainTextToHtml(text)),
            // YAML（2026-09-07 补全）：同 json/xml 轻包装
            (".yaml", "md") or (".yml", "md") => text,
            (".yaml", "html") or (".yml", "html") => MarkdownTransformer.BuildStandaloneHtml(
                name, MarkdownTransformer.PlainTextToHtml(text)),
            // 文本类近善近全（2026-09-07）：结构化文本 → txt 轻包装
            (".json", "txt") or (".xml", "txt") or (".yaml", "txt") or (".yml", "txt") => text,
            // 表格文本（2026-09-07）：csv/tsv → md 表格 / HTML 表格 / 纯文本（RFC4180 引号感知，纯托管）
            (".csv", "md") or (".tsv", "md") => CsvToMarkdown(text, SeparatorOf(ext)),
            (".csv", "html") or (".tsv", "html") => MarkdownTransformer.BuildStandaloneHtml(
                name, CsvToHtmlTable(text, SeparatorOf(ext))),
            (".csv", "txt") or (".tsv", "txt") => CsvToPlainText(text, SeparatorOf(ext)),
            // 结构化互转（2026-09-07）：json→yaml（手写递归，字符串引号保护）；yaml→json（YamlDotNet 反序列化）
            (".json", "yaml") => JsonToYaml(text),
            (".yaml", "json") or (".yml", "json") => YamlToJson(text),
            // 数据语义直连（2026-09-07）：csv/tsv → json/yaml（列头=键，标量类型推断同 YAML 语义）
            (".csv", "json") or (".tsv", "json") => CsvToJson(text, SeparatorOf(ext)),
            (".csv", "yaml") or (".tsv", "yaml") => JsonToYaml(CsvToJson(text, SeparatorOf(ext))),
            // json/yaml → csv/tsv（根须为对象数组，否则转换报错提示）；md 表格 → csv/tsv（表格语法可逆→csv↔md 无损往返）
            (".json", "csv") or (".json", "tsv") => JsonToCsv(text, SeparatorOf(ext)),
            (".yaml", "csv") or (".yml", "csv") or (".yaml", "tsv") or (".yml", "tsv") => JsonToCsv(YamlToJson(text), SeparatorOf(ext)),
            (".md", "csv") or (".md", "tsv") => MdTableToCsv(text, SeparatorOf(ext)),
            // xml → json/yaml（System.Xml.Linq；元素→对象、属性→@attr、同名重复→数组、混合文本→#text；叶子保持字符串不推断类型）
            (".xml", "json") => XmlToJson(text),
            (".xml", "yaml") => JsonToYaml(XmlToJson(text)),
            (".html", "md") or (".htm", "md") => new Converter().Convert(text),
            _ => throw new ConvertException(ConvertError.InputInvalid, $"纯托管引擎不支持的转换: {ext} → {format}"),
        };
        File.WriteAllText(product, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        IReadOnlyList<string> products = [product];
        return Task.FromResult(products);
    }

    /// <summary>md → 独立 HTML（final 导出与两跳中转共用：中转阶段相对图片转绝对 file URI）。</summary>
    internal string ToStandaloneHtml(string markdown, string title, string? resolveImagesAgainst)
    {
        var body = MarkdownTransformer.ToHtmlBody(MarkdownTransformer.ApplyFrontMatter(markdown, GetFrontMatterMode()));
        if (resolveImagesAgainst is not null)
        {
            body = MarkdownTransformer.ResolveRelativeImageSrcs(body, resolveImagesAgainst);
        }
        return MarkdownTransformer.BuildStandaloneHtml(title, body);
    }

    // —— 文本类近善近全（2026-09-07）——

    private static char SeparatorOf(string ext) => ext == ".tsv" ? '\t' : ',';

    /// <summary>RFC4180 引号感知分隔解析（支持字段内分隔符/换行/双引号转义；TSV 同规则，分隔符为 tab）。</summary>
    private static List<string[]> ParseDelimited(string text, char separator)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { inQuotes = false; }
                }
                else { field.Append(c); }
            }
            else
            {
                switch (c)
                {
                    case '"': inQuotes = true; break;
                    case '\r': break; // 忽略 CR
                    case '\n':
                        row.Add(field.ToString()); field.Clear();
                        rows.Add(row.ToArray()); row.Clear();
                        break;
                    default:
                        if (c == separator) { row.Add(field.ToString()); field.Clear(); }
                        else { field.Append(c); }
                        break;
                }
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private static string CsvToMarkdown(string text, char separator)
    {
        var rows = ParseDelimited(text, separator);
        if (rows.Count == 0) { return string.Empty; }
        var sb = new StringBuilder();
        sb.Append('|').Append(string.Join('|', rows[0].Select(EscapePipe))).Append('|').AppendLine();
        sb.Append('|').Append(string.Join('|', rows[0].Select(_ => "---"))).Append('|').AppendLine();
        foreach (var row in rows.Skip(1))
        {
            sb.Append('|').Append(string.Join('|', row.Select(EscapePipe))).Append('|').AppendLine();
        }
        return sb.ToString();
    }

    private static string CsvToHtmlTable(string text, char separator)
    {
        var rows = ParseDelimited(text, separator);
        if (rows.Count == 0) { return string.Empty; }
        var sb = new StringBuilder("<table>");
        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append("<tr>");
            var tag = r == 0 ? "th" : "td";
            foreach (var cell in rows[r])
            {
                sb.Append('<').Append(tag).Append('>').Append(HtmlEncode(cell)).Append("</").Append(tag).Append('>');
            }
            sb.Append("</tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    private static string CsvToPlainText(string text, char separator)
    {
        var rows = ParseDelimited(text, separator);
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(" | ", row));
        }
        return sb.ToString();
    }

    /// <summary>json → yaml（手写递归；JSON 是 YAML 1.2 子集，字符串一律单引号保护——防止 "123"/"true" 被 YAML 推断成标量）。</summary>
    private static string JsonToYaml(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        AppendYaml(sb, doc.RootElement, 0);
        return sb.ToString();
    }

    private static void AppendYaml(StringBuilder sb, JsonElement el, int indent)
    {
        var pad = new string(' ', indent);
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    if (!el.EnumerateObject().Any()) { sb.Append(pad).Append("{}\n"); return; }
                    var first = true;
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (!first) { sb.AppendLine(); }
                        first = false;
                        sb.Append(pad).Append(QuoteYaml(prop.Name)).Append(':');
                        AppendYamlValue(sb, prop.Value, indent);
                    }
                    sb.AppendLine();
                    break;
                }
            case JsonValueKind.Array:
                {
                    if (el.GetArrayLength() == 0) { sb.Append(pad).Append("[]\n"); return; }
                    foreach (var item in el.EnumerateArray())
                    {
                        sb.Append(pad).Append('-');
                        if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        {
                            sb.AppendLine();
                            AppendYaml(sb, item, indent + 2);
                        }
                        else
                        {
                            sb.Append(' ').Append(YamlScalar(item)).AppendLine();
                        }
                    }
                    break;
                }
            default:
                sb.Append(pad).Append(YamlScalar(el)).AppendLine();
                break;
        }
    }

    private static void AppendYamlValue(StringBuilder sb, JsonElement value, int indent)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            sb.AppendLine();
            AppendYaml(sb, value, indent + 2);
        }
        else
        {
            sb.Append(' ').Append(YamlScalar(value)).AppendLine();
        }
    }

    private static string YamlScalar(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => QuoteYaml(el.GetString() ?? string.Empty),
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => "null",
    };

    private static string QuoteYaml(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>yaml → json（YamlDotNet 反序列化 object 树 → System.Text.Json；YAML 类型推断即其语义）。</summary>
    private static string YamlToJson(string yaml)
    {
        // WithAttemptingUnquotedStringTypeDeserialization：YAML 未引号标量按语义推断类型（1→int、true→bool）；
        // 带引号字符串保持 string（2026-09-07 运行时验证：用例全过）
        var deserializer = new DeserializerBuilder()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build();
        var obj = deserializer.Deserialize<object>(yaml);
        return JsonSerializer.Serialize(ConvertForJson(obj), new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>YamlDotNet 的 object 树（Dictionary&lt;object,object&gt;/List&lt;object&gt;）→ JSON 可序列化树。</summary>
    private static object? ConvertForJson(object? value)
    {
        switch (value)
        {
            case IDictionary<object, object> dict:
                return dict.ToDictionary(kv => kv.Key.ToString() ?? string.Empty, kv => ConvertForJson(kv.Value));
            case IEnumerable<object> list when value is not string:
                return list.Select(ConvertForJson).ToList();
            default:
                return value;
        }
    }

    private static string EscapePipe(string s) => s.Replace("|", "\\|");

    private static string HtmlEncode(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    // —— 数据语义直连（2026-09-07）——

    /// <summary>csv/tsv → JSON 对象数组（列头=键；标量类型推断：空→null、true/false→bool、整数→long、小数→double、否则字符串，同 YAML 语义）。</summary>
    private static string CsvToJson(string text, char separator)
    {
        var rows = ParseDelimited(text, separator);
        if (rows.Count == 0) { return "[]"; }
        var header = rows[0];
        var arr = new JsonArray();
        foreach (var row in rows.Skip(1))
        {
            var obj = new JsonObject();
            for (var c = 0; c < header.Length; c++)
            {
                var cell = c < row.Length ? row[c] : string.Empty;
                obj[header[c]] = InferScalar(cell);
            }
            arr.Add(obj);
        }
        return JsonSerializer.Serialize(arr, new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode? InferScalar(string s)
    {
        if (s.Length == 0) { return null; }
        if (s is "true" or "false") { return JsonValue.Create(bool.Parse(s)); }
        if (long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l)) { return JsonValue.Create(l); }
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) { return JsonValue.Create(d); }
        return JsonValue.Create(s);
    }

    /// <summary>JSON → csv/tsv（根须为对象数组；列键取首对象并随后续对象扩展；嵌套对象/数组不导出为空，防破坏表结构）。</summary>
    private static string JsonToCsv(string json, char separator)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("JSON 根必须是对象数组才能转换为表格");
        }
        var keys = new List<string>();
        var rows = new List<Dictionary<string, string>>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { continue; }
            var map = new Dictionary<string, string>();
            foreach (var prop in item.EnumerateObject())
            {
                map[prop.Name] = ScalarText(prop.Value);
                if (!keys.Contains(prop.Name)) { keys.Add(prop.Name); }
            }
            rows.Add(map);
        }
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(separator, keys.Select(EscapeCsv)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(separator, keys.Select(k => EscapeCsv(row.TryGetValue(k, out var v) ? v : string.Empty))));
        }
        return sb.ToString();
    }

    private static string ScalarText(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => string.Empty, // 嵌套对象/数组不导出
    };

    /// <summary>RFC4180 字段转义：含分隔符/引号/换行/tab 时双引号包裹、内部引号翻倍。</summary>
    private static string EscapeCsv(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n', '\t' }) < 0) { return s; }
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Markdown 表格 → csv/tsv（只取含 | 的表格行；分隔行 |---|---| 跳过；无表格抛错提示）。</summary>
    private static string MdTableToCsv(string text, char separator)
    {
        var sb = new StringBuilder();
        var rows = 0;
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains('|')) { continue; }
            var t = line.Trim();
            if (t.StartsWith('|') && t.EndsWith('|')) { t = t.Substring(1, t.Length - 2); }
            var cells = t.Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length > 0 && cells.All(c => c.Length > 0 && c.All(ch => ch == '-' || ch == ':' || ch == ' '))) { continue; }
            sb.AppendLine(string.Join(separator, cells.Select(EscapeCsv)));
            rows++;
        }
        if (rows == 0) { throw new InvalidOperationException("未找到 Markdown 表格"); }
        return sb.ToString();
    }

    /// <summary>xml → JSON（约定：元素→对象/标量、属性→@name、同名重复元素→数组、混合文本→#text；叶子与属性保持字符串——XML 无语义类型）。</summary>
    private static string XmlToJson(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidOperationException("XML 根节点为空");
        return JsonSerializer.Serialize(XmlElementToNode(root), new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode? XmlElementToNode(XElement el)
    {
        var attrs = el.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        var childElems = el.Elements().ToList();
        if (childElems.Count == 0 && attrs.Count == 0)
        {
            return JsonValue.Create(el.Value.Trim());
        }
        var obj = new JsonObject();
        foreach (var attr in attrs)
        {
            obj["@" + attr.Name.LocalName] = JsonValue.Create(attr.Value);
        }
        foreach (var group in childElems.GroupBy(c => c.Name.LocalName))
        {
            var list = group.ToList();
            obj[group.Key] = list.Count == 1 ? XmlElementToNode(list[0]) : new JsonArray(list.Select(XmlElementToNode).ToArray());
        }
        var text = string.Concat(el.Nodes().OfType<XText>().Select(n => n.Value)).Trim();
        if (text.Length > 0 && childElems.Count > 0) { obj["#text"] = JsonValue.Create(text); }
        return obj;
    }

    /// <summary>front matter 模式读取（红线 10：默认剥离；设置 convert.front-matter=heading 转标题区）。</summary>
    private FrontMatterMode GetFrontMatterMode() =>
        _settings?.Get<string>(MarkdownTransformer.FrontMatterModeKey)?.Equals("heading", StringComparison.OrdinalIgnoreCase) == true
            ? FrontMatterMode.Heading
            : FrontMatterMode.Strip;
}

/// <summary>
/// 两跳中转引擎（hub-spoke：矩阵显式登记 hop=2，禁止隐式超过两跳）：
/// md→docx/pdf：md → 中转 html（相对图片转绝对 URI，红线 7）→ soffice；
/// docx→md（pandoc 缺失离线兜底）：docx → soffice html → ReverseMarkdown（样式有损，菜单已标注）。
/// </summary>
public sealed class TwoHopEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.TwoHop;

    public string Name => "two-hop";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && (target.Prefer == EngineKind.TwoHop || target.Fallback == EngineKind.TwoHop);

    /// <summary>执行段是 soffice：可用性与 soffice 引擎一致（缓存共享）。</summary>
    public EngineAvailability Probe() => SofficeEngine.LocateSoffice() is not null
        ? EngineAvailability.Ok("soffice 两跳")
        : EngineAvailability.Missing;

    public async Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        var soffice = SofficeEngine.LocateSoffice()
            ?? throw new ConvertException(ConvertError.EngineMissing, "内置引擎未就绪（未找到 LibreOffice）——这不是文件错误");
        var input = job.PrimarySource;
        var ext = Path.GetExtension(input).ToLowerInvariant();
        var format = job.Target.Format;
        var name = Path.GetFileNameWithoutExtension(input);

        switch (ext, format)
        {
            case (".md", "docx") or (".md", "pdf"):
                {
                    // md → 中转 html（相对图片解析为绝对 file URI + CJK 样式随 html 进 soffice，红线 7/8）
                    var htmlPath = Path.Combine(job.TempDir, name + ".html");
                    var markdown = File.ReadAllText(input);
                    var body = MarkdownTransformer.ToHtmlBody(MarkdownTransformer.StripFrontMatter(markdown).Body);
                    body = MarkdownTransformer.ResolveRelativeImageSrcs(body, Path.GetDirectoryName(input)!);
                    await File.WriteAllTextAsync(htmlPath, MarkdownTransformer.BuildStandaloneHtml(name, body), ct);
                    var product = await SofficeEngine.ConvertAsync(soffice, htmlPath, format, job.TempDir, ct);
                    IReadOnlyList<string> products = [product];
                    return products;
                }
            case (".doc", "md") or (".docx", "md") or (".docm", "md") or (".rtf", "md")
                or (".odt", "md") or (".wps", "md") or (".wpt", "md") or (".wpd", "md"):
                {
                    // Word 族 → html → md（2026-09-07 扩展：soffice 万能读；pandoc 直读的 docx/odt/rtf 走 pandoc，此处为兜底/旧格式）
                    var html = await SofficeEngine.ConvertAsync(soffice, input, "html", job.TempDir, ct);
                    var md = new ReverseMarkdown.Converter().Convert(File.ReadAllText(html));
                    var product = Path.Combine(job.TempDir, name + ".md");
                    await File.WriteAllTextAsync(product, md, ct);
                    IReadOnlyList<string> products = [product];
                    return products;
                }
            default:
                throw new ConvertException(ConvertError.InputInvalid, $"两跳引擎不支持的转换: {ext} → {format}");
        }
    }
}
