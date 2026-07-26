namespace MultiCat.Core.Flex;

/// <summary>
/// Builds the ASCII <c>key=value</c> card carried in a discovery packet.
/// <para>
/// The field set is modelled on a live FLEX-8600M capture (fw 4.2.20, 31 fields)
/// and is deliberately complete: GUI clients (SmartSDR, Maestro) parse far more of
/// this card than the Genius boxes do, and missing keys can wedge a Maestro's
/// boot-time radio scan. The occupancy fields advertise a single-seat radio whose
/// seat is already taken, so pickers list the radio but do not casually connect —
/// this bridge can serve the Genius boxes' slice/interlock diet, not panadapters
/// or DAX.
/// </para>
/// </summary>
public static class DiscoveryPayload
{
    public static string Build(FlexIdentity identity)
    {
        // Spaces in name/nickname are encoded as underscores on the wire.
        var name = identity.Name.Replace(' ', '_');
        var nickname = identity.Nickname.Replace(' ', '_');
        var ip = identity.AdvertiseIp;

        string[] fields =
        [
            "discovery_protocol_version=3.1.0.4",
            $"model={identity.Model}",
            $"serial={identity.Serial}",
            $"version={identity.Version}",
            $"name={name}",
            $"nickname={nickname}",
            $"callsign={identity.Callsign}",
            $"ip={ip}",
            $"port={identity.CommandPort}",
            "status=In_Use",
            $"inuse_ip={ip}",
            "inuse_host=multicat",
            "max_licensed_version=v3",
            "radio_license_id=00-1C-2D-00-08-95",
            "fpc_mac=00:1c:2d:00:08:95",
            "wan_connected=0",
            "licensed_clients=1",
            "available_clients=0",
            "max_panadapters=4",
            "available_panadapters=0",
            "max_slices=4",
            "available_slices=0",
            $"gui_client_ips={ip}",
            "gui_client_hosts=multicat",
            "gui_client_programs=MultiCAT-Bridge",
            $"gui_client_stations={nickname}",
            "gui_client_handles=0x40000001",
            "min_software_version=3.8.0.0",
            "external_port_link=1",
            "license_is_unknown=0",
            "is_system_model=0",
            "turf_region=USA",
        ];

        return string.Join(" ", fields);
    }
}
