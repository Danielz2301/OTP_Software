namespace OTPSoftware.Tests;

using System.Threading.Tasks;
using OTPSoftware.Core.Caching;
using Xunit;

public sealed class LruMemoryCacheTests
{
    [Fact]
    public void SetAndGet_ReturnsCorrectValue()
    {
        var cache = new LruMemoryCache<string, string>(capacity: 3);
        cache.Set("A", "Apple");
        cache.Set("B", "Banana");

        Assert.True(cache.TryGetValue("A", out var valA));
        Assert.Equal("Apple", valA);

        Assert.True(cache.TryGetValue("B", out var valB));
        Assert.Equal("Banana", valB);

        Assert.False(cache.TryGetValue("C", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ExceedingCapacity_EvictsOldestItem()
    {
        var cache = new LruMemoryCache<string, int>(capacity: 3);
        cache.Set("A", 1);
        cache.Set("B", 2);
        cache.Set("C", 3);

        Assert.Equal(3, cache.Count);

        // Add 4th item -> should evict A (least recently used)
        cache.Set("D", 4);

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGetValue("A", out _));
        Assert.True(cache.TryGetValue("B", out var valB));
        Assert.Equal(2, valB);
        Assert.True(cache.TryGetValue("C", out var valC));
        Assert.Equal(3, valC);
        Assert.True(cache.TryGetValue("D", out var valD));
        Assert.Equal(4, valD);
    }

    [Fact]
    public void AccessingItem_PromotesItToMostRecentlyUsed()
    {
        var cache = new LruMemoryCache<string, int>(capacity: 3);
        cache.Set("A", 1);
        cache.Set("B", 2);
        cache.Set("C", 3);

        // Access "A" -> moves A to most recently used
        Assert.True(cache.TryGetValue("A", out _));

        // Add "D" -> should evict "B" (now the least recently used, since A was accessed)
        cache.Set("D", 4);

        Assert.True(cache.TryGetValue("A", out _));
        Assert.False(cache.TryGetValue("B", out _));
        Assert.True(cache.TryGetValue("C", out _));
        Assert.True(cache.TryGetValue("D", out _));
    }

    [Fact]
    public void UpdatingExistingKey_UpdatesValueAndPromotes()
    {
        var cache = new LruMemoryCache<string, string>(capacity: 2);
        cache.Set("K1", "V1");
        cache.Set("K2", "V2");

        // Update K1
        cache.Set("K1", "V1_Updated");

        // Insert K3 -> should evict K2, not K1
        cache.Set("K3", "V3");

        Assert.True(cache.TryGetValue("K1", out var v1));
        Assert.Equal("V1_Updated", v1);
        Assert.False(cache.TryGetValue("K2", out _));
        Assert.True(cache.TryGetValue("K3", out _));
    }

    [Fact]
    public void RemoveAndClear_WorkCorrectly()
    {
        var cache = new LruMemoryCache<string, string>(capacity: 10);
        cache.Set("X", "1");
        cache.Set("Y", "2");

        Assert.True(cache.Remove("X"));
        Assert.False(cache.Remove("Z"));
        Assert.False(cache.TryGetValue("X", out _));
        Assert.Equal(1, cache.Count);

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGetValue("Y", out _));
    }

    [Fact]
    public void ConcurrentAccess_IsThreadSafe()
    {
        var cache = new LruMemoryCache<int, int>(capacity: 50);

        Parallel.For(0, 500, i =>
        {
            cache.Set(i % 100, i);
            _ = cache.TryGetValue((i * 3) % 100, out _);
        });

        Assert.Equal(50, cache.Count);
    }
}

