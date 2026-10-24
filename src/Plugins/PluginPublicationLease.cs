using System;
using System.Threading;

namespace LiteMonitor.src.Plugins
{
    // The manager owns Gate. Retirement, cache/data commits and timer bookkeeping all
    // linearize here. Never cancel a CTS, await, parse payloads or dispatch events here.
    internal sealed class PluginPublicationLease
    {
        internal object Gate { get; }
        private readonly CancellationToken _token;
        private bool _active = true;

        internal PluginPublicationLease(object gate, CancellationToken token)
        {
            Gate = gate;
            _token = token;
        }

        internal bool TryCommit(Action commit)
        {
            lock (Gate)
            {
                if (!_active || _token.IsCancellationRequested) return false;
                commit();
                return true;
            }
        }

        internal bool IsCurrent
        {
            get { lock (Gate) return _active && !_token.IsCancellationRequested; }
        }

        internal void Retire()
        {
            lock (Gate) _active = false;
        }
    }
}
