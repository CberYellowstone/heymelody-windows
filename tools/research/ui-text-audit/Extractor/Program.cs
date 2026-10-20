using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args is ["--self-test"]) { SelfTest(); return 0; }
if (args.Length != 2) { Console.Error.WriteLine("Usage: dotnet run --project Extractor -- <repo> <new.json> | --self-test"); return 2; }
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (File.Exists(output)) throw new IOException("Refusing to overwrite an audit baseline.");
var candidates = new List<Candidate>();
var files = new List<object>();
var methods = new List<object>();
var resources = new List<object>();
foreach (var full in Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
{
    var path = Path.GetRelativePath(root, full).Replace('\\', '/');
    if (path.Split('/').Any(p => p is "obj" or "bin")) continue;
    var extension = Path.GetExtension(path);
    if (extension is not (".cs" or ".axaml" or ".json")) continue;
    var bytes = File.ReadAllBytes(full);
    var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    var before = candidates.Count;
    var category = "源码字符串候选";
    if (extension == ".cs")
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview));
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException("C# syntax errors: " + path);
        candidates.AddRange(ExtractCs(path, tree));
        foreach (var member in tree.GetRoot().DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or PropertyDeclarationSyntax))
            methods.Add(new { path, symbol = Symbol(member), line = Line(member), endLine = tree.GetLineSpan(member.Span).EndLinePosition.Line + 1,
                calls = member.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(n => n.Expression.ToString()).Distinct().ToArray() });
    }
    else if (extension == ".axaml")
    {
        candidates.AddRange(ExtractXaml(path, text));
        category = "界面显示属性及绑定；布局、样式键不作为文本候选";
    }
    else if (path.EndsWith("/Bundles/heymelody.json", StringComparison.Ordinal))
    {
        using var doc = JsonDocument.Parse(text);
        Resources(doc.RootElement, "$", null);
        category = "欢律内置配置：设备名字段投影；功能编号与条件供调用追踪";
    }
    else category = path.Contains("/Bundles/data/") || path.Contains("/Assets/")
        ? "资源数据：动画/模型不直接作为控件文案；资源加载失败提示由 C# 候选覆盖"
        : "资源目录、溯源或认证配置：不导出密钥与地址；UI消费端由绑定候选覆盖";
    var lines = text.Split('\n');
    files.Add(new { path, bytes = bytes.Length, chars = text.Length, lines = lines.Length,
        longest = lines.Max(l => l.TrimEnd('\r').Length), sha256 = Hash(bytes), candidates = candidates.Count - before, category });
}
foreach (var full in Directory.EnumerateFiles(Path.Combine(root, "packaging"), "*", SearchOption.TopDirectoryOnly)
    .Where(p => Path.GetExtension(p) is ".ps1" or ".cmd"))
{
    var bytes = File.ReadAllBytes(full); var text = Encoding.UTF8.GetString(bytes);
    var lines = text.Split('\n');
    files.Add(new { path = Path.GetRelativePath(root, full).Replace('\\','/'), bytes = bytes.Length, chars = text.Length,
        lines = lines.Length, longest = lines.Max(l=>l.Length), sha256 = Hash(bytes), candidates = 0,
        category = "启动脚本已静态检查：仅启动程序/命令行错误，无应用拥有的 GUI 文案；系统与控制台输出不入册" });
}
var appVersion = XDocument.Load(Path.Combine(root, "src/HeyMelody.App/HeyMelody.App.csproj"))
    .Descendants("Version").Single().Value;
