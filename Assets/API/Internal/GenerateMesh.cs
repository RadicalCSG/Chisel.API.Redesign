using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Unity.Burst;
using Debug = UnityEngine.Debug;
using System.Collections.Generic;
using System.Reflection;

// MeshRenderer per "mesh settings"/layer
//      Meshes
//          Mesh per Material
//      Materials
//          Material list
//
// MeshCollider per PhysicMaterial/layer
//      Mesh


[BurstCompatible, Serializable]
public struct ModelSettings
{
    public readonly static ModelSettings Default = new ModelSettings
    {
        renderingEnabled                = false,
        collidersEnabled                = false,

        // MeshRenderer
        lightProbeProxyVolumeOverride   = null,  
        probeAnchor                     = null,                    
        motionVectorGenerationMode      = MotionVectorGenerationMode.Camera,
        reflectionProbeUsage            = ReflectionProbeUsage.Off,
        lightProbeUsage                 = LightProbeUsage.Off,                
        rayTracingMode                  = RayTracingMode.Off,                 
        receiveGI                       = ReceiveGI.Lightmaps,                      
        allowOcclusionWhenDynamic       = false,      
        stitchLightmapSeams             = false,            
        rendererPriority                = 0,               
        lightmapScaleOffset             = Vector4.zero,            
        realtimeLightmapScaleOffset     = Vector4.zero,    

        // MeshCollider    
        cookingOptions                  = MeshColliderCookingOptions.None,                 
        convex                          = false,                         
        isTrigger                       = false,                      
        contactOffset                   = 0.0f,

        // GameObject
        //staticEditorFlags             = UnityEditor.StaticEditorFlags.BatchingStatic | UnityEditor.StaticEditorFlags.NavigationStatic | UnityEditor.StaticEditorFlags.
    };


    public bool                             renderingEnabled;
    public bool                             collidersEnabled;

    // MeshRenderer
    public GameObject                       lightProbeProxyVolumeOverride;  /// If set, the Renderer will use the Light Probe Proxy Volume component attached to the source GameObject.
    public Transform                        probeAnchor;                    /// If set, Renderer will use this Transform's position to find the light or reflection probe.
    public MotionVectorGenerationMode       motionVectorGenerationMode;     /// Specifies the mode for motion vector rendering.
    public ReflectionProbeUsage             reflectionProbeUsage;           /// Should reflection probes be used for this Renderer?
    public LightProbeUsage                  lightProbeUsage;                /// The light probe interpolation type.
    public RayTracingMode                   rayTracingMode;                 /// Describes how this renderer is updated for ray tracing.
    public ReceiveGI                        receiveGI;                      /// Determines how the object will receive global illumination. (Editor only)
    public bool                             allowOcclusionWhenDynamic;      /// Controls if dynamic occlusion culling should be performed for this renderer.
    public bool                             stitchLightmapSeams;            /// When enabled, seams in baked lightmaps will get smoothed. (Editor only)
    public int                              rendererPriority;               /// This value sorts renderers by priority. Lower values are rendered first and higher values are rendered last.
    public Vector4                          lightmapScaleOffset;            /// The UV scale & offset used for a lightmap.
    public Vector4                          realtimeLightmapScaleOffset;    /// The UV scale & offset used for a realtime lightmap.

    // MeshCollider    
    public MeshColliderCookingOptions       cookingOptions;                 /// Options used to enable or disable certain features in mesh cooking.
    public bool                             convex;                         /// Use a convex collider from the mesh.
    public bool                             isTrigger;                      /// Is the collider a trigger?
    public float                            contactOffset;                  /// Contact offset value of this collider.

    // GameObject
    //public UnityEditor.StaticEditorFlags  staticEditorFlags;              /// Describes which Unity systems include the GameObject in their precomputations (Editor only)
}

[Serializable]
public enum DebugSurfaceType
{
    None,           /// Not a debug surface type
    CastShadows,    /// Mesh holding all surfaces that cast shadows
    ShadowOnly,     /// Mesh holding all surfaces that are set to shadow only (not rendered)
    ReceiveShadows, /// Mesh holding all surfaces that receive shadows
    Colliders,      /// Mesh holding all surfaces that are colliders
    Culled          /// Mesh holding all surfaces that are culled
}

