using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Silk.NET.OpenGL;

namespace MikuEngine.Render.GLES;

/// <summary>
/// 纹理库。id 0 恒为 1×1 白色，作为"加载失败"的兜底，调用方无需判空。
///
/// 注意：V 轴处理：
/// PMX 的 UV、以及 PmxEditor 里所有程序化 UV（Sphere / Toon）都是按
/// <b>D3D9 左上原点</b> 约定写的。GL 上传时数据行序与 D3D9 一致
/// （第 0 行 → 采样坐标 v=0），因此<b>按文件行序原样上传即可复现 PmxEditor 的结果</b>，
/// 不能翻转图像 —— 翻转反而会让 toon 明暗颠倒（实测 sph/toon1.png 上亮下暗）。
/// </summary>
public sealed class GlesTextureLibrary : IDisposable
{
    public const int None = -1;

    private readonly GlesDevice _device;
    private readonly List<uint> _ids = new();
    private readonly Dictionary<string, int> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public GlesTextureLibrary(GlesDevice device)
    {
        _device = device;
        CreateFallbackWhite();
    }

    public int Count => _ids.Count;

    /// <summary>
    /// 加载纹理。失败返回 <see cref="None"/>（调用方按"无纹理"处理，不要画成黑块）。
    /// </summary>
    /// <param name="path">磁盘绝对路径。</param>
    /// <param name="toon">true = toon 图（CLAMP + 线性，不生成 mipmap）。</param>
    public int Load(string path, bool toon = false)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return None;

        if (_byPath.TryGetValue(path, out int existing))
            return existing;

        var gl = _device.Gl;
        uint tex = gl.CreateTexture(TextureTarget.Texture2D);
        gl.BindTexture(TextureTarget.Texture2D, tex);

        try
        {
            using var image = Image.Load<Rgba32>(path);
            var pixels = new Rgba32[image.Width * image.Height];
            image.CopyPixelDataTo(pixels);

            var bytes = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                bytes[i * 4 + 0] = pixels[i].R;
                bytes[i * 4 + 1] = pixels[i].G;
                bytes[i * 4 + 2] = pixels[i].B;
                bytes[i * 4 + 3] = pixels[i].A;
            }

            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)image.Width, (uint)image.Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, bytes.AsSpan());

            if (toon)
            {
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            }
            else
            {
                gl.GenerateMipmap(TextureTarget.Texture2D);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            }
        }
        catch (Exception ex)
        {
            gl.DeleteTexture(tex);
            Console.WriteLine($"[GlesTextureLibrary] 加载失败：{path} —— {ex.Message}");
            return None;
        }

        int id = _ids.Count;
        _ids.Add(tex);
        _byPath[path] = id;
        return id;
    }

    /// <summary>取 GL 纹理句柄；id 无效时返回白色兜底纹理。</summary>
    public uint Get(int id) => id >= 0 && id < _ids.Count ? _ids[id] : _ids[0];

    public bool IsValid(int id) => id >= 0 && id < _ids.Count;

    private void CreateFallbackWhite()
    {
        var gl = _device.Gl;
        uint tex = gl.CreateTexture(TextureTarget.Texture2D);
        gl.BindTexture(TextureTarget.Texture2D, tex);

        Span<byte> white = stackalloc byte[] { 255, 255, 255, 255 };
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, white);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        _ids.Add(tex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var gl = _device.Gl;
        foreach (uint id in _ids)
            gl.DeleteTexture(id);
        _ids.Clear();
        _byPath.Clear();
    }
}

/// <summary>
/// PMX 纹理路径 → 磁盘路径的解析。
/// PMX 里路径可能混用 '\' 与 '/'（如 "textures\cloth.png"），且大小写未必与磁盘一致。
/// </summary>
public static class PmxFileResolver
{
    public static string? Resolve(string modelDirectory, string pmxRelativePath)
    {
        if (string.IsNullOrEmpty(pmxRelativePath)) return null;

        string normalized = MikuEngine.Core.Models.PmxTexturePath.Normalize(pmxRelativePath);
        if (normalized.Length == 0) return null;

        string full = Path.GetFullPath(Path.Combine(
            modelDirectory,
            normalized.Replace('/', Path.DirectorySeparatorChar)));

        if (File.Exists(full)) return full;

        // 大小写不敏感兜底（Windows 上常见，Linux/Android 上必需）
        string? dir = Path.GetDirectoryName(full);
        if (dir is null || !Directory.Exists(dir)) return null;

        string name = Path.GetFileName(full);
        foreach (string candidate in Directory.EnumerateFiles(dir))
        {
            if (string.Equals(Path.GetFileName(candidate), name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    /// <summary>共享 toon（toon01..toon10）：在模型目录及其一级子目录里找。</summary>
    public static string? ResolveSharedToon(string modelDirectory, int sharedIndex)
    {
        string fileName = $"toon{sharedIndex + 1:00}";
        foreach (string ext in new[] { ".png", ".bmp", ".jpg", ".tga" })
        {
            string direct = Path.Combine(modelDirectory, fileName + ext);
            if (File.Exists(direct)) return direct;

            foreach (string sub in Directory.EnumerateDirectories(modelDirectory))
            {
                string candidate = Path.Combine(sub, fileName + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
