using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace ChengyuDock;

internal sealed record GpuReading(string Id, string Name, double? Usage, long? DedicatedBytes, long CapacityBytes);

internal interface IGpuTelemetry : IDisposable
{
    GpuReading[] Read();
}

// One wildcard query follows process creation/exit without spawning a sampler process.
internal sealed class WindowsGpuTelemetry : IGpuTelemetry
{
    const uint MoreData = 0x800007D2, DoubleFormat = 0x00000200;
    IntPtr _query, _engines, _memory, _buffer;
    uint _bufferSize;
    long _retryAt;
    Adapter[] _adapters = Array.Empty<Adapter>();
    sealed record Adapter(string Luid, string Id, string Name, long Capacity);

    [StructLayout(LayoutKind.Sequential)]
    struct CounterItem { public IntPtr Name; public uint Status; public double Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string? source, UIntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

    public GpuReading[] Read()
    {
        if (_query == IntPtr.Zero && Environment.TickCount64 >= _retryAt)
        {
            _retryAt = Environment.TickCount64 + 10_000;
            _adapters = EnumerateAdapters();
            if (PdhOpenQuery(null, UIntPtr.Zero, out _query) == 0)
            {
                PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out _engines);
                PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", UIntPtr.Zero, out _memory);
                if (_engines == IntPtr.Zero && _memory == IntPtr.Zero) CloseQuery();
                else
                {
                    // Prime the rate counter. The next normal tick supplies the second sample.
                    PdhCollectQueryData(_query);
                    return EmptyReadings();
                }
            }
        }

        if (_query == IntPtr.Zero) return EmptyReadings();
        if (PdhCollectQueryData(_query) != 0) { CloseQuery(); return EmptyReadings(); }

        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in Values(_engines))
        {
            int luid = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            int engineType = instance.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            if (luid < 0 || engineType <= luid) continue;
            string engine = instance[luid..engineType];
            engines[engine] = engines.GetValueOrDefault(engine) + Math.Clamp(value, 0, 100);
        }

        var memory = Values(_memory);
        return _adapters.Select(adapter =>
        {
            string prefix = adapter.Luid + "_phys_";
            double? usage = engines.Where(e => e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(e => (double?)Math.Clamp(e.Value, 0, 100)).DefaultIfEmpty().Max();
            var dedicated = memory.Where(m => m.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            long? bytes = dedicated.Length == 0 ? null : (long)Math.Clamp(dedicated.Sum(m => m.Value), 0, long.MaxValue / 2.0);
            return new GpuReading(adapter.Id, adapter.Name, usage, bytes, adapter.Capacity);
        }).ToArray();
    }

    GpuReading[] EmptyReadings() => _adapters.Select(a => new GpuReading(a.Id, a.Name, null, null, a.Capacity)).ToArray();

    List<(string Name, double Value)> Values(IntPtr counter)
    {
        var values = new List<(string, double)>();
        if (counter == IntPtr.Zero) return values;
        uint size = _bufferSize;
        uint status = PdhGetFormattedCounterArray(counter, DoubleFormat, ref size, out uint count, _buffer);
        if (status == MoreData && size > _bufferSize && size <= 8 * 1024 * 1024)
        {
            _buffer = _buffer == IntPtr.Zero ? Marshal.AllocHGlobal((IntPtr)size) : Marshal.ReAllocHGlobal(_buffer, (IntPtr)size);
            _bufferSize = size;
            status = PdhGetFormattedCounterArray(counter, DoubleFormat, ref size, out count, _buffer);
        }
        if (status != 0) return values;

        int stride = Marshal.SizeOf<CounterItem>();
        for (int i = 0; i < count && (i + 1) * stride <= _bufferSize; i++)
        {
            var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(_buffer, i * stride));
            if (item.Status <= 1 && double.IsFinite(item.Value) && Marshal.PtrToStringUni(item.Name) is { } name)
                values.Add((name, item.Value));
        }
        return values;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint Vendor, Device, Subsystem, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedMemory;
        public uint LuidLow, LuidHigh, Flags;
    }
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(in Guid id, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc1(IntPtr adapter, out AdapterDescription description);

    static Adapter[] EnumerateAdapters()
    {
        var id = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(id, out IntPtr factory) < 0) return Array.Empty<Adapter>();
        var adapters = new List<Adapter>();
        var ordinals = new Dictionary<string, int>();
        try
        {
            // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1.
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdapters1>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            for (uint i = 0; enumerate(factory, i, out IntPtr adapter) == 0; i++)
            {
                try
                {
                    var describe = Marshal.GetDelegateForFunctionPointer<GetDesc1>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size));
                    if (describe(adapter, out var desc) < 0 || (desc.Flags & 2) != 0) continue;
                    string hardware = $"{desc.Vendor:x4}:{desc.Device:x4}:{desc.Subsystem:x8}";
                    int ordinal = ordinals.GetValueOrDefault(hardware);
                    ordinals[hardware] = ordinal + 1;
                    adapters.Add(new Adapter($"luid_0x{desc.LuidHigh:x8}_0x{desc.LuidLow:x8}", $"{hardware}:{ordinal}", desc.Name, (long)desc.DedicatedVideoMemory.ToUInt64()));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return adapters.ToArray();
    }

    void CloseQuery()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = _engines = _memory = IntPtr.Zero;
    }

    public void Dispose()
    {
        CloseQuery();
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
        _bufferSize = 0;
    }
}