[BurstCompatible, Serializable]
public struct RenderSurfaceSettings : IChiselHash
{
    public readonly static RenderSurfaceSettings DebugCastShadows       = new RenderSurfaceSettings(DebugSurfaceType.CastShadows);
    public readonly static RenderSurfaceSettings DebugShadowOnly        = new RenderSurfaceSettings(DebugSurfaceType.ShadowOnly);
    public readonly static RenderSurfaceSettings DebugReceiveShadows    = new RenderSurfaceSettings(DebugSurfaceType.ReceiveShadows);
    public readonly static RenderSurfaceSettings DebugColliders         = new RenderSurfaceSettings(DebugSurfaceType.Colliders);
    public readonly static RenderSurfaceSettings DebugCulled            = new RenderSurfaceSettings(DebugSurfaceType.Culled);
    public readonly static RenderSurfaceSettings Default                = new RenderSurfaceSettings
    {
        layer               = 0,
        renderingLayerMask  = 0,
        shadowCastingMode   = ShadowCastingMode.On,
        receiveShadows      = true,
        debugSurfaceType    = DebugSurfaceType.None // Not a debug surface type
    };

    public DebugSurfaceType     debugSurfaceType;

    // TODO: how do "layer" and "renderingLayerMask" relate to each other? 
    //       "renderingLayerMask" is SRP only, does it completely replace "layer" there?

    // GameObject
    public int                  layer;              /// The layer the game object is in. 

    // MeshRenderer
    public uint                 renderingLayerMask; /// Determines which rendering layer this renderer lives on. (SRP only)
    public ShadowCastingMode    shadowCastingMode;  /// Does this object cast shadows?
    public bool                 receiveShadows;     /// Does this object receive shadows?


    public RenderSurfaceSettings(DebugSurfaceType debugSurfaceType)
    {
        UnityEngine.Debug.Assert(debugSurfaceType != DebugSurfaceType.None);
        layer                   = 0;
        renderingLayerMask      = 0;
        shadowCastingMode       = ShadowCastingMode.Off;
        receiveShadows          = false;
        this.debugSurfaceType   = debugSurfaceType;
    }

    public uint GetHash()
    {
        unchecked
        {
            if (debugSurfaceType != DebugSurfaceType.None)
                return (uint)debugSurfaceType;
            if (shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                return math.hash(new uint4((uint)layer, renderingLayerMask, (uint)shadowCastingMode, (uint)debugSurfaceType));
            else
                return math.hash(new uint4((uint)layer, renderingLayerMask, (uint)shadowCastingMode | (receiveShadows ? 65536u : 0u), (uint)debugSurfaceType));
        }
    }
}

[BurstCompatible, Serializable]
public struct RenderSurfaceGroup 
{
    public RenderSurfaceSettings    settings;
    public NativeList<int>          materialInstanceIDs;
}

[BurstCompatible, Serializable]
public struct ColliderSurfaceSettings : IChiselHash
{
    public readonly static ColliderSurfaceSettings Default = new ColliderSurfaceSettings { layer = 0 };

    // GameObject
    public int layer;              /// The layer the game object is in. 

    public uint GetHash() { unchecked { return (uint)layer; } }
}

[BurstCompatible, Serializable]
public struct ColliderSurfaceGroup
{
    public ColliderSurfaceSettings  settings;
    public NativeList<int>          physicMaterialInstanceIDs;
}



// Stored based on hash
// -- separate:
//          - surface vertex positions + surface type
//          - surface with uvs etc. generated
public struct GeneratedSurfaceMeshPositions     : IChiselHash { public uint GetHash() { throw new NotImplementedException(); } }
public struct GeneratedSurfaceMeshTextureUV     : IChiselHash { public uint GetHash() { throw new NotImplementedException(); } }
public struct GeneratedSurfaceMeshLightmapUV    : IChiselHash { public uint GetHash() { throw new NotImplementedException(); } }

// should also contain tangents
public struct GeneratedSurfaceMeshNormals       : IChiselHash { public uint GetHash() { throw new NotImplementedException(); } }

// Contains information that can be used to add it to the appropriate mesh(es)
public struct GeneratedSurfaceMeshMetaData      : IChiselHash { public uint GetHash() { throw new NotImplementedException(); } }

public struct GeneratedSurfaceList
{
    // TODO: have information with which we can identify the lists and map them to the right meshes
    public UnsafeList<uint> surfaceIndices;
}


// 1. we need to know which SurfaceGroups we have (models + submodels)
// 2. we need to know which unique materials / physicmaterials | layer combinations there are, so we know how many meshes we need
//      3. we need to be able to return this _before_ we do CSG
// 4. we need to be able to add / remove surfaces when we add / remove brushes / transform combos
// 5. we need to be able to update brush GeneratedSurfaceMeshPositions after we performed CSG on the brush / transform combo
// 6. we need to be able to generate UVs/lightmap Uvs/normals etc. based on surface description + GeneratedSurfaceMeshPositions
// 7. we need to be able to request to fill meshes with surfaces _on demand_

public unsafe struct GeneratedSurfaceGroup
{
    // TODO: Need some way to map brush/surface to index in surface group so we can add/remove/update them
    public NativeList<GeneratedSurfaceMeshPositions>    positions;
    public NativeList<GeneratedSurfaceMeshTextureUV>    materialUV0;
    public NativeList<GeneratedSurfaceMeshLightmapUV>   lightmapUV;
    public NativeList<GeneratedSurfaceMeshNormals>      normals;

