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

public class RenderSurfaceGroupComponents
{
    public RenderSurfaceSettings settings;
    public GameObject            gameObject;
    public MeshFilter            meshFilter;
    public MeshRenderer          meshRenderer;
    public Mesh                  mesh;
}

public class ColliderCollection
{
    public ColliderSurfaceSettings  settings;
    public GameObject               gameObject;
    public readonly List<Mesh>      meshes = new List<Mesh>();
    public readonly Dictionary<PhysicMaterial, MeshCollider> meshColliders = new Dictionary<PhysicMaterial, MeshCollider>();
}

// TODO: put this inside model >component<
public class ModelState
{
    public GameObject       containerGameObject;
    public Transform        containerTransform;
    public ModelSettings    settings;

    public readonly List<RenderSurfaceGroupComponents>   renderSurfaceGroupComponents    = new List<RenderSurfaceGroupComponents>();
    public readonly List<ColliderCollection>             colliderCollections             = new List<ColliderCollection>();
}

// TODO: need place to hold ALL generated gameobjects/meshrenderers etc.
// TODO: need a way to get the modelsettings of a model
// TODO: need a way to find all RenderSurfaceSettings/ColliderSurfaceSettings in a model
// TODO: need a way to find all >modified< RenderSurfaceGroups/ColliderSurfaceGroups
public class GameObjectManager
{
    public static ModelState CreateModelState(GameObject modelGameObject, ModelSettings settings)
    {
        var activeState = modelGameObject.activeSelf;
        SetGameObjectActive(modelGameObject, false);
        try
        {
            var modelTransform = modelGameObject.transform;
            var containerGameObject = new GameObject("<GENERATED>"); // TODO: better name
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