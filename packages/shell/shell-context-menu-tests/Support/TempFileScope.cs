namespace BetterDesktop.Shell.ContextMenu.Tests.Support;

/// <summary>测试临时文件作用域（目录级清理；异常吞掉避免Dispose抛错掩盖断言）。</summary>
public sealed class TempFileScope : IDisposable
{
    private readonly string _root;

    public TempFileScope()
    {
        _root = Path.Combine(Path.GetTempPath(), "bd-cmenu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string CreateDirectory()
    {
        var dir = Path.Combine(_root, "dir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string CreateFile(string name, string extension, bool readOnly = false)
    {
        var path = Path.Combine(_root, name + extension);
        File.WriteAllText(path, "test");
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结果
        }
    }
}
