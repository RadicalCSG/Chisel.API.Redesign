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

using GeneratedComponentGroupManager = ObjectLifetime<GeneratedComponentGroup, GeneratedMeshGroup,          uint,                     ModelSettings                          , (ModelSettings, GeneratedModelHierarchy)>;
using RenderComponentsManager        = ObjectLifetime<RenderComponents,        GeneratedRenderMesh,         RenderSurfaceSettings,   (ModelSettings, GeneratedComponentGroup), (ModelSettings, GeneratedModelHierarchy)>;
using ColliderCollectionManager      = ObjectLifetime<ColliderCollection,      GeneratedColliderCollection, ColliderSurfaceSettings, (ModelSettings, GeneratedComponentGroup), (ModelSettings, GeneratedModelHierarchy)>;

using RenderComponentsGroup          = RecyclableCollection<RenderComponents,   RenderSurfaceSettings,   (ModelSettings, GeneratedComponentGroup)>;
using ColliderComponentsGroup        = RecyclableCollection<ColliderCollection, ColliderSurfaceSettings, (ModelSettings, GeneratedComponentGroup)>;


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

// TODO: need default model support for when people don't put generators/composites outside a model
// TODO: in case we'd use entity components, then we'd still need all the Meshes, this implies we need to manage the meshes _separately_ from the GameObjects
// TODO: Handle model/submodel being disabled, in which case we don't want to rebuild it, we just want to disable the generated meshes too, until the model/submodel is enabled again
// TODO: Handle when model is locked in version control and is not allowed to be modified


// Temporary placeholder
public sealed class ModelBehaviour : MonoBehaviour
{
    [SerializeField] internal ModelSettings             settings;
    [SerializeField] internal GeneratedModelHierarchy   generatedModelHierarchy;

    // TODO: this needs to be initialized/registered somewhere
    public ChiselCSGModel model = new ChiselCSGModel();
    

    public void UpdateMeshes(GeneratedModelMeshes generatedModelMeshes)
    {
        generatedModelHierarchy.Update(settings, generatedModelMeshes);
    }
}

// Holds ALL generated gameobjects/meshrenderers etc. for a specific model
// Also holds the model specific settings, and the GameObject (& its cached transform) that's a wrapper around the generated GameObjects
[Serializable]
public sealed class GeneratedModelHierarchy : RecyclableCollection<GeneratedComponentGroup, uint, ModelSettings>
{
    public GeneratedModelHierarchy(GameObject containerGameObject) { this.containerGameObject = containerGameObject; this.containerTransform = containerGameObject.transform; }

    [SerializeField] GameObject     containerGameObject;
    public GameObject               ContainerGameObject { get { return containerGameObject; } }
    
    [SerializeField] Transform      containerTransform;
    public Transform                ContainerTransform  { get { return containerTransform; } }
    
    public void Update(ModelSettings modelSettings, GeneratedModelMeshes generatedModelMeshes)
    {
        GeneratedComponentGroupManager.Manage(
            existingItems:      this,
            currentStateItems:  generatedModelMeshes.GeneratedMeshGroups,

            create:             GeneratedComponentGroup.Create, createContext: (modelSettings),
            updateContext:      (modelSettings, this)); // GeneratedComponentGroup.Update
    }


    const string kGeneratedGameObjectContainerName = "‹[generated]›";

    public static GeneratedModelHierarchy Create(GameObject modelGameObject)
    {
        if (modelGameObject == null) throw new NullReferenceException($"{nameof(modelGameObject)} is null");
        var activeState = modelGameObject.activeSelf;
        GameObjectManager.SetGameObjectActive(modelGameObject, false);// Set gameObject temporarily to false, to defer events until the end
        try
        {
            var modelTransform = modelGameObject.transform;
            var containerGameObject = new GameObject(kGeneratedGameObjectContainerName);
            var containerTransform = containerGameObject.transform;
            GameObjectManager.SetParent(containerTransform, modelTransform);
            containerTransform.SetParent(modelTransform, false);
            GameObjectManager.SetNotEditable(containerTransform); // Set transform to be not editable by the user

            return new GeneratedModelHierarchy(containerGameObject: containerGameObject);
        }
        finally
        {
            GameObjectManager.SetGameObjectActive(modelGameObject, activeState);
        }
    }
}

