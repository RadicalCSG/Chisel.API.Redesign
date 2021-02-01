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

public enum Intersection
{
    SelfInsideOther,
    OtherInsideSelf,
    Intersection
}

public struct ChiselIntersectionManager
{
    // keeps track of (mesh+transformation) intersections using hashed grid. hash for each brush, representing all its relative intersections

    // TODO: Update transformations

    public void UpdateIntersections([ReadOnly] in CompactTree packedHierarchy) // also use transformations
    {
        for (uint i = 0; i < packedHierarchy.brushes.Length; i++)
        {

        }
        throw new NotImplementedException();
    }
}

