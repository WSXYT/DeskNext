using System.Runtime.InteropServices;

internal static class NvidiaAdapter
{
    internal sealed record Info(int DxgiIndex, string Name, uint VendorId, ulong DedicatedVideoBytes);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enumerate(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Describe(IntPtr adapter, out Description description);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint Vendor, Device, Subsystem, Revision;
        public UIntPtr DedicatedVideo, DedicatedSystem, Shared;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    internal static Info Read(int index)
    {
        if (index is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(index));
        Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        try
        {
            var enumerate = Marshal.GetDelegateForFunctionPointer<Enumerate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(enumerate(factory, (uint)index, out var adapter));
            try
            {
                var describe = Marshal.GetDelegateForFunctionPointer<Describe>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size));
                Marshal.ThrowExceptionForHR(describe(adapter, out var d));
                if (d.Vendor != 0x10DE || (d.Flags & 2) != 0 || d.DedicatedVideo.ToUInt64() == 0)
                    throw new NotSupportedException("Select a hardware NVIDIA DXGI adapter with dedicated memory.");
                return new Info(index, d.Name, d.Vendor, d.DedicatedVideo.ToUInt64());
            }
            finally { Marshal.Release(adapter); }
        }
        finally { Marshal.Release(factory); }
    }
}
