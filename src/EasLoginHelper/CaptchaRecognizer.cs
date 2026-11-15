// SPDX-License-Identifier: MPL-2.0
using System.Drawing.Drawing2D;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EasLoginHelper;

public static class CtcDecoder
{
    public static CaptchaResult Decode(float[] logits, int timeSteps, int classes, string[] charset)
    {
        if (classes != charset.Length || classes < 2 || timeSteps < 1 || logits.Length != timeSteps * classes)
            throw new InvalidDataException("OCR 模型输出与字符表不匹配。");
        var tokens = new List<(string Text, float Confidence)>();
        var previous = -1;
        for (var t = 0; t < timeSteps; t++)
        {
            var offset = t * classes; var best = 0; var maximum = float.NegativeInfinity;
            for (var c = 0; c < classes; c++)
            {
                var value = logits[offset + c];
                if (!float.IsFinite(value)) return new CaptchaResult("", 0, "验证码输出异常，请手动填写。");
                if (value > maximum) { maximum = value; best = c; }
            }
            double sum = 0;
            for (var c = 0; c < classes; c++) sum += Math.Exp(logits[offset + c] - maximum);
            var confidence = (float)(1d / sum);
            if (best != 0)
            {
                if (best != previous) tokens.Add((charset[best], confidence));
                else if (tokens.Count > 0 && confidence > tokens[^1].Confidence) tokens[^1] = (tokens[^1].Text, confidence);
            }
            previous = best;
        }
        var text = string.Concat(tokens.Select(t => t.Text));
        var result = new CaptchaResult(text, tokens.Count == 0 ? 0 : tokens.Min(t => t.Confidence), "");
        return result with { Message = result.Accepted ? "验证码已填写，请检查后登录。" : "验证码识别不确定，请手动填写。", ErrorCode = result.Accepted ? null : "Ocr.LowConfidence" };
    }
}

public interface ICaptchaRecognizer
{
    Task<CaptchaResult> RecognizeAsync(Bitmap source, CancellationToken cancellation);
}

public sealed class CaptchaRecognizer(string? modelDirectory = null) : IDisposable, ICaptchaRecognizer
{
    static CaptchaRecognizer()
    {
        // Set before any native ORT initialization; the official runtime otherwise enables 1DS telemetry.
        Environment.SetEnvironmentVariable("ORT_DISABLE_TELEMETRY", "1", EnvironmentVariableTarget.Process);
    }
    private readonly string? _directory = modelDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loadSync = new();
    private Task? _loading;
    private InferenceSession? _session;
    private string[] _charset = [];
    private bool _disposed;
    public bool Ready => _session is not null && !_disposed;