[Serializable]
public sealed class GeneratedComponentGroup : IRecyclable<GeneratedComponentGroup, uint, ModelSettings>, IUpdatable<GeneratedMeshGroup, (ModelSettings, GeneratedModelHierarchy)>, UniqueIDProvider<uint>
{
    uint UniqueIDProvider<uint>.UniqueID => meshContainerID;

    public GeneratedComponentGroup(uint meshContainerID) { this.meshContainerID = meshContainerID; }

    // Used to identify the current MeshContainer which might be a model or submodel, 
    // so we can manage its components when a submodel is added/removed
    [SerializeField] uint meshContainerID;
    public uint MeshContainerID { get { return meshContainerID; } }

    public uint UniqueID { get { return MeshContainerID; } }

    public RenderComponentsGroup    renderComponentsGroup   = new RenderComponentsGroup();
    public ColliderComponentsGroup  colliderComponentsGroup = new ColliderComponentsGroup();
    
    public bool CanDispose() { return renderComponentsGroup.CanDispose() && colliderComponentsGroup.CanDispose(); }

    public void Dispose()
    {
        renderComponentsGroup.Dispose();
        colliderComponentsGroup.Dispose();
    }

    void IHideShow.Hide()
    {
        ((IHideShow)renderComponentsGroup).Hide();
        ((IHideShow)colliderComponentsGroup).Hide();
    }

    void IHideShow.Show()
    {
        ((IHideShow)renderComponentsGroup).Show();
        ((IHideShow)colliderComponentsGroup).Show();
    }

    internal static GeneratedComponentGroup Create(uint meshContainerID, ModelSettings settings) 
    { 
        return new GeneratedComponentGroup(meshContainerID); 
    }

    public GeneratedComponentGroup Recycle(uint meshContainerID, ModelSettings settings)
    {
        this.meshContainerID = meshContainerID;
        var renderComponents = renderComponentsGroup.Components;
        for (int i = 0; i < renderComponents.Count; i++)
        {
            renderComponents[i].Recycle
        }

        // And enable the components in new GeneratedComponentGroups
        // TODO: GeneratedComponentGroup should be enabled etc.
        return this;
    }

    public void Update(GeneratedMeshGroup generatedMeshGroup, (ModelSettings, GeneratedModelHierarchy) context)
    {
        var (modelSettings, generatedModelHierarchy) = context;

        // TODO: use already created meshes in generatedModelMesh
        RenderComponentsManager.Manage(
                existingItems:      renderComponentsGroup,
                currentStateItems:  generatedMeshGroup.GeneratedRenderMeshes,

                create:             RenderComponents.Create, 
                createContext:      (modelSettings, this),
                updateContext:      context);

        ColliderCollectionManager.Manage(
                existingItems:      colliderComponentsGroup,
                currentStateItems:  generatedMeshGroup.GeneratedColliderCollections,
                
                create:             ColliderCollection.Create, 
                createContext:      (modelSettings, this),
                updateContext:      context);
    }
}

// Holds a MeshRenderer with the given settings for a specific Model/SubModel 
[Serializable]
public sealed class RenderComponents : UniqueIDProvider<RenderSurfaceSettings>, IRecyclable<RenderComponents, RenderSurfaceSettings, (ModelSettings, GeneratedComponentGroup)>, IUpdatable<GeneratedRenderMesh, (ModelSettings, GeneratedModelHierarchy)>
{
    RenderSurfaceSettings UniqueIDProvider<RenderSurfaceSettings>.UniqueID => settings;
    
    RenderComponents(RenderSurfaceSettings settings, GameObject gameObject, Mesh mesh, MeshFilter meshFilter, MeshRenderer meshRenderer)
    {
        this.settings     = settings;
        this.gameObject   = gameObject;
        this.mesh         = mesh; 
        this.meshFilter   = meshFilter;
        this.meshRenderer = meshRenderer;
    }

