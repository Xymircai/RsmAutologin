// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace EasLoginHelper;

public sealed record ModuleRuntime(string Status, bool Paused, bool RecognizedLogin = false, string? LastStage = null,
    bool LoginAttempted = false, bool LoggedIn = false, bool AutoLoginEnabled = true, int Submissions = 0,
    int UserNameWrites = 0, int PasswordWrites = 0, int CaptchaWrites = 0, string? ErrorCode = null);

public sealed class RuntimeStatusWriter(string directory)
{
    public void Write(ModuleRuntime eas, ModuleRuntime newEra)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "runtime-status.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
            {
                FormatVersion = 2, CreatedUtc = DateTime.UtcNow, ProcessId = Environment.ProcessId,
                Modules = new Dictionary<string, ModuleRuntime> { ["eas"] = eas, ["new-era"] = newEra }
            }, SettingsStore.JsonOptions));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
