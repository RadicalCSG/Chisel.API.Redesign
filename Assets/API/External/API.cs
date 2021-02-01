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
using Unity.Jobs;
using System.Collections.Generic;
using UnityEngine.Rendering;

[Serializable]
public enum NodeType : byte { None, Tree, Branch, Brush };

[Serializable]
public enum Operation : byte { Additive, Subtractive, Intersection }

public interface IChiselHash { uint GetHash(); }

public interface IChiselChild : IChiselHash {}

public interface IChiselNodeContainer : IChiselHash
{
    int ChildCount              { get; }
    ref IChiselChild            GetChildAt(int index);
    ref ChiselTransformation    GetChildTransformAt(int index);
    ref Operation               GetChildOperationAt(int index);
}

public interface IChiselMeshContainer : IChiselHash
{
    uint MeshContainerID { get; }
    NativeList<RenderSurfaceSettings>   UniqueRenderSurfaceSettings     { get; }
    NativeList<ColliderSurfaceSettings> UniqueColliderSurfaceSettings   { get; }
}

[Serializable]
public partial struct ChiselTransformation : IChiselHash { }

[Serializable]
public partial struct ChiselCSGModel : IChiselNodeContainer, IChiselMeshContainer { }

[Serializable]
public partial struct ChiselCSGSubModel : IChiselChild, IChiselNodeContainer, IChiselMeshContainer { }

[Serializable]
public partial struct ChiselCSGComposite : IChiselChild, IChiselNodeContainer { }

[Serializable]
public partial struct ChiselCSGBrush : IChiselChild { }

public struct MeshSource : IEquatable<MeshSource>
{
    public uint modelID;
    public uint meshContainerID;
    public uint settingsHash;
    public uint meshID;

    public override int GetHashCode()
    {
        unchecked { return (int)math.hash(new uint4(modelID, meshContainerID, settingsHash, meshID)); }
    }

    public bool Equals(MeshSource other)
    {
        return modelID == other.modelID &&
               meshContainerID == other.meshContainerID &&
               settingsHash == other.settingsHash &&
               meshID == other.meshID;
    }
    public override bool Equals(object obj) { return obj is MeshSource source && Equals(source); }
    public static bool operator ==(MeshSource left, MeshSource right) { return left.Equals(right); }
    public static bool operator !=(MeshSource left, MeshSource right) { return !left.Equals(right); }
}


public partial struct ChiselManager
{

    static JobHandle StartUpdateJobs(in ChiselCSGModel model, in Mesh.MeshDataArray meshDataArray, Dictionary<uint, NativeList<MeshSource>> updateMeshes)
    {
        // TODO: need a way to find all >modified< RenderSurfaceGroups/ColliderSurfaceGroups

        // TODO: update hierarchy / transformations etc. etc.
        // TODO: determine modified brushes
        // TODO: Perform CSG on our our modified brushes
        // TODO: Triangulate modified surfaces
        // TODO: Create surface meshes and cache them
        // TODO: Fill the modfied meshes using all surface meshes (new and cached)
        return new JobHandle();
    }

    static int GetMaximumMeshCount(in ChiselCSGModel model)
    {
        // TODO: ensure all submodels are properly registered, and all brush-meshes/surfaces are properly registered
        //       we need to know all the surfaces that exist in each model, and each submodel, before we know how many meshes to generate

        throw new NotImplementedException();
    }

    public delegate Dictionary<MeshSource, Mesh> GetMeshLookup(List<ChiselCSGModel> models);
    public delegate void MeshCleanup(Dictionary<MeshSource, Mesh> lookup);

    static readonly List<Mesh> s_MeshList = new List<Mesh>();
    static readonly Dictionary<uint, Mesh.MeshDataArray> s_ModelMeshDataArrays = new Dictionary<uint, Mesh.MeshDataArray>();
    static readonly Dictionary<uint, NativeList<MeshSource>> s_UpdateMeshes = new Dictionary<uint, NativeList<MeshSource>>();
    static readonly Mesh s_EmptyDummyMesh = new Mesh();

    public static void Update(List<ChiselCSGModel> models, GetMeshLookup getMeshLookup, MeshCleanup meshCleanup)
    {
        Dictionary<MeshSource, Mesh> meshLookup = null;
        try
        {
            s_UpdateMeshes.Clear();
            var allJobs = new JobHandle();
            for (int i = 0; i < models.Count; i++)
            {
                var model = models[i];
                if (!model.changed)
                    continue;

                try
                {
                    var maximumMeshCount = GetMaximumMeshCount(in model);
                    var meshDataArray    = Mesh.AllocateWritableMeshData(maximumMeshCount);
                    s_ModelMeshDataArrays[model.ModelID] = meshDataArray;

                    var modelJobs = StartUpdateJobs(in model, in meshDataArray, s_UpdateMeshes);
                    allJobs = JobHandle.CombineDependencies(modelJobs, allJobs);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    // Mark the model as no longer changed to prevent the failure to be repeated infinitely
                    model.changed = false;
                }
            }

            // Gets a lookup to all the mesh sources to all the meshes we could potentially fill
            // This is put here so that any non jobifyable code can be run while the above jobs are still working
            meshLookup = getMeshLookup(models);

            // Complete all the jobs so we can start applying the MeshData to our Meshes
            allJobs.Complete();

            for (int m = 0; m < models.Count; m++)
            {
                var model = models[m];
                if (!model.changed)
                    continue;

                // Mark it as no longer changed here, so that if something goes wrong we don't end up in an infinite loop of trying to rebuild it
                model.changed = false;

                // Get the MeshDataArray for our model
                var meshDataArray = s_ModelMeshDataArrays[model.ModelID];
                
                // Remove it from our list so that if something goes wrong, we can still dispose everything else correctly
                s_ModelMeshDataArrays.Remove(model.ModelID);

                try
                {
                    // Find each Mesh for each MeshData
                    s_MeshList.Clear();

                    var modelUpdatedMeshes = s_UpdateMeshes[model.ModelID];
                    for (int u = 0; u < modelUpdatedMeshes.Length; u++)
                    {
                        // Get the updated meshSources
                        var meshSource  = modelUpdatedMeshes[u];
                        // And find the corresponding mesh for it
                        var mesh        = meshLookup[meshSource];
                        // Add it, in order, to the MeshList
                        s_MeshList.Add(mesh);
                    }

                    // Fill s_MeshList with an empty mesh until it's the same size as meshDataArray
                    for (int c = s_MeshList.Count; c < meshDataArray.Length; c++)
                        s_MeshList[c] = s_EmptyDummyMesh;

                    Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, s_MeshList, MeshUpdateFlags.Default);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    meshDataArray.Dispose();
                }
            }
        }
        finally
        {
            meshCleanup(meshLookup);
            s_UpdateMeshes.Clear();
            s_MeshList.Clear(); // prevent dangling resources
            try
            {
                // There shouldn't be anything left, if there is, then something went wrong and it wasn't disposed properly
                foreach (var meshDataArray in s_ModelMeshDataArrays.Values)
                    meshDataArray.Dispose();
            }
            finally { s_ModelMeshDataArrays.Clear(); }
        }
    }
}
