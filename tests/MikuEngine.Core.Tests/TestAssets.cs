namespace MikuEngine.Core.Tests;

/// <summary>
/// 仓库内 MMD 示例资源（PMX / VMD）的定位器。
///
/// 这些资源属于 **Demo 项目**，而 Demo 项目目录改过名（<c>MikuEngine.Demo</c> → <c>MikuEngine.Demo.old</c>）、
/// 以后还可能再搬（旧项目计划转为自动化验证专用）⇒ 这里**不写死目录名**，
/// 改为在仓库根的 <c>samples/*/</c> 下按相对路径顺序查找（按目录名序，结果稳定）。
/// </summary>
internal static class TestAssets
{
    /// <summary>在 <c>samples/&lt;任意子目录&gt;/</c> 下按相对路径找文件；找不到返回 null。</summary>
    public static string? Find(string relativeUnderSamples)
    {
        string samples = Path.Combine(RepoRoot(), "samples");
        if (!Directory.Exists(samples)) return null;

        string native = relativeUnderSamples.Replace('/', Path.DirectorySeparatorChar);
        foreach (string dir in Directory.EnumerateDirectories(samples).OrderBy(d => d, StringComparer.Ordinal))
        {
            string candidate = Path.Combine(dir, native);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Demo 的 PMX（默认 <c>Model/1/1.pmx</c>）。缺失即抛，消息里带上期望路径。</summary>
    public static string Model(string relative = "1/1.pmx")
        => Find("Model/" + relative)
           ?? throw new IOException($"未找到 samples/*/Model/{relative}（Demo 资源缺失）");

    /// <summary>Demo 的 <c>Motion/</c> 下的 VMD。</summary>
    public static string Motion(string fileName)
        => Find("Motion/" + fileName)
           ?? throw new IOException($"未找到 samples/*/Motion/{fileName}（Demo 资源缺失）");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "samples"))) return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