var snapshot = new { schemaVersion = 1, createdUtc = DateTimeOffset.UtcNow, commit = Git("rev-parse", "HEAD"),
    workingTree = Git("status", "--short"), appVersion, runtime = Environment.Version.ToString(),
    boundary = "当前源码及内置欢律配置；未读取用户密钥、设备快照或运行时缓存，在线/缓存增量待核实；无蓝牙操作。",
    files, candidates, methods, resourceNames = resources };
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using (var stream = new FileStream(output, FileMode.CreateNew))
    JsonSerializer.Serialize(stream, snapshot, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
Console.WriteLine(JsonSerializer.Serialize(new { output, files = files.Count, candidates = candidates.Count, resourceNames = resources.Count,
    sha256 = Hash(File.ReadAllBytes(output)) }));
return 0;

void Resources(JsonElement element, string pointer, string? product)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        if (element.TryGetProperty("id", out var id)) product = id.ToString();
        foreach (var p in element.EnumerateObject())
        {
            if (p.Name == "name" && p.Value.ValueKind == JsonValueKind.String)
                resources.Add(new { path = "src/HeyMelody.Resources/Bundles/heymelody.json", pointer = pointer + ".name", product,
                    name = p.Value.GetString(), evidence = "配置声明；最终设备标题还需区分 Windows 名称、演示名称和配置回退" });
            else if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Resources(p.Value, pointer + "." + p.Name, product);
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    { var i = 0; foreach (var value in element.EnumerateArray()) Resources(value, pointer + "[" + i++ + "]", product); }
}

string Git(params string[] arguments)
{
    var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!; var result = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd(); process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException(error);
    return result.Trim();
}

static List<Candidate> ExtractCs(string path, SyntaxTree tree)
{
    var result = new List<Candidate>(); var ordinals = new Dictionary<string, int>();
    foreach (var node in tree.GetRoot().DescendantNodes().Where(n => n is InterpolatedStringExpressionSyntax ||
        n is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression)))
    {
        var text = node is LiteralExpressionSyntax literal ? literal.Token.ValueText : node.ToString();
        var owner = node.Ancestors().FirstOrDefault(n => n is MethodDeclarationSyntax or ConstructorDeclarationSyntax or
            PropertyDeclarationSyntax or LocalFunctionStatementSyntax or FieldDeclarationSyntax);
        var symbol = owner is null ? "<top-level>" : Symbol(owner);
        var anchor = node.Ancestors().FirstOrDefault(n => n is AssignmentExpressionSyntax or VariableDeclaratorSyntax or
            ReturnStatementSyntax or ArrowExpressionClauseSyntax or ArgumentSyntax) ?? node;
        var role = anchor switch
        {
            AssignmentExpressionSyntax a => a.Left.ToString(),
            VariableDeclaratorSyntax v => v.Identifier.ValueText,
            ArgumentSyntax a => (a.Parent?.Parent is InvocationExpressionSyntax call ? call.Expression.ToString() :
                a.Parent?.Parent is ObjectCreationExpressionSyntax create ? "new " + create.Type :
                a.Parent?.Parent is ImplicitObjectCreationExpressionSyntax ? "new" : "argument") +
                "[" + (a.Parent as BaseArgumentListSyntax)?.Arguments.IndexOf(a) + "]",
            _ => anchor.Kind().ToString()
        };
        var statement = node.Ancestors().FirstOrDefault(n => n is StatementSyntax or MemberDeclarationSyntax) ?? node;
        var structural = string.Join(" ", statement.DescendantTokens().Select(t => t.IsKind(SyntaxKind.StringLiteralToken) ||
            t.IsKind(SyntaxKind.InterpolatedStringTextToken) ? "<text>" : t.ValueText));
        var key = path + "|" + symbol + "|" + role + "|" + Hash(Encoding.UTF8.GetBytes(structural))[..12];
        var ordinal = ordinals.GetValueOrDefault(key); ordinals[key] = ordinal + 1;
        var conditions = node.Ancestors().Where(n => n is IfStatementSyntax or ConditionalExpressionSyntax or SwitchExpressionArmSyntax or SwitchSectionSyntax)
            .Select(n => n switch {
                IfStatementSyntax i => "if " + i.Condition + (i.Else?.Span.Contains(node.Span) == true ? " [否则]" : " [成立]"),
                ConditionalExpressionSyntax c => "条件 " + c.Condition + (c.WhenTrue.Span.Contains(node.Span) ? " [成立]" : " [否则]"),
                SwitchExpressionArmSyntax a => "分支 " + a.Pattern + (a.WhenClause is null ? "" : " when " + a.WhenClause.Condition),
                SwitchSectionSyntax s => string.Join(" ", s.Labels), _ => "" }).Reverse();
        var disposition = Classify(path, text, node, role);
        result.Add(new Candidate("TXT-" + Hash(Encoding.UTF8.GetBytes(key + "|" + ordinal))[..14], key + "|" + ordinal,
            path, Line(node), symbol, role, text, node is InterpolatedStringExpressionSyntax ? "动态模板" : "C# 字符串",
            Clip(string.Join("；", conditions), 1000), Clip(statement.ToString(), 1600),
            owner?.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(n => n.Expression.ToString()).Distinct().Take(45).ToArray() ?? [],
            disposition.Item1, disposition.Item2));
    }
    return result;
}

