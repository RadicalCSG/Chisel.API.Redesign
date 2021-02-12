using System;
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
using Unity.Jobs;

public interface UniqueIDProvider<K>
{
    K UniqueID { get; }
}

[Serializable]
public sealed class GeneratedMeshGroup : UniqueIDProvider<uint>
{
    uint UniqueIDProvider<uint>.UniqueID => meshContainerID;

    public GeneratedMeshGroup(uint meshContainerID) { this.meshContainerID = meshContainerID; }

    // Used to identify the current MeshContainer which might be a model or submodel, 
    // so we can manage its meshes when a submodel is added/removed
    [SerializeField] uint meshContainerID;
    public uint MeshContainerID { get { return meshContainerID; } }

    public uint UniqueID { get { return MeshContainerID; } }

    [SerializeField] List<GeneratedRenderMesh>          generatedRenderMesh          = new List<GeneratedRenderMesh>();
    public List<GeneratedRenderMesh>                    GeneratedRenderMeshes          { get { return generatedRenderMesh; } }

    [SerializeField] List<GeneratedColliderCollection>  generatedColliderCollections = new List<GeneratedColliderCollection>();
    public List<GeneratedColliderCollection>            GeneratedColliderCollections { get { return generatedColliderCollections; } }
}


// Holds a Mesh for the given settings on a specific Model/SubModel 
[Serializable]
public sealed class GeneratedRenderMesh : IDisposable, UniqueIDProvider<RenderSurfaceSettings>
{
    RenderSurfaceSettings UniqueIDProvider<RenderSurfaceSettings>.UniqueID => settings;

    public GeneratedRenderMesh(RenderSurfaceSettings settings, Mesh mesh)
    {
        this.settings     = settings;
        this.mesh         = mesh; 
    }

    [SerializeField] RenderSurfaceSettings  settings;
    public RenderSurfaceSettings            Settings { get { return settings; } }


    [SerializeField] Mesh                   mesh;
    public Mesh                             Mesh { get { return mesh; } }
    
    [SerializeField] List<Material>         sharedMaterials = new List<Material>();
    public List<Material>                   SharedMaterials { get { return sharedMaterials; } }


    public void Dispose()
    {
        if (Mesh) Mesh.SafeDestroy();
        sharedMaterials.Clear(); // prevent dangling references
        settings     = RenderSurfaceSettings.Default;
        mesh         = null;
    }
    
    static readonly HashSet<int> s_LookupMaterials = new HashSet<int>(); 
    public bool HaveMaterialsChanged(in RenderSurfaceGroup renderSurfaceGroup)
    {
        if (sharedMaterials.Count != renderSurfaceGroup.materialInstanceIDs.Length)
            return true;

        s_LookupMaterials.Clear();
        foreach (var sharedMaterial in sharedMaterials)
            s_LookupMaterials.Add(sharedMaterial.GetInstanceID());

        if (renderSurfaceGroup.materialInstanceIDs.IsCreated)
        {
            for (int m = 0; m < renderSurfaceGroup.materialInstanceIDs.Length; m++)
            {
                var instanceID = renderSurfaceGroup.materialInstanceIDs[m];
                if (!s_LookupMaterials.Contains(instanceID))
                    return true;
                s_LookupMaterials.Remove(instanceID);
            }
        }

        return s_LookupMaterials.Count > 0;
    }

    public void UpdateMaterials(in RenderSurfaceGroup renderSurfaceGroup)
    {
        if (!renderSurfaceGroup.materialInstanceIDs.IsCreated ||
            renderSurfaceGroup.materialInstanceIDs.Length == 0)
        {
            sharedMaterials.Clear();
            return;
        }
        
        var materialLookup = MaterialManager.Lookup;
        sharedMaterials.Clear();
        if (sharedMaterials.Capacity < renderSurfaceGroup.materialInstanceIDs.Length)
            sharedMaterials.Capacity = renderSurfaceGroup.materialInstanceIDs.Length;
        for (int m = 0; m < renderSurfaceGroup.materialInstanceIDs.Length; m++)
            sharedMaterials.Add(materialLookup.GetMaterialByInstanceID(renderSurfaceGroup.materialInstanceIDs[m]));
    }
}

[Serializable]
public sealed class PhysicMaterialMesh : IDisposable
{
    [SerializeField] PhysicMaterial physicMaterial;
    [SerializeField] Mesh           mesh;
    [SerializeField] uint           cachedKey;
    

    public PhysicMaterialMesh(PhysicMaterial physicMaterial, Mesh mesh)
    {
        this.physicMaterial = physicMaterial; this.mesh = mesh;
        unchecked { cachedKey = (uint)physicMaterial.GetHashCode(); }
    }

