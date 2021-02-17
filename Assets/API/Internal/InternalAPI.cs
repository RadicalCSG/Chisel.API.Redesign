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
using System.Collections.Generic;

partial struct ChiselCSGModel : IChiselNodeContainer, IChiselMeshContainer, IDisposable
{
    internal ChiselMeshManager                  chiselMeshes;
    internal ChiselIntersectionManager          intersections;
    internal GeneratedSurfaceManager            surfaces;
    internal CompactTree                        packedHierarchy;
    
    internal NativeList<ChiselCSGSubModel>      subModels;
    internal int    maximumRequiredMeshes;

    internal uint   selfCachedHash;
    internal uint   cachedHash;

    internal bool   changed; // TODO: "Somehow" update this when *anything* in the model changes

    public uint ModelID                 { get; private set; }
    public uint MeshContainerID         { get; private set; }
    public int  ChildCount              { get { return 0; } }
    
    public ref  IChiselChild            GetChildAt(int index)           { throw new NotImplementedException(); }
    public ref  ChiselTransformation    GetChildTransformAt(int index)  { throw new NotImplementedException(); }
    public ref  Operation               GetChildOperationAt(int index)  { throw new NotImplementedException(); }
    
    public int  SubModelCount           { get { return subModels.Length; } }
    public unsafe ChiselCSGSubModel              GetSubModelAt(int index)        { return subModels[index]; }

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

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    NativeList<RenderSurfaceSettings>           uniqueRenderSurfaceSettings;
    public NativeList<RenderSurfaceSettings>    UniqueRenderSurfaceSettings     { get { return uniqueRenderSurfaceSettings; } }
    NativeList<ColliderSurfaceSettings>         uniqueColliderSurfaceSettings;
    public NativeList<ColliderSurfaceSettings>  UniqueColliderSurfaceSettings   { get { return uniqueColliderSurfaceSettings; } }


    // TODO: implement this 
    [BurstDiscard]
    public GeneratedModelMeshes GetGeneratedModelMeshes()
    {
        throw new NotImplementedException();
    }

    // TODO: implement this 
    [BurstDiscard]
    public ModelBehaviour GetGameObjectForModel()
    {
        throw new NotImplementedException();
    }

    // TODO: implement this 
    static readonly List<IChiselMeshContainer> s_MeshContainerList = new List<IChiselMeshContainer>();
    [BurstDiscard]
    public List<IChiselMeshContainer> GetMeshContainerList()
    {
        // Make a list of all containers
        // TODO: instead of building this list, just manage this per model (we're already storing submodels in a list)
        s_MeshContainerList.Clear();
        s_MeshContainerList.Add(this);
        for (int s = 0; s < this.SubModelCount; s++)
            s_MeshContainerList.Add(this.GetSubModelAt(s));
        return s_MeshContainerList;
    }

}


partial struct ChiselCSGSubModel : IChiselChild, IChiselNodeContainer, IChiselMeshContainer, IDisposable
{
    internal uint selfCachedHash;
    internal uint cachedHash;
    internal int maximumRequiredMeshes;

    public uint SubModelID              { get; private set; }
    public uint MeshContainerID         { get; private set; }
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

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    NativeList<RenderSurfaceSettings>           uniqueRenderSurfaceSettings;
    public NativeList<RenderSurfaceSettings>    UniqueRenderSurfaceSettings     { get { return uniqueRenderSurfaceSettings; } }
    NativeList<ColliderSurfaceSettings>         uniqueColliderSurfaceSettings;
    public NativeList<ColliderSurfaceSettings>  UniqueColliderSurfaceSettings   { get { return uniqueColliderSurfaceSettings; } }
}

partial struct ChiselCSGComposite : IChiselChild, IChiselNodeContainer
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

partial struct ChiselCSGBrush : IChiselChild
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
    public static uint GetChildrenHashes(this IChiselNodeContainer container)
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