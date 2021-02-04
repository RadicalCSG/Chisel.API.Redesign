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

public interface IMaterialLookup
{
    Material        GetMaterialByInstanceID(int instanceID);
    PhysicMaterial  GetPhysicMaterialByInstanceID(int instanceID);
}

public static partial class MaterialManager
{
    // TODO: need some way to (un)register all used materials per model/submodel (refcounted)
    //       this will help us figure out how many gameobjects we need to generate
}

#if UNITY_EDITOR
public class EditorMaterialLookup : IMaterialLookup
{
    public Material GetMaterialByInstanceID(int instanceID) { return UnityEditor.EditorUtility.InstanceIDToObject(instanceID) as Material; }
    public PhysicMaterial GetPhysicMaterialByInstanceID(int instanceID) { return UnityEditor.EditorUtility.InstanceIDToObject(instanceID) as PhysicMaterial; }
}

public static partial class MaterialManager
{
    static readonly EditorMaterialLookup materialLookup = new EditorMaterialLookup();
    public static IMaterialLookup Lookup { get { return materialLookup; } }
}
#else
public static partial class MaterialManager
{
    public static IMaterialLookup Lookup { get { throw new NotImplementedException(); } }   
}
#endif