    public NativeList<GeneratedSurfaceList>             generatedSurfaceLists;

    public ref GeneratedSurfaceMeshPositions GetSurfaceMeshPositions(int brushIndex, int surfaceIndex)
    {
        throw new NotImplementedException();
    }

    void GenerateTextureUVs()
    {
        // GeneratedSurfaceMeshPositions => GeneratedSurfaceMeshTextureUV
        throw new NotImplementedException();
    }

    void GeneratedSurfaceMeshLightmapUV()
    {
        // GeneratedSurfaceMeshPositions => GeneratedSurfaceMeshLightmapUV
        throw new NotImplementedException();
    }

    void GeneratedSurfaceMeshNormals()
    {
        // GeneratedSurfaceMeshPositions => GeneratedSurfaceMeshNormals
        throw new NotImplementedException();
    }

    public void UpdateModifiedSurfaceProperties()
    {
        // TODO: only update when modified + necessary
        GenerateTextureUVs();
        GeneratedSurfaceMeshLightmapUV();
        GeneratedSurfaceMeshNormals();
    }

    public void GenerateMeshes(GeneratedSurfaceList generatedMesh, Mesh.MeshData meshData)
    {
        for (int surfaceIndex = 0; surfaceIndex < generatedMesh.surfaceIndices.length; surfaceIndex++)
        {
            // get surface from surfaceGroup
            // write to meshData, based on surface specific information
        }
    }
}

public struct GeneratedSurfaceManager
{
    public NativeList<GeneratedSurfaceGroup>    surfaceGroups;

    // TODO: shouldn't be static
    public static int RenderSurfaceGroupCount      { get { throw new NotImplementedException(); } }
    // TODO: shouldn't be static
    public static int ColliderSurfaceGroupCount    { get { throw new NotImplementedException(); } }

    // TODO: shouldn't be static
    public static RenderSurfaceGroup GetRenderSurfaceGroupWithSettings(in RenderSurfaceSettings settings)
    {
        throw new NotImplementedException();
    }

    // TODO: shouldn't be static
    public static ColliderSurfaceGroup GetColliderSurfaceGroupWithSettings(in ColliderSurfaceSettings settings)
    {
        throw new NotImplementedException();
    }

    public void AddSurface(int surfaceGroupIndex, int brushIndex, int surfaceIndex)
    {
        // Register a surface (material, physicMaterial etc.) when a brush is added, so we can determine how many meshes we need to generate
        throw new NotImplementedException();
    }

    public void RemoveSurface(int surfaceGroupIndex, int brushIndex, int surfaceIndex)
    {
        // Unregister a surface (material, physicMaterial etc.) when a brush is added, so we can determine how many meshes we need to generate
        throw new NotImplementedException();
    }

    public ref GeneratedSurfaceMeshPositions GetSurfaceMeshPositions(int surfaceGroupIndex, int brushIndex, int surfaceIndex)
    {
        return ref surfaceGroups[surfaceGroupIndex].GetSurfaceMeshPositions(brushIndex, surfaceIndex);
    }

