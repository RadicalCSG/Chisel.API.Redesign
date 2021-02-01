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

// using a DAG - how to store?
//      generators containing entire csg tree branches
//      instancing reusing other branches
// ignoring brushes one way
//      "contents"
// uv generators
//      uv matrix / texture atlas
//      hotspot mapping / texture atlas
//      lightmap uv
// surfaces being added to different meshes (+ optional types)
//      different material
//      different physicMaterial
//      different rendering layer / physics layer
//      different (sub)models
//      debug meshes
//
// edge smoothing
// surface subdivision
// normal smoothing
// vertex coloring
// decals


partial struct ChiselManager
{
    public static int GetMaximumMeshCount() { throw new NotImplementedException(); }

    public int ModelCount { get { return models.Length; } }
    public unsafe ChiselCSGModel GetModelAt(int index) { return models[index]; }

    NativeList<ChiselCSGModel>  models;
    NativeList<uint>   modelHashes;

    // When something is modified, their hash changes, and their parents hash changes etc.
    // Update transformation hierarchy when modified
    // Update mesh hashed grid lookup, find which branches have been modified 
    //      --- how to identify unique branches to meshes?
    //      store how they intersect -> inside/intersection
    // When updating, just go through unknown hashes, register meshes to update & register the known hashes
    // Create a CSGTree per unique ChiselMesh (keep in mind it's a DAG, so any ChiselMesh can exist multiple times)
    //      optimize "intersecting" brushes, remove all brushes that do not contribute (and don't use their hashes to identify this)
    //          brush inside another brush -> can ignore all brushes before (depending on operations)
    //          when brushes intersect
    //              -> for each other brush plane our brush intersect with 
    //              -> hash distance to this plane
    //              -> windows on wall -> makes them all identical from the pov of the brush itself 
    //                  --- how to avoid floating point gaps though?
    //      chisel-meshes with identical csg tree and identical intersection hashes will produce identical meshes

    public void UpdateModels()
    {
        for (int i = 0; i < models.Length; i++)
        {
            var modelHash = models[i].GetHash();
            if (modelHash == modelHashes[i])
                continue;

            modelHashes[i] = modelHash;
            UpdateModel(models[i]);
        }
    }

    void UpdateModel(ChiselCSGModel model)
    {
        // From outside this method:
        //      - query the maximum required meshes
        //      - query which meshes need to be updated
        //      - request these specific meshes
        int requiredMeshes = model.maximumRequiredMeshes;
        for (int i = 0; i < model.subModels.Length; i++)
            requiredMeshes += model.subModels[i].maximumRequiredMeshes;
        var meshDataArray       = UnityEngine.Mesh.AllocateWritableMeshData(requiredMeshes);
        var meshDataNativeArray = meshDataArray.ToNativeArray(Allocator.Temp);

        model.surfaces.EnsureSurfaceGroups(in model.subModels, in model);

        // jobify generators => make generators build branches

        // build unpacked hierarchy 
        //      update transformation + transformation hash
        //      csg hash (operation + transformation hash)
        //      update intersection hashed grid

        model.packedHierarchy.UpdatePackedHierarchy(in model);
        model.intersections.UpdateIntersections(in model.packedHierarchy);

        // what data do we need to perform CSG per brush
        //      how can we update this iteratively?
        //      how can we store this efficiently?
        for (uint i = 0; i < model.packedHierarchy.brushes.Length; i++)
        {
            CreateRoutingTable(in model.intersections, in model.packedHierarchy, i, out BlobAssetReference<RoutingTable> routingTable);
            PerformCSG(in model.chiselMeshes, in routingTable, ref model.surfaces);
        }

        model.surfaces.UpdateModifiedSurfaceProperties();

        // wireframes / wireframe-meshes?

        model.surfaces.CopyToMeshes(ref meshDataNativeArray);


        meshDataNativeArray.Dispose();
    }


    static void CreateRoutingTable([ReadOnly] in  ChiselIntersectionManager         intersections,
                                   [ReadOnly] in  CompactTree                       packedHierarchy,
                                                  uint                              nodeIndex,
                                              out BlobAssetReference<RoutingTable>  routingTable) // also use transformations
    {
        throw new NotImplementedException();
    }

