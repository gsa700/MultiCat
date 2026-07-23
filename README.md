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
- Client endpoints: raw CAT over TCP (localhost) works out of the box; virtual COM
  ports activate automatically when the configured com0com pair exists
- Service host: radio sessions from `appsettings.json`, gRPC control API over a
  named pipe, built-in simulated K3 for driverless development
- Avalonia GUI connected live to the service: radio status, per-port state, and a
  streaming traffic monitor (falls back to demo data when the service is offline)

Not yet built: rigctld-protocol listener, hamlib rig database enumeration, CI-V
session wiring (the framer exists; sessions are Kenwood-family for now), PTT
arbitration, com0com pair management from the GUI.

### Virtual COM ports

MultiCAT manages the virtual port driver for you — you never touch `setupc.exe`
or see a CNCA0/CNCB0 name. Install the signed
[com0com](https://sourceforge.net/projects/com0com/) driver once (a future
MultiCAT release will ship its own driver in this slot), then click **Add port**
in the GUI: MultiCAT picks a free COM name, creates the pair silently (one UAC
prompt), starts arbitrating it, and persists it to configuration. Point your
application at the new COM port and its CAT traffic is multiplexed like any
other client. Without the driver installed, Add port explains what's missing
and every other endpoint keeps working.

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
| `MultiCat.Hamlib` | P/Invoke bindings to libhamlib and rig database enumeration |
| `MultiCat.Contracts` | Shared GUI ↔ service contracts |
| `MultiCat.Service` | The multiplexer host: owns the radio ports, runs sessions |
| `MultiCat.Gui` | Avalonia configuration and monitoring front end |
| `tests/MultiCat.Core.Tests` | Engine unit tests |

## License

GPLv3 — see [LICENSE](LICENSE).
