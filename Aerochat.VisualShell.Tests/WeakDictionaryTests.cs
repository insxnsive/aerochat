using System.Collections;
using System.Reflection;
using Aerochat.Helpers;

namespace Aerochat.VisualShell.Tests;

public sealed class WeakDictionaryTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static Delegate Callback(WeakDictionary<string, object> dictionary, object value)
    {
        object table = dictionary.GetType().GetField("_conditionalWeakTable", Private)!.GetValue(dictionary)!;
        object?[] args = [value, null];
        Assert.That(table.GetType().GetMethod("TryGetValue")!.Invoke(table, args), Is.True);
        return (Delegate)args[1]!.GetType().GetField("ValueFinalized", Private)!.GetValue(args[1])!;
    }
    private static void Fire(Delegate callback, string key)
    {
        // Invoke the saved callback directly, without waiting for GC or finalizer scheduling.
        callback.DynamicInvoke(callback.Method.GetParameters().Length == 0 ? [] : [key]);
    }
    private static void ClearTarget(WeakDictionary<string, object> dictionary, string key)
    {
        var entries = (IDictionary)dictionary.GetType().GetField("_internalDictionary", Private)!.GetValue(dictionary)!;
        ((WeakReference)entries[key]!).Target = null;
    }

    [Test]
    public void Retired_value_callback_does_not_remove_replacement()
    {
        var dictionary = new WeakDictionary<string, object>();
        object old = new(), replacement = new();
        dictionary.Add("key", old);
        Delegate callback = Callback(dictionary, old);
        dictionary["key"] = replacement;
        Fire(callback, "key");
        Assert.That(dictionary.TryGetValue("key", out var actual), Is.True);
        Assert.That(actual, Is.SameAs(replacement));
        GC.KeepAlive(old); GC.KeepAlive(replacement);
    }
    [Test]
    public void Collected_target_is_a_lookup_miss()
    {
        var dictionary = new WeakDictionary<string, object>(); object value = new();
        dictionary.Add("key", value); ClearTarget(dictionary, "key");
        Assert.That(dictionary.TryGetValue("key", out var actual), Is.False);
        Assert.That(actual, Is.Null); GC.KeepAlive(value);
    }
    [Test]
    public void Collected_target_is_not_an_equal_item()
    {
        var dictionary = new WeakDictionary<string, object>(); object value = new();
        dictionary.Add("key", value); ClearTarget(dictionary, "key");
        var item = new KeyValuePair<string, object>("key", value);
        Assert.That(dictionary.Contains(item), Is.False);
        Assert.That(dictionary.Remove(item), Is.False); GC.KeepAlive(value);
    }
    [Test]
    public void Keys_and_enumerator_are_snapshots()
    {
        var dictionary = new WeakDictionary<string, object>(); object value = new();
        dictionary.Add("key", value);
        var keys = dictionary.Keys;
        using var enumerator = dictionary.GetEnumerator();
        dictionary.Clear();
        Assert.That(keys, Is.EqualTo(new[] { "key" }));
        Assert.That(enumerator.MoveNext(), Is.True);
        Assert.That(enumerator.Current.Value, Is.SameAs(value));
        Assert.That(enumerator.MoveNext(), Is.False); GC.KeepAlive(value);
    }
    [Test]
    public void Same_value_can_be_registered_under_two_keys()
    {
        var dictionary = new WeakDictionary<string, object>(); object value = new();
        dictionary.Add("first", value); dictionary.Add("second", value);
        Fire(Callback(dictionary, value), "first");
        Assert.That(dictionary.Count, Is.Zero); GC.KeepAlive(value);
    }
    [Test]
    public async Task Concurrent_cache_operations_and_retired_callbacks_remain_usable()
    {
        var dictionary = new WeakDictionary<string, object>();
        using var start = new Barrier(4);
        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(10));
            for (int i = 0; i < 1000; i++)
            {
                string key = worker.ToString(); object value = new();
                dictionary[key] = value;
                dictionary.TryGetValue(key, out _);
                _ = dictionary.Keys; _ = dictionary.Values; _ = dictionary.Count;
                foreach (var item in dictionary) _ = item.Key;
                dictionary.Remove(key); GC.KeepAlive(value);
            }
        })).ToArray();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));
        object sentinel = new(); Assert.That(dictionary.TryAdd("sentinel", sentinel), Is.True);
        Assert.That(dictionary.TryGetValue("sentinel", out var actual), Is.True);
        Assert.That(actual, Is.SameAs(sentinel)); GC.KeepAlive(sentinel);
    }
}
