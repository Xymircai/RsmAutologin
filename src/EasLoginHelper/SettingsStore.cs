// SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.Json;

namespace EasLoginHelper;

public sealed class SettingsStore
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasLoginHelper");
    public string FilePath { get; }
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };
    public AppSettings Current { get; private set; }

    public SettingsStore(string? filePath = null, bool persistMigration = true)
    {
        FilePath = filePath ?? Path.Combine(DataDirectory, "settings.json");
        Current = new AppSettings();
        if (!File.Exists(FilePath)) return;
        var original = File.ReadAllText(FilePath);
        using var document = JsonDocument.Parse(original);
        var version = document.RootElement.TryGetProperty("SchemaVersion", out var property) ? property.GetInt32() : 1;
        if (version == 1 && (!document.RootElement.TryGetProperty("Accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array) ||
            version == 2 && (!document.RootElement.TryGetProperty("Eas", out var eas) || eas.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("NewEra", out var newEra) || newEra.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("账号配置缺少必要模块，未自动覆盖原文件。");
        if (version == 1)
        {
            Current.Eas = JsonSerializer.Deserialize<ModuleSettings>(original, JsonOptions)
                ?? throw new InvalidDataException("账号配置为空。请保留原文件后检查配置。");
        }
        else Current = JsonSerializer.Deserialize<AppSettings>(original, JsonOptions)
            ?? throw new InvalidDataException("账号配置为空。请保留原文件后检查配置。");
        Validate(Current);
        if (version == 1 && persistMigration)
        {
            var backup = FilePath + ".schema1.bak";
            if (!File.Exists(backup)) File.Copy(FilePath, backup, false);
            Update(_ => { });
        }
    }

    public void Update(Action<AppSettings> change)
    {
        // Work on metadata only; no password can enter the JSON model.
        var next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Current, JsonOptions), JsonOptions)!;
        change(next);
        Validate(next);
        WriteAtomically(FilePath, next);
        Current = next;
    }

    internal static void WriteAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Validate(AppSettings value)
    {
        if (value.SchemaVersion != 2 || !Enum.IsDefined(value.SelectedModule) || value.Eas is null || value.NewEra is null)
            throw new InvalidDataException("模块配置格式无效，未自动覆盖原文件。");
        ValidateModule(value.Eas);
        ValidateModule(value.NewEra);
        if (value.Eas.Accounts.Select(a => a.Id).Intersect(value.NewEra.Accounts.Select(a => a.Id)).Any())
            throw new InvalidDataException("两个模块的账号凭据标识必须独立。");
    }

    internal static void ValidateAccounts(AccountSettings value)
    {
        if (value.Accounts is null || value.Accounts.Count > 100 ||
            value.Accounts.Any(a => a is null || a.Id == Guid.Empty || string.IsNullOrWhiteSpace(a.UserName) ||
                a.UserName.Length > 256 || string.IsNullOrWhiteSpace(a.Label) || a.Label.Length > 80) ||
            value.Accounts.Select(a => a.Id).Distinct().Count() != value.Accounts.Count ||
            value.DefaultAccountId is not null && value.DefaultAccount is null)
            throw new InvalidDataException("账号配置格式无效，未自动覆盖原文件。");
    }

    private static void ValidateModule(ModuleSettings value)
    {
        ValidateAccounts(value);
        if (value.Calibration is { } c && (c.Panel is null || c.Captcha is null || c.Anchor is null || c.Fields is null || c.Fields.Length != 3 ||
            !c.Panel.IsValid || c.Fields.Any(r => r is null || !r.IsValid) || !c.Captcha.IsValid ||
            !c.Anchor.IsValid || c.OriginalDpi is < 48 or > 768 || c.AnchorGray is null || c.AnchorGray.Length != VisualLocator.AnchorWidth * VisualLocator.AnchorHeight ||
            c.OriginalClientWidth < 200 || c.OriginalClientHeight < 200 || !Path.IsPathFullyQualified(c.ExecutablePath)))
            throw new InvalidDataException("登录页校准数据无效。请保留原配置后重新校准。") ;
    }
}
