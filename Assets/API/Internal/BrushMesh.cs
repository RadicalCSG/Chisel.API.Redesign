using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Entities;
using UnityEngine;
using Unity.Burst;
using Debug = UnityEngine.Debug;


public struct ChiselMesh
{
    public uint GetHash() { return 0; }
}

public struct ChiselSurface : IChiselHash
{
    public uint GetHash() { return 0; }
}

public struct ChiselMeshManager
{
    NativeList<ChiselMesh> chiselMeshes;
    NativeHashMap<uint, int2> chiselMeshHashes;
    NativeList<int> emptyChiselMeshIndices;

    public int RegisterMesh(ref ChiselMesh mesh)
    {
        var meshHash = mesh.GetHash();
        if (!chiselMeshHashes.TryGetValue(meshHash, out int2 indexUsageCount))
        {
            if (emptyChiselMeshIndices.Length == 0)
            {
                chiselMeshHashes[meshHash] = new int2(chiselMeshes.Length, 1);
                chiselMeshes.Add(mesh);
            } else
            {
                var lastIndex = emptyChiselMeshIndices[emptyChiselMeshIndices.Length - 1];
                emptyChiselMeshIndices.ResizeUninitialized(emptyChiselMeshIndices.Length - 1);
                chiselMeshHashes[meshHash] = new int2(lastIndex, 1);
                chiselMeshes[lastIndex] = mesh;
            }
        } else
        {
            indexUsageCount.y++;
            chiselMeshHashes[meshHash] = indexUsageCount;
        }
        return indexUsageCount.x;
    }

    public void UnregisterMesh(ref ChiselMesh mesh)
    {
        var meshHash = mesh.GetHash();
        if (!chiselMeshHashes.TryGetValue(meshHash, out int2 indexUsageCount))
            throw new ArgumentOutOfRangeException($"Cannot unregister mesh because it's not registered");

        indexUsageCount.y--;
        if (indexUsageCount.y > 0)
        {
            chiselMeshHashes[meshHash] = indexUsageCount;
            return;
        }

        chiselMeshes[indexUsageCount.x] = default;
        emptyChiselMeshIndices.Add(indexUsageCount.x);
        chiselMeshHashes.Remove(meshHash);
    }
}