    public async Task<OcrHealth> WarmupAsync(CancellationToken cancellation = default)
    {
        Task loading;
        lock (_loadSync) loading = _loading ??= Task.Run(() =>
        {
            _gate.Wait();
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureLoaded();
                if (_disposed) { _session?.Dispose(); _session = null; throw new ObjectDisposedException(nameof(CaptchaRecognizer)); }
            }
            finally { _gate.Release(); }
        });
        try { await loading.WaitAsync(TimeSpan.FromSeconds(20), cancellation); return new(true, "Ocr.Ready", "内置验证码识别已就绪。"); }
        catch (TimeoutException) { return new(false, "Ocr.Initializing", "OCR 初始化较慢，请稍候或运行环境自检；尚未提交登录。"); }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or OnnxRuntimeException or DllNotFoundException or BadImageFormatException or TypeInitializationException or EntryPointNotFoundException or ObjectDisposedException)
        {
            var code = e is FileNotFoundException or DirectoryNotFoundException ? "Ocr.AssetsMissing" :
                e is InvalidDataException or JsonException ? "Ocr.AssetsInvalid" : "Ocr.EngineUnavailable";
            var message = code switch
            {
                "Ocr.AssetsMissing" => "OCR 资源缺失，请重新下载单文件 EXE；暂请手动填写验证码。",
                "Ocr.AssetsInvalid" => "OCR 资源校验失败，请重新下载程序；暂请手动填写验证码。",
                _ => "OCR 引擎无法加载，请运行环境自检并检查 Windows x64 环境；暂请手动填写验证码。"
            };
            return new(false, code, message);
        }
    }

    public async Task<CaptchaResult> RecognizeAsync(Bitmap source, CancellationToken cancellation)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CaptchaRecognizer));
        var health = await WarmupAsync(cancellation);
        if (!health.Ready) return new CaptchaResult("", 0, health.Message) { ErrorCode = health.Code };
        var clone = (Bitmap)source.Clone();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var token = deadline.Token;
        try
        {
            // The worker owns its clone, even if the deadline expires before model initialization finishes.
            return await Task.Run(() =>
            {
                using (clone)
                {
                    _gate.Wait(token);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        token.ThrowIfCancellationRequested();
                        using var run = new RunOptions();
                        using var registration = token.Register(() => run.Terminate = true);
                        using var image = Resize(source: clone);
                        var original = RunPass(image, run, false);
                        token.ThrowIfCancellationRequested();
                        var cleaned = RunPass(image, run, true);
                        token.ThrowIfCancellationRequested();
                        if (original.Accepted && cleaned.Accepted && original.Text != cleaned.Text)
                            return new CaptchaResult("", 0, "验证码两次识别结果不一致，请手动填写。") { ErrorCode = "Ocr.ConflictingResults" };
                        if (original.Accepted) return original;
                        if (cleaned.Accepted) return cleaned;
                        return original.Confidence >= cleaned.Confidence ? original : cleaned;
                    }
                    finally { _gate.Release(); }
                }
            }, CancellationToken.None).WaitAsync(token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new CaptchaResult("", 0, "验证码识别超时，请运行环境自检或手动填写。") { ErrorCode = "Ocr.Timeout" };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OnnxRuntimeException or JsonException)
        {
            return new CaptchaResult("", 0, "验证码识别失败，请运行环境自检；暂请手动填写。") { ErrorCode = "Ocr.InferenceFailed" };
        }
    }

    private CaptchaResult RunPass(Bitmap image, RunOptions options, bool removeColoredBackground)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 1, 64, image.Width });
        for (var y = 0; y < 64; y++) for (var x = 0; x < image.Width; x++)
        {
            var pixel = image.GetPixel(x, y);
            var gray = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 255000f;
            // EAS uses dark glyphs on lighter colored tiles. A second pass removes those tiles.
            if (removeColoredBackground) gray = gray < 0.43f ? 0 : 1;
            tensor[0, 0, y, x] = (gray - 0.5f) / 0.5f;
        }
        var inputName = _session!.InputMetadata.Keys.Single();
        using var output = _session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)], _session.OutputNames, options);
        var values = output.First().AsTensor<float>(); var dimensions = values.Dimensions.ToArray();
        if (dimensions.Length != 3 || dimensions[0] != 1 && dimensions[1] != 1)
            throw new InvalidDataException("OCR 模型输出维度无效。");
        return CtcDecoder.Decode(values.ToArray(), Math.Max(dimensions[0], dimensions[1]), dimensions[2], _charset);
    }

    private void EnsureLoaded()
    {
        if (_session is not null) return;
        var assets = EmbeddedAssets.Ocr(_directory);
        _charset = JsonSerializer.Deserialize<string[]>(assets.Charset) ?? throw new InvalidDataException("字符表为空。");
        if (_charset.Length < 1000 || _charset[0] != "") throw new InvalidDataException("OCR 字符表格式无效。");
        OrtEnv.Instance().DisableTelemetryEvents();
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        _session = new InferenceSession(assets.Model, options);
    }

    private static Bitmap Resize(Bitmap source)
    {
        if (source.Width < 8 || source.Height < 8 || source.Width > 1500 || source.Height > 500)
            throw new InvalidDataException("验证码图片尺寸无效。");
        var width = Math.Clamp((int)(source.Width * (64d / source.Height)), 16, 1024);
        var resized = new Bitmap(width, 64);
        using var graphics = Graphics.FromImage(resized);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(0, 0, width, 64));
        return resized;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_gate.Wait(0))
        {
            _session?.Dispose();
            _session = null;
            _gate.Release();
        }
    }
}
