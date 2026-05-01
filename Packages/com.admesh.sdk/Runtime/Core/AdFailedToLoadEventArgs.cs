using System;

namespace AdMesh.Core
{
    public sealed class AdFailedToLoadEventArgs : EventArgs
    {
        public AdFailedToLoadEventArgs(string message)
        {
            Message = message;
        }

        public string Message { get; }
    }
}
