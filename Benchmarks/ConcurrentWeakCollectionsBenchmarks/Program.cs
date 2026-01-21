using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Singulink.Collections;

// NOTE: this should be run with the .NET 10.0 tfm.

BenchmarkRunner.Run<Benchs>();

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.HostProcess, id: ".NET 10.0", baseline: true)]
[SimpleJob(RuntimeMoniker.Net90, id: ".NET 9.0")]
[SimpleJob(RuntimeMoniker.Net80, id: ".NET 8.0 (.NET Standard 2.1)")]
[SimpleJob(RuntimeMoniker.Net60, id: ".NET 6.0 (.NET Standard 2.0)")]
public class Benchs
{
    [Params(0, 1, 3, 10, 30, 100, 300, 1000, 3000, 10000)]
    public int N { get; set; }

    private readonly Random _random = new();
    private ConcurrentWeakList<object> _list = null!;
    private readonly object _value = new();
    private object[] _values = null!;
    private ConcurrentWeakList<object>.Node[] _nodes = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _list = new();
        _values = [.. Enumerable.Range(0, N).Select(_ => new object())];
        _nodes = new ConcurrentWeakList<object>.Node[N];
        int i = 0;
        foreach (object x in _values) _nodes[i++] = _list.AddLast(x);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _list.Dispose();
        GC.KeepAlive(_values);
        GC.KeepAlive(_value);
    }

    [Benchmark]
    public void AddRemoveNodeAtStart()
    {
        ConcurrentWeakList<object> list = _list;
        var node = list.AddFirst(_value);
        list.Remove(node);
    }

    [Benchmark]
    public void AddRemoveNodeAtEnd()
    {
        ConcurrentWeakList<object> list = _list;
        var node = list.AddLast(_value);
        list.Remove(node);
    }

    [Benchmark]
    public void AddRemoveNodeRandomPosition()
    {
        ConcurrentWeakList<object> list = _list;
        var node = list.UnsafeInsertAt(_value, _random.Next(0, N + 1));
        list.Remove(node);
    }

    [Benchmark]
    public void AddRemoveNodeRandomPositionEach()
    {
        ConcurrentWeakList<object> list = _list;
        int n = N;
        if (n == 0) return;
        int idx = _random.Next(0, n);
        ref var nodeSlot = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_nodes), (uint)idx)!;
        object oldValue = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_values), (uint)idx)!;
        list.Remove(nodeSlot);
        nodeSlot = list.UnsafeInsertAt(oldValue, _random.Next(0, n));
    }

    [Benchmark]
    public void Enumerate()
    {
        ConcurrentWeakList<object> list = _list;
#pragma warning disable IDE0059 // Unnecessary assignment of a value
        foreach (object x in list)
#pragma warning restore IDE0059 // Unnecessary assignment of a value
        {
        }
    }

    [Benchmark]
    public void CreateAddNodesClearDispose()
    {
        ConcurrentWeakList<object> list = new();

        int n = N;
        var nodes = _nodes;
        object[] values = _values;
        if (n > 0)
        {
            _ = nodes[n - 1];
            _ = values[n - 1];
        }

        for (int i = 0; i < n; i++)
        {
            nodes[i] = list.AddLast(values[i] = new object());
        }

        list.Clear();

        GC.KeepAlive(values);

        list.Dispose();
    }

    [Benchmark]
    public void CreateAddPreexistingNodesClearDispose()
    {
        ConcurrentWeakList<object> list = new();

        int n = N;
        var nodes = _nodes;
        object value = _value;
        if (n > 0) _ = nodes[n - 1];

        for (int i = 0; i < n; i++)
        {
            nodes[i] = list.AddLast(value);
        }

        list.Clear();

        GC.KeepAlive(value);

        list.Dispose();
    }

    [Benchmark]
    public void CreateAddNodesDispose()
    {
        ConcurrentWeakList<object> list = new();

        int n = N;
        var nodes = _nodes;
        object[] values = _values;
        if (n > 0)
        {
            _ = nodes[n - 1];
            _ = values[n - 1];
        }

        for (int i = 0; i < n; i++)
        {
            nodes[i] = list.AddLast(values[i] = new object());
        }

        list.Dispose();

        GC.KeepAlive(values);
    }

    [Benchmark]
    public void CreateAddPreexistingNodesDispose()
    {
        ConcurrentWeakList<object> list = new();

        int n = N;
        var nodes = _nodes;
        object value = _value;
        if (n > 0) _ = nodes[n - 1];

        for (int i = 0; i < n; i++)
        {
            nodes[i] = list.AddLast(value);
        }

        list.Dispose();

        GC.KeepAlive(value);
    }
}
