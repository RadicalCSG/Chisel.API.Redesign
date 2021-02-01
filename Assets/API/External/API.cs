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

public enum NodeType : byte { None, Tree, Branch, Brush };

public enum Operation : byte
{
    Additive,
    Subtractive,
    Intersection
}


public interface IChiselHash { uint GetHash(); }

public interface IChiselChild : IChiselHash {}

public interface IChiselContainer : IChiselHash
{
    int ChildCount              { get; }
    ref IChiselChild            GetChildAt(int index);
    ref ChiselTransformation    GetChildTransformAt(int index);
    ref Operation               GetChildOperationAt(int index);
}


public partial struct ChiselTransformation : IChiselHash { }

public partial struct Model : IChiselContainer { }

public partial struct SubModel : IChiselChild, IChiselContainer { }

public partial struct Composite : IChiselChild, IChiselContainer { }

public partial struct Brush : IChiselChild { }