static (string, string) Classify(string path, string text, SyntaxNode node, string role)
{
    if (text.Length == 0 || string.IsNullOrWhiteSpace(text)) return ("排除", "空字符串/空白；用于清空或布局，不是可审阅文案");
    if (path.StartsWith("src/HeyMelody.Probe/") || path.StartsWith("src/HeyMelody.Experiments/") || path.Contains("/OfflineChecks/"))
        return ("排除", "独立 CLI 或离线检查程序，不属于产品 GUI");
    if (node.Ancestors().Any(n => n is AttributeSyntax)) return ("排除", "编译、互操作或序列化属性，不是控件显示属性");
    if (Regex.IsMatch(text, "[\\u3400-\\u9fff]")) return ("待核实", "中文文案候选；须核对 UI 消费路径");
    if (Regex.IsMatch(role, "(^|\\.)(Text|Title|Content|Description|Header|ToolTip|PlaceholderText|Watermark|OnContent|OffContent)$|SetName|NativeMenuItem|SetStatus|SetError|Confirm|Alert|SetResult|UiOption|DailyFeatureOption"))
        return ("已入册", "显示属性、界面方法参数或菜单构造参数");
    if (text is "—" or "…" or "·" or "OK" or "Cancel" || Regex.IsMatch(text, "[0-9]%|[0-9] Hz|[0-9] dB|FPS|^HeyMelody$"))
        return ("待核实", "显示符号、名称或单位候选");
    if (node is InterpolatedStringExpressionSyntax && (path.Contains(".App/") || path.Contains(".Client/") || path.Contains(".Calibration.UI/")))
        return ("待核实", "界面层动态模板；需区分显示内容与内部键");
    return ("排除", "非中文内部键/路径/协议/格式等候选；完整表达式保留供人工复核，绑定产生值另列");
}

static List<Candidate> ExtractXaml(string path, string source)
{
    var doc = XDocument.Parse(source, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
    var result = new List<Candidate>();
    var properties = new HashSet<string>(StringComparer.Ordinal) { "Text", "Title", "Content", "Description", "Header", "ToolTip.Tip", "ToolTipText",
        "Watermark", "PlaceholderText", "OnContent", "OffContent", "AutomationProperties.Name", "AutomationProperties.HelpText",
        "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText", "StringFormat" };
    foreach (var element in doc.Descendants())
    {
        var segments = element.AncestorsAndSelf().Reverse().Select(e =>
            e.Name.LocalName + "[" + (e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ??
                e.ElementsBeforeSelf(e.Name).Count().ToString()) + "]");
        var elementPath = string.Join("/", segments);
        foreach (var attribute in element.Attributes().Where(a => properties.Contains(a.Name.LocalName)))
        {
            var owner = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ?? elementPath;
            var key = path + "|" + elementPath + "|" + attribute.Name.LocalName;
            result.Add(new Candidate("TXT-" + Hash(Encoding.UTF8.GetBytes(key))[..14], key, path, ((IXmlLineInfo)attribute).LineNumber,
                owner, attribute.Name.LocalName, attribute.Value, attribute.Value.StartsWith('{') ? "AXAML 绑定/资源" : "AXAML 固定文本",
                string.Join("；", element.AncestorsAndSelf().SelectMany(e => e.Attributes().Where(a => a.Name.LocalName is "IsVisible" or "IsEnabled").Select(a => a.Name + "=" + a.Value))),
                Clip(element.ToString(SaveOptions.DisableFormatting), 1600),
                element.AncestorsAndSelf().SelectMany(e => e.Attributes().Where(a => a.Name.LocalName is "Click" or "Command" or "SelectionChanged").Select(a => a.Name + "=" + a.Value)).Distinct().ToArray(),
                "已入册", "AXAML 显示属性；绑定条目另追踪值的生成位置"));
        }
        foreach (var text in element.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)))
        {
            var key = path + "|" + elementPath + "|#text";
            result.Add(new Candidate("TXT-" + Hash(Encoding.UTF8.GetBytes(key))[..14], key, path, ((IXmlLineInfo)text).LineNumber,
                elementPath, "#text", text.Value.Trim(), "AXAML 元素文本", "", Clip(element.ToString(), 1600), [], "已入册", "非空元素正文"));
        }
    }
    return result;
}

