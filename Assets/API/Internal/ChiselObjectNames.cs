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
    public static string GetName(GeneratedComponentGroup generatedComponentGroup, in RenderSurfaceSettings renderSettings)
    {
        if (renderSettings.debugSurfaceType != DebugSurfaceType.None)
            return $"[Debug|{renderSettings.debugSurfaceType}]";

        string receiveShadowsName       = string.Empty;
        string layerName                = renderSettings.layer == 0 ? string.Empty : $"|layer:{renderSettings.layer}";
        string renderingLayerMaskName   = renderSettings.renderingLayerMask == 0 ? string.Empty : $"|mask:{renderSettings.renderingLayerMask}";
        string shadowCastingModeName;
        switch (renderSettings.shadowCastingMode)
        {
            case ShadowCastingMode.Off:         shadowCastingModeName = "|NoCastShadows";        break; // No shadows are cast from this object.
            case ShadowCastingMode.On:          shadowCastingModeName = "|CastShadows]";         break; // Shadows are cast from this object.
            case ShadowCastingMode.TwoSided:    shadowCastingModeName = "|TwoSidedCastShadows]"; break; // Shadows are cast from this object, treating it as two-sided.
            case ShadowCastingMode.ShadowsOnly: shadowCastingModeName = "|ShadowsOnly]";         break; // Object casts shadows, but is otherwise invisible in the Scene.
            default: shadowCastingModeName = "|UNKNOWN]"; break;
        }
        if (renderSettings.shadowCastingMode != ShadowCastingMode.ShadowsOnly && renderSettings.receiveShadows)
            receiveShadowsName = "ReceiveShadows";
        return $"[MeshRenderer{shadowCastingModeName}{receiveShadowsName}{layerName}{renderingLayerMaskName}]";
    }

    /// <summary>
    /// Returns a name that represents a given collidable surface (used for MeshCollider GameObject)
    /// </summary>
    public static string GetName(GeneratedComponentGroup generatedComponentGroup, in ColliderSurfaceSettings colliderSettings)
    {
        if (colliderSettings.layer != 0) return $"[MeshColliders]";
        else return $"[MeshColliders|layer:{colliderSettings.layer}]";
    }

    /// <summary>
    /// Returns a name that represents a collider based on the collider name, and a PhysicMaterial (used for collider meshes)
    /// </summary>
    public static string GetName(string gameObjectName, PhysicMaterial material)
    {
        return $"{gameObjectName}|{material.name}";
    }
}