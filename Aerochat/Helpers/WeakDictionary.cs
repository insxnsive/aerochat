using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Aerochat.Helpers
{
    /// <summary>Weak values with serialized access and identity-checked finalizer cleanup.</summary>
    public class WeakDictionary<TKey, TValue> : IDictionary<TKey, TValue>
        where TKey : notnull
        where TValue : class
    {
        private readonly object _gate = new();
        private readonly IDictionary<TKey, WeakReference> _internalDictionary = new Dictionary<TKey, WeakReference>();
        private readonly ConditionalWeakTable<TValue, Finalizer> _conditionalWeakTable = new();

        public TValue this[TKey key]
        {
            get { lock (_gate) return (TValue)_internalDictionary[key].Target!; }
            set { lock (_gate) { Remove(key); Add(key, value); } }
        }
        public ICollection<TKey> Keys { get { lock (_gate) return _internalDictionary.Keys.ToArray(); } }
        public ICollection<TValue> Values { get { lock (_gate) return _internalDictionary.Values.Select(r => (TValue)r.Target!).ToArray(); } }
        public int Count { get { lock (_gate) return _internalDictionary.Count; } }
        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value)
        {
            lock (_gate)
            {
                var reference = new WeakReference(value);
                _internalDictionary.Add(key, reference);
                var finalizer = _conditionalWeakTable.GetValue(value, _ => new Finalizer());
                finalizer.ValueFinalized += () => RemoveFinalized(key, reference);
            }
        }
        public bool TryAdd(TKey key, TValue value)
        {
            lock (_gate)
            {
                if (_internalDictionary.ContainsKey(key)) return false;
                Add(key, value);
                return true;
            }
        }
        public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
        public void Clear() { lock (_gate) _internalDictionary.Clear(); }
        public bool ContainsKey(TKey key) { lock (_gate) return _internalDictionary.ContainsKey(key); }
        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            lock (_gate) return TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);
        }
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            KeyValuePair<TKey, TValue>[] snapshot;
            lock (_gate) snapshot = Snapshot();
            ((ICollection<KeyValuePair<TKey, TValue>>)snapshot).CopyTo(array, arrayIndex);
        }
        public bool Remove(TKey key) { lock (_gate) return _internalDictionary.Remove(key); }
        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            lock (_gate) return Contains(item) && _internalDictionary.Remove(item.Key);
        }
        public bool TryGetValue(TKey key, out TValue value)
        {
            lock (_gate)
            {
                if (_internalDictionary.TryGetValue(key, out var reference) && reference.Target is TValue target)
                {
                    value = target;
                    return true;
                }
                value = default!;
                return false;
            }
        }
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            lock (_gate) return ((IEnumerable<KeyValuePair<TKey, TValue>>)Snapshot()).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private KeyValuePair<TKey, TValue>[] Snapshot() => _internalDictionary
            .Select(pair => new KeyValuePair<TKey, TValue>(pair.Key, (TValue)pair.Value.Target!)).ToArray();
        private void RemoveFinalized(TKey key, WeakReference reference)
        {
            lock (_gate)
            {
                if (_internalDictionary.TryGetValue(key, out var current) && ReferenceEquals(current, reference))
                    _internalDictionary.Remove(key);
            }
        }
        private sealed class Finalizer
        {
            ~Finalizer() { ValueFinalized?.Invoke(); }
            public event Action? ValueFinalized;
        }
    }
}
