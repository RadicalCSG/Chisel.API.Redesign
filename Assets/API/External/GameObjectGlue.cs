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


// This file holds the glue between the Core API and the GameObject world, this might be moved outside the API, 
// but it's necessary here to inform us what the Core API needs
//
// One complication is that the most efficient way to fill a Mesh, is using MeshDataArray.
// Unfortunately MeshDataArray requires us to know exactly the maximum number of Meshes we need up front.
//
// This makes it necessary to make the number of meshes as predictable as possible, 
// and to register all types of surfaces we're using up front, and keep track of which meshes we might need.
// Keep in mind that even though we might have some surface descriptions, those surfaces might be completely 
// removed by the CSG algorithm and will generate empty meshes.
//
// Rough outline of what happens below
//  * MeshRenderers have certain settings such as "shadowCastingMode" & "receiveShadows" that we 
//      want to expose to individual surfaces. This means we need a MeshRenderer for each of the possible
//      combinations of these settings.
//  * MeshRenderers have a 1:1 relationship with MeshFilter (which holds the Mesh), 
//      which limits 1 MeshRenderer to each GameObject, so we need a GameObject per MeshRenderer.
//  * A renderable mesh can have submeshes, each submesh can have its own Material, 
//      so we can combine them together to limit the number of MeshRenderers/GameObjects we need to manage.
//
//  * MeshColliders need 1 Mesh per PhysicMaterial and cannot be combined, 
//      so we need 1 MeshCollider per PhysicMaterial
//  * However, it's possible to have multiple MeshColliders per GameObject, 
//      so we only need 1 GameObject for all MeshColliders
//  
//  * Models hold multiple GameObjects for all the MeshRenderers and MeshColliders it generates
//  * SubModels allow us to split the meshes for a model into more fine grained pieces. This is useful for culling.
//  * Just like models, submodels have GameObjects for all the MeshRenderers and MeshColliders it generates 
//  * All surfaces that belong to brushes that are children of a submodel, 
//      will be put in it's Meshes, instead of the Meshes of the Model
//  * Note: We need to put the GameObjects of the SubModels underneath the model, to avoid floating point 
//      inaccuracies in case SubModels have an offset compared to the model.
//  * Q: It *might* not make sense to subdivide MeshColliders with SubModels, but only MeshRenderers?
//
// Higher level outline
//  * Hierarchy is defined somewhere
//  * Brushes and surfaces on leaves are registered, letting us know what meshes we potentially need to generate. 
//      Some preprocessing (transformations are updated) and caching happens at this phase
//  * We ask the API let us know which types of meshes need to be generated, and get a unique hash for each mesh that needs to be generated
//      * This hash is generated based on the input that defines the mesh. If the input doesn't change, the output shouldn't change either
//  * We use the code below to create/update the MeshRenderers/MeshColliders/Meshes and their GameObjects.
//      * Q: We know which materials our MeshRenderers need, but we might want to not set Materials for submeshes that turn out to be empty?
//  * We then give a list of specific meshes (from those MeshRenderers/MeshColliders) with specific descriptions, and ask the API to generate those meshes
//      * CSG is applied on all modified brushes, caches are updated, meshes are filled
//
//  TODO: need default model support for when people don't put generators/composites outside a model

// TODO: in case we'd use entity components, then we'd still need all the Meshes, this implies we need to manage the meshes _separately_ from the GameObjects

// Temporary placeholder
public sealed class ModelBehaviour : MonoBehaviour
{
    [SerializeField] internal ModelSettings             settings;
    [SerializeField] internal GeneratedModelHierarchy   generatedModelHierarchy;

    // TODO: this needs to be initialized/registered somewhere
    public ChiselCSGModel model = new ChiselCSGModel();
}


// Holds ALL generated gameobjects/meshrenderers etc. for a specific model
// Also holds the model specific settings, and the GameObject (& its cached transform) that's a wrapper around the generated GameObjects
[Serializable]
public sealed class GeneratedModelHierarchy
{
    public GeneratedModelHierarchy(GameObject containerGameObject) { this.containerGameObject = containerGameObject; this.containerTransform = containerGameObject.transform; }

    [SerializeField] GameObject         containerGameObject;
    public GameObject                   ContainerGameObject { get { return containerGameObject; } }
    
    [SerializeField] Transform          containerTransform;
    public Transform                    ContainerTransform  { get { return containerTransform; } }

