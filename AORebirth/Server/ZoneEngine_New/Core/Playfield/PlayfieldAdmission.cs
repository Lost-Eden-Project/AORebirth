namespace ZoneEngine_New.Core.Playfield
{
    using System;
    using System.Threading;

    /// <summary>Keeps a destination alive until queued owner work or a transfer takes responsibility.</summary>
    internal sealed class PlayfieldAdmission : IDisposable
    {
        private Playfield? _owner;

        internal PlayfieldAdmission(Playfield playfield)
        {
            Playfield = playfield;
            _owner = playfield;
        }

        public Playfield Playfield { get; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseAdmission();
    }
}
