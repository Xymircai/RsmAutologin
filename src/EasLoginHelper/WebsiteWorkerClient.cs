// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace EasLoginHelper;

internal sealed class WebsiteWorkerClient : IDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<string, WebsiteNotice> _attempts = new();
    private NamedPipeClientStream? _pipe;
    private bool _disposed;

    internal event Action<WebsiteNotice>? StatusChanged;

    internal async Task<WebsiteAccountSettings?> SendAsync(WebsiteRequest request, bool start = true)
    {
        if (_disposed) return null;
        try { await _serial.WaitAsync(_stopping.Token); }
        catch (OperationCanceledException) when (_disposed) { return null; }
        try
        {
            if (_pipe is null && !start) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            WebsiteReply reply;
            try
            {
                var pipe = _pipe ??= await ConnectAsync(deadline.Token);
                await WebsitePipe.WriteAsync(pipe, request, deadline.Token);
                reply = await WebsitePipe.ReadAsync<WebsiteReply>(pipe, deadline.Token);
            }
            catch (Exception e) when (e is COMException or Win32Exception or IOException or
                UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or JsonException)
            {
                _pipe?.Dispose();
                _pipe = null;
                if (_disposed) return null;
                foreach (var pending in _attempts.Values)
                    StatusChanged?.Invoke(pending with { Stage = WebsiteStage.Unconfirmed, Reason = WebsiteReason.None });
                _attempts.Clear();
                throw new IOException("无法连接当前桌面用户的网站助手。请重新打开网站页签后重试。", e);
            }
            if (!reply.Ok) throw new IOException(reply.Error ?? "网站操作未完成。");
            foreach (var notice in reply.Notices ?? [])
                if (Enum.IsDefined(notice.Stage) && Enum.IsDefined(notice.Reason) && Website.All.Any(s => s.Key == notice.Site))
                {
                    if (notice.AttemptId is { Length: 32 } id && id.All(char.IsAsciiHexDigitLower))
                    {
                        if (WebsiteLaunchTickets.IsTerminal(notice.Stage)) _attempts.Remove(id);
                        else _attempts[id] = notice;
                    }
                    StatusChanged?.Invoke(notice);
                }
            return reply.Settings;
        }
        finally { _serial.Release(); }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("无法确定助手的 EXE 路径。");
        var user = WebsiteProcessIdentity.DesktopUser();
        using var caller = WindowsIdentity.GetCurrent();
        var owner = caller.User ?? throw new IOException("无法确定助手的 Windows 用户。");
        var key = Guid.NewGuid().ToString("N");
        ExplorerDesktopLaunch.Open(executable, $"{WebsiteWorker.Argument} {key} {Environment.ProcessId} {owner.Value}");
        var pipe = new NamedPipeClientStream(".", WebsiteWorker.PipePrefix + key,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        try
        {
            await pipe.ConnectAsync(token);
            if (!WebsiteProcessIdentity.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var id) ||
                !WebsiteProcessIdentity.IsCaller(id, user))
                throw new IOException("无法验证网站助手的 Windows 用户。");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping.Cancel();
        _pipe?.Dispose();
        _pipe = null;
        _stopping.Dispose();
    }
}
