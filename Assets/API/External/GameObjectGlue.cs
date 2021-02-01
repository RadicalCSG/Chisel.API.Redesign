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
//  * Q: We *might* need to put the GameObjects of the SubModels underneath the model, 
//      to avoid floating point inaccuracies in case SubModels have an offset compared to the model
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


// Holds a MeshRenderer with the given settings for a specific Model/SubModel 
public class RenderSurfaceGroupComponents
{
    public RenderSurfaceSettings settings;
    public GameObject            gameObject;
    public MeshFilter            meshFilter;
    public MeshRenderer          meshRenderer;
    public Mesh                  mesh;
}

// Holds all Colliders with the given settings for a specific Model/SubModel
// Each collider will have it's own PhysicMaterial
public class ColliderCollection
{
    public ColliderSurfaceSettings  settings;
    public GameObject               gameObject;
    public readonly Dictionary<PhysicMaterial, MeshCollider> meshColliders = new Dictionary<PhysicMaterial, MeshCollider>();
    public readonly List<Mesh> meshes = new List<Mesh>();
}

// TODO: put this inside model >component<
// TODO: need a way to get the modelsettings of a model
// TODO: need a way to find all RenderSurfaceSettings/ColliderSurfaceSettings in a model
// TODO: need a way to find all >modified< RenderSurfaceGroups/ColliderSurfaceGroups
// Holds ALL generated gameobjects/meshrenderers etc.
// Also holds the model specific settings, and the GameObject (& its cached transform) that's a wrapper around the generated GameObjects
public class ModelState
{
    public ModelSettings    settings;               // Needs to be serialized with the Model

    public GameObject       containerGameObject;    // Make readonly?
    public Transform        containerTransform;     // Make readonly?

    public readonly List<RenderSurfaceGroupComponents>   renderSurfaceGroupComponents    = new List<RenderSurfaceGroupComponents>();
    public readonly List<ColliderCollection>             colliderCollections             = new List<ColliderCollection>();
}

public class GameObjectManager
{
    const string kGeneratedGameObjectContainerName = "‹[generated]›";

    public static ModelState CreateModelState(GameObject modelGameObject, ModelSettings settings)
    {
        var activeState = modelGameObject.activeSelf;
        SetGameObjectActive(modelGameObject, false);
        try
        {
            var modelTransform = modelGameObject.transform;
            var containerGameObject = new GameObject(kGeneratedGameObjectContainerName);
            var containerTransform = containerGameObject.transform;
            SetParent(containerTransform, modelTransform);
            containerTransform.SetParent(modelTransform, false);
            containerTransform.hideFlags = HideFlags.NotEditable;

            // TODO: find all render settings / collider settings from model *somehow*
            // TODO: add update method to find _modified_ stuff

            return new ModelState
            {
                settings            = settings,
                containerGameObject = containerGameObject,
                containerTransform  = containerTransform,
            };
        }
        finally
        {
            SetGameObjectActive(modelGameObject, activeState);
        }
    }

    // Creates a unique GameObject, MeshRenderer and MeshFilter for the given RenderSurfaceGroup
    public static RenderSurfaceGroupComponents GenerateSurfaceGroup(ModelState modelState, RenderSurfaceSettings settings)
    {
        var name = ChiselObjectNames.GetName(settings);
        var gameObject = new GameObject(name);
        SetGameObjectActive(gameObject, false);
        try
        {
            // The layer in these unique surface settings never changes, so it's only set once
            gameObject.layer = settings.layer;

            var components = CreateRendererComponent(gameObject, name, settings);

            // Update the model settings for this meshRenderer (these CAN change)
            UpdateRenderModelSettings(modelState, components);

            return components;
        }
        finally { SetGameObjectActive(gameObject, modelState.settings.renderingEnabled); }
    }

    // Updates the materials for a given MeshRenderer belonging to a surfaceGroup
    public static void UpdateRenderSurfaceGroup(ModelState modelState, RenderSurfaceGroupComponents components)
    {
        SetGameObjectActive(components.gameObject, false);
        try
        {
            UpdateRenderModelSettings(modelState, components);

            var surfaceGroup = GeneratedSurfaceManager.GetRenderSurfaceGroupWithSettings(components.settings);
            if (!surfaceGroup.materialInstanceIDs.IsCreated || surfaceGroup.materialInstanceIDs.Length == 0 ||
                components.meshFilter.sharedMesh == null || components.meshFilter.sharedMesh.vertexCount == 0)
            {
                if (components.meshRenderer.sharedMaterial != null ) components.meshRenderer.sharedMaterial = null;
                if (components.meshRenderer.enabled        != false) components.meshRenderer.enabled = false;
                return;
            }

            if (HaveMaterialsChanged(components.meshRenderer, in surfaceGroup))
                UpdateMaterials(components.meshRenderer, in surfaceGroup);

            if (components.meshRenderer.enabled != true) components.meshRenderer.enabled = true;
        }
        finally { SetGameObjectActive(components.gameObject, modelState.settings.renderingEnabled); }
    }