    public uint UniqueID { get { return settings.GetHash(); } }

    [SerializeField] RenderSurfaceSettings settings;
    public RenderSurfaceSettings           Settings     { get { return settings; } }
    
    [SerializeField] GameObject            gameObject;
    public GameObject                      GameObject   { get { return gameObject; } }
    
    [SerializeField] Mesh                  mesh;
    public Mesh                            Mesh         { get { return mesh; } internal set { mesh = value; } }

    [SerializeField] MeshFilter            meshFilter;
    public MeshFilter                      MeshFilter   { get { return meshFilter; } }

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

    void IHideShow.Hide()
    {
        if (mesh) mesh.SafeDestroy();
        mesh = null;
        meshRenderer.enabled = false;
        meshRenderer.sharedMaterial = null;
        meshFilter.sharedMesh = null;
    }

    void IHideShow.Show()
    {
        if (!mesh) mesh = new Mesh() { name = gameObject.name };
        meshRenderer.enabled = true;
        meshFilter.sharedMesh = mesh;
    }

    public bool CanDispose() { return (gameObject == null) || gameObject.CanDestroy(); }

    internal static RenderComponents Create(RenderSurfaceSettings renderSettings, (ModelSettings, GeneratedComponentGroup) context)
    {
        var (modelSettings, generatedComponentGroup) = context;
        var name = ChiselObjectNames.GetName(generatedComponentGroup, in renderSettings);
        var gameObject = new GameObject(name);
        GameObjectManager.SetGameObjectActive(gameObject, false); // Set gameObject temporarily to false, to defer events until the end 
        try
        {
            GameObjectManager.SetNotEditable(gameObject.transform); // Set transform to be not editable by the user

            // The layer in these unique surface settings never changes, so it's only set once
            gameObject.layer = renderSettings.layer;

            var meshFilter      = gameObject.AddComponent<MeshFilter>();

            // The mesh for these unique surface settings never changes, so it's set once
            var mesh            = new Mesh() { name = name };
            meshFilter.sharedMesh = mesh;

            var meshRenderer    = gameObject.AddComponent<MeshRenderer>();
            // The surface settings never change, so are set once
            meshRenderer.renderingLayerMask = renderSettings.renderingLayerMask;
            meshRenderer.shadowCastingMode  = renderSettings.shadowCastingMode;
            meshRenderer.receiveShadows     = renderSettings.receiveShadows;

            return new RenderComponents(settings: renderSettings, gameObject: gameObject, mesh: mesh, meshFilter: meshFilter, meshRenderer: meshRenderer);
        }
        finally { GameObjectManager.SetGameObjectActive(gameObject, modelSettings.renderingEnabled); }
    }

    public RenderComponents Recycle(RenderSurfaceSettings renderSettings, (ModelSettings, GeneratedComponentGroup) context)
    {
        // TODO: not implemented
        throw new NotImplementedException();
    }

    // Updates the materials for a given MeshRenderer belonging to a surfaceGroup

    public void Update(GeneratedRenderMesh generatedRenderMesh, (ModelSettings, GeneratedModelHierarchy) context)
    {
        var (modelSettings, generatedModelHierarchy) = context;
        GameObjectManager.SetGameObjectActive(gameObject, false);// Set gameObject temporarily to false, to defer events until the end 
        try
        {
            GameObjectManager.UpdateRenderModelSettings(in modelSettings, generatedModelHierarchy, this);

            var surfaceGroup = GeneratedSurfaceManager.GetRenderSurfaceGroupWithSettings(settings);
            if (!surfaceGroup.materialInstanceIDs.IsCreated || surfaceGroup.materialInstanceIDs.Length == 0 ||
                meshFilter.sharedMesh == null || meshFilter.sharedMesh.vertexCount == 0)
            {
                if (meshRenderer.sharedMaterial != null) meshRenderer.sharedMaterial = null;
                if (meshRenderer.enabled != false) meshRenderer.enabled = false;
                return;
            }

            if (GameObjectManager.HaveMaterialsChanged(in surfaceGroup, meshRenderer))
                GameObjectManager.UpdateMaterials(in surfaceGroup, meshRenderer);

            if (meshRenderer.enabled != true) meshRenderer.enabled = true;
        }
        finally { GameObjectManager.SetGameObjectActive(gameObject, modelSettings.renderingEnabled); }
    }
}

