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

public static class UnityObjectExtensions
{
    public static void SafeDestroy(this UnityEngine.Object obj)
    {
        if (!obj)
            return;

        // TODO: handle situation where object is GameObject that's part of a prefab 
        //      (can't delete GameObjects that are part of prefab when not editing that prefab)

#if UNITY_EDITOR
        UnityEngine.Object.DestroyImmediate(obj);
#else
        UnityEngine.Object.Destroy(obj);
#endif
    }

    public static bool CanDestroy(this UnityEngine.Object obj)
    {
        // TODO: if this object is part of a prefab, which is not currently editable, or if the object is somehow locked, return true
        throw new NotImplementedException();
    }
}