    public static void CollectMeshes(RenderSurfaceGroupComponents components, List<Mesh> meshes)
    {
        meshes.Add(components.mesh);
    }

    // Creates a unique GameObject and a List of MeshColliders for the given ColliderSurfaceGroup
    public static ColliderCollection CreateColliderCollection(ModelState modelState, ColliderSurfaceSettings settings)
    {
        var name                = ChiselObjectNames.GetName(settings);
        var colliderCollection  = new ColliderCollection { settings = settings, gameObject = new GameObject(name) };
        SetGameObjectActive(colliderCollection.gameObject, false);
        try
        {
            // The layer in these unique surface settings never changes, so it's only set once
            colliderCollection.gameObject.layer = settings.layer;

            UpdateColliderCollection(modelState, colliderCollection);
        }
        finally { SetGameObjectActive(colliderCollection.gameObject, modelState.settings.collidersEnabled); }
        return colliderCollection;
    }

    // Updates the required colliders of a given ColliderSurfaceGroup to match the required physicMaterials, and sets/updates their settings which are set in its ModelSettings
    public static void UpdateColliderCollection(ModelState modelState, ColliderCollection colliderCollection)
    {
        SetGameObjectActive(colliderCollection.gameObject, false);
        try
        {
            UpdateGameObjectModelSettings(modelState, colliderCollection.gameObject);

            var surfaceGroup = GeneratedSurfaceManager.GetColliderSurfaceGroupWithSettings(colliderCollection.settings);
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
            UpdatePhysicMaterialColliders(modelState, colliderCollection, in surfaceGroup);
        }
        finally { SetGameObjectActive(colliderCollection.gameObject, modelState.settings.collidersEnabled); }
    }

    public static void CollectMeshes(ColliderCollection colliderCollection, List<Mesh> meshes)
    {
        meshes.AddRange(colliderCollection.meshes);
    }

    #region Private methods
    static void AddColliderComponent(PhysicMaterial physicMaterial, ColliderCollection colliderCollection)
    {
        var gameObject = colliderCollection.gameObject;

        var meshCollider = gameObject.AddComponent<MeshCollider>();

        // The mesh for these unique colliders never changes, so it's set once
        var mesh = new Mesh { name = ChiselObjectNames.GetName(gameObject.name, physicMaterial) };
        meshCollider.sharedMesh = mesh;

        // The collider settings never change, so are set once
        meshCollider.sharedMaterial = physicMaterial;

        colliderCollection.meshes.Add(mesh);
        colliderCollection.meshColliders[physicMaterial] = meshCollider;
    }

    static void UpdateColliderModelSettings(ModelState modelState, MeshCollider meshCollider)
    {
        ref var settings = ref modelState.settings;
        if (meshCollider.cookingOptions != settings.cookingOptions) meshCollider.cookingOptions = settings.cookingOptions;
        if (meshCollider.convex         != settings.convex        ) meshCollider.convex         = settings.convex;
        if (meshCollider.isTrigger      != settings.isTrigger     ) meshCollider.isTrigger      = settings.isTrigger;
        if (meshCollider.contactOffset  != settings.contactOffset ) meshCollider.contactOffset  = settings.contactOffset;

        var hasVertices = meshCollider.sharedMesh.vertexCount > 0;
        if (meshCollider.enabled        != hasVertices) meshCollider.enabled = hasVertices;
    }

    static readonly HashSet<PhysicMaterial> s_RemovedPhysicMaterials = new HashSet<PhysicMaterial>();

    static void UpdatePhysicMaterialColliders(ModelState modelState, ColliderCollection colliderCollection, in ColliderSurfaceGroup surfaceGroup)
    {
        if (colliderCollection.meshes.Capacity < surfaceGroup.physicMaterialInstanceIDs.Length)
            colliderCollection.meshes.Capacity = surfaceGroup.physicMaterialInstanceIDs.Length;

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
            UpdateColliderModelSettings(modelState, meshCollider);
        }

