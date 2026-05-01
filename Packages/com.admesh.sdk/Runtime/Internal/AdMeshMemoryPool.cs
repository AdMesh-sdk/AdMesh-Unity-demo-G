using System.Collections.Generic;
using UnityEngine;

namespace AdMesh.Internal
{
    internal static class AdMeshMemoryPool
    {
        private static readonly Dictionary<int, Queue<Texture2D>> TexturePools = new Dictionary<int, Queue<Texture2D>>();
        private const int MaxPoolSize = 50;

        internal static Texture2D GetTexture(int width, int height, TextureFormat format = TextureFormat.ARGB32, bool mipChain = false)
        {
            var key = GetTextureKey(width, height, format);
            if (TexturePools.TryGetValue(key, out var pool) && pool.Count > 0)
            {
                var texture = pool.Dequeue();
                texture.Reinitialize(width, height, format, mipChain);
                return texture;
            }

            return new Texture2D(width, height, format, mipChain);
        }

        internal static void ReturnTexture(Texture2D texture)
        {
            if (texture == null)
            {
                return;
            }

            if (texture.width > 2048 || texture.height > 2048)
            {
                UnityEngine.Object.Destroy(texture);
                return;
            }

            var key = GetTextureKey(texture.width, texture.height, texture.format);
            if (!TexturePools.TryGetValue(key, out var pool))
            {
                pool = new Queue<Texture2D>();
                TexturePools[key] = pool;
            }

            if (pool.Count < MaxPoolSize)
            {
                pool.Enqueue(texture);
            }
            else
            {
                UnityEngine.Object.Destroy(texture);
            }
        }

        private static int GetTextureKey(int width, int height, TextureFormat format) => ((width << 16) | height) ^ (int)format;
    }
}
