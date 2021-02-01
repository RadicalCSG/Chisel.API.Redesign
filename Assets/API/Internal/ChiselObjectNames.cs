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

// Helper class to create consistent names for our generated GameObjects and Meshes
public static class ChiselObjectNames
{
    /// <summary>
    /// Returns a name that represents a given renderable surface (used for MeshRenderer GameObject and its mesh)
    /// </summary>
    public static string GetName(RenderSurfaceSettings settings)
    {
        if (settings.debugSurfaceType != DebugSurfaceType.None)
            return $"[Debug|{settings.debugSurfaceType}]";

        string receiveShadowsName = string.Empty;
        string layerName = settings.layer == 0 ? string.Empty : $"|layer:{settings.layer}";
        string renderingLayerMaskName = settings.renderingLayerMask == 0 ? string.Empty : $"|mask:{settings.renderingLayerMask}";
        string shadowCastingModeName;
        switch (settings.shadowCastingMode)
        {
            case ShadowCastingMode.Off: shadowCastingModeName = "|NoCastShadows"; break; // No shadows are cast from this object.
            case ShadowCastingMode.On: shadowCastingModeName = "|CastShadows]"; break; // Shadows are cast from this object.
            case ShadowCastingMode.TwoSided: shadowCastingModeName = "|TwoSidedCastShadows]"; break; // Shadows are cast from this object, treating it as two-sided.
            case ShadowCastingMode.ShadowsOnly: shadowCastingModeName = "|ShadowsOnly]"; break; // Object casts shadows, but is otherwise invisible in the Scene.
            default: shadowCastingModeName = "|UNKNOWN]"; break;
        }
        if (settings.shadowCastingMode != ShadowCastingMode.ShadowsOnly && settings.receiveShadows)
            receiveShadowsName = "ReceiveShadows";
        return $"[MeshRenderer{shadowCastingModeName}{receiveShadowsName}{layerName}{renderingLayerMaskName}]";
    }

    /// <summary>
    /// Returns a name that represents a given collidable surface (used for MeshCollider GameObject)
    /// </summary>
    public static string GetName(ColliderSurfaceSettings settings)
    {
        if (settings.layer != 0) return $"[MeshColliders]";
        else return $"[MeshColliders|layer:{settings.layer}]";
    }

    /// <summary>
    /// Returns a name that represents a collider based on the collider name, and a PhysicMaterial (used for collider meshes)
    /// </summary>
    public static string GetName(string gameObjectName, PhysicMaterial material)
    {
        return $"{gameObjectName}|{material.name}";
    }
}