// Holds all Colliders with the given settings for a specific Model/SubModel
// Each collider will have it's own PhysicMaterial
[Serializable]
public sealed class ColliderCollection : UniqueIDProvider<ColliderSurfaceSettings>, IRecyclable<ColliderCollection, ColliderSurfaceSettings, (ModelSettings, GeneratedComponentGroup)>, IUpdatable<GeneratedColliderCollection, (ModelSettings, GeneratedModelHierarchy)>
{
    ColliderSurfaceSettings UniqueIDProvider<ColliderSurfaceSettings>.UniqueID => settings;

    ColliderCollection(ColliderSurfaceSettings settings, GameObject gameObject)
    {
        this.settings   = settings;
        this.gameObject = gameObject;
    }

    public uint UniqueID { get { return settings.GetHash(); } }

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

    void IHideShow.Hide()
    {
        foreach (var mesh in meshes.Values)
        {
            if (mesh) mesh.SafeDestroy();
        }
        foreach (var collider in meshColliders.Values)
        {
            if (collider) collider.SafeDestroy();
        }
        meshColliders.Clear();
        // TODO: make this work properly
        throw new NotImplementedException();
    }

    void IHideShow.Show()
    {
        // TODO: make this work properly
        throw new NotImplementedException();
    }

    public bool CanDispose() { return (gameObject == null) || gameObject.CanDestroy(); }

    internal static ColliderCollection Create(ColliderSurfaceSettings colliderSettings, (ModelSettings, GeneratedComponentGroup) context)
    {
        var (modelSettings, generatedComponentGroup) = context;
        var name = ChiselObjectNames.GetName(generatedComponentGroup, colliderSettings);
        var gameObject = new GameObject(name);
        GameObjectManager.SetGameObjectActive(gameObject, false); // Set gameObject temporarily to false, to defer events until the end 
        try
        {
            GameObjectManager.SetNotEditable(gameObject.transform); // Set transform to be not editable by the user

            // The layer in these unique surface settings never changes, so it's only set once
            gameObject.layer = colliderSettings.layer;
        }
        finally { GameObjectManager.SetGameObjectActive(gameObject, modelSettings.collidersEnabled); }
        var colliderCollection = new ColliderCollection(settings: colliderSettings, gameObject: gameObject);
        return colliderCollection;
    }

    public ColliderCollection Recycle(ColliderSurfaceSettings colliderSettings, (ModelSettings, GeneratedComponentGroup) context)
    {
        // TODO: not implemented
        throw new NotImplementedException();
    }
    
    // Updates the required colliders of a given ColliderSurfaceGroup to match the required physicMaterials, and sets/updates their settings which are set in its ModelSettings
    public void Update(GeneratedColliderCollection generatedColliderCollection, (ModelSettings, GeneratedModelHierarchy) context)
    {
        var (modelSettings, generatedModelHierarchy) = context;
        GameObjectManager.SetGameObjectActive(gameObject, false);// Set gameObject temporarily to false, to defer events until the end 
        try
        {
            GameObjectManager.UpdateGameObjectModelSettings(generatedModelHierarchy, gameObject);

            var surfaceGroup = GeneratedSurfaceManager.GetColliderSurfaceGroupWithSettings(settings);
            if (!surfaceGroup.physicMaterialInstanceIDs.IsCreated || 
                surfaceGroup.physicMaterialInstanceIDs.Length == 0)
            {
                // Remove all meshes and colliders that are not being used
                meshes.Clear();
                meshColliders.Clear();
                return;
            }

            // Reuse existing colliders when a physicMaterial in surfaceGroup already exists, add new ones when they don't & 
            // remove colliders for physicMaterials that are no longer in use.
            GameObjectManager.UpdatePhysicMaterialColliders(in modelSettings, this, in surfaceGroup);
        }
        finally { GameObjectManager.SetGameObjectActive(gameObject, modelSettings.collidersEnabled); }
    }
}

