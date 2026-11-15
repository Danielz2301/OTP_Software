using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using OTPSoftware.Core.Caching;

namespace OTPSoftware.Benchmarks;

/// <summary>
/// Verifies zero steady-state node allocations during continuous eviction churn in
/// <see cref="LruMemoryCache{TKey, TValue}"/> compared to a naive allocating <see cref="LinkedList{T}"/> cache.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class LruCacheBenchmarks
{
    private const int CacheCapacity = 24;
    private const int Operations = 20_000;

    private string[] _values = null!;
    private LruMemoryCache<int, string> _reusingCache = null!;
    private LruMemoryCache<int, string> _hotReadCache = null!;

    [GlobalSetup]
    public void Setup()
    {
        _values = new string[64];
        for (int i = 0; i < _values.Length; i++)
        {
            _values[i] = $"Panel_{i:D3}";
        }

        _reusingCache = new LruMemoryCache<int, string>(CacheCapacity);
        _hotReadCache = new LruMemoryCache<int, string>(CacheCapacity);
        for (int k = 0; k < CacheCapacity; k++)
        {
            _reusingCache.Set(-(k + 1), _values[k]);
            _hotReadCache.Set(k, _values[k]);
        }
    }

    [Benchmark(Baseline = true)]
    public int NaiveLinkedListAllocatingCache_EvictionChurn()
    {
        var map = new Dictionary<int, LinkedListNode<(int Key, string Value)>>(CacheCapacity);
        var list = new LinkedList<(int Key, string Value)>();
        int hits = 0;

        for (int i = 0; i < Operations; i++)
        {
            string val = _values[i & 63];
            if (map.TryGetValue(i, out var existing))
            {
                list.Remove(existing);
                list.AddFirst(existing);
                existing.Value = (i, val);
                hits++;
                continue;
            }

            if (map.Count >= CacheCapacity && list.Last is { } tail)
            {
                map.Remove(tail.Value.Key);
                list.RemoveLast();
            }

            var newNode = new LinkedListNode<(int, string)>((i, val));
            list.AddFirst(newNode);
            map[i] = newNode;
        }

        return hits + map.Count;
    }

    [Benchmark]
    public int LruMemoryCache_ZeroAllocEvictionChurn()
    {
        for (int i = 0; i < Operations; i++)
        {
            _reusingCache.Set(i, _values[i & 63]);
        }

        return _reusingCache.Count;
    }

    [Benchmark]
    public int LruMemoryCache_HotReadHits()
    {
        int hits = 0;
        for (int i = 0; i < Operations; i++)
        {
            if (_hotReadCache.TryGetValue(i % CacheCapacity, out _))
            {
                hits++;
            }
        }

        return hits;
    }
}
