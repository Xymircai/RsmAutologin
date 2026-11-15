// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Windows.Automation;

namespace EasLoginHelper;

public sealed class FillController(TargetPolicy policy, LoginLocator locator, InputActivity activity,
    CredentialVault vault, ICaptchaRecognizer ocr, Action<string>? trace = null,
    ICaptchaFrameSource? frameSource = null, LoginWriteCounts? counters = null, CaptchaSession? captchaSession = null)
{
    public Task<FillResult> FillAsync(LoginPage expected, AccountProfile account, bool ocrEnabled,
        bool captchaOnly, CancellationToken cancellation) => Task.Run(async () =>
    {
        var counts = counters ?? new LoginWriteCounts(); var state = captchaSession ?? new CaptchaSession();
        var writer = new BoundLoginWriter(policy, locator, counts, trace); var credentialsFilled = false;
        var keys = activity.KeyboardGenerationFor(expected.Window);
        try
        {
            writer.Require(expected, cancellation);
            if (!captchaOnly)
            {
                using var secret = vault.Read(account.Id);
                if (secret is null) return new FillResult(FillOutcome.MissingCredential, "未找到保存的密码，请在账号管理中重新保存。");
                writer.Set(expected, 0, account.UserName, cancellation); writer.Set(expected, 1, secret.Characters, cancellation);
                credentialsFilled = true;
            }
            keys = activity.KeyboardGenerationFor(expected.Window);
            if (!ocrEnabled) return new FillResult(FillOutcome.Filled, "账号密码已填写，请手动输入验证码并登录。")
                { CredentialsFilled = credentialsFilled, InputGeneration = keys };
            var captcha = new CaptchaCoordinator(writer, frameSource ?? WindowCaptureSource.Shared, ocr, trace);
            var result = await captcha.PrepareAsync(expected, state, cancellation);
            return new FillResult(result.Accepted ? FillOutcome.Filled : FillOutcome.Partial, result.Message, state.ImageHash)
            {
                CredentialsFilled = credentialsFilled, CaptchaFilled = result.Accepted,
                CaptchaValue = result.Accepted ? state.Value : null, InputGeneration = keys
            };
        }
        catch (OperationCanceledException) { return new FillResult(FillOutcome.Interrupted, "本轮操作已取消或窗口暂不可用。"); }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            trace?.Invoke("Fill.Stopped." + e.GetType().Name);
            return new FillResult(FillOutcome.Partial, "后台控件或验证码帧暂不可用，未提交登录；请恢复窗口或重新识别。")
                { CredentialsFilled = credentialsFilled, InputGeneration = keys };
        }
    }, cancellation);

    public static string ImageHash(Bitmap image)
    {
        using var bytes = new MemoryStream(); image.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }
}
