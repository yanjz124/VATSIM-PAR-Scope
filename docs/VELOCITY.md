# VATSIM Velocity and the vPilot plugin

## What Velocity sends

Since January 2022, pilot clients can send fast position packets alongside the classic 5-second `@`
update:

| Packet | Rate | Contents |
|---|---|---|
| `^` fast | up to 5 Hz, when the server asks for them (`$SF`) | position, true altitude, AGL, packed attitude, linear velocity (m/s: east, up, north), rotational rates, nose-gear angle |
| `#SL` slow | every 5 s | the same fields as `^` |
| `#ST` stopped | when stationary | position and attitude, no velocities |

The server only asks a pilot for fast packets when a client that wants them is nearby. In openfsd
(a reverse-engineered server) that threshold is 5 NM. VATSIM's real threshold is not public, so an
aircraft 10 NM out may still be sending only 5-second updates. Measure it with `TrafficProbe`.

Sources: openfsd protocol notes, the xPilot and swift FSD source code, and VATSIM announcements.
See the research brief in the project history.

## What vPilot does with them (decompiled from vPilot, Feb 2026 build, plugin API 3.12.1)

* The public plugin event `AircraftUpdated` is raised only from the slow `@` handler (internal topic
  `AircraftUpdateReceived`). So it fires about every 5 seconds. It does carry the latest fast position,
  but without velocities or a timestamp.
* Fast packets go to the internal topic `FastPositionUpdateReceived`. vPilot uses them to drive its
  rendered aircraft but never raises a plugin event for them.
* The `IBroker` object handed to plugins holds vPilot's internal event broker (bbv.Common.EventBroker).

## How the plugin gets 5 Hz data

`RadarStreamerPlugin` installs an `IEventBrokerExtension` on that broker. Extensions see every event
fired, so the plugin picks out `FastPositionUpdateReceived` and streams each one with its velocity
vector (`"src":"fast"` lines).

vPilot is obfuscated, so the plugin finds the event fields by type and declaration order, not by name:

* the event: `From`, then the visual state, then two `Vector3` values (positional, then rotational);
* the visual state: a `WorldPoint`, then altitude.

Every packet is range-checked. After 20 consecutive failures the hook disables itself, and the plugin
falls back to the public 5-second events. Set `FastHook=false` in `RadarStreamerPlugin.ini` to skip it
entirely.

This only reads data vPilot has already received. It never talks to the network, so it is a local
read-only tap like any other plugin.

## Display-side tracking

Every report goes into a per-aircraft tracker (`TargetTrack`), kept in ECEF so it doesn't depend on
the selected runway.

* Between reports, the position is dead-reckoned from the velocity: reported (fast) or estimated from
  position changes (slow).
* When a report disagrees with the prediction, the difference is blended out exponentially (τ 0.35 s
  for fast, 1.5 s for slow) instead of jumping. This is the same idea as xPilot's error velocities.
* Extrapolation stops after 12 s, and a track is dropped after 30 s of silence.

The PAR radar model then paints from this continuous truth only when its beam crosses the aircraft,
so the update rate you see is the radar's, not the network's.