        if (s_RemovedPhysicMaterials.Count > 0)
        {
            // Remove all meshes and colliders that are no longer being used
            foreach (var key in s_RemovedPhysicMaterials)
            {
                colliderCollection.meshes.Remove(colliderCollection.meshColliders[key].sharedMesh);
                colliderCollection.meshColliders.Remove(key);
            }
            s_RemovedPhysicMaterials.Clear(); // prevent dangling references
        }
    }

    static RenderSurfaceGroupComponents CreateRendererComponent(GameObject gameObject, string gameObjectName, RenderSurfaceSettings settings)
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

        return new RenderSurfaceGroupComponents { settings = settings, gameObject = gameObject, meshFilter = meshFilter, meshRenderer = meshRenderer, mesh = mesh };
    }

    // Updates the model settings for this gameObject and meshRenderer, used when they're modified on the model
    static void UpdateRenderModelSettings(ModelState modelState, RenderSurfaceGroupComponents components)
    {
        ref var settings = ref modelState.settings;
        UpdateGameObjectModelSettings(modelState, components.gameObject);

        var meshRenderer = components.meshRenderer;

        if (meshRenderer.lightProbeProxyVolumeOverride != settings.lightProbeProxyVolumeOverride) meshRenderer.lightProbeProxyVolumeOverride  = settings.lightProbeProxyVolumeOverride;
        if (meshRenderer.probeAnchor                   != settings.probeAnchor                  ) meshRenderer.probeAnchor                    = settings.probeAnchor;
        if (meshRenderer.motionVectorGenerationMode    != settings.motionVectorGenerationMode   ) meshRenderer.motionVectorGenerationMode     = settings.motionVectorGenerationMode;
        if (meshRenderer.reflectionProbeUsage          != settings.reflectionProbeUsage         ) meshRenderer.reflectionProbeUsage           = settings.reflectionProbeUsage;
        if (meshRenderer.lightProbeUsage               != settings.lightProbeUsage              ) meshRenderer.lightProbeUsage                = settings.lightProbeUsage;
        if (meshRenderer.rayTracingMode                != settings.rayTracingMode               ) meshRenderer.rayTracingMode                 = settings.rayTracingMode;
        if (meshRenderer.allowOcclusionWhenDynamic     != settings.allowOcclusionWhenDynamic    ) meshRenderer.allowOcclusionWhenDynamic      = settings.allowOcclusionWhenDynamic;
        if (meshRenderer.rendererPriority              != settings.rendererPriority             ) meshRenderer.rendererPriority               = settings.rendererPriority;
        if (meshRenderer.lightmapScaleOffset           != settings.lightmapScaleOffset          ) meshRenderer.lightmapScaleOffset            = settings.lightmapScaleOffset;
        if (meshRenderer.realtimeLightmapScaleOffset   != settings.realtimeLightmapScaleOffset  ) meshRenderer.realtimeLightmapScaleOffset    = settings.realtimeLightmapScaleOffset;
#if UNITY_EDITOR
        if (meshRenderer.receiveGI                     != settings.receiveGI                    ) meshRenderer.receiveGI                      = settings.receiveGI;
        if (meshRenderer.stitchLightmapSeams           != settings.stitchLightmapSeams          ) meshRenderer.stitchLightmapSeams            = settings.stitchLightmapSeams;
#endif
    }
    
    static readonly HashSet<int>    s_LookupMaterials = new HashSet<int>(); 
    static readonly List<Material>  s_SharedMaterials = new List<Material>();

    static bool HaveMaterialsChanged(MeshRenderer meshRenderer, in RenderSurfaceGroup renderSurfaceGroup)
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
    static void UpdateMaterials(MeshRenderer meshRenderer, in RenderSurfaceGroup renderSurfaceGroup)
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
        if (gameObjectTransform.localPosition != Vector3.zero       ) gameObjectTransform.localPosition  = Vector3.zero;
        if (gameObjectTransform.localRotation != Quaternion.identity) gameObjectTransform.localRotation  = Quaternion.identity;
        if (gameObjectTransform.localScale    != Vector3.one        ) gameObjectTransform.localScale     = Vector3.one;
    }
     
    static void UpdateGameObjectModelSettings(ModelState modelState, GameObject gameObject)
    {
        // Check if anything has changed first because setting values might trigger events somewhere
        var transform = gameObject.transform;
        SetParent(transform, modelState.containerTransform);
        if (transform.hideFlags != HideFlags.NotEditable) transform.hideFlags = HideFlags.NotEditable;

#if UNITY_EDITOR
        var requiredEditorFlags = UnityEditor.GameObjectUtility.GetStaticEditorFlags(modelState.containerGameObject);
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
    #endregion
}