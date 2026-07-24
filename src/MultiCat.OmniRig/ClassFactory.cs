using System.Runtime.InteropServices;

namespace MultiCat.OmniRig;

[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

/// <summary>Serves the singleton OmniRigX object to every connecting client,
/// mirroring real OmniRig's one-server-many-clients model.</summary>
public sealed class ClassFactory(Func<object> instanceProvider) : IClassFactory
{
    private const int ClassENoAggregation = unchecked((int)0x80040110);

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (pUnkOuter != IntPtr.Zero)
        {
            return ClassENoAggregation;
        }

        var unknown = Marshal.GetIUnknownForObject(instanceProvider());
        try
        {
            return Marshal.QueryInterface(unknown, in riid, out ppvObject);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    public int LockServer(bool fLock) => 0;
}
