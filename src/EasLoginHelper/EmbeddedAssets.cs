// SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EasLoginHelper;

public static class EmbeddedAssets
{
    private static Assembly Assembly => typeof(EmbeddedAssets).Assembly;
    public static string SourceUrl => "https://github.com/Xymircai/RsmAutologin/tree/v" + Branding.Version;
    public static bool OcrPresent => new[] { "Model", "Charset", "Manifest" }.All(n => Assembly.GetManifestResourceInfo("EasLoginHelper.Ocr." + n) is not null);
    public static byte[] Read(string name)
    {
        using var stream = Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException("程序内置资源缺失，请重新下载完整 EXE。");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
    public static string Text(string name) => Encoding.UTF8.GetString(Read(name));
    /// <summary>读取内置图片并返回独立副本，底层流可立即释放。</summary>
    public static System.Drawing.Image LoadImage(string name)
    {
        using var stream = new MemoryStream(Read(name));
        using var source = System.Drawing.Image.FromStream(stream);
        return new System.Drawing.Bitmap(source);
    }
    internal static (byte[] Model, byte[] Charset) Ocr(string? directory = null)
    {
        byte[] Load(string resource, string file) => directory is null ? Read("EasLoginHelper.Ocr." + resource) : File.ReadAllBytes(Path.Combine(directory, file));
        var model = Load("Model", "common_old.onnx"); var table = Load("Charset", "charset.json");
        using var manifest = JsonDocument.Parse(Load("Manifest", "manifest.json"));
        Verify(model, manifest.RootElement.GetProperty("modelSha256").GetString());
        Verify(table, manifest.RootElement.GetProperty("charsetSha256").GetString());
        return (model, table);
    }
    private static void Verify(byte[] value, string? expected)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(value)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("内置 OCR 资源校验失败，请重新下载程序。");
    }
    public static string Licenses() => string.Join("\r\n\r\n", Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("EasLoginHelper.Licenses.", StringComparison.Ordinal)).Order()
        .Select(n => "===== " + n["EasLoginHelper.Licenses.".Length..] + " =====\r\n" + Text(n)));
}

public sealed record OcrHealth(bool Ready, string Code, string Message);
