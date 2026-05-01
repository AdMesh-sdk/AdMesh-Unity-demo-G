using System;

namespace AdMesh.Core
{
    [Serializable]
    public class AdRequest
    {
        public string AdFormat { get; private set; } = "image";
        public bool TestMode { get; private set; }

        public sealed class Builder
        {
            private readonly AdRequest _request = new AdRequest();

            public Builder SetAdFormat(string adFormat)
            {
                if (!string.IsNullOrWhiteSpace(adFormat))
                {
                    _request.AdFormat = adFormat.Trim();
                }
                return this;
            }

            public Builder SetTestMode(bool testMode)
            {
                _request.TestMode = testMode;
                return this;
            }

            public AdRequest Build() => _request;
        }
    }
}