    public PhysicMaterial   PhysicMaterial      { get { return physicMaterial; } }
    public Mesh             Mesh                { get { return mesh; } }
    public uint             CachedKey           { get { return cachedKey; } }

    public void Dispose()
    {
        if (mesh) mesh.SafeDestroy();
        physicMaterial = null;
        mesh = null;
        cachedKey = 0;
    }
}

// Holds all meshes with the given settings for a specific Model/SubModel
// Each PhysicMaterial will have its own mesh
[Serializable]
public sealed class GeneratedColliderCollection : IDisposable, UniqueIDProvider<ColliderSurfaceSettings>
{
    ColliderSurfaceSettings UniqueIDProvider<ColliderSurfaceSettings>.UniqueID => settings;

    public GeneratedColliderCollection(ColliderSurfaceSettings settings)
    {
        this.settings = settings;
    }
    public uint UniqueID { get { return settings.GetHash(); } }


    [SerializeField] ColliderSurfaceSettings    settings;
    public ColliderSurfaceSettings              Settings    { get { return settings; } }


    [SerializeField] List<PhysicMaterialMesh>   physicMaterialMeshes      = new List<PhysicMaterialMesh>();
    public List<PhysicMaterialMesh>             PhysicMaterialMeshes      { get { return physicMaterialMeshes; } }
    
    public void Dispose()
    {
        foreach (var physicMaterialMesh in physicMaterialMeshes)
            physicMaterialMesh.Dispose();

        physicMaterialMeshes.Clear();
        settings = ColliderSurfaceSettings.Default;
    }
}

// Holds all generated meshes for a specific model
[Serializable]
public sealed class GeneratedModelMeshes
{
    // First index is model, followed by all the SubModels
    [SerializeField] List<GeneratedMeshGroup> generatedMeshGroups = new List<GeneratedMeshGroup>();
    public List<GeneratedMeshGroup> GeneratedMeshGroups { get { return generatedMeshGroups; } }
    




    static readonly Dictionary<uint, GeneratedMeshGroup> s_FoundGeneratedMeshes = new Dictionary<uint, GeneratedMeshGroup>();

    public void Update(in ChiselCSGModel model)
    {
        // TODO: make this all undoable so any registered "undo" for any brush modification will include these changes too
        //          might make undos use a lot of memory? but efficiently redoing the work might be more problematic ...?

        // Find all existing GeneratedMeshGroups and make a lookup table based on their ids
        s_FoundGeneratedMeshes.Clear();
        for (int g = 0; g < this.generatedMeshGroups.Count; g++)
        {
            var generatedMeshGroup = this.generatedMeshGroups[g];
            var meshContainerID    = generatedMeshGroup.MeshContainerID;
            s_FoundGeneratedMeshes.Add(meshContainerID, generatedMeshGroup);
        }

        // Get a list of all the containers
        var meshContainerList = model.GetMeshContainerList();

        // Find all added and removed generatedMeshGroups (if any)
        for (int m = meshContainerList.Count - 1; m >= 0; m--)
        {
            var meshContainer   = meshContainerList[m];
            var meshContainerID = meshContainer.MeshContainerID;
            if (!s_FoundGeneratedMeshes.TryGetValue(meshContainerID, out GeneratedMeshGroup generatedMeshGroup))
            {
                // If it doesn't exist, add it
                generatedMeshGroup = new GeneratedMeshGroup(meshContainerID);
                this.generatedMeshGroups.Add(generatedMeshGroup);
            } else
                // This container exists, so we can remove it from the remove list
                s_FoundGeneratedMeshes.Remove(meshContainerID);

            // Update the meshes in the meshContainer, ensure they exist
            UpdateGeneratedMeshGroups(generatedMeshGroup, meshContainer);
        }

        // Go through all removed generatedMeshGroups and destroy them
        foreach (var generatedMeshGroup in s_FoundGeneratedMeshes.Values)
        {
            DestroyGeneratedMeshes(generatedMeshGroup);
            this.generatedMeshGroups.Remove(generatedMeshGroup);
        }

        // Ensure we don't have any dangling resources left
        s_FoundGeneratedMeshes.Clear();
    }