    // First index is model, followed by all the SubModels
    [SerializeField] List<GeneratedComponentGroup>  generatedComponentGroups = new List<GeneratedComponentGroup>();
    public List<GeneratedComponentGroup>            GeneratedComponentGroups { get { return generatedComponentGroups; } }
}

[Serializable]
public sealed class GeneratedComponentGroup
{
    public GeneratedComponentGroup(uint meshContainerID) { this.meshContainerID = meshContainerID; }

    // TODO: should each group have its own container?

    // Used to identify the current MeshContainer which might be a model or submodel, 
    // so we can manage its components when a submodel is added/removed
    [SerializeField] uint meshContainerID;
    public uint MeshContainerID { get { return meshContainerID; } }

    [SerializeField] List<RenderComponents>     renderComponents    = new List<RenderComponents>();
    public List<RenderComponents>               RenderComponents    { get { return renderComponents; } }

    [SerializeField] List<ColliderCollection>   colliderCollections = new List<ColliderCollection>();
    public List<ColliderCollection>             ColliderCollections { get { return colliderCollections; } }
}


// Holds a MeshRenderer with the given settings for a specific Model/SubModel 
[Serializable]
public sealed class RenderComponents : IDisposable
{
    public RenderComponents(RenderSurfaceSettings settings, GameObject gameObject, Mesh mesh, MeshFilter meshFilter, MeshRenderer meshRenderer)
    {
        this.settings     = settings;
        this.gameObject   = gameObject;
        this.mesh         = mesh; 
        this.meshFilter   = meshFilter;
        this.meshRenderer = meshRenderer;
    }

    [SerializeField] RenderSurfaceSettings settings;
    public RenderSurfaceSettings           Settings { get { return settings; } }
    [SerializeField] GameObject            gameObject;
    public GameObject                      GameObject { get { return gameObject; } }
    [SerializeField] Mesh                  mesh;
    public Mesh                            Mesh { get { return mesh; } }
    [SerializeField] MeshFilter            meshFilter;
    public MeshFilter                      MeshFilter { get { return meshFilter; } }
    [SerializeField] MeshRenderer          meshRenderer;
    public MeshRenderer                    MeshRenderer { get { return meshRenderer; } }

    public void Dispose()
    {
        if (GameObject) GameObject.SafeDestroy();
        if (Mesh) Mesh.SafeDestroy();
        settings     = RenderSurfaceSettings.Default;
        gameObject   = null;
        mesh         = null;
        meshFilter   = null;
        meshRenderer = null;
    }
}

// Holds all Colliders with the given settings for a specific Model/SubModel
// Each collider will have it's own PhysicMaterial
[Serializable]
public sealed class ColliderCollection : IDisposable
{
    public ColliderCollection(ColliderSurfaceSettings settings, GameObject gameObject)
    {
        this.settings   = settings;
        this.gameObject = gameObject;
    }

    [SerializeField] ColliderSurfaceSettings    settings;
    public ColliderSurfaceSettings              Settings { get { return settings; } }
    [SerializeField] GameObject                 gameObject;
    public GameObject                           GameObject { get { return gameObject; } }

    // TODO: have a way to serialize this
    public readonly Dictionary<uint, Mesh>                   meshes          = new Dictionary<uint, Mesh>();
    public readonly Dictionary<PhysicMaterial, MeshCollider> meshColliders   = new Dictionary<PhysicMaterial, MeshCollider>();

    public void Dispose()
    {
        if (GameObject) GameObject.SafeDestroy();
        foreach (var mesh in meshes.Values)
        {
            if (mesh) mesh.SafeDestroy();
        }
        meshColliders.Clear();
        meshes.Clear();
        settings = ColliderSurfaceSettings.Default;
        gameObject = null;
    }
}

public static class GameObjectManager
{
    const string kGeneratedGameObjectContainerName = "‹[generated]›";

    public static ModelBehaviour FindGameObjectForModel(in ChiselCSGModel model)
    {
        throw new NotImplementedException();
    }

