// SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace EasLoginHelper;

public sealed record AccountProfile(Guid Id, string Label, string UserName);

public enum ModuleId { Eas, NewEra }

public static class Modules
{
    public static string Name(ModuleId module) => module == ModuleId.Eas ? "EAS / 工具箱" : "新纪元";
    public static string Key(ModuleId module) => module == ModuleId.Eas ? "eas" : "new-era";
}

public sealed class ModuleSettings
{
    public List<AccountProfile> Accounts { get; set; } = [];
    public Guid? DefaultAccountId { get; set; }
    public bool OcrEnabled { get; set; } = true;
    public bool AutoLoginEnabled { get; set; } = true;
    public Calibration? Calibration { get; set; }
    [JsonIgnore] public AccountProfile? DefaultAccount => Accounts.Find(a => a.Id == DefaultAccountId);
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public ModuleSettings Eas { get; set; } = new();
    public ModuleSettings NewEra { get; set; } = new() { OcrEnabled = false };
    public ModuleId SelectedModule { get; set; }
    public ModuleSettings For(ModuleId module) => module == ModuleId.Eas ? Eas : NewEra;

    // Source compatibility for the established EAS controllers. These aliases never enter JSON.
    [JsonIgnore] public List<AccountProfile> Accounts { get => Eas.Accounts; set => Eas.Accounts = value; }
    [JsonIgnore] public Guid? DefaultAccountId { get => Eas.DefaultAccountId; set => Eas.DefaultAccountId = value; }
    [JsonIgnore] public bool OcrEnabled { get => Eas.OcrEnabled; set => Eas.OcrEnabled = value; }
    [JsonIgnore] public bool AutoLoginEnabled { get => Eas.AutoLoginEnabled; set => Eas.AutoLoginEnabled = value; }
    [JsonIgnore] public Calibration? Calibration { get => Eas.Calibration; set => Eas.Calibration = value; }
    [JsonIgnore] public AccountProfile? DefaultAccount => Eas.DefaultAccount;
}

public sealed record RelativeRect(double X, double Y, double Width, double Height)
{
    public Rectangle Map(Rectangle panel) => new(
        panel.X + (int)Math.Round(X * panel.Width), panel.Y + (int)Math.Round(Y * panel.Height),
        (int)Math.Round(Width * panel.Width), (int)Math.Round(Height * panel.Height));
    public static RelativeRect From(Rectangle region, Rectangle panel) => new(
        (double)(region.X - panel.X) / panel.Width, (double)(region.Y - panel.Y) / panel.Height,
        (double)region.Width / panel.Width, (double)region.Height / panel.Height);
    [JsonIgnore] public bool IsValid => X >= 0 && Y >= 0 && Width > 0 && Height > 0 &&
        X + Width <= 1.001 && Y + Height <= 1.001 && double.IsFinite(X + Y + Width + Height);
}

public sealed class Calibration
{
    public string ExecutablePath { get; set; } = "";
    public string FileVersion { get; set; } = "";
    public int OriginalClientWidth { get; set; }
    public int OriginalClientHeight { get; set; }
    public uint OriginalDpi { get; set; } = 96;
    public RelativeRect Panel { get; set; } = new(0, 0, 0, 0);
    public RelativeRect[] Fields { get; set; } = [];
    public RelativeRect Captcha { get; set; } = new(0, 0, 0, 0);
    public RelativeRect Anchor { get; set; } = new(0, 0, 0, 0);
    // Only the EasyAudit heading is sampled, never a field or a full screenshot.
    public byte[] AnchorGray { get; set; } = [];
}

public enum FillOutcome { Filled, Partial, Interrupted, MissingCredential, InvalidPage }
public sealed record FillResult(FillOutcome Outcome, string Message, string? CaptchaHash = null)
{
    public string? ErrorCode { get; init; }
    public bool CredentialsFilled { get; init; }
    public bool CaptchaFilled { get; init; }
    public string? CaptchaValue { get; init; }
    public long? InputGeneration { get; init; }
}
public sealed record CaptchaResult(string Text, float Confidence, string Message)
{
    public string? ErrorCode { get; init; }
    public bool Accepted => Text.Length == 4 && Text.All(char.IsAsciiDigit) &&
        float.IsFinite(Confidence) && Confidence >= 0.8f;
}