    public void CollectMeshes(in ChiselCSGModel model, Dictionary<MeshSource, Mesh> meshCollection)
    {
        // TODO: - Should have a way to cache this when unmodified
        var meshSource = new MeshSource
        {
            modelID = model.ModelID
        };
        for (int g = 0; g < this.GeneratedMeshGroups.Count; g++)
        {
            var generatedMeshGroup = this.GeneratedMeshGroups[g];
            meshSource.meshContainerID = generatedMeshGroup.MeshContainerID;

            foreach (var generatedRenderMesh in generatedMeshGroup.GeneratedRenderMeshes)
            {
                meshSource.settingsHash = generatedRenderMesh.Settings.GetHash();
                meshSource.meshID = 0;
                meshCollection[meshSource] = generatedRenderMesh.Mesh;
            }

            foreach (var generatedColliderCollection in generatedMeshGroup.GeneratedColliderCollections)
            {
                meshSource.settingsHash = generatedColliderCollection.Settings.GetHash();
                var physicMaterialMeshes = generatedColliderCollection.PhysicMaterialMeshes;
                foreach (var physicMaterialMesh in physicMaterialMeshes)
                {
                    meshSource.meshID = physicMaterialMesh.CachedKey;
                    meshCollection[meshSource] = physicMaterialMesh.Mesh;
                }
            }
        }
    }


    public static void DestroyGeneratedMeshes(GeneratedMeshGroup generatedMeshGroup)
    {
        var generatedRenderMesh = generatedMeshGroup.GeneratedRenderMeshes;
        for (int i = 0; i < generatedRenderMesh.Count; i++)
            generatedRenderMesh[i].Dispose();
        generatedRenderMesh.Clear();

        var generatedColliderCollections = generatedMeshGroup.GeneratedColliderCollections;
        for (int i = 0; i < generatedColliderCollections.Count; i++)
            generatedColliderCollections[i].Dispose();
        generatedColliderCollections.Clear();
    }

    static readonly Dictionary<RenderSurfaceSettings,   GeneratedRenderMesh>         s_FoundGeneratedRenderMeshes       = new Dictionary<RenderSurfaceSettings, GeneratedRenderMesh>();
    static readonly Dictionary<ColliderSurfaceSettings, GeneratedColliderCollection> s_FoundGeneratedColliderCollection = new Dictionary<ColliderSurfaceSettings, GeneratedColliderCollection>();
    static void UpdateGeneratedMeshGroups(GeneratedMeshGroup generatedMeshGroup, IChiselMeshContainer meshContainer)
    {
        var renderSurfaceSettings   = meshContainer.UniqueRenderSurfaceSettings;
        var generatedRenderMeshes   = generatedMeshGroup.GeneratedRenderMeshes;

        // Create a lookup table for all meshes that currently exist
        s_FoundGeneratedRenderMeshes.Clear();
        for (int r = 0; r < generatedRenderMeshes.Count; r++)
            s_FoundGeneratedRenderMeshes[generatedRenderMeshes[r].Settings] = generatedRenderMeshes[r];

        if (renderSurfaceSettings.IsCreated)
        {
            if (generatedRenderMeshes.Capacity < renderSurfaceSettings.Length)
                generatedRenderMeshes.Capacity = renderSurfaceSettings.Length;
            for (int r = 0; r < renderSurfaceSettings.Length; r++)
            {
                var renderSettings = renderSurfaceSettings[r];
                // If it doesn't already exist, we need to create it
                if (!s_FoundGeneratedRenderMeshes.ContainsKey(renderSettings))
                {
                    var generatedRenderMesh = new GeneratedRenderMesh(settings: renderSettings, mesh: new Mesh());
                    generatedRenderMeshes.Add(generatedRenderMesh);
                    continue;
                }
                // If it DOEST exist, we remove it so we end up with a list of all items to remove
                s_FoundGeneratedRenderMeshes.Remove(renderSettings);
            }
        }

        // Remove all items that we don't need anymore
        foreach (var removeGeneratedRenderMesh in s_FoundGeneratedRenderMeshes.Values)
        {
            generatedRenderMeshes.Remove(removeGeneratedRenderMesh);
            removeGeneratedRenderMesh.Dispose();
        }
        s_FoundGeneratedRenderMeshes.Clear(); // prevent dangling references

        var colliderSurfaceSettings      = meshContainer.UniqueColliderSurfaceSettings;
        var generatedColliderCollections = generatedMeshGroup.GeneratedColliderCollections;

        // Create a lookup table for all meshes that currently exist
        s_FoundGeneratedColliderCollection.Clear();
        for (int c = 0; c < generatedColliderCollections.Count; c++)
            s_FoundGeneratedColliderCollection[generatedColliderCollections[c].Settings] = generatedColliderCollections[c];

        if (colliderSurfaceSettings.IsCreated)
        {
            if (generatedColliderCollections.Capacity < colliderSurfaceSettings.Length)
                generatedColliderCollections.Capacity = colliderSurfaceSettings.Length;
            for (int c = 0; c < colliderSurfaceSettings.Length; c++)
            {
                var colliderSettings = colliderSurfaceSettings[c];
                // If it doesn't already exist, we need to create it
                if (!s_FoundGeneratedColliderCollection.ContainsKey(colliderSettings))
                {
                    var generatedColliderCollection = new GeneratedColliderCollection(settings: colliderSettings);
                    generatedColliderCollections.Add(generatedColliderCollection);
                    continue;
                }
                // If it DOEST exist, we remove it so we end up with a list of all items to remove
                s_FoundGeneratedColliderCollection.Remove(colliderSettings);
            }
        }

        // Remove all items that we don't need anymore
        foreach (var foundGeneratedColliderCollection in s_FoundGeneratedColliderCollection.Values)
        {
            generatedColliderCollections.Remove(foundGeneratedColliderCollection);
            foundGeneratedColliderCollection.Dispose();
        }
        s_FoundGeneratedColliderCollection.Clear(); // prevent dangling references

        // Update all existing render meshes
        for (int r = 0; r < generatedRenderMeshes.Count; r++)
        {
            var renderSettings  = generatedRenderMeshes[r].Settings;
            var surfaceGroup    = GeneratedSurfaceManager.GetRenderSurfaceGroupWithSettings(renderSettings);
            if (generatedRenderMeshes[r].HaveMaterialsChanged(in surfaceGroup))
                generatedRenderMeshes[r].UpdateMaterials(in surfaceGroup);
        }

        // Update all existing collider mesh collections
        for (int c = 0; c < generatedColliderCollections.Count; c++)
            UpdatePhysicMaterialColliders(generatedColliderCollections[c]);

    }