    static void PerformCSG([ReadOnly] in  ChiselMeshManager                 chiselMeshes,
                           [ReadOnly] in  BlobAssetReference<RoutingTable>  routingTable,
                                      ref GeneratedSurfaceManager           surfaces)
    {
        ref var meshPositions = ref surfaces.GetSurfaceMeshPositions(surfaceGroupIndex: 0, brushIndex: 0, surfaceIndex: 0);

        // Generate GeneratedSurfaceMeshPositions
        throw new NotImplementedException();
    }
}

public struct RoutingTable : IChiselHash
{
    [Serializable]
    public enum CategoryIndex : sbyte
    {
        None                = -1,
        Inside              = 0,
        Aligned             = 1,
        ReverseAligned      = 2,
        Outside             = 3,

        ValidAligned        = Aligned,
        ValidReverseAligned = ReverseAligned,
        LastCategory        = Outside
    };

    [Serializable]
    public enum CategoryGroupIndex : byte
    {
        First   = 0,
        Invalid = 255
    }

    [DebuggerTypeProxy(typeof(CategoryRoutingRow.DebuggerProxy))]
    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct CategoryRoutingRow
    {
        internal sealed class DebuggerProxy
        {
            public int inside;
            public int aligned;
            public int reverseAligned;
            public int outside;
            public DebuggerProxy(CategoryRoutingRow v)
            {
                inside          = (int)v[0];
                aligned         = (int)v[1];
                reverseAligned  = (int)v[2];
                outside         = (int)v[3];
            }
        }

        const byte Invalid          = (byte)CategoryGroupIndex.Invalid;
        const byte Inside           = (byte)(CategoryGroupIndex)CategoryIndex.Inside;
        const byte Aligned          = (byte)(CategoryGroupIndex)CategoryIndex.Aligned;
        const byte ReverseAligned   = (byte)(CategoryGroupIndex)CategoryIndex.ReverseAligned;
        const byte Outside          = (byte)(CategoryGroupIndex)CategoryIndex.Outside;

        public readonly static CategoryRoutingRow invalid               = new CategoryRoutingRow(Invalid, Invalid, Invalid, Invalid);
        public readonly static CategoryRoutingRow identity              = new CategoryRoutingRow(Inside, Aligned, ReverseAligned, Outside);
        public readonly static CategoryRoutingRow selfAligned           = new CategoryRoutingRow(Aligned, Aligned, Aligned, Aligned);
        public readonly static CategoryRoutingRow selfReverseAligned    = new CategoryRoutingRow(ReverseAligned, ReverseAligned, ReverseAligned, ReverseAligned);
        public readonly static CategoryRoutingRow outside               = new CategoryRoutingRow(Outside, Outside, Outside, Outside);
        public readonly static CategoryRoutingRow inside                = new CategoryRoutingRow(Inside, Inside, Inside, Inside);

        public const int Length = (int)CategoryIndex.LastCategory + 1;

        // Is PolygonGroupIndex instead of int, but C# doesn't like that
        [FieldOffset(0)] fixed byte destination[Length];

        #region Operation tables            
        public readonly static byte[] kOperationTables = // NOTE: burst supports static readonly tables like this
            {
                // Regular Operation Tables
                // Additive set operation on polygons: output = (left-node || right-node)
                // 
                //  right node                                                              | Additive Operation
                //  inside            aligned           reverse-aligned   outside           |     left-node       
                //-----------------------------------------------------------------------------------------------
                    Inside,           Inside,           Inside,           Inside            , // inside
                    Inside,           Aligned,          Inside,           Aligned           , // aligned
                    Inside,           Inside,           ReverseAligned,   ReverseAligned    , // reverse-aligned
                    Inside,           Aligned,          ReverseAligned,   Outside           , // outside

                // Subtractive set operation on polygons: output = !(!left-node || right-node)
                //
                //  right node                                                              | Subtractive Operation
                //  inside            aligned           reverse-aligned   outside           |     left-node       
                //-----------------------------------------------------------------------------------------------
                    Outside,          ReverseAligned,   Aligned,          Inside            , // inside
                    Outside,          Outside,          Aligned,          Aligned           , // aligned
                    Outside,          ReverseAligned,   Outside,          ReverseAligned    , // reverse-aligned
                    Outside,          Outside,          Outside,          Outside           , // outside

                // Common set operation on polygons: output = !(!left-node || !right-node)
                //
                //  right node                                                              | Intersection Operation
                //  inside            aligned           reverse-aligned   outside           |     left-node       
                //-----------------------------------------------------------------------------------------------
                    Inside,           Aligned,          ReverseAligned,   Outside           , // inside
                    Aligned,          Aligned,          Outside,          Outside           , // aligned
	                ReverseAligned,   Outside,          ReverseAligned,   Outside           , // reverse-aligned
                    Outside,          Outside,          Outside,          Outside           , // outside

	            //  right node                                                              |
                //  inside            aligned           reverse-aligned   outside           |     left-node       
                //-----------------------------------------------------------------------------------------------
	                Invalid,          Invalid,          Invalid,          Invalid           , // inside
                    Invalid,          Invalid,          Invalid,          Invalid           , // aligned
                    Invalid,          Invalid,          Invalid,          Invalid           , // reverse-aligned
                    Invalid,          Invalid,          Invalid,          Invalid           , // outside
            
                // Remove Overlapping Tables
                // Additive set operation on polygons: output = (left-node || right-node)
                //
	            //  right node                                                              | Additive Operation
                //  inside            aligned           reverse-aligned   outside           |     left-node       
                //-----------------------------------------------------------------------------------------------
	                Inside,           Inside,           Inside,           Inside            , // inside
                    Inside,           Inside,           Inside,           Aligned           , // aligned
                    Inside,           Inside,           Inside,           ReverseAligned    , // reverse-aligned
                    Inside,           Inside,           Inside,           Outside           , // outside

                // Subtractive set operation on polygons: output = !(!left-node || right-node)
                //
	            //  right node                                                              | Subtractive Operation
                //  inside            aligned           reverse-aligned   outside           |     left-node       
                //-----------------------------------------------------------------------------------------------
                    Outside,          Outside,          Outside,          Inside            , // inside
                    Outside,          Outside,          Outside,          Aligned           , // aligned
                    Outside,          Outside,          Outside,          ReverseAligned    , // reverse-aligned
                    Outside,          Outside,          Outside,          Outside           , // outside

                // Common set operation on polygons: output = !(!left-node || !right-node)
                //
	            //  right node                                                              | Subtractive Operation
                //  inside            aligned           reverse-aligned   outside           |     left-node       
                //-----------------------------------------------------------------------------------------------
	                Inside,           Outside,          Outside,          Outside           , // inside
                    Aligned,          Outside,          Outside,          Outside           , // aligned
                    ReverseAligned,   Outside,          Outside,          Outside           , // reverse-aligned
                    Outside,          Outside,          Outside,          Outside           , // outside

	            //  right node                                                              |
                //  inside            aligned           reverse-aligned   outside           |     left-node       
                //-----------------------------------------------------------------------------------------------
	                Invalid,          Invalid,          Invalid,          Invalid           , // inside
                    Invalid,          Invalid,          Invalid,          Invalid           , // aligned
                    Invalid,          Invalid,          Invalid,          Invalid           , // reverse-aligned
                    Invalid,          Invalid,          Invalid,          Invalid           , // outside
            };


        public const int RemoveOverlappingOffset = 4;
        public const int OperationStride = 4 * 4;
        public const int RowStride = 4;
        #endregion


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CategoryRoutingRow(int operationIndex, CategoryIndex left, in CategoryRoutingRow right)
        {
            var operationOffset = operationIndex * OperationStride + ((int)left * RowStride);
            destination[0] = kOperationTables[(int)(operationOffset + (int)right.destination[0])];
            destination[1] = kOperationTables[(int)(operationOffset + (int)right.destination[1])];
            destination[2] = kOperationTables[(int)(operationOffset + (int)right.destination[2])];
            destination[3] = kOperationTables[(int)(operationOffset + (int)right.destination[3])];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CategoryRoutingRow operator +(CategoryRoutingRow oldRow, int offset)
        {
            var newRow = new CategoryRoutingRow();
            newRow.destination[0] = (byte)(oldRow.destination[0] + offset);
            newRow.destination[1] = (byte)(oldRow.destination[1] + offset);
            newRow.destination[2] = (byte)(oldRow.destination[2] + offset);
            newRow.destination[3] = (byte)(oldRow.destination[3] + offset);
            return newRow;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        CategoryRoutingRow(byte inside, byte aligned, byte reverseAligned, byte outside)
        {
            destination[0] = inside;
            destination[1] = aligned;
            destination[2] = reverseAligned;
            destination[3] = outside;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CategoryRoutingRow(CategoryGroupIndex inside, CategoryGroupIndex aligned, CategoryGroupIndex reverseAligned, CategoryGroupIndex outside)
        {
            destination[0] = (byte)inside;
            destination[1] = (byte)aligned;
            destination[2] = (byte)reverseAligned;
            destination[3] = (byte)outside;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CategoryRoutingRow(CategoryGroupIndex value)
        {
            destination[0] = (byte)value;
            destination[1] = (byte)value;
            destination[2] = (byte)value;
            destination[3] = (byte)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AreAllTheSame()
        {
            return destination[0] == destination[1] &&
                   destination[1] == destination[2] &&
                   destination[2] == destination[3];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AreAllValue(int value)
        {
            return (destination[0] == value &&
                    destination[1] == value &&
                    destination[2] == value &&
                    destination[3] == value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(CategoryRoutingRow other)
        {
            return (destination[0] == other.destination[0] &&
                    destination[1] == other.destination[1] &&
                    destination[2] == other.destination[2] &&
                    destination[3] == other.destination[3]);
        }

        public CategoryGroupIndex this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (CategoryGroupIndex)destination[index]; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set { destination[index] = (byte)value; }
        }
    }

    public struct RoutingLookup
    {
        public int startIndex;
        public int endIndex;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetRoute([NoAlias] ref RoutingTable table, CategoryGroupIndex inputIndex, out CategoryRoutingRow routingRow)
        {
            var tableIndex = startIndex + (int)inputIndex;

            if (tableIndex < startIndex || tableIndex >= endIndex)
            {
                routingRow = new CategoryRoutingRow(inputIndex);
                return false;
            }

            routingRow = table.routingRows[tableIndex];
            return true;
        }
    }

    public struct BrushDescription
    {
        public ChiselCSGBrush            brush;
        public ChiselTransformation   transformation;
    }

    public uint selfMeshID;
    public int  subModel;
    public BlobArray<BrushDescription>      brushes;
    public BlobArray<CategoryRoutingRow>	routingRows;
    public BlobArray<RoutingLookup>         routingLookups;
    public BlobArray<int>	                nodeIndexToTableIndex;
    public int	                            nodeIndexOffset;

    public uint GetHash() { return 0; }
}


public struct CompactTree
{
    public struct CompactHierarchyNode
    {
        // TODO: combine bits
        public NodeType     type;
        public Operation    operation;
        public int          childCount;
        public int          childOffset;

        public override string ToString() { return $"({nameof(type)}: {type}, {nameof(childCount)}: {childCount}, {nameof(childOffset)}: {childOffset}, {nameof(operation)}: {operation})"; }
    }

    public struct BrushDescription
    {
        public int      nodeIndex;
        public int      meshID;
        public int      subModel;
        public override string ToString() { return $"({nameof(nodeIndex)}: {nodeIndex}, {nameof(meshID)}: {meshID}, {nameof(subModel)}: {subModel})"; }
    }

    public NativeArray<CompactHierarchyNode>  compactHierarchy;
    public NativeArray<ChiselTransformation>  transformations;
    public NativeArray<BrushDescription>      brushes;

    public void UpdatePackedHierarchy([ReadOnly] in ChiselCSGModel model)
    {
        throw new NotImplementedException();
    }
}