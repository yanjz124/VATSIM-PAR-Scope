# Using vibe PAR with xPilot (X-Plane)

If you fly with X-Plane and xPilot, you can feed vibe PAR live VATSIM traffic from an xPilot **tower
view**: positions at up to 5 times a second, the same quality as vPilot with the plugin. No plugin and no
extra software are needed; xPilot already has everything built in.

Where your facility is on CRC, the CRC feed is simpler and needs no simulator (see the
[README](../README.md#with-crc-recommended)). xPilot is for everywhere else.

## What you need

* X-Plane 11 or 12 with xPilot installed and your VATSIM details saved in xPilot's settings.
* vibe PAR on the **same PC as the xPilot app**.
* X-Plane running with a flight loaded while you control. xPilot will not connect without it. Any
  aircraft at any airport will do; it just sits there.

## One-time setup

1. Start xPilot. It does not need X-Plane for this step.
2. In xPilot's message box, type:

   ```
   .visualip 127.0.0.1
   ```

   xPilot answers "X-Plane visual machine(s) set to 127.0.0.1."
3. Close xPilot and start it again. The setting only takes effect after a restart.

That's it. xPilot remembers the setting from now on.

> **Already using visual PCs with X-Plane?** `.visualip` replaces the whole list, so name them all,
> with vibe PAR's address last: `.visualip 192.168.1.20 192.168.1.21 127.0.0.1`.

## Each session

1. Connect to your position in your ATC client as usual.
2. Start X-Plane and load a flight.
3. Start xPilot and type `.towerview`. Don't connect with the normal Connect button; tower view is a
   separate, observer-only connection.
4. Start vibe PAR, or leave it running. Order doesn't matter: xPilot keeps retrying until vibe PAR is
   listening.

## Checking it works

* The **status bar** at the bottom of vibe PAR shows **xPilot tower view · 5 Hz data, N aircraft**.
* **Settings › Data** shows "Receiving from xPilot (1234567_TV)" under **Receive traffic from xPilot**.

Until xPilot connects, that line says "Listening on 127.0.0.1:53100…". Once xPilot has connected but no
traffic has arrived yet, it says "xPilot is connected; waiting for traffic."

## Troubleshooting

| What you see | What to do |
|---|---|
| Settings still says "Listening on 127.0.0.1:53100" | xPilot isn't connecting. Check that you typed `.visualip 127.0.0.1` and **restarted xPilot** afterwards. |
| xPilot says "X-Plane connection not found" when you type `.towerview` | X-Plane isn't running, or no flight is loaded yet. |
| "Cannot listen on TCP 53100" in Settings › Data | Something else on this PC already uses port 53100. The usual cause is xPilot's X-Plane plugin set to use TCP on the same PC; set it back to the default. |
| Connected, but no aircraft | VATSIM only sends traffic within range of your tower view. Check that the field has traffic, and that you haven't hidden an aircraft with xPilot's `.ignore` (`.ignorelist` shows them). |
| Distant aircraft update only every 5 s | Normal. VATSIM sends 5 Hz data only for aircraft near you, and xPilot passes on positions only from that data. Aircraft on final get the full rate. |
| Aircraft appear from both CRC and xPilot | Fine: **Settings › Data › Source priority** decides which one each aircraft uses, and the next takes over if that source stops. |

## Turning it off

* **In vibe PAR:** turn off **Settings › Data › Receive traffic from xPilot**.
* **In xPilot:** type `.visualip` on its own to clear the list, then restart xPilot.

## Good to know

* **Rules:** a tower view counts as your one extra observer connection, `(CID)_TV`, which the VATSIM
  Code of Conduct (C7(b)) allows next to your controller login. It is the same allowance vPilot's tower
  view uses.
* **Read-only:** vibe PAR only listens. It sends nothing to xPilot beyond the connection handshake and
  never touches X-Plane or the network.
* **This PC only:** vibe PAR accepts the connection only from the same PC, so nothing on your network can
  reach it.
* **How it works:** xPilot can copy everything it sends its X-Plane plugin to "visual machines", extra
  PCs that draw more of the outside view. vibe PAR poses as one of those PCs on TCP port 53100 and reads
  the aircraft positions from that copy.
