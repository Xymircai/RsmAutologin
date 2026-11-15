// SPDX-License-Identifier: MPL-2.0
using System.Windows.Automation;

namespace EasLoginHelper;

public sealed class LoginWriteCounts
{
    public int UserName { get; private set; }
    public int Password { get; private set; }
    public int Captcha { get; private set; }
    public int Submissions { get; set; }
    internal void Record(int index) { if (index == 0) UserName++; else if (index == 1) Password++; else Captcha++; }
}

public sealed class CaptchaSession
{
    public string? Value { get; internal set; }
    public string? ImageHash { get; internal set; }
    public string? LastObservedHash { get; internal set; }
}

public sealed class BoundLoginWriter(TargetPolicy policy, LoginLocator locator, LoginWriteCounts counts, Action<string>? trace = null)
{
    public LoginPage Require(LoginPage expected, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!policy.Allows(expected.Window) || Native.IsIconic(expected.Window) || !Native.IsWindowVisible(expected.Window))
            throw new OperationCanceledException("目标窗口暂不可用。");
        var fresh = locator.Find(expected.Window);
        if (fresh is null || fresh.Identity != expected.Identity || fresh.Fields.Any(f => f.Element is null))
            throw new InvalidOperationException("登录框不支持后台绑定填写，请检查软件页面。");
        return fresh;
    }

    public bool Set(LoginPage expected, int index, ReadOnlySpan<char> text, CancellationToken cancellation)
    {
        foreach (var character in text) if (char.IsControl(character)) throw new InvalidOperationException("输入含不支持的控制字符。");
        var page = Require(expected, cancellation); var field = page.Fields[index]; var element = field.Element!;
        if (index == 1 && !element.Current.IsPassword) throw new InvalidOperationException("密码输入框标识不匹配。");
        if (index != 1 && LoginLocator.ReadNonSecretValue(field).AsSpan().SequenceEqual(text))
        { trace?.Invoke(index == 2 ? "Captcha.ValueReused" : "Username.ValueReused"); return false; }
        var handle = new IntPtr(element.Current.NativeWindowHandle);
        counts.Record(index);
        if (handle != IntPtr.Zero && Native.GetAncestor(handle, Native.GaRoot) == page.Window && NewEraLocator.IsEditClass(Native.ClassName(handle)))
        {
            if (!Native.SetBoundText(handle, text)) throw new InvalidOperationException("绑定文本框填写失败。");
        }
        else
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || ((ValuePattern)pattern).Current.IsReadOnly)
                throw new InvalidOperationException("登录框不支持直接赋值。");
            ((ValuePattern)pattern).SetValue(new string(text));
        }
        cancellation.ThrowIfCancellationRequested();
        trace?.Invoke(index switch { 0 => "Username.BoundWritten", 1 => "Password.BoundWritten", _ => "Captcha.BoundWritten" });
        return true;
    }
}

public sealed class CaptchaCoordinator(BoundLoginWriter writer, ICaptchaFrameSource frames, ICaptchaRecognizer ocr, Action<string>? trace = null)
{
    public async Task<CaptchaResult> PrepareAsync(LoginPage expected, CaptchaSession state, CancellationToken cancellation)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var page = writer.Require(expected, cancellation);
            if (page.Captcha.IsEmpty) return new("", 0, "未定位到验证码图片，已停止自动登录。");
            using var image = await frames.CaptureAsync(page.Window, page.Captcha, cancellation);
            var hash = FillController.ImageHash(image); state.LastObservedHash = hash;
            if (state.ImageHash != hash || state.Value is null)
            {
                var recognized = await ocr.RecognizeAsync(image, cancellation);
                if (!recognized.Accepted) return recognized;
                if (recognized.Text == state.Value) trace?.Invoke("Captcha.SemanticReused");
                state.Value = recognized.Text; state.ImageHash = hash;
            }
            writer.Set(expected, 2, state.Value, cancellation);
            page = writer.Require(expected, cancellation);
            using var current = await frames.CaptureAsync(page.Window, page.Captcha, cancellation);
            var currentHash = FillController.ImageHash(current); state.LastObservedHash = currentHash;
            if (currentHash == state.ImageHash) return new(state.Value, 1, "验证码已就绪。");
            // A repaint alone is not a new challenge. Verify its digits before deciding to write again.
            var verified = await ocr.RecognizeAsync(current, cancellation);
            if (!verified.Accepted) return verified;
            var same = verified.Text == state.Value;
            state.ImageHash = currentHash; state.Value = verified.Text;
            if (same) { trace?.Invoke("Captcha.SemanticReused"); return verified; }
            trace?.Invoke("Captcha.ChallengeChanged");
        }
        return new("", 0, "验证码持续变化，已停止循环填写；请检查页面后重试。");
    }
}