static string Symbol(SyntaxNode node)
{
    var type = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText);
    var name = node switch {
        MethodDeclarationSyntax m => m.Identifier.ValueText + "(" + string.Join(",", m.ParameterList.Parameters.Select(p => p.Type?.ToString())) + ")",
        ConstructorDeclarationSyntax c => c.Identifier.ValueText + "(" + string.Join(",", c.ParameterList.Parameters.Select(p => p.Type?.ToString())) + ")",
        LocalFunctionStatementSyntax l => l.Identifier.ValueText + "(local)",
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        FieldDeclarationSyntax f => string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.ValueText)),
        _ => "<top-level>" };
    return string.Join(".", type.Append(name));
}
static int Line(SyntaxNode node) => node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static string Clip(string value, int max) => value.Length <= max ? value : value[..max] + " … [上下文限长；原文请按符号查源码]";
static void SelfTest()
{
    void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    const string code = "class V { string Name => \"标题\"; void Go() { Text = $\"电量 {n}%\"; if (ready) Show(\"完成\"); } }";
    var first = ExtractCs("src/HeyMelody.App/V.cs", CSharpSyntaxTree.ParseText(code));
    var second = ExtractCs("src/HeyMelody.App/V.cs", CSharpSyntaxTree.ParseText("\n\n" + code.Replace("标题", "名称").Replace("完成", "已完成")));
    Check(first.Count == 3 && first.Select(c => c.Id).SequenceEqual(second.Select(c => c.Id)), "IDs survive text and line edits");
    Check(first.Any(c => c.Kind == "动态模板" && c.Text.Contains("{n}")), "Interpolated templates retained");
    Check(first.Any(c => c.Condition.Contains("ready")), "Branch conditions retained");
    var xml = ExtractXaml("V.axaml", "<View xmlns:x='urn:x'><Button x:Name='save' Content='保存' Command='{Binding SaveCommand}'/><TextBlock Text='{Binding Status}'/><TextBlock Text='保存'/></View>");
    Check(xml.Count == 3 && xml.Select(c => c.Id).Distinct().Count() == 3, "Repeated text is not merged; binding included");
    Check(xml[0].Calls.Contains("Command={Binding SaveCommand}"), "Command evidence retained");
    var log = ExtractCs("src/HeyMelody.Probe/Program.cs", CSharpSyntaxTree.ParseText("Console.WriteLine(\"只读日志\");"));
    Check(log.Single().Disposition == "排除", "CLI candidates have explicit exclusion");
    var english = ExtractCs("src/HeyMelody.App/V.cs", CSharpSyntaxTree.ParseText("class V { void Go() { var content = \"none\"; Text = \"Hello\"; } }"));
    Check(english[0].Disposition == "排除" && english[1].Disposition == "已入册", "Local content variables are not display properties");
}

record Candidate(string Id, string IdentityKey, string Path, int Line, string Symbol, string Role, string Text, string Kind,
    string Condition, string Context, string[] Calls, string Disposition, string Reason);
