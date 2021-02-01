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

partial struct Model : IChiselContainer
{
    internal ChiselMeshManager                  chiselMeshes;
    internal ChiselIntersectionManager          intersections;
    internal GeneratedSurfaceManager            surfaces;
    internal CompactTree                        packedHierarchy;
    
    internal NativeList<SubModel>               subModels;
    internal int maximumRequiredMeshes;

    internal uint   selfCachedHash;
    internal uint   cachedHash;

    public uint ModelID                 { get; }
    public int  ChildCount              { get { return 0; } }
    
    public ref  IChiselChild            GetChildAt(int index)           { throw new NotImplementedException(); }
    public ref  ChiselTransformation    GetChildTransformAt(int index)  { throw new NotImplementedException(); }
    public ref  Operation               GetChildOperationAt(int index)  { throw new NotImplementedException(); }
    
    public int  SubModelCount           { get { return subModels.Length; } }
    public unsafe SubModel              GetSubModelAt(int index)        { return subModels[index]; }

    uint GetSelfHash() { return 0; }

    public uint GetHash() 
    {
        if (cachedHash == 0)
        {
            if (selfCachedHash == 0)
                selfCachedHash = GetSelfHash();
            cachedHash = math.hash(new uint2(selfCachedHash, this.GetChildrenHashes()));
        }
        return cachedHash;
    }
}


partial struct SubModel : IChiselChild, IChiselContainer
{
    internal uint selfCachedHash;
    internal uint cachedHash;
    internal int maximumRequiredMeshes;

    public uint SubModelID              { get; }
    public int  ChildCount              { get { return 0; } }
    public ref  IChiselChild            GetChildAt(int index) { throw new NotImplementedException(); }
    public ref  ChiselTransformation    GetChildTransformAt(int index) { throw new NotImplementedException(); }
    public ref  Operation               GetChildOperationAt(int index) { throw new NotImplementedException(); }

    uint GetSelfHash() 
    { 
        return 0; 
    }

    public uint GetHash()
    {
        if (cachedHash == 0)
        {
            if (selfCachedHash == 0)
                selfCachedHash = GetSelfHash();
            cachedHash = math.hash(new uint2(selfCachedHash, this.GetChildrenHashes()));
        }
        return cachedHash;
    }

}

partial struct Composite : IChiselChild, IChiselContainer
{
    internal uint selfCachedHash;
    internal uint cachedHash;

    public int ChildCount { get { return 0; } }
    public ref IChiselChild GetChildAt(int index) { throw new NotImplementedException(); }
    public ref ChiselTransformation GetChildTransformAt(int index) { throw new NotImplementedException(); }
    public ref Operation GetChildOperationAt(int index) { throw new NotImplementedException(); }

    uint GetSelfHash() 
    { 
        return 0; 
    }

    public uint GetHash()
    {
        if (cachedHash == 0)
        {
            if (selfCachedHash == 0)
                selfCachedHash = GetSelfHash();
            cachedHash = math.hash(new uint2(selfCachedHash, this.GetChildrenHashes()));
        }
        return cachedHash;
    }
}

partial struct Brush : IChiselChild
{
    public uint meshID;
    
    public uint GetHash() { return meshID; }
}

partial struct ChiselTransformation : IChiselHash
{
    public float4x4 transformation;
    public float4x4 localToModel;
    internal uint selfCachedHash;

    public uint GetHash()
    {
        if (selfCachedHash == 0)
            selfCachedHash = math.hash(transformation);
        return selfCachedHash;
    }
}

public static class IChiselContainerExtensions
{
    public static uint GetChildrenHashes(this IChiselContainer container)
    {
        uint hash = 0;
        for (int i = 0; i < container.ChildCount; i++)
        {
            ref var child           = ref container.GetChildAt(i);
            ref var transformation  = ref container.GetChildTransformAt(i);
            ref var operation       = ref container.GetChildOperationAt(i);
            hash = math.hash(new uint4(child.GetHash(), transformation.GetHash(), (uint)operation, hash));
        }
        return hash;
    }
}