public static class GameObjectManager
{
    static readonly Dictionary<MeshSource, Mesh> s_UniqueMeshes = new Dictionary<MeshSource, Mesh>();
    static Dictionary<MeshSource, Mesh> GetMeshLookup(List<ChiselCSGModel> models)
    {
        s_UniqueMeshes.Clear();
        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];

            if (!model.changed)
                continue;

            var modelBehaviour = model.GetGameObjectForModel();

            // TODO: When model is generated, initialize this _somewhere_ (not here), update otherwise
            if (modelBehaviour.generatedModelHierarchy == null)
            {
                modelBehaviour.settings = ModelSettings.Default;
                modelBehaviour.generatedModelHierarchy = GeneratedModelHierarchy.Create(modelBehaviour.gameObject);
            }
            ref var modelSettings           = ref modelBehaviour.settings;
            ref var generatedModelHierarchy = ref modelBehaviour.generatedModelHierarchy;
            var generatedModelMeshes = model.GetGeneratedModelMeshes();

            try
            {
                // Generate our meshes based on our model settings and used surface settings.
                // This depends on the submodels inside the model, and the surfaces of the brushes inside the model or submodels
                generatedModelMeshes.Update(in model);

                // Generate our components based on what kinds of meshes we need to generate (Colliders and MeshRenderers)
                // This depends on the submodels inside the model, and the surfaces of the brushes inside the model or submodels

                // TODO: make this all undoable so any registered "undo" for any brush modification will include these changes too
                //          might make undos use a lot of memory? but efficiently redoing the work might be more problematic ...?

                modelBehaviour.UpdateMeshes(generatedModelMeshes);

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
    internal static void UpdatePhysicMaterialColliders(in ModelSettings modelSettings, ColliderCollection colliderCollection, in ColliderSurfaceGroup surfaceGroup)
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

    // Updates the model settings for this gameObject and meshRenderer, used when they're modified on the model
    internal static void UpdateRenderModelSettings(in ModelSettings modelSettings, GeneratedModelHierarchy generatedModelHierarchy, RenderComponents components)
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
    internal static bool HaveMaterialsChanged(in RenderSurfaceGroup renderSurfaceGroup, MeshRenderer meshRenderer)
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

    internal static void UpdateMaterials(in RenderSurfaceGroup renderSurfaceGroup, MeshRenderer meshRenderer)
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

    internal static void SetParent(Transform gameObjectTransform, Transform parentTransform)
    {
        if (gameObjectTransform.parent != parentTransform)
            gameObjectTransform.SetParent(parentTransform, worldPositionStays: false);

        SetNotEditable(gameObjectTransform);

        if (gameObjectTransform.localPosition != Vector3.zero       ) gameObjectTransform.localPosition  = Vector3.zero;
        if (gameObjectTransform.localRotation != Quaternion.identity) gameObjectTransform.localRotation  = Quaternion.identity;
        if (gameObjectTransform.localScale    != Vector3.one        ) gameObjectTransform.localScale     = Vector3.one;
    }
     
    internal static void UpdateGameObjectModelSettings(GeneratedModelHierarchy generatedModelHierarchy, GameObject gameObject)
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

    internal static void SetGameObjectActive(GameObject gameObject, bool active)
    {
        // Check if anything has changed first because setting values might trigger events somewhere
        if (gameObject.activeSelf != active)
            gameObject.SetActive(active);
    }

    internal static void SetNotEditable(UnityEngine.Object obj)
    {
        var prevHideFlags = obj.hideFlags;
        if ((prevHideFlags & HideFlags.NotEditable) != HideFlags.NotEditable)
            obj.hideFlags = prevHideFlags | HideFlags.NotEditable;
    }
    #endregion
}