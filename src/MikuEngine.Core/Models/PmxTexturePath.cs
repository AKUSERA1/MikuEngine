using System.Text;

namespace MikuEngine.Core.Models;

/// <summary>
/// PMX 纹理路径处理工具。
/// PMX 模型常由不同制作者/工具生成，纹理路径里可能混用 '/' 与 '\' 分隔符
/// （例如 "texture\\cloth/01.png"），直接作为文件路径解析会失败。这里统一归一化。
/// </summary>
public static class PmxTexturePath
{
    /// <summary>
    /// 把路径分隔符归一化为 '/'，并清理开头的 "./" 与多余分隔符。
    /// 不触碰文件名本身（保留大小写与扩展名），也不展开相对上级 ".."（交由解析层决定是否拒绝）。
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        // 第一步：把 '\' 统一成 '/'
        var sb = new StringBuilder(path.Length);
        foreach (char c in path)
            sb.Append(c == '\\' ? '/' : c);

        string p = sb.ToString();

        // 截断到最后一个 NUL（部分工具在字符串尾填充 \0）——PMX 有时会有尾部空字节沿袭
        int nul = p.IndexOf('\0');
        if (nul >= 0)
            p = p[..nul];

        // 去空白（前后）
        p = p.Trim();

        // 折叠重复分隔符，同时剥掉开头的 "./" 或纯分隔符前缀
        var parts = p.Split('/');
        var kept = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0)
                continue; // 空段（重复分隔符，或开头根分隔符）跳过
            if (part == ".")
                continue; // 当前目录段跳过
            kept.Add(part);
        }

        return string.Join('/', kept);
    }
}