namespace OTPSoftware.Core.Caching;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

/// <summary>
/// Thread-safe in-memory Least Recently Used (LRU) cache with fixed maximum capacity.
/// Supports factory floor 24/7 caching for MES details and Camera image paths without heap explosion.
/// </summary>
public sealed class LruMemoryCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Lock _syncLock = new();
    private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _map;
    private readonly LinkedList<CacheItem> _lruList = new();

    private readonly record struct CacheItem(TKey Key, TValue Value);

    public LruMemoryCache(int capacity = 100, IEqualityComparer<TKey>? comparer = null)
    {
        _capacity = Math.Max(1, capacity);
        _map = new Dictionary<TKey, LinkedListNode<CacheItem>>(_capacity, comparer);
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_syncLock)
            {
                return _map.Count;
            }
        }
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (_syncLock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            value = default;
            return false;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_syncLock)
        {
            if (_map.TryGetValue(key, out var existingNode))
            {
                _lruList.Remove(existingNode);
                existingNode.Value = new CacheItem(key, value);
                _lruList.AddFirst(existingNode);
                return;
            }

            if (_map.Count >= _capacity && _lruList.Last is { } reusedNode)
            {
                _lruList.RemoveLast();
                _map.Remove(reusedNode.Value.Key);
                reusedNode.Value = new CacheItem(key, value);
                _lruList.AddFirst(reusedNode);
                _map[key] = reusedNode;
                return;
            }

            var newNode = new LinkedListNode<CacheItem>(new CacheItem(key, value));
            _lruList.AddFirst(newNode);
            _map[key] = newNode;
        }
    }

    public bool Remove(TKey key)
    {
        lock (_syncLock)
        {
            if (_map.Remove(key, out var node))
            {
                _lruList.Remove(node);
                return true;
            }
            return false;
        }
    }

    public void Clear()
    {
        lock (_syncLock)
        {
            _map.Clear();
            _lruList.Clear();
        }
    }
}