    static readonly Dictionary<MeshSource, Mesh> s_UniqueMeshes = new Dictionary<MeshSource, Mesh>();
    static Dictionary<MeshSource, Mesh> GetMeshLookup(List<ChiselCSGModel> models)
    {
        s_UniqueMeshes.Clear();
        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            var modelBehaviour = FindGameObjectForModel(model);

            // When model is generated, initialize this _somewhere_ (not here), update otherwise
            if (modelBehaviour.generatedModelHierarchy == null)
            {
                modelBehaviour.settings = ModelSettings.Default;
                modelBehaviour.generatedModelHierarchy = CreateModelState(modelBehaviour.gameObject);
            }
            ref var modelSettings           = ref modelBehaviour.settings;
            ref var generatedModelHierarchy = ref modelBehaviour.generatedModelHierarchy;

            if (!model.changed)
                continue;

            try
            {
                // Generate our components based on what kinds of meshes we need to generate (Colliders and MeshRenderers)
                // This depends on the submodels inside the model, and the surfaces of the brushes inside the model or submodels
                EnsureAllGeneratedModelComponentsExist(in modelSettings, in model, generatedModelHierarchy);

                CollectMeshes(in model, generatedModelHierarchy, s_UniqueMeshes);
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

    static readonly List<IChiselMeshContainer> s_MeshContainerList = new List<IChiselMeshContainer>();
    static readonly Dictionary<uint, GeneratedComponentGroup> s_GeneratedComponentsLookup = new Dictionary<uint, GeneratedComponentGroup>();
    static readonly Dictionary<uint, GeneratedComponentGroup> s_RemoveGeneratedComponents = new Dictionary<uint, GeneratedComponentGroup>();
    static readonly List<uint> s_AddGeneratedComponents = new List<uint>();

    static void EnsureAllGeneratedModelComponentsExist(in ModelSettings modelSettings, in ChiselCSGModel model, GeneratedModelHierarchy generatedModelHierarchy)
    {
        // TODO: make this all undoable so any registered "undo" for any brush modification will include these changes too
        //          might make undos use a lot of memory? but efficiently redoing the work might be more problematic ...?

        // Find all existing GeneratedComponentGroups and make a lookup table based on their ids
        s_GeneratedComponentsLookup.Clear();
        s_RemoveGeneratedComponents.Clear();
        for (int g = 0; g < generatedModelHierarchy.GeneratedComponentGroups.Count; g++)
        {
            var generatedComponentGroup = generatedModelHierarchy.GeneratedComponentGroups[g];
            var meshContainerID         = generatedComponentGroup.MeshContainerID;
            s_GeneratedComponentsLookup.Add(meshContainerID, generatedComponentGroup);
            s_RemoveGeneratedComponents.Add(meshContainerID, generatedComponentGroup);
        }

        // Make a list of all containers
        // TODO: instead of building this list, just manage this per model (we're already storing submodels in a list)
        s_MeshContainerList.Add(model);
        for (int s = 0; s < model.SubModelCount; s++)
            s_MeshContainerList.Add(model.GetSubModelAt(s));

        // Find all added and removed GeneratedComponentGroups (if any)
        s_AddGeneratedComponents.Clear();
        for (int m = s_MeshContainerList.Count - 1; m >= 0; m--)
        {
            var meshContainer   = s_MeshContainerList[m];
            var meshContainerID = meshContainer.MeshContainerID;
            if (!s_GeneratedComponentsLookup.ContainsKey(meshContainerID))
                s_AddGeneratedComponents.Add(meshContainerID);
            s_RemoveGeneratedComponents.Remove(meshContainerID);
        }

        // Go through all removed GeneratedComponentGroups
        foreach (var generatedComponentGroup in s_RemoveGeneratedComponents.Values)
        {
            if (s_AddGeneratedComponents.Count > 0)
            {
                // As long as we have GeneratedComponentGroups to add, recycle them instead of destroying and creating new ones
                var meshContainerID = s_AddGeneratedComponents[s_AddGeneratedComponents.Count - 1];
                s_AddGeneratedComponents.RemoveAt(s_AddGeneratedComponents.Count - 1);

                // We need to create a new one to hold the containerID though
                var newGeneratedComponents = new GeneratedComponentGroup(meshContainerID);

                // But we copy all the components
                newGeneratedComponents.RenderComponents.AddRange(generatedComponentGroup.RenderComponents);
                newGeneratedComponents.ColliderCollections.AddRange(generatedComponentGroup.ColliderCollections);

                // And clear the old one (just in case)
                generatedComponentGroup.RenderComponents.Clear();
                generatedComponentGroup.ColliderCollections.Clear();

                // And register the new GeneratedComponentGroups
                var index = generatedModelHierarchy.GeneratedComponentGroups.IndexOf(generatedComponentGroup);
                generatedModelHierarchy.GeneratedComponentGroups[index] = newGeneratedComponents;
                s_GeneratedComponentsLookup[meshContainerID] = newGeneratedComponents;
            } else
                // If we don't have any GeneratedComponentGroups to add, we can't recycle it so we destroy this it instead
                ClearGeneratedComponents(generatedComponentGroup);
            generatedModelHierarchy.GeneratedComponentGroups.Remove(generatedComponentGroup);
        }

        // If we have any GeneratedComponentGroups left to create, we just go through them and create them
        if (s_AddGeneratedComponents.Count > 0)
        {
            for(int a = 0; a < s_AddGeneratedComponents.Count; a++)
            { 
                var generatedComponentGroup = new GeneratedComponentGroup(s_AddGeneratedComponents[a]);
                generatedModelHierarchy.GeneratedComponentGroups.Add(generatedComponentGroup);
            }
        }

        // Go through the GeneratedComponentGroups for each MeshContainer and update its components
        for (int m = 0; m < s_MeshContainerList.Count; m++)
        {
            var meshContainer           = s_MeshContainerList[m];
            var meshContainerID         = meshContainer.MeshContainerID;
            var generatedComponentGroup = s_GeneratedComponentsLookup[meshContainerID];

            // Update the components in the meshContainer, ensure they exist
            ManageMeshContainerGeneratedComponents(in modelSettings, generatedComponentGroup, generatedModelHierarchy, meshContainer);
        }

        // Ensure we don't have any dangling resources left
        s_GeneratedComponentsLookup.Clear();
        s_MeshContainerList.Clear();
        s_AddGeneratedComponents.Clear();
        s_RemoveGeneratedComponents.Clear();

        // TODO: combine this with above
        for (int g = 0; g < generatedModelHierarchy.GeneratedComponentGroups.Count; g++)
        {
            var generatedComponentGroup = generatedModelHierarchy.GeneratedComponentGroups[g];
            foreach (var renderComponents in generatedComponentGroup.RenderComponents)
                UpdateRenderComponents(in modelSettings, generatedModelHierarchy, renderComponents);

            foreach (var colliderCollection in generatedComponentGroup.ColliderCollections)
                UpdateColliderCollection(in modelSettings, generatedModelHierarchy, colliderCollection);
        }
    }


    public static GeneratedModelHierarchy CreateModelState(GameObject modelGameObject)
    {
        if (modelGameObject == null) throw new NullReferenceException($"{nameof(modelGameObject)} is null");
        var activeState = modelGameObject.activeSelf;
        SetGameObjectActive(modelGameObject, false);// Set gameObject temporarily to false, to defer events until the end
        try
        {
            var modelTransform = modelGameObject.transform;
            var containerGameObject = new GameObject(kGeneratedGameObjectContainerName);
            var containerTransform = containerGameObject.transform;
            SetParent(containerTransform, modelTransform);
            containerTransform.SetParent(modelTransform, false);
            SetNotEditable(containerTransform); // Set transform to be not editable by the user

            return new GeneratedModelHierarchy(containerGameObject: containerGameObject);
        }
        finally
        {
            SetGameObjectActive(modelGameObject, activeState);
        }
    }

    public static void ClearGeneratedComponents(GeneratedComponentGroup generatedComponentGroup)
    {
        // TODO: this is problematic with prefabs, it doesn't allow you to delete GameObjects ..

        var renderComponents = generatedComponentGroup.RenderComponents;
        for (int i = 0; i < renderComponents.Count; i++)
            renderComponents[i].Dispose();
        renderComponents.Clear();

        var colliderCollections = generatedComponentGroup.ColliderCollections;
        for (int i = 0; i < colliderCollections.Count; i++)
            colliderCollections[i].Dispose();
        colliderCollections.Clear();
    }

    public static void ManageMeshContainerGeneratedComponents(in ModelSettings modelSettings, GeneratedComponentGroup generatedComponentGroup, GeneratedModelHierarchy generatedModelHierarchy, IChiselMeshContainer meshContainer)
    {
        // TODO: Handle model/submodel being disabled, in which case we don't want to rebuild it, we just want to disable the generated components too, until the model/submodel is enabled again

        var renderSurfaceSettings   = meshContainer.UniqueRenderSurfaceSettings;
        var colliderSurfaceSettings = meshContainer.UniqueColliderSurfaceSettings;

        // TODO: only create when it doesn't already exist, disable when it's no longer used, ensure that it's enabled when it's actually used

        var renderComponents = generatedComponentGroup.RenderComponents;
        renderComponents.Capacity = renderSurfaceSettings.Length;
        for (int r = 0; r < renderComponents.Count; r++)
        {
            var renderSettings = renderSurfaceSettings[r];
            renderComponents.Add(CreateRenderComponents(in modelSettings, generatedModelHierarchy, generatedComponentGroup, in renderSettings));
        }

        var colliderCollections = generatedComponentGroup.ColliderCollections;
        colliderCollections.Capacity = colliderSurfaceSettings.Length;
        for (int c = 0; c < colliderCollections.Count; c++)
        {
            var colliderSettings = colliderSurfaceSettings[c];
            colliderCollections.Add(CreateColliderCollection(in modelSettings, generatedModelHierarchy, generatedComponentGroup, in colliderSettings));
        }
    }

    static void CollectMeshes(in ChiselCSGModel model, GeneratedModelHierarchy generatedModelHierarchy, Dictionary<MeshSource, Mesh> meshes)
    {
        // TODO: - Should have a way to cache this when unmodified
        var meshSource = new MeshSource
        {
            modelID = model.ModelID
        };
        for (int g = 0; g < generatedModelHierarchy.GeneratedComponentGroups.Count; g++)
        {
            var generatedComponentGroup = generatedModelHierarchy.GeneratedComponentGroups[g];
            meshSource.meshContainerID = generatedComponentGroup.MeshContainerID;

            foreach (var renderComponents in generatedComponentGroup.RenderComponents)
            {
                meshSource.settingsHash = renderComponents.Settings.GetHash();
                meshSource.meshID = 0;
                meshes[meshSource] = renderComponents.Mesh;
            }

            foreach (var colliderCollection in generatedComponentGroup.ColliderCollections)
            {
                meshSource.settingsHash = colliderCollection.Settings.GetHash();
                foreach (var pair in colliderCollection.meshes)
                {
                    meshSource.meshID = pair.Key;
                    meshes[meshSource] = pair.Value;
                }
            }
        }
    }

    // Creates a unique GameObject, MeshRenderer and MeshFilter for the given RenderSurfaceGroup
    public static RenderComponents CreateRenderComponents(in ModelSettings modelSettings, GeneratedModelHierarchy generatedModelHierarchy, GeneratedComponentGroup generatedComponentGroup, in RenderSurfaceSettings renderSettings)
    {
        var name = ChiselObjectNames.GetName(in generatedComponentGroup, in renderSettings);
        var gameObject = new GameObject(name);
        SetGameObjectActive(gameObject, false);// Set gameObject temporarily to false, to defer events until the end 
        try
        {
            SetNotEditable(gameObject.transform); // Set transform to be not editable by the user

            // The layer in these unique surface settings never changes, so it's only set once
            gameObject.layer = renderSettings.layer;

            var components = CreateRendererComponent(in renderSettings, gameObject, name);

            // Update the model settings for this meshRenderer (these CAN change)
            UpdateRenderModelSettings(in modelSettings, generatedModelHierarchy, components);

            return components;
        }
        finally { SetGameObjectActive(gameObject, modelSettings.renderingEnabled); }
    }

    // Updates the materials for a given MeshRenderer belonging to a surfaceGroup
    public static void UpdateRenderComponents(in ModelSettings modelSettings, GeneratedModelHierarchy generatedModelHierarchy, in RenderComponents renderComponents)
    {
        SetGameObjectActive(renderComponents.GameObject, false);// Set gameObject temporarily to false, to defer events until the end 
        try
        {
            UpdateRenderModelSettings(in modelSettings, generatedModelHierarchy, renderComponents);

            var surfaceGroup = GeneratedSurfaceManager.GetRenderSurfaceGroupWithSettings(renderComponents.Settings);
            if (!surfaceGroup.materialInstanceIDs.IsCreated || surfaceGroup.materialInstanceIDs.Length == 0 ||
                renderComponents.MeshFilter.sharedMesh == null || renderComponents.MeshFilter.sharedMesh.vertexCount == 0)
            {
                if (renderComponents.MeshRenderer.sharedMaterial != null ) renderComponents.MeshRenderer.sharedMaterial = null;
                if (renderComponents.MeshRenderer.enabled        != false) renderComponents.MeshRenderer.enabled = false;
                return;
            }

            if (HaveMaterialsChanged(in surfaceGroup, renderComponents.MeshRenderer))
                UpdateMaterials(in surfaceGroup, renderComponents.MeshRenderer);

            if (renderComponents.MeshRenderer.enabled != true) renderComponents.MeshRenderer.enabled = true;
        }
        finally { SetGameObjectActive(renderComponents.GameObject, modelSettings.renderingEnabled); }
    }


    // Creates a unique GameObject and a List of MeshColliders for the given ColliderSurfaceGroup
    public static ColliderCollection CreateColliderCollection(in ModelSettings modelSettings, GeneratedModelHierarchy generatedModelHierarchy, GeneratedComponentGroup generatedComponentGroup, in ColliderSurfaceSettings colliderSettings)
    {
        var name                = ChiselObjectNames.GetName(generatedComponentGroup, colliderSettings);
        var gameObject          = new GameObject(name);
        var colliderCollection  = new ColliderCollection(settings: colliderSettings, gameObject: gameObject);
        SetGameObjectActive(gameObject, false);// Set gameObject temporarily to false, to defer events until the end 
        try
        {
            SetNotEditable(gameObject.transform); // Set transform to be not editable by the user

            // The layer in these unique surface settings never changes, so it's only set once
            gameObject.layer = colliderSettings.layer;

            UpdateColliderCollection(in modelSettings, generatedModelHierarchy, colliderCollection);
        }
        finally { SetGameObjectActive(gameObject, modelSettings.collidersEnabled); }
        return colliderCollection;
    }

    // Updates the required colliders of a given ColliderSurfaceGroup to match the required physicMaterials, and sets/updates their settings which are set in its ModelSettings
    public static void UpdateColliderCollection(in ModelSettings modelSettings, GeneratedModelHierarchy generatedModelHierarchy, ColliderCollection colliderCollection)
    {
        SetGameObjectActive(colliderCollection.GameObject, false);// Set gameObject temporarily to false, to defer events until the end 
        try
        {
            UpdateGameObjectModelSettings(generatedModelHierarchy, colliderCollection.GameObject);

            var surfaceGroup = GeneratedSurfaceManager.GetColliderSurfaceGroupWithSettings(colliderCollection.Settings);
            if (!surfaceGroup.physicMaterialInstanceIDs.IsCreated || 
                surfaceGroup.physicMaterialInstanceIDs.Length == 0)
            {
                // Remove all meshes and colliders that are not being used
                colliderCollection.meshes.Clear();
                colliderCollection.meshColliders.Clear();
                return;
            }

            // Reuse existing colliders when a physicMaterial in surfaceGroup already exists, add new ones when they don't & 
            // remove colliders for physicMaterials that are no longer in use.
            UpdatePhysicMaterialColliders(in modelSettings, colliderCollection, in surfaceGroup);
        }
        finally { SetGameObjectActive(colliderCollection.GameObject, modelSettings.collidersEnabled); }
    }

    #region Private methods
    static void AddColliderComponent(PhysicMaterial physicMaterial, ColliderCollection colliderCollection)
    {
        var gameObject = colliderCollection.GameObject;

        var meshCollider = gameObject.AddComponent<MeshCollider>();

        // The mesh for these unique colliders never changes, so it's set once
        var mesh = new Mesh { name = ChiselObjectNames.GetName(gameObject.name, physicMaterial) };
        meshCollider.sharedMesh = mesh;

        // The collider settings never change, so are set once
        meshCollider.sharedMaterial = physicMaterial;

        unchecked { colliderCollection.meshes[(uint)physicMaterial.GetHashCode()] = mesh; }
        colliderCollection.meshColliders[physicMaterial] = meshCollider;
    }

    static void UpdateColliderModelSettings(in ModelSettings modelSettings, MeshCollider meshCollider)
    {
        if (meshCollider.cookingOptions != modelSettings.cookingOptions) meshCollider.cookingOptions = modelSettings.cookingOptions;
        if (meshCollider.convex         != modelSettings.convex        ) meshCollider.convex         = modelSettings.convex;
        if (meshCollider.isTrigger      != modelSettings.isTrigger     ) meshCollider.isTrigger      = modelSettings.isTrigger;
        if (meshCollider.contactOffset  != modelSettings.contactOffset ) meshCollider.contactOffset  = modelSettings.contactOffset;

        var hasVertices = meshCollider.sharedMesh.vertexCount > 0;
        if (meshCollider.enabled        != hasVertices) meshCollider.enabled = hasVertices;
    }

    static readonly HashSet<PhysicMaterial> s_RemovedPhysicMaterials = new HashSet<PhysicMaterial>();
    static void UpdatePhysicMaterialColliders(in ModelSettings modelSettings, ColliderCollection colliderCollection, in ColliderSurfaceGroup surfaceGroup)
    {
        s_RemovedPhysicMaterials.Clear();
        foreach (var key in colliderCollection.meshColliders.Keys)
            s_RemovedPhysicMaterials.Add(key);

        for (int i = 0; i < surfaceGroup.physicMaterialInstanceIDs.Length; i++)
        {
            // Retrieve the unique physic Materials for this surfaceGroup
            var physicMaterial = MaterialManager.Lookup.GetPhysicMaterialByInstanceID(surfaceGroup.physicMaterialInstanceIDs[i]);
            s_RemovedPhysicMaterials.Remove(physicMaterial); // this physicMaterial is used, so remove it from the remove list

            // Ensure we have a collider for each physicMaterial
            if (!colliderCollection.meshColliders.TryGetValue(physicMaterial, out MeshCollider meshCollider))
                AddColliderComponent(physicMaterial, colliderCollection);

            // And set or update the model settings for this collider
            UpdateColliderModelSettings(in modelSettings, meshCollider);
        }

        if (s_RemovedPhysicMaterials.Count > 0)
        {
            // Remove all meshes and colliders that are no longer being used
            foreach (var key in s_RemovedPhysicMaterials)
            {
                unchecked { colliderCollection.meshes.Remove((uint)key.GetHashCode()); }
                colliderCollection.meshColliders.Remove(key);
            }
            s_RemovedPhysicMaterials.Clear(); // prevent dangling references
        }
    }

    static RenderComponents CreateRendererComponent(in RenderSurfaceSettings settings, GameObject gameObject, string gameObjectName)
    {
        var meshFilter      = gameObject.AddComponent<MeshFilter>();

        // The mesh for these unique surface settings never changes, so it's set once
        var mesh            = new Mesh() { name = gameObjectName };
        meshFilter.sharedMesh = mesh;

        var meshRenderer    = gameObject.AddComponent<MeshRenderer>();
        // The surface settings never change, so are set once
        meshRenderer.renderingLayerMask = settings.renderingLayerMask;
        meshRenderer.shadowCastingMode  = settings.shadowCastingMode;
        meshRenderer.receiveShadows     = settings.receiveShadows;

        return new RenderComponents(settings: settings, gameObject: gameObject, mesh: mesh, meshFilter: meshFilter, meshRenderer: meshRenderer);
    }

    // Updates the model settings for this gameObject and meshRenderer, used when they're modified on the model
    static void UpdateRenderModelSettings(in ModelSettings modelSettings, GeneratedModelHierarchy generatedModelHierarchy, RenderComponents components)
    {
        UpdateGameObjectModelSettings(generatedModelHierarchy, components.GameObject);

        var meshRenderer = components.MeshRenderer;

        if (meshRenderer.lightProbeProxyVolumeOverride != modelSettings.lightProbeProxyVolumeOverride) meshRenderer.lightProbeProxyVolumeOverride  = modelSettings.lightProbeProxyVolumeOverride;
        if (meshRenderer.probeAnchor                   != modelSettings.probeAnchor                  ) meshRenderer.probeAnchor                    = modelSettings.probeAnchor;
        if (meshRenderer.motionVectorGenerationMode    != modelSettings.motionVectorGenerationMode   ) meshRenderer.motionVectorGenerationMode     = modelSettings.motionVectorGenerationMode;
        if (meshRenderer.reflectionProbeUsage          != modelSettings.reflectionProbeUsage         ) meshRenderer.reflectionProbeUsage           = modelSettings.reflectionProbeUsage;
        if (meshRenderer.lightProbeUsage               != modelSettings.lightProbeUsage              ) meshRenderer.lightProbeUsage                = modelSettings.lightProbeUsage;
        if (meshRenderer.rayTracingMode                != modelSettings.rayTracingMode               ) meshRenderer.rayTracingMode                 = modelSettings.rayTracingMode;
        if (meshRenderer.allowOcclusionWhenDynamic     != modelSettings.allowOcclusionWhenDynamic    ) meshRenderer.allowOcclusionWhenDynamic      = modelSettings.allowOcclusionWhenDynamic;
        if (meshRenderer.rendererPriority              != modelSettings.rendererPriority             ) meshRenderer.rendererPriority               = modelSettings.rendererPriority;
        if (meshRenderer.lightmapScaleOffset           != modelSettings.lightmapScaleOffset          ) meshRenderer.lightmapScaleOffset            = modelSettings.lightmapScaleOffset;
        if (meshRenderer.realtimeLightmapScaleOffset   != modelSettings.realtimeLightmapScaleOffset  ) meshRenderer.realtimeLightmapScaleOffset    = modelSettings.realtimeLightmapScaleOffset;
#if UNITY_EDITOR
        if (meshRenderer.receiveGI                     != modelSettings.receiveGI                    ) meshRenderer.receiveGI                      = modelSettings.receiveGI;
        if (meshRenderer.stitchLightmapSeams           != modelSettings.stitchLightmapSeams          ) meshRenderer.stitchLightmapSeams            = modelSettings.stitchLightmapSeams;
#endif
    }
    
    static readonly HashSet<int>    s_LookupMaterials = new HashSet<int>(); 
    static readonly List<Material>  s_SharedMaterials = new List<Material>();
    static bool HaveMaterialsChanged(in RenderSurfaceGroup renderSurfaceGroup, MeshRenderer meshRenderer)
    {
        meshRenderer.GetSharedMaterials(s_SharedMaterials);
        try
        {
            if (s_SharedMaterials.Count != renderSurfaceGroup.materialInstanceIDs.Length)
                return true;

            s_LookupMaterials.Clear();
            foreach (var sharedMaterial in s_SharedMaterials)
                s_LookupMaterials.Add(sharedMaterial.GetInstanceID());

            for (int m = 0; m < renderSurfaceGroup.materialInstanceIDs.Length; m++)
            {
                if (!s_LookupMaterials.Contains(renderSurfaceGroup.materialInstanceIDs[m]))
                    return true;
            }

            s_LookupMaterials.Clear(); // avoid dangling references
            return false;
        }
        finally
        {
            s_SharedMaterials.Clear(); // avoid dangling references
        }
    }
    static void UpdateMaterials(in RenderSurfaceGroup renderSurfaceGroup, MeshRenderer meshRenderer)
    {
        if (renderSurfaceGroup.materialInstanceIDs.Length == 0)
        {
            if (meshRenderer.sharedMaterials != null) meshRenderer.sharedMaterials = null;
        } else
        {
            var materialLookup = MaterialManager.Lookup;
            var materials = new Material[renderSurfaceGroup.materialInstanceIDs.Length];
            for (int m = 0; m < renderSurfaceGroup.materialInstanceIDs.Length; m++)
                materials[m] = materialLookup.GetMaterialByInstanceID(renderSurfaceGroup.materialInstanceIDs[m]);
            meshRenderer.sharedMaterials = materials;
        }
    }

    static void SetParent(Transform gameObjectTransform, Transform parentTransform)
    {
        if (gameObjectTransform.parent != parentTransform)
            gameObjectTransform.SetParent(parentTransform, worldPositionStays: false);

        SetNotEditable(gameObjectTransform);

        if (gameObjectTransform.localPosition != Vector3.zero       ) gameObjectTransform.localPosition  = Vector3.zero;
        if (gameObjectTransform.localRotation != Quaternion.identity) gameObjectTransform.localRotation  = Quaternion.identity;
        if (gameObjectTransform.localScale    != Vector3.one        ) gameObjectTransform.localScale     = Vector3.one;
    }
     
    static void UpdateGameObjectModelSettings(GeneratedModelHierarchy generatedModelHierarchy, GameObject gameObject)
    {
        // Check if anything has changed first because setting values might trigger events somewhere
        var transform = gameObject.transform;
        SetParent(transform, generatedModelHierarchy.ContainerTransform);
        SetNotEditable(transform);

#if UNITY_EDITOR
        var requiredEditorFlags = UnityEditor.GameObjectUtility.GetStaticEditorFlags(generatedModelHierarchy.ContainerGameObject);
        var currentEditorFlags  = UnityEditor.GameObjectUtility.GetStaticEditorFlags(gameObject);
        if (requiredEditorFlags != currentEditorFlags)
            UnityEditor.GameObjectUtility.SetStaticEditorFlags(gameObject, requiredEditorFlags);
#endif
    }
    
    static void SetGameObjectActive(GameObject gameObject, bool active)
    {
        // Check if anything has changed first because setting values might trigger events somewhere
        if (gameObject.activeSelf != active)
            gameObject.SetActive(active);
    }

    static void SetNotEditable(UnityEngine.Object obj)
    {
        var prevHideFlags = obj.hideFlags;
        if ((prevHideFlags & HideFlags.NotEditable) != HideFlags.NotEditable)
            obj.hideFlags = prevHideFlags | HideFlags.NotEditable;
    }
    #endregion
}