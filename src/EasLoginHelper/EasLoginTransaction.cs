// SPDX-License-Identifier: MPL-2.0
namespace EasLoginHelper;

public sealed class EasLoginEpisode
{
    public LoginWriteCounts Counts { get; } = new();
    public CaptchaSession Captcha { get; } = new();
    public bool CredentialsWritten { get; set; }
    public bool? UserNameConfirmed { get; set; }
    public long CredentialKeys { get; set; }
    public FillResult? Fill { get; set; }
    public LoginResult? Login { get; set; }
    public int Preparations { get; set; }
    public bool Completed { get; set; }
    public Guid AccountId { get; set; }
    public string? PageIdentity { get; set; }
    internal string ApplicationName { get; set; } = "EAS";
    public string Stage { get; set; } = "Detected";
}

public sealed class EasLoginTransaction(TargetPolicy policy, LoginLocator locator, InputActivity activity, CredentialVault vault,
    ICaptchaRecognizer ocr, ICaptchaFrameSource frames, Action<string>? trace = null, TimeSpan? loginTimeout = null)
{
    public async Task RunAsync(LoginPage page, AccountProfile account, EasLoginEpisode episode, bool ocrEnabled, bool submit,
        LoginAttemptGate gate, Func<bool> canContinue, CancellationToken cancellation, bool captchaOnly = false)
    {
        episode.ApplicationName = page.ApplicationName;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20) + (loginTimeout ?? TimeSpan.FromSeconds(12)));
        var token = deadline.Token;
        try { await Task.Run(async () =>
        {
            try
            {
                void Stage(string stage) { episode.Stage = stage; if (stage == "Login.SubmitRequested") episode.Counts.Submissions = 1; trace?.Invoke(stage); }
                if (!canContinue()) throw new OperationCanceledException();
                if (episode.AccountId != account.Id || episode.PageIdentity != page.Identity)
                {
                    episode.CredentialsWritten = false; episode.AccountId = account.Id; episode.PageIdentity = page.Identity;
                }
                var writer = new BoundLoginWriter(policy, locator, episode.Counts, Stage);
                var keys = activity.KeyboardGenerationFor(page.Window);
                var needsCredentials = !episode.CredentialsWritten || keys != episode.CredentialKeys;
                var fill = new FillController(policy, locator, activity, vault, ocr, Stage, frames, episode.Counts, episode.Captcha);
                Stage("Filling"); episode.Preparations++;
                episode.Fill = await fill.FillAsync(page, account, ocrEnabled, captchaOnly || !needsCredentials, token);
                if (episode.Fill.CredentialsFilled)
                { episode.CredentialsWritten = true; episode.CredentialKeys = episode.Fill.InputGeneration ?? keys; }
                episode.UserNameConfirmed = LoginLocator.ReadNonSecretValue(writer.Require(page, token).Fields[0]) == account.UserName;
                episode.Completed = true;
                if (!submit || !LoginReadiness.CanAutoSubmit(true, episode.CredentialsWritten, episode.Fill)) return;
                if (!canContinue()) throw new OperationCanceledException();
                // The default account wins. Correct target-form edits once; unrelated desktop activity is ignored.
                var fresh = writer.Require(page, token);
                if (activity.KeyboardGenerationFor(page.Window) != episode.CredentialKeys || LoginLocator.ReadNonSecretValue(fresh.Fields[0]) != account.UserName)
                {
                    using var secret = vault.Read(account.Id);
                    if (secret is null) throw new InvalidOperationException("保存的密码不可用。");
                    writer.Set(page, 0, account.UserName, token); writer.Set(page, 1, secret.Characters, token);
                    episode.CredentialKeys = activity.KeyboardGenerationFor(page.Window); Stage("Credentials.DefaultCorrected");
                }
                writer.Set(page, 2, episode.Captcha.Value!, token);
                episode.UserNameConfirmed = LoginLocator.ReadNonSecretValue(writer.Require(page, token).Fields[0]) == account.UserName;
                Stage("Submitting");
                var login = new AutoLoginController(policy, locator, activity, Stage, loginTimeout);
                episode.Login = await login.SubmitAsync(writer.Require(page, token), account, episode.Captcha.Value!, episode.Captcha.ImageHash,
                    episode.CredentialKeys, false, () => canContinue() && gate.TryReserve(page.ProcessKey), token);
                episode.Counts.Submissions = episode.Login.Submitted ? 1 : 0;
                if (episode.Login.Outcome == LoginOutcome.Succeeded) gate.MarkSucceeded(page.ProcessKey);
            }
            catch (OperationCanceledException)
            { episode.Stage = "Cancelled"; episode.Fill ??= new(FillOutcome.Interrupted, "配置或窗口已变化，本轮未继续提交。"); }
            catch (Exception e) when (NewEraLocator.IsAutomationError(e) || e is IOException or System.ComponentModel.Win32Exception)
            { episode.Stage = "Stopped." + e.GetType().Name; episode.Fill = new(FillOutcome.Partial, "后台字段内容未能确认，已停止本轮提交。"); }
        }, CancellationToken.None).WaitAsync(token); }
        catch (OperationCanceledException)
        {
            episode.Completed = true; episode.Stage = "Cancelled";
            if (episode.Counts.Submissions > 0) episode.Login = new(LoginOutcome.Pending, true, "已请求登录，正在等待结果；不会重复提交。");
            else episode.Fill = new(FillOutcome.Interrupted, "窗口或配置已变化，未继续提交登录。");
        }
    }
}
