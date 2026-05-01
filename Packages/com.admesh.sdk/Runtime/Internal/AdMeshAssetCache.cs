using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace AdMesh.Internal
{
    internal static class AdMeshAssetCache
    {
        private const int MaxCachedAssets = 16;
        private static readonly Dictionary<string, CacheEntry> Entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private static string _cacheRoot;

        internal static string BuildAssetKey(string assetId, string assetVersion)
        {
            var normalizedId = string.IsNullOrWhiteSpace(assetId) ? "asset" : assetId.Trim();
            var normalizedVersion = string.IsNullOrWhiteSpace(assetVersion) ? "v1" : assetVersion.Trim();
            return $"{SanitizeSegment(normalizedId)}_{SanitizeSegment(normalizedVersion)}";
        }

        internal static bool TryGetCachedAsset(string assetId, string assetVersion, out CachedAssetHandle handle)
        {
            var assetKey = BuildAssetKey(assetId, assetVersion);
            if (Entries.TryGetValue(assetKey, out var entry) && File.Exists(entry.LocalPath))
            {
                entry.LastAccessUtc = DateTime.UtcNow;
                handle = new CachedAssetHandle(assetKey, entry.LocalPath, entry.MediaType, true);
                return true;
            }

            handle = default;
            return false;
        }

        internal static IEnumerator EnsureAssetAvailable(string assetId, string assetVersion, string mediaType, string mediaUrl, Action<CachedAssetHandle?> onComplete)
        {
            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                onComplete?.Invoke(null);
                yield break;
            }

            if (TryGetCachedAsset(assetId, assetVersion, out var cachedHandle))
            {
                onComplete?.Invoke(cachedHandle);
                yield break;
            }

            using (var request = UnityWebRequest.Get(mediaUrl))
            {
                request.timeout = 20;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    onComplete?.Invoke(null);
                    yield break;
                }

                var bytes = request.downloadHandler.data;
                var assetKey = BuildAssetKey(assetId, assetVersion);
                var extension = ResolveExtension(mediaType, mediaUrl);
                var filePath = Path.Combine(GetCacheRoot(), $"{assetKey}{extension}");
                File.WriteAllBytes(filePath, bytes);

                Entries[assetKey] = new CacheEntry
                {
                    AssetKey = assetKey,
                    LocalPath = filePath,
                    MediaType = mediaType ?? string.Empty,
                    LastAccessUtc = DateTime.UtcNow,
                };

                EvictIfNeeded();
                onComplete?.Invoke(new CachedAssetHandle(assetKey, filePath, mediaType, false));
            }
        }

        private static void EvictIfNeeded()
        {
            if (Entries.Count <= MaxCachedAssets)
            {
                return;
            }

            var ordered = new List<CacheEntry>(Entries.Values);
            ordered.Sort((left, right) => left.LastAccessUtc.CompareTo(right.LastAccessUtc));

            while (Entries.Count > MaxCachedAssets && ordered.Count > 0)
            {
                var candidate = ordered[0];
                ordered.RemoveAt(0);
                Entries.Remove(candidate.AssetKey);
                if (File.Exists(candidate.LocalPath))
                {
                    File.Delete(candidate.LocalPath);
                }
            }
        }

        private static string GetCacheRoot()
        {
            if (!string.IsNullOrWhiteSpace(_cacheRoot))
            {
                return _cacheRoot;
            }

            _cacheRoot = Path.Combine(Application.persistentDataPath, "admesh_asset_cache");
            Directory.CreateDirectory(_cacheRoot);
            return _cacheRoot;
        }

        private static string ResolveExtension(string mediaType, string mediaUrl)
        {
            if (string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase))
            {
                return ".mp4";
            }

            var extension = Path.GetExtension(mediaUrl);
            return string.IsNullOrWhiteSpace(extension) ? ".bin" : extension;
        }

        private static string SanitizeSegment(string value)
        {
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidChar, '_');
            }

            return value.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
        }

        private sealed class CacheEntry
        {
            public string AssetKey;
            public string LocalPath;
            public string MediaType;
            public DateTime LastAccessUtc;
        }
    }

    internal struct CachedAssetHandle
    {
        public CachedAssetHandle(string assetKey, string localPath, string mediaType, bool cacheHit)
        {
            AssetKey = assetKey;
            LocalPath = localPath;
            MediaType = mediaType ?? string.Empty;
            CacheHit = cacheHit;
        }

        public string AssetKey { get; }
        public string LocalPath { get; }
        public string MediaType { get; }
        public bool CacheHit { get; }

        public string PlaybackUrl => $"file:///{LocalPath.Replace("\\", "/")}";
    }
}
