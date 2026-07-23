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
  short-TTL poll cache, radio state tracker with frequency/mode events (unit tested)
- Service host running the engine against a simulated K3 with concurrent demo clients
- Avalonia GUI shell: radio list, connection settings, client-port table with per-port
  PTT policy, animated signal-flow view, traffic monitor (mock data, not yet wired to
  the service)

Not yet built: real serial transport, GUI↔service connection, virtual COM port
provider (com0com), hamlib rig database enumeration, rigctld TCP listener.

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
