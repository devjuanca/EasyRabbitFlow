using System;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Services
{
    // Serialises a consumer's recovery passes and remembers the requests that arrive while one is running, so a
    // cancel or a shutdown of the very channel the running pass just built is never lost: the holder either goes
    // round again before leaving, or is told on the way out that a fresh pass is due.
    internal sealed class ConsumerRecoveryGate : IDisposable
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        // 1 while a request that arrived with the gate held is still waiting for a pass to honour it.
        private int _pending;

        /// <summary>
        /// Tries to become the running recovery. <c>false</c> when another recovery holds the gate: the request has
        /// been recorded for it (see <see cref="TakePending"/> and <see cref="Exit"/>).
        /// </summary>
        public bool TryEnter()
        {
            if (_gate.Wait(0))
            {
                return true;
            }

            Interlocked.Exchange(ref _pending, 1);

            // The holder may have left between the failed probe and the flag, in which case its Exit read the
            // flag as clear. Probe once more: either this caller becomes the holder itself (and BeginPass clears
            // the flag), or the gate is still held and the holder's Exit, which reads the flag after releasing,
            // is guaranteed to see it.
            return _gate.Wait(0);
        }

        /// <summary>The holder starts a pass; every request recorded before this moment is satisfied by it.</summary>
        public void BeginPass() => Interlocked.Exchange(ref _pending, 0);

        /// <summary>The holder finished a pass: <c>true</c> when a request arrived during it, so another pass is due.</summary>
        public bool TakePending() => Interlocked.Exchange(ref _pending, 0) == 1;

        /// <summary>
        /// The holder leaves. <c>true</c> when a request arrived after its last <see cref="TakePending"/> and found the
        /// gate still held: nobody else will honour it, so the caller must trigger a new recovery.
        /// </summary>
        public bool Exit()
        {
            _gate.Release();

            return Volatile.Read(ref _pending) == 1;
        }

        /// <summary>Bounded wait for the gate, for a caller that is not a recovery (shutdown).</summary>
        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _gate.WaitAsync(timeout, cancellationToken);

        public void Release() => _gate.Release();

        public void Dispose() => _gate.Dispose();
    }
}