    public void UpdateModifiedSurfaceProperties()
    {
        for (int surfaceGroupIndex = 0; surfaceGroupIndex < surfaceGroups.Length; surfaceGroupIndex++)
            surfaceGroups[surfaceGroupIndex].UpdateModifiedSurfaceProperties();
    }

    public void EnsureSurfaceGroups(in NativeList<ChiselCSGSubModel> subModels, in ChiselCSGModel model)
    {
        // TODO: ensure we have the right surfaceGroups
        var surfaceGroupsLength = this.surfaceGroups.Length;
        if (surfaceGroupsLength != 1 + subModels.Length)
            throw new ArgumentOutOfRangeException(nameof(subModels), $"{nameof(subModels)}.Length is expected to be {surfaceGroupsLength - 1}, but is {subModels.Length}.");
        Debug.Assert(surfaceGroupsLength == 1 + subModels.Length);
    }

    public unsafe void CopyToMeshes(ref NativeList<Mesh.MeshData> meshDataList)
    {
        // TODO: properly map surfaceGroup to MeshData
        for (int surfaceGroupIndex = 0; surfaceGroupIndex < this.surfaceGroups.Length; surfaceGroupIndex++)
        {
            var meshes = surfaceGroups[surfaceGroupIndex].generatedSurfaceLists;
            for (int i = 0; i < meshes.Length; i++)
                surfaceGroups[surfaceGroupIndex].GenerateMeshes(meshes[i], meshDataList[i]);
        }
    }
}


static unsafe class MeshDataArrayExtensions
{
    public static NativeList<Mesh.MeshData> ToNativeArray(this Mesh.MeshDataArray meshDataArray, Allocator allocator)
    {
        var meshDataList = new NativeList<Mesh.MeshData>(meshDataArray.Length, allocator);
        meshDataArray.CopyTo(meshDataList);
        return meshDataList;
    }

    public static void CopyTo(this Mesh.MeshDataArray meshDataArray, NativeList<Mesh.MeshData> destination)
    {
        destination.ResizeUninitialized(meshDataArray.Length);
        for (int i = 0; i < meshDataArray.Length; i++)
            destination[i] = meshDataArray[i];
    }

    delegate void ApplyToMeshesImplDelegate(Mesh[] meshes, IntPtr* datas, int count, MeshUpdateFlags flags);

    static readonly ApplyToMeshesImplDelegate ApplyToMeshesImpl = (ApplyToMeshesImplDelegate)Delegate.CreateDelegate(typeof(ApplyToMeshesImplDelegate), null, typeof(Mesh.MeshDataArray).GetMethod("ApplyToMeshesImpl", BindingFlags.NonPublic | BindingFlags.Static), true);

    //struct PtrStruct { public IntPtr m_Ptr; }

    public static unsafe void ApplyAndDisposeWritableMeshData(this Mesh.MeshDataArray @this, Mesh[] meshes, int realLength, MeshUpdateFlags flags = MeshUpdateFlags.Default)
    {
        if (meshes == null)
            throw new ArgumentNullException(nameof(meshes), "Mesh list is null");
        if (@this.Length < realLength)
            throw new InvalidOperationException($"{nameof(Mesh.MeshDataArray)} length ({@this.Length}) cannot be less than destination meshes length ({realLength})");
        if (meshes.Length < realLength)
            throw new InvalidOperationException($"{nameof(meshes)} length ({meshes.Length}) cannot be less than destination meshes length ({realLength})");
        if (realLength == 0)
        {
            @this.Dispose();
            return;
        } 

        for (int i = 0; i < realLength; ++i)
        {
            Mesh m = meshes[i];
            if (m == null)
                throw new ArgumentNullException(nameof(meshes), $"Mesh at index {i} is null");
        }

        // UNTESTED
        var ptrs = (IntPtr*)UnsafeUtility.AddressOf(ref @this); // First value of Mesh.MeshDataArray should be IntPtr*
        /*
        var ptrs = stackalloc IntPtr[realLength];
        for (int i = 0; i < realLength; i++)
        {
            var meshData = @this[i];
            //ptrs[i] = ((PtrStruct*)&meshData)->m_Ptr;
            ptrs[i] = *((IntPtr*)&meshData);
        }*/

        ApplyToMeshesImpl(meshes, ptrs, realLength, flags);
        @this.Dispose();
    }
}