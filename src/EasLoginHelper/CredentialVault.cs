// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EasLoginHelper;

public sealed class CredentialSecret(char[] characters) : IDisposable
{
    public char[] Characters { get; } = characters;
    public void Dispose() => Array.Clear(Characters);
}

public sealed class CredentialVault(string prefix = "EasLoginHelper/Account/")
{
    private string Target(Guid id) => prefix + id.ToString("N");

    public void Save(Guid id, string userName, ReadOnlySpan<char> password)
    {
        if (password.IsEmpty || password.Length > 1280) throw new ArgumentException("密码长度需为 1–1280 个字符。");
        var bytes = new byte[Encoding.Unicode.GetByteCount(password)];
        Encoding.Unicode.GetBytes(password, bytes);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var credential = new NativeCredential
            {
                Type = 1, TargetName = Target(id), UserName = userName, Persist = 2,
                CredentialBlob = memory, CredentialBlobSize = (uint)bytes.Length
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存 Windows 凭据。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var i = 0; i < password.Length * 2; i++) Marshal.WriteByte(memory, i, 0);
            Marshal.FreeHGlobal(memory);
        }
    }

    public CredentialSecret? Read(Guid id)
    {
        if (!CredRead(Target(id), 1, 0, out var memory))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "无法读取 Windows 凭据。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(memory);
            if (credential.CredentialBlobSize is 0 or > 2560 || credential.CredentialBlobSize % 2 != 0)
                throw new InvalidDataException("保存的凭据格式无效，请重新设置密码。");
            var chars = new char[credential.CredentialBlobSize / 2];
            Marshal.Copy(credential.CredentialBlob, chars, 0, chars.Length);
            return new CredentialSecret(chars);
        }
        finally { CredFree(memory); }
    }

    public void Delete(Guid id)
    {
        if (!CredDelete(Target(id), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法删除 Windows 凭据。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr memory);
}

public sealed class AccountService
{
    private readonly SettingsStore? _desktopStore;
    private readonly ModuleId _desktopModule;
    private readonly Func<AccountSettings> _current;
    private readonly Action<Action<AccountSettings>> _update;
    private readonly CredentialVault _vault;

    public AccountService(SettingsStore store, CredentialVault vault, ModuleId module = ModuleId.Eas)
        : this(vault, () => store.Current.For(module), change => store.Update(s => change(s.For(module))))
    {
        _desktopStore = store;
        _desktopModule = module;
    }

    internal AccountService(CredentialVault vault, Func<AccountSettings> current, Action<Action<AccountSettings>> update)
    {
        _vault = vault;
        _current = current;
        _update = update;
    }

    public void Save(Guid id, string label, string userName, string password)
        => SaveSecret(id, label, userName, password.AsSpan());

    private void SaveSecret(Guid id, string label, string userName, ReadOnlySpan<char> password)
    {
        label = label.Trim(); userName = userName.Trim();
        if (string.IsNullOrWhiteSpace(label) || label.Length > 80 || string.IsNullOrWhiteSpace(userName) || userName.Length > 256)
            throw new ArgumentException("请填写有效的账号名称和用户名。");
        if (userName.Any(char.IsControl))
            throw new ArgumentException("账号和密码不能包含换行、Tab 等控制字符。");
        foreach (var character in password) if (char.IsControl(character))
            throw new ArgumentException("账号和密码不能包含换行、Tab 等控制字符。");
        if (_desktopStore is not null && _desktopStore.Current
            .For(_desktopModule == ModuleId.Eas ? ModuleId.NewEra : ModuleId.Eas).Accounts.Any(a => a.Id == id))
            throw new ArgumentException("账号凭据属于另一个模块。");
        var previous = _current().Accounts.Find(a => a.Id == id);
        using var oldSecret = _vault.Read(id);
        if (password.Length == 0 && oldSecret is null) throw new ArgumentException("新账号需要填写密码。");
        var replace = password.Length > 0;
        if (replace) _vault.Save(id, userName, password);
        try
        {
            _update(target =>
            {
                target.Accounts.RemoveAll(a => a.Id == id);
                target.Accounts.Add(new AccountProfile(id, label, userName));
                target.DefaultAccountId ??= id;
            });
        }
        catch
        {
            if (replace)
            {
                if (oldSecret is not null) _vault.Save(id, previous?.UserName ?? userName, oldSecret.Characters);
                else _vault.Delete(id);
            }
            throw;
        }
    }

    public void Delete(Guid id)
    {
        var previous = _current().Accounts.Find(a => a.Id == id);
        if (previous is null) return;
        using var oldSecret = _vault.Read(id);
        _vault.Delete(id);
        try
        {
            _update(target =>
            {
                target.Accounts.RemoveAll(a => a.Id == id);
                if (target.DefaultAccountId == id) target.DefaultAccountId = target.Accounts.FirstOrDefault()?.Id;
            });
        }
        catch
        {
            if (oldSecret is not null) _vault.Save(id, previous.UserName, oldSecret.Characters);
            throw;
        }
    }

    public Guid CopyFrom(ModuleId sourceModule, Guid sourceId)
    {
        if (_desktopStore is null || sourceModule == _desktopModule) throw new ArgumentException("请选择另一个模块的账号。");
        var source = _desktopStore.Current.For(sourceModule).Accounts.Find(a => a.Id == sourceId)
            ?? throw new ArgumentException("源账号不存在。");
        using var secret = _vault.Read(source.Id);
        if (secret is null) throw new InvalidOperationException("源账号没有保存密码，请先重新保存。");
        var newId = Guid.NewGuid();
        SaveSecret(newId, source.Label, source.UserName, secret.Characters);
        return newId;
    }
}