    // Reuse existing meshes when a physicMaterial in surfaceGroup already exists, add new ones when they don't & 
    // remove meshes for physicMaterials that are no longer in use.
    static readonly Dictionary<PhysicMaterial, PhysicMaterialMesh> s_FoundColliderMeshes = new Dictionary<PhysicMaterial, PhysicMaterialMesh>();
    static void UpdatePhysicMaterialColliders(GeneratedColliderCollection generatedColliderCollection)
    {
        var surfaceGroup = GeneratedSurfaceManager.GetColliderSurfaceGroupWithSettings(generatedColliderCollection.Settings);

        var physicMaterialMeshes = generatedColliderCollection.PhysicMaterialMeshes;

        s_FoundColliderMeshes.Clear();
        foreach (var physicMaterialMesh in physicMaterialMeshes)
            s_FoundColliderMeshes[physicMaterialMesh.PhysicMaterial] = physicMaterialMesh;

        if (surfaceGroup.physicMaterialInstanceIDs.IsCreated)
        {
            for (int i = 0; i < surfaceGroup.physicMaterialInstanceIDs.Length; i++)
            {
                // Retrieve the unique physic Materials for this surfaceGroup
                var physicMaterial = MaterialManager.Lookup.GetPhysicMaterialByInstanceID(surfaceGroup.physicMaterialInstanceIDs[i]);

                // Ensure we have a mesh for each physicMaterial
                if (!s_FoundColliderMeshes.ContainsKey(physicMaterial))
                    physicMaterialMeshes.Add(new PhysicMaterialMesh(physicMaterial, new Mesh()));
                else
                    // This physicMaterial is used, so we remove it from the list, we'll be left with a list of items to remove
                    s_FoundColliderMeshes.Remove(physicMaterial); 
            }
        }

        // Remove all meshes that are no longer being used
        foreach (var removeColliderMesh in s_FoundColliderMeshes.Values)
        {
            removeColliderMesh.Dispose();
            physicMaterialMeshes.Remove(removeColliderMesh);
        }
        s_FoundColliderMeshes.Clear(); // remove any dangling references
    }
}


public static class GeneratedMeshManager
{

    static readonly Dictionary<MeshSource, Mesh> s_UniqueMeshes = new Dictionary<MeshSource, Mesh>();
    static Dictionary<MeshSource, Mesh> GetMeshLookup(List<ChiselCSGModel> models)
    {
        s_UniqueMeshes.Clear();
        for (int i = 0; i < models.Count; i++)
        {
            var model                   = models[i];
            var generatedModelMeshes    = model.GetGeneratedModelMeshes();
            
            if (!model.changed)
                continue;

            try
            {
                // Generate our meshes based on our model settings and used surface settings.
                // This depends on the submodels inside the model, and the surfaces of the brushes inside the model or submodels
                generatedModelMeshes.Update(in model);

                generatedModelMeshes.CollectMeshes(in model, s_UniqueMeshes);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                // Mark the model as no longer changed to prevent the failure to be repeated infinitely
                model.changed = false;
            }
        }

        return s_UniqueMeshes;
    }

    static void MeshCleanup(Dictionary<MeshSource, Mesh> lookup)
    {
        lookup.Clear(); // prevent dangling resources
    }

    public static void Update(List<ChiselCSGModel> models)
    {
        ChiselManager.Update(models, GetMeshLookup, MeshCleanup);
    }
}