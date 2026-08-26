// bamldecode: 用 WPF Baml2006Reader 遍历 BAML 二进制，导出 XAML 结构（元素/属性/值）。
// 用法: bamldecode <file.baml> <out.txt>
using System.IO;
using System.Text;
using System.Windows.Baml2006;
using System.Xaml;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: bamldecode <baml> <out.txt>");
    return 1;
}

var baml = File.ReadAllBytes(args[0]);
using var stream = new MemoryStream(baml);
using var reader = new Baml2006Reader(stream);
var sb = new StringBuilder();
int indent = 0;

while (reader.Read())
{
    switch (reader.NodeType)
    {
        case XamlNodeType.StartObject:
            sb.AppendLine(new string(' ', indent * 2) + "<" + DescribeType(reader) + ">");
            indent++;
            break;
        case XamlNodeType.GetObject:
            sb.AppendLine(new string(' ', indent * 2) + "<!GetObject>");
            break;
        case XamlNodeType.EndObject:
            indent--;
            sb.AppendLine(new string(' ', indent * 2) + "</" + DescribeType(reader) + ">");
            break;
        case XamlNodeType.StartMember:
            sb.AppendLine(new string(' ', indent * 2) + "  " + reader.Member?.ToString());
            break;
        case XamlNodeType.EndMember:
            break;
        case XamlNodeType.Value:
            sb.AppendLine(new string(' ', indent * 2) + "    value=" + (reader.Value?.ToString() ?? "null"));
            break;
        case XamlNodeType.NamespaceDeclaration:
            sb.AppendLine(new string(' ', indent * 2) + "    xmlns: " + reader.Namespace);
            break;
    }
}

File.WriteAllText(args[1], sb.ToString());
Console.WriteLine($"ok: {args[1]} ({sb.Length} chars)");
return 0;

static string DescribeType(Baml2006Reader r) => r.Type?.Name ?? "?";
