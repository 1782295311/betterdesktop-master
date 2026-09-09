// BetterDesktop.Shell.Convert — PDF 安全引擎（2026-09-07 补全，对齐 flyingmouse 的 PDF 加密/解密）
// PDFsharp 6.2.4（MIT，csproj 许可白名单已含；2026-09-08 C7 替换 PdfSharpCore）：加密（用户/所有者密码，库内标准安全处理，默认 AES-256）/
// 解密（经 PdfReader.PasswordProvider 回调取原密码打开后清除安全设置）。
// 密码按作业构造注入（菜单 PasswordPrompt 输入），不落日志。

using System.IO;
using BetterDesktop.Shell.Convert.Contracts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

namespace BetterDesktop.Shell.Convert.Services.Engines;

/// <summary>
/// PDF 加密/解密引擎：EncryptPdfMarker → 设置用户/所有者密码；DecryptPdfMarker → 原密码打开后清除。
/// 注意：PDFsharp 6.2.4 未公开加密算法选择（无 EncryptionAlgorithm 属性），
/// 加密强度由库内安全处理器决定——能力诚实，不在菜单宣称具体算法。
/// </summary>
public sealed class PdfSecurityEngine : IConversionEngine
{
    private readonly string _password;

    public PdfSecurityEngine(string password) => _password = password;

    public EngineKind Kind => EngineKind.PdfSecurity;

    public string Name => "pdf-security";

    public bool CanHandle(IReadOnlyList<string> sources, ConversionTarget target) =>
        sources.Count == 1
        && Path.GetExtension(sources[0]).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
        && (target.Prefer == EngineKind.PdfSecurity || target.Fallback == EngineKind.PdfSecurity);

    public EngineAvailability Probe() => EngineAvailability.Ok("PDFsharp 6.2.4");

    public Task<IReadOnlyList<string>> RunAsync(ConversionJob job, CancellationToken ct)
    {
        IReadOnlyList<string> products = job.Target.Filter switch
        {
            ConversionTarget.EncryptPdfMarker => Encrypt(job),
            ConversionTarget.DecryptPdfMarker => Decrypt(job),
            _ => throw new ConvertException(ConvertError.InputInvalid, "未知的 PDF 安全操作"),
        };
        return Task.FromResult(products);
    }

    private IReadOnlyList<string> Encrypt(ConversionJob job)
    {
        var input = job.PrimarySource;
        var product = Path.Combine(job.TempDir,
            Path.GetFileNameWithoutExtension(input) + "-encrypted.pdf");
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        doc.SecuritySettings.UserPassword = _password;
        doc.SecuritySettings.OwnerPassword = _password;
        // PDFsharp 6.2.4 未公开加密算法选择（无 DocumentSecurityLevel 属性），加密强度由库内标准安全处理器决定（默认 AES-256，高于 1.3.2 的 128 位上限）
        doc.Save(product);
        return [product];
    }

    private IReadOnlyList<string> Decrypt(ConversionJob job)
    {
        var input = job.PrimarySource;
        var product = Path.Combine(job.TempDir,
            Path.GetFileNameWithoutExtension(input) + "-decrypted.pdf");
        // PDFsharp 6.2.4 的 Save 强制要求「至少一个用户或所有者密码」，清空密码后 Save 会抛异常；
        // 故解密 = 密码 Import 打开 → 逐页复制进全新文档（无安全设置）→ Save（同 Merge/Split 已验证的复制路径）
        using var source = PdfReader.Open(input, _password, PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        for (var i = 0; i < source.PageCount; i++)
        {
            output.AddPage(source.Pages[i]);
        }
        output.Save(product);
        return [product];
    }
}
