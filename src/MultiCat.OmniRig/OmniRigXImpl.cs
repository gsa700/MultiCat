using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MultiCat.OmniRig;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IOmniRigX))]
[Guid(OmniRigGuids.OmniRigXClass)]
public sealed class OmniRigXImpl : IOmniRigX, IConnectionPointContainer
{
    private readonly EventConnectionPoint _events;
    private readonly RigXImpl _rig1;
    private readonly RigXImpl _rig2;

    public OmniRigXImpl(string host, int rig1Port, int? rig2Port)
    {
        _events = new EventConnectionPoint(this, new Guid(OmniRigGuids.IOmniRigXEvents));
        _rig1 = new RigXImpl(new RigctldClient(host, rig1Port), 1, OnParamsChange, OnStatusChange);
        _rig2 = rig2Port is { } port
            ? new RigXImpl(new RigctldClient(host, port), 2, OnParamsChange, OnStatusChange)
            : new RigXImpl(null, 2, null, null);
    }

    // OmniRig 1.x reports interface version 0x101; clients check the major byte.
    public int InterfaceVersion => 0x101;

    public int SoftwareVersion => 101;

    public IRigX Rig1 => _rig1;

    public IRigX Rig2 => _rig2;

    public bool DialogVisible
    {
        get => false;
        set { }
    }

    private void OnParamsChange(int rigNumber, int paramsChanged) =>
        _events.Fire(OmniRigEventIds.ParamsChange, rigNumber, paramsChanged);

    private void OnStatusChange(int rigNumber) =>
        _events.Fire(OmniRigEventIds.StatusChange, rigNumber);

    public void EnumConnectionPoints(out IEnumConnectionPoints ppEnum) =>
        throw new NotImplementedException();

    public void FindConnectionPoint(ref Guid riid, out IConnectionPoint? ppCP)
    {
        if (riid == new Guid(OmniRigGuids.IOmniRigXEvents) || riid == typeof(IOmniRigX).GUID)
        {
            ppCP = _events;
            return;
        }

        ppCP = null;
        Marshal.ThrowExceptionForHR(unchecked((int)0x80040200)); // CONNECT_E_NOCONNECTION
    }
}
