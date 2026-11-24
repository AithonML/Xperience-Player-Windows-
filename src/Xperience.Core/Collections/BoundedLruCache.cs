using System.Diagnostics.CodeAnalysis;

namespace Xperience.Core.Collections;

/// <summary>
/// Thread-safe, fixed-capacity Least Recently Used (LRU) cache backed by a hash table and doubly-linked list.
/// Guarantees O(1) lookup, insertion, update, removal, and bounded memory retention.
/// </summary>
/// <typeparam name="TKey">The cache key type.</typeparam>
/// <typeparam name="TValue">The cached value type.</typeparam>
public sealed class BoundedLruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly struct CacheEntry
    {
        public CacheEntry(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }

        public TKey Key { get; }
        public TValue Value { get; }
    }

    private readonly object _sync = new();
    private readonly int _maxCapacity;
    private readonly IEqualityComparer<TKey>? _comparer;
    private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _map;
    private readonly LinkedList<CacheEntry> _lruList = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BoundedLruCache{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="maxCapacity">The maximum number of entries retained in the cache (must be &gt; 0).</param>
    /// <param name="comparer">Optional key equality comparer.</param>
    public BoundedLruCache(int maxCapacity, IEqualityComparer<TKey>? comparer = null)
    {
        if (maxCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCapacity), maxCapacity, "Maximum capacity must be greater than zero.");
        }

        _maxCapacity = maxCapacity;
        _comparer = comparer;
        _map = new Dictionary<TKey, LinkedListNode<CacheEntry>>(Math.Min(maxCapacity, 64), comparer);
    }

    /// <summary>
    /// Gets the maximum number of entries this cache can retain before evicting the least recently used entry.
    /// </summary>
    public int MaxCapacity => _maxCapacity;

    /// <summary>
    /// Gets the current number of entries stored in the cache.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>
    /// Attempts to retrieve the cached value for <paramref name="key"/> and promotes it to most-recently-used.
    /// </summary>
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (!ReferenceEquals(node, _lruList.First))
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                }

                value = node.Value.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Inserts or updates the value for <paramref name="key"/>, promoting it to most-recently-used and evicting
    /// the least-recently-used entry when capacity is exceeded.
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existingNode))
            {
                existingNode.Value = new CacheEntry(key, value);
                if (!ReferenceEquals(existingNode, _lruList.First))
                {
                    _lruList.Remove(existingNode);
                    _lruList.AddFirst(existingNode);
                }

                return;
            }

            if (_map.Count >= _maxCapacity && _lruList.Last is { } lruNode)
            {
                _map.Remove(lruNode.Value.Key);
                _lruList.RemoveLast();
                // Reuse the evicted LinkedListNode instance to avoid extra allocation
                lruNode.Value = new CacheEntry(key, value);
                _lruList.AddFirst(lruNode);
                _map[key] = lruNode;
                return;
            }

            var newNode = new LinkedListNode<CacheEntry>(new CacheEntry(key, value));
            _lruList.AddFirst(newNode);
            _map[key] = newNode;
        }
    }

    /// <summary>
    /// Removes the entry with the specified <paramref name="key"/> if present.
    /// </summary>
    /// <returns><c>true</c> if the entry was found and removed; otherwise <c>false</c>.</returns>
    public bool Remove(TKey key)
    {
        lock (_sync)
        {
            if (_map.Remove(key, out var node))
            {
                _lruList.Remove(node);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Checks whether the cache currently contains <paramref name="key"/> without updating its recency.
    /// </summary>
    public bool ContainsKey(TKey key)
    {
        lock (_sync)
        {
            return _map.ContainsKey(key);
        }
    }

    /// <summary>
    /// Removes all entries from the cache.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _map.Clear();
            _lruList.Clear();
        }
    }

    /// <summary>
    /// Returns a point-in-time snapshot of all cache entries ordered from most-recently-used to least-recently-used.
    /// </summary>
    public List<KeyValuePair<TKey, TValue>> Snapshot()
    {
        lock (_sync)
        {
            var result = new List<KeyValuePair<TKey, TValue>>(_map.Count);
            for (var node = _lruList.First; node is not null; node = node.Next)
            {
                result.Add(new KeyValuePair<TKey, TValue>(node.Value.Key, node.Value.Value));
            }

            return result;
        }
    }

    /// <summary>
    /// Returns a point-in-time dictionary copy of the cache contents.
    /// </summary>
    public Dictionary<TKey, TValue> ToDictionarySnapshot()
    {
        lock (_sync)
        {
            var dict = new Dictionary<TKey, TValue>(_map.Count, _comparer);
            for (var node = _lruList.First; node is not null; node = node.Next)
            {
                dict[node.Value.Key] = node.Value.Value;
            }

            return dict;
        }
    }
}
