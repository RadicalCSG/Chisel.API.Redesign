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


// TODO: rename
public interface IHideShow
{
    void Hide();
    void Show();
}

public interface ICanDispose
{
    bool CanDispose();
}

public interface IRecyclable<T, K, C> : IDisposable, IHideShow, ICanDispose
{
    T Recycle(K key, C context);
}

public interface IUpdatable<K, C>
{
    void Update(K key, C context);
}

public interface IRecyclingContainer<T, K, C> : IDisposable, IHideShow, ICanDispose
    where T : IRecyclable<T, K, C>
    where C : struct
{
    List<T> Trash { get; }
}

static class RecyclableExtensions
{
    public delegate T CreateWithContext<T, K, C>(K key, C context);

    public static T CreateOrRecycle<T, K, C>(this IRecyclingContainer<T, K, C> @this, CreateWithContext<T, K, C> Create, K key, C context) where T : class, IRecyclable<T, K, C> where C : struct
    {
        var trash = @this.Trash;
        // If we don't have anything to recycle, just create a new one
        if (trash.Count == 0)
            return Create(key, context);

        // Otherwise get the last one from the trash, and recycle it instead
        var lastIndex = trash.Count - 1;
        var obj = trash[lastIndex];
        trash.RemoveAt(lastIndex);
        return obj.Recycle(key, context);
    }

    public static bool RemoveOrThrowAway<T, K, C>(this IRecyclingContainer<T, K, C> @this, T obj) where T : class, IRecyclable<T, K, C> where C : struct
    {
        var trash = @this.Trash;

        // Should not happen
        if (trash.Contains(obj))
        {
            UnityEngine.Debug.LogError("Trying to remove item that's already removed");
            return false;
        }

        // Try to destroy our object
        if (obj.CanDispose())
        {
            obj.Dispose();

            // While we're at it, try to clear all other trashObjects 
            for (int t = trash.Count - 1; t >= 0; t--)
            {
                if (trash[t].CanDispose())
                {
                    trash[t].Dispose();
                    trash.RemoveAt(t);
                }
            }
            return true;
        }

        // Sometimes we're not allowed to destroy GameObjects.
        // This could be because it's part of a prefab instance that is not currently being edited.
        // So instead of deleting this gameObject, we keep it around but disable it.
        // At the same time we keep track of it so we can recycle it when we can
        obj.Hide();
        @this.Trash.Add(obj);
        return false;
    }
}


// TODO: Put gameobjects directly in here?

[Serializable]
public class RecyclableCollection<T, K, C> : IRecyclingContainer<T, K, C>
    where T : IRecyclable<T, K, C>
    where C : struct
{
    [SerializeField] List<T> components = new List<T>();
    public List<T> Components { get { return components; } }

    public bool CanDispose()
    {
        // TODO: should make sure that all children of container gameobject are, in fact, children, or this will fail
        for (int i = 0; i < components.Count; i++)
        {
            if (!components[i].CanDispose())
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        for (int i = 0; i < components.Count; i++)
            components[i].Dispose();
        components.Clear();
    }

    void IHideShow.Hide()
    {
        // Clear all references to unused assets, disable all GameObjects
        for (int i = 0; i < components.Count; i++)
            components[i].Hide();
    }

    void IHideShow.Show()
    {
        // Clear all references to unused assets, disable all GameObjects
        for (int i = 0; i < components.Count; i++)
            components[i].Hide();
    }

    List<T> IRecyclingContainer<T, K, C>.Trash => trash;
    [SerializeField] List<T> trash = new List<T>();
}

// TODO: document
static class ObjectLifetime<E, N, K, CreateContext, UpdateContext>
    where E : class, UniqueIDProvider<K>, IRecyclable<E, K, CreateContext>, IUpdatable<N, UpdateContext>
    where N : class, UniqueIDProvider<K>
    where CreateContext : struct
{
    static readonly Dictionary<K, E> s_FoundExistingItemList = new Dictionary<K, E>();
    static readonly Dictionary<K, E> s_RemoveExistingItemList = new Dictionary<K, E>();
    static readonly List<K> s_AddNewList = new List<K>();

    public delegate E       CreateWithContext <Context>(K uniqueID, Context context);
    public delegate void    DestroyWithContext<Context>(E existingItem, Context context);
    public delegate void    UpdateWithContext <Context>(E existingItem, N newItem, Context context);


    public static void Manage(
                            RecyclableCollection<E, K, CreateContext> existingItems, List<N> currentStateItems,
                            RecyclableExtensions.CreateWithContext<E, K, CreateContext> create, CreateContext createContext,
                            UpdateContext  updateContext)
    {
        // Find all existing items and make a lookup table based on their hashCodes
        s_RemoveExistingItemList.Clear();
        s_FoundExistingItemList.Clear();
        for (int g = 0; g < existingItems.Components.Count; g++)
        {
            var item = existingItems.Components[g];
            var uniqueID = item.UniqueID;
            s_RemoveExistingItemList.Add(uniqueID, item);
            s_FoundExistingItemList.Add(uniqueID, item);
        }

        // Find all added and removed items (if any)
        s_AddNewList.Clear();
        for (int m = currentStateItems.Count - 1; m >= 0; m--)
        {
            var currentStateItem = currentStateItems[m];
            var uniqueID = currentStateItem.UniqueID;

            // Register it as needing to be added when not found
            if (!s_FoundExistingItemList.ContainsKey(uniqueID))
                s_AddNewList.Add(uniqueID);

            // Remove it from the remove list
            s_RemoveExistingItemList.Remove(uniqueID);
        }

        // Go through all removed items
        foreach (var pair in s_RemoveExistingItemList)
        {
            var (key, item) = (pair.Key, pair.Value);
            s_FoundExistingItemList.Remove(key);

            existingItems.Components.Remove(item);

            existingItems.RemoveOrThrowAway(item);
        }

        // If we have any items left to create (and none to remove), we just go through each and create it
        if (s_AddNewList.Count > 0)
        {
            for (int a = 0; a < s_AddNewList.Count; a++)
            {
                var uniqueID = s_AddNewList[a];
                var createdItem = existingItems.CreateOrRecycle(create, uniqueID, createContext);
                existingItems.Components.Add(createdItem);
                s_FoundExistingItemList[uniqueID] = createdItem;
            }
        }

        // Now go through the existing GeneratedComponentGroups for each MeshContainer and update its components
        for (int m = 0; m < currentStateItems.Count; m++)
        {
            var currentStateItem = currentStateItems[m];
            var uniqueID = currentStateItem.UniqueID;
            var existingItem = s_FoundExistingItemList[uniqueID];

            // Update the existing item with the new data
            existingItem.Update((N)currentStateItem, updateContext);
        }

        // Ensure we don't have any dangling resources left
        s_FoundExistingItemList.Clear();
        s_AddNewList.Clear();
        s_RemoveExistingItemList.Clear();
    }
}
