using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MultiCat.OmniRig;

/// <summary>
/// Hand-rolled COM connection point (the .NET Core runtime does not expose
/// ComSourceInterfaces-style event plumbing). Sinks are stored as IDispatch and
/// events are fired by dispid, which is how dispinterface sinks expect them.
/// </summary>
public sealed class EventConnectionPoint(object owner, Guid eventsIid) : IConnectionPoint
{
    private readonly ConcurrentDictionary<int, object> _sinks = new();
    private int _nextCookie;

    public void GetConnectionInterface(out Guid pIID) => pIID = eventsIid;

    public void GetConnectionPointContainer(out IConnectionPointContainer ppCPC) =>
        ppCPC = (IConnectionPointContainer)owner;

    public void Advise(object pUnkSink, out int pdwCookie)
    {
        pdwCookie = Interlocked.Increment(ref _nextCookie);
        _sinks[pdwCookie] = pUnkSink;
    }

    public void Unadvise(int dwCookie)
    {
        if (_sinks.TryRemove(dwCookie, out var sink))
        {
            Marshal.ReleaseComObject(sink);
        }
    }

    public void EnumConnections(out IEnumConnections ppEnum) => throw new NotImplementedException();

    public void Fire(int dispId, params object[] args)
    {
        foreach (var sink in _sinks.Values)
        {
            try
            {
                DispatchInvoker.Invoke(sink, dispId, args);
            }
            catch (Exception)
            {
                // A dead or throwing sink must not break the others.
            }
        }
    }
}

/// <summary>Late-bound IDispatch::Invoke by dispid, for firing events into sinks.</summary>
internal static class DispatchInvoker
{
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatch
    {
        int GetTypeInfoCount();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetTypeInfo(int iTInfo, int lcid);

        void GetIDsOfNames(ref Guid riid, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] rgszNames, int cNames, int lcid, [Out] int[] rgDispId);

        void Invoke(int dispIdMember, ref Guid riid, int lcid, ushort wFlags,
            ref System.Runtime.InteropServices.ComTypes.DISPPARAMS pDispParams,
            IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
    }

    private const ushort DispatchMethod = 1;
    private const int VariantSize = 24;

    public static void Invoke(object sink, int dispId, object[] args)
    {
        if (sink is not IDispatch dispatch)
        {
            return;
        }

        // IDispatch::Invoke takes its arguments in reverse order.
        var buffer = Marshal.AllocCoTaskMem(VariantSize * Math.Max(args.Length, 1));
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                Marshal.GetNativeVariantForObject(args[args.Length - 1 - i], buffer + (i * VariantSize));
            }

            var dispParams = new System.Runtime.InteropServices.ComTypes.DISPPARAMS
            {
                rgvarg = buffer,
                cArgs = args.Length,
                cNamedArgs = 0,
                rgdispidNamedArgs = IntPtr.Zero,
            };

            var empty = Guid.Empty;
            dispatch.Invoke(dispId, ref empty, 0, DispatchMethod, ref dispParams, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }
}
