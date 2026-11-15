// SPDX-License-Identifier: MPL-2.0
namespace EasLoginHelper;

// Both modules share this lease. Acquiring it never activates a window.
public sealed class InputCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IDisposable? TryEnter() => _gate.Wait(0) ? new Lease(_gate) : null;
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release(); }
    }
}
