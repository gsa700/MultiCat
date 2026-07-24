# MultiCAT

*One radio, many owners.*

MultiCAT is a graphical CAT multiplexer for Windows: it takes exclusive ownership of a
radio's CAT port, then shares that radio with as many applications as you like — each
one convinced it has the rig to itself. Configure your rig from the hamlib database,
hand N1MM+ a virtual COM port, point WSJT-X at the built-in rigctld-compatible TCP
listener, and let your own tools subscribe to a clean frequency/mode event stream.

## Why

Only one program can hold a COM port. The existing workarounds each solve half the
problem: generic serial-port splitters share the bytes but corrupt interleaved CAT
transactions; rigctld multiplexes properly but only for network-aware clients;
OmniRig has its own rig database and its own client API; the polished commercial
suites are single-brand and closed source. MultiCAT aims at the missing combination:

- **Protocol-aware arbitration** — one command outstanding at a time, each response
  routed back to only the client that asked, unsolicited traffic broadcast to all.
- **Virtual COM ports** — legacy apps that only know "a Kenwood on COM5" work unmodified.
- **hamlib underneath** — rig selection and connection defaults come from the hamlib
  rig database; a rigctld-compatible TCP listener comes free per radio.
- **Poll deduplication** — three apps polling `FA;` at 4 Hz becomes one poll on the
  wire and two cache hits.
- **A live traffic monitor** — every decoded frame attributed to the app that sent it,
  with notes on how the mux handled it. CAT debugging without the mystery.

## Status

Early development — pre-alpha. Working today:

- Core engine: Kenwood/Elecraft and Icom CI-V framers, transaction arbiter,
  short-TTL poll cache, client port endpoints, radio state tracker with
  frequency/mode events (unit tested)
- Serial transport for real radios (opens only the configured port, never probes)
- Client endpoints, all driverless: a hamlib **rigctld-protocol listener** (WSJT-X,
  fldigi, JTDX, GridTracker, and anything hamlib-aware connects natively) and raw
  CAT over TCP, both on localhost. **Validated with real WSJT-X**: connected as
  "Hamlib NET rigctl" at `localhost:4532`, it polled at 1 Hz (mostly served from
  the poll cache while three other clients polled concurrently), and a band change
  QSYed the radio with every other client following
- Virtual COM port management for com0com, where its driver still loads (see below)
- Service host: radio sessions from `appsettings.json`, gRPC control API over a
  named pipe, built-in simulated K3 for driverless development
- Avalonia GUI connected live to the service: radio status, per-port state, one-click
  Add port, and a streaming traffic monitor (falls back to demo data when the
  service is offline)

- Rig picker backed by hamlib's full rig database (311 models, harvested from
  hamlib 4.7.2 at build time by `tools/HamlibHarvest` — the shipped app contains
  no native hamlib, only the knowledge)

- **OmniRig-compatible COM server** (`MultiCat.OmniRig`): registers as
  `OmniRig.OmniRigX` (one UAC prompt; machine-wide, because current Windows 11
  builds no longer honor per-user COM server activation), implements VE3NEA's
  published interfaces GUID-for-GUID, and forwards to MultiCAT's rigctld
  endpoint — so Log4OM, CW Skimmer, SDR Console, WSJT-X, and other
  OmniRig-aware apps see "OmniRig" while MultiCAT arbitrates. Verified with
  both late-bound (IDispatch) and early-bound (vtable/typelib) clients,
  including COM launch-on-demand. (Note: N1MM+ supports neither OmniRig nor
  any network CAT — it is serial-COM-only, making it the prime motivation for
  the first-party virtual COM driver.)

Not yet built: CI-V session wiring (the framer exists; sessions are
Kenwood-family for now), PTT arbitration, applying the selected rig's serial
defaults to the connection form, OmniRig event notifications to early-bound
sinks under load, first-party virtual COM driver.

### Virtual COM ports and the driver reality

MultiCAT manages the virtual port driver for you — you never touch `setupc.exe`
or see a CNCA0/CNCB0 name. With the signed
[com0com](https://sourceforge.net/projects/com0com/) driver installed, **Add
port** picks a free COM name (avoiding names burned in the Windows COM Name
Arbiter database), creates the pair silently (one UAC prompt), starts
arbitrating it, and persists it to configuration.

**However:** current Windows 11 builds enforce driver-signing policy that
rejects com0com's 2012-era signature outright (device problem code 52),
regardless of Memory Integrity settings. On such systems MultiCAT runs fully
driverless via the rigctld and TCP endpoints; a first-party attestation-signed
driver is the planned long-term fix for real COM ports there. On Windows 10 and
older Windows 11 builds, the com0com path works as designed.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build MultiCat.sln
dotnet test MultiCat.sln
dotnet run --project src/MultiCat.Gui
```

## Layout

| Project | Purpose |
| --- | --- |
| `MultiCat.Core` | Framers, transaction arbiter, poll cache, state tracker — no I/O dependencies |
| `MultiCat.Hamlib` | Rig capability database harvested from hamlib at build time — no native dependency |
| `MultiCat.Contracts` | Shared GUI ↔ service contracts |
| `MultiCat.Service` | The multiplexer host: owns the radio ports, runs sessions |
| `MultiCat.OmniRig` | OmniRig-compatible COM server bridging OmniRig apps to the mux |
| `MultiCat.Gui` | Avalonia configuration and monitoring front end |
| `tests/MultiCat.Core.Tests` | Engine unit tests |

## Credits

The rig capability database in `MultiCat.Hamlib` is derived from the
[Hamlib project](https://hamlib.github.io) (LGPL-2.1-or-later), harvested at
build time from `rigctl --dump-caps`. MultiCAT ships no hamlib code — but the
knowledge of 300+ rigs' CAT parameters is the Hamlib community's work, and
this project would be poorer without it.

The OmniRig COM interface definitions and bundled `OmniRig.tlb` come from
[OmniRig](https://github.com/VE3NEA/OmniRig) by Alex Shovkoplyas VE3NEA (MIT
license) — the de facto rig-control API of the Windows ham ecosystem, generously
open-sourced.

## License

GPLv3 — see [LICENSE](LICENSE).
