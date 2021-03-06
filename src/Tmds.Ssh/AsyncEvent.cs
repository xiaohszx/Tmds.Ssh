// This file is part of Tmds.Ssh which is released under MIT.
// See file LICENSE for full license details.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tmds.Ssh
{
    sealed class AsyncEvent : IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(0);
        private int _waiters;
        private const int SET = -1;

        public async ValueTask WaitAsync(CancellationToken ct1, CancellationToken ct2 = default)
        {
            do
            {
                int waiters = Volatile.Read(ref _waiters);
                if (waiters == SET)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _waiters, waiters + 1, waiters) == waiters)
                {
                    if (ct1.CanBeCanceled && ct2.CanBeCanceled)
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct1, ct2);
                        await _semaphore.WaitAsync(cts.Token).ConfigureAwait(false);
                    }
                    else if (!ct2.CanBeCanceled)
                    {
                        await _semaphore.WaitAsync(ct1).ConfigureAwait(false);
                    }
                }
            } while (true);
        }

        public void Set()
        {
            int waiters = Interlocked.Exchange(ref _waiters, SET);
            if (waiters > 0)
            {
                _semaphore.Release(waiters);
            }
        }

        public void Dispose()
        {
            _semaphore.Dispose();
        }
    }
}