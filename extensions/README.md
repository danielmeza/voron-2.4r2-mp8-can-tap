# Voron extensions

A small .NET service that runs on the CB1 next to Klipper and Moonraker and adds hardware that
Klipper itself has no concept of. Today that is one extension: **joystick jog** — free-hand movement
of the Stealthburner from a physical stick on the printer.

```
  joystick hardware                 this service                    the printer
 ┌──────────────────┐        ┌──────────────────────────┐       ┌──────────────────┐
 │ Sanwa JLF        │        │ IJoystickInputSource     │       │                  │
 │ arcade buttons   │──GPIO─▶│   digital / analog       │       │                  │
 │ pot stick + ADC  │──I2C──▶│            │             │       │                  │
 │                  │──SPI──▶│            ▼             │       │                  │
 └──────────────────┘        │      JogController       │──ws──▶│ Moonraker ─▶ Klipper
                             │  safety gates, G1 stream │ 7125  │                  │
                             └──────────────────────────┘       └──────────────────┘
```

## How the motion works

Klipper has no jog command, so a held direction becomes a stream of short **absolute** `G1` moves —
by default one every 20 ms. Consecutive collinear segments pass straight through Klipper's lookahead
without decelerating, which is what makes the movement continuous instead of a visible series of steps.

Two properties are worth understanding before tuning anything:

* **The target is integrated in this service and never read back mid-jog.** Position comes from
  Klipper once, when a jog session starts; after that the service owns it. Rounding cannot accumulate
  and a slow status update cannot make the toolhead stutter.
* **Queued motion is latency.** The service keeps `LookaheadSeconds` of movement in Klipper's queue
  (80 ms by default) because a starved queue stutters. A move handed to Klipper cannot be recalled, so
  that number is also exactly how long the toolhead keeps going after you let go — 80 ms at 100 mm/s is
  about 8 mm of coast. Raise it if motion feels rough, lower it if the stop feels mushy.

Each session is wrapped in `SAVE_GCODE_STATE` / `RESTORE_GCODE_STATE`, so jogging can never leave the
printer in relative mode. Acceleration and velocity limits are left alone — the printer's own
`max_accel` (5700) and `max_velocity` (400) already make jogging responsive.

## Safety

| Rule | Behaviour |
|---|---|
| Never during a print | Ignored whenever `print_stats.state` is `printing` or `paused` |
| Auto-home | The first stick input on an unhomed printer runs `_CG28`, then waits for the stick to return to centre before moving |
| Soft limits | Targets are clamped to `axis_minimum`/`axis_maximum` less `EdgeMarginMm`, so a jog never trips a boundary error |
| Z floor | Jogged Z is clamped at `MinZ` (0 by default), even though Klipper allows −5 |
| Klipper down | No connection, or klippy not `ready`, means no motion; the link reconnects on its own |

The print guard deliberately reads `print_stats.state` rather than `idle_timeout.state`. The latter
reports `Printing` for *any* gcode activity, including the jog moves this service issues — the same
trap that was fixed in the standby macros (commit b8438d8).

## Supported hardware

Input hardware sits behind [`IJoystickInputSource`](src/extensions/Voron.Joystick/Input/IJoystickInputSource.cs),
so the jog loop is identical whichever you pick. Choose with `Joystick:Source`.

### `Digital` — switch-per-direction sticks

For arcade sticks such as the **Sanwa JLF-TP-8YT-SK**. Four microswitches, no potentiometers, so
speed comes from a ramp instead: a tap creeps, a held direction winds up to full speed over
`RampSeconds`. Pair it with arcade buttons for Z and for the precision modifier.

Wiring — every switch is normally-open with a shared common:

| Stick / button | CB1 pin | Notes |
|---|---|---|
| JLF common (COM) | any GND | pins 6, 9, 14, 20, 25, 30, 34, 39 |
| JLF up / down / left / right | one GPIO each | internal pull-up, closed switch reads low |
| Z+ / Z− | one GPIO each | optional, `ZMode: Buttons` |
| Precision | one GPIO | optional, holds speed at `PrecisionFactor` |

The two Z inputs are just switches, so they can equally be **a second stick used as a vertical
lever** — wire only its up/down microswitches to `ZPlus`/`ZMinus` and ignore left/right. That gives
the same one-axis vertical control as the mock UI's Z stick, with the same hold-to-ramp behaviour.
Arcade buttons work too if you prefer them.

No pull-up resistors, level shifters or extra power are needed: the switches only short a
pulled-up input to ground.

### `Analog` — potentiometer sticks through an ADC

The CB1 has no analog inputs, so a pot stick needs an external converter:

| `Analog:Device` | Bus | Notes |
|---|---|---|
| `Ads1115` | I2C | 16-bit, 4 channels. Needs a real `/dev/i2c-N` — check with `ls /dev/i2c-*` and `i2cdetect -y N` |
| `Mcp3008` | SPI | 10-bit, 8 channels |
| `Mcp3208` | SPI | 12-bit, 8 channels |

SPI can be a real `/dev/spidev` node (`Spi:Transport: Hardware`) or **bit-banged over four GPIO pins**
(`Software`). Bit-banging needs no device-tree overlay, which makes it the reliable choice on a stock
CB1 image where `/dev/spidev*` and `/dev/i2c-*` may simply not exist.

Axis wiring is one pot per channel: the two ends to 3.3 V and GND, the wiper to the ADC input.

**Z must self-centre.** A twist axis or a spring-return thumb lever is fine; a mixer-style slide
fader is not — left off centre it would make Z creep forever. If your stick has no third axis, use
`ZMode: Buttons` or `ZMode: ToggleWithY`.

### `Mock` — a virtual joystick in the browser

An on-screen stick served over HTTP, for testing the whole chain with no hardware attached. It is a
source like any other, so the controller, safety gates and gcode stream under test are exactly the
ones the real stick will use — only the input is swapped out.

```powershell
# on the printer, over SSH
ssh biqu@192.168.100.81 'cd voron-extensions && Joystick__Source=Mock ./Voron.Extensions.Service'

# in a second window, since the UI binds to loopback by default
ssh -L 8088:127.0.0.1:8088 biqu@192.168.100.81
```

Then open <http://127.0.0.1:8088>. There are **two sticks**: a round pad for X/Y and a **vertical
lever for Z**, both proportional and both spring-returning to centre — how far you push is how fast
it goes. Arrow keys, `PgUp`/`PgDn` for Z and `Shift` for precision work as well. A status panel
shows the live link state and says *why* jogging is refused — "blocked — a print is printing", "not
homed", "no connection to Moonraker" — instead of just doing nothing.

With `ZMode: None` the Z lever greys out and Z input is dropped, so the mock refuses exactly what
the real stick would.

**The browser is not trusted to keep talking.** The page sends every 50 ms; if updates stop for
`InputTimeoutMs` (250 ms) the service treats the input as neutral, so a closed tab or dropped wifi
stops the toolhead rather than leaving it running. That window is also the worst-case travel after a
browser dies — 25 mm at full speed.

`Mock:Prefix` is loopback by default on purpose. Setting it to `http://+:8088/` hands toolhead
control to everyone on the network, with no authentication of any kind.

### `None`

Disables jogging without removing the configuration.

## CB1 pins

Pin names accept `PI14`, `gpio270` or `270` — all the same pin. The number is
`(bank − 'A') × 32 + index`, the rule from the CB1 manual and the same numbering `cb1.cfg` uses.

Pins already claimed by Klipper in [`config/printer/mcu/cb1.cfg`](../config/printer/mcu/cb1.cfg) —
**PI1** (power), **PI9** (pause), **PI13** (resume) — must not be reused. Nothing arbitrates GPIO
between Klipper and this service, so a shared pin means two owners fighting over it.

The 40-pin header documented in the CB1 manual carries:

| | | | | | |
|--:|:--|:--|--:|:--|:--|
| 1 | 3.3 V | | 2 | 5 V | |
| 3 | *NC* | | 4 | 5 V | |
| 5 | *NC* | | 6 | GND | |
| 7 | PC7 | gpio71 | 8 | UART TX | |
| 9 | GND | | 10 | UART RX | |
| 11 | PC14 | gpio78 | 12 | PC13 | gpio77 |
| 13 | PC12 | gpio76 | 14 | GND | |
| 15 | PC10 | gpio74 | 16 | PC11 | gpio75 |
| 17 | 3.3 V | | 18 | PC9 | gpio73 |
| 19 | PH7 | gpio231 | 20 | GND | |
| 21 | PH8 | gpio232 | 22 | *NC* | |
| 23 | PH6 | gpio230 | 24 | *NC* | |
| 25 | GND | | 26 | PG8 | gpio200 |
| 27 | *NC* | | 28 | PG7 | gpio199 |
| 29 | *NC* | | 30 | GND | |
| 31 | PG6 | gpio198 | 32 | PG9 | gpio201 |
| 33 | *NC* | | 34 | GND | |
| 35 | PC6 | gpio70 | 36 | *NC* | |
| 37 | PC15 | gpio79 | 38 | PH10 | gpio234 |
| 39 | GND | | 40 | PC8 | gpio72 |

Two caveats:

* **PC pins are 1.8 V logic on CB1 v2.1** (3.3 V on v2.2). Prefer PH and PG pins for switches.
* That table is the arrangement printed in the CB1 manual, which shows a Manta M4P. It contains no
  bank-I pins, yet `cb1.cfg` drives buttons on PI1/PI9/PI13 — so the M8P V2 exposes more than the
  manual's diagram shows. **Confirm your own wiring with `--probe` before trusting any pin number**,
  including the defaults shipped in `appsettings.json`.

## Commissioning

Deploy first (see *Deploying*), then drive the diagnostics from Windows over SSH. `-t` gives them a
terminal, which `--probe` and `--calibrate` need to redraw in place.

```powershell
$printer = 'biqu@192.168.100.81'
$run = 'cd voron-extensions && ./Voron.Extensions.Service'

# Only one process can own the pins, so stop the service first.
ssh $printer 'sudo systemctl stop voron-extensions'

ssh -t $printer "$run --probe"            # live pin states / ADC counts, no motion
ssh -t $printer "$run --calibrate"        # analog only: prints a calibration block to paste back
ssh -t $printer "$run --simulate x+ 1.0"  # MOVES the toolhead: 1 s of +X, no joystick involved

ssh $printer 'sudo systemctl start voron-extensions'
```

1. **`--probe`** — press each direction and button in turn and watch which entry flips to `PRESSED`.
   This is how you map the physical stick to `Joystick:Digital:Pins` and settle the orientation
   question (which switch is "up") without guessing.
2. **`--calibrate`** — leave the stick centred for a second, then sweep every axis to both extremes.
   On Ctrl+C it prints a ready-to-paste `X`/`Y`/`Z` block with the observed min, centre and max.
3. **`--simulate x+ 1.0`** — drives the jog loop directly with a synthetic full deflection. Use it to
   confirm the toolhead moves the right way and at the speed you expect. Neither the stick nor the
   GPIO pins are involved, so it also works before any hardware arrives.

Before any of that, the `Mock` source lets you exercise the entire chain from a browser with no
hardware at all — see [`Mock`](#mock--a-virtual-joystick-in-the-browser) above. It is safe to open
during a print: the banner will read *blocked* and no gcode is sent.

## Configuration

Everything lives in `appsettings.json` next to the binary. Environment variables override it, e.g.
`Joystick__Motion__MaxSpeedXY=60`.

| Key | Default | Meaning |
|---|---|---|
| `Joystick:Source` | `Digital` | `Digital`, `Analog`, `Mock` or `None` |
| `Joystick:SampleRateHz` | `50` | Sample and emit rate; also the length of one move |
| `Joystick:ZMode` | `Buttons` | `None`, `Buttons`, `Analog` or `ToggleWithY` |
| `Joystick:Digital:Pins:*` | — | `XPlus`, `XMinus`, `YPlus`, `YMinus`, `ZPlus`, `ZMinus`, `Precision`, `ModeToggle` |
| `Joystick:Digital:ActiveLow` | `true` | Closed switch reads low (correct for switch-to-ground) |
| `Joystick:Digital:RampStartFraction` | `0.12` | Speed the instant a direction is pressed |
| `Joystick:Digital:RampSeconds` | `1.2` | Time to reach full speed; `0` disables the ramp |
| `Joystick:Analog:Device` | `Ads1115` | `Ads1115`, `Mcp3008`, `Mcp3208` |
| `Joystick:Analog:X/Y/Z` | — | `Channel`, `Min`, `Center`, `Max`, `Deadband`, `Exponent`, `Invert` |
| `Joystick:Analog:SmoothingFactor` | `0.35` | Lower is calmer, at the cost of response |
| `Joystick:Mock:Prefix` | `http://127.0.0.1:8088/` | Where the virtual joystick listens |
| `Joystick:Mock:InputTimeoutMs` | `250` | Silence from the page longer than this reads as neutral |
| `Joystick:Motion:MaxSpeedXY` | `100` mm/s | Full-deflection XY speed |
| `Joystick:Motion:MaxSpeedZ` | `12` mm/s | Full-deflection Z speed |
| `Joystick:Motion:PrecisionFactor` | `0.2` | Multiplier while the precision button is held |
| `Joystick:Motion:LookaheadSeconds` | `0.08` | Queued motion — smoothness against coast distance |
| `Joystick:Safety:AutoHome` | `true` | Home on the first input when unhomed |
| `Joystick:Safety:HomeGcode` | `_CG28` | The conditional-home macro from `macros/homing.cfg` |
| `Joystick:Safety:EdgeMarginMm` | `1.0` | Keep-out from the soft limits |
| `Joystick:Safety:MinZ` | `0.0` | Floor for jogged Z |
| `Moonraker:Host` / `:Port` | `127.0.0.1` / `7125` | |
| `Moonraker:ApiKey` | `null` | Only needed if this host is not a trusted client |
| `Gpio:Driver` | `Sun50iw9p1` | Or `Default` for libgpiod/sysfs |

`Exponent` shapes the analog response: `1` is linear, `2` (the default) gives fine control near
centre and full speed only at the edge of travel.

## Deploying

From Windows PowerShell (or PowerShell 7 on any platform):

```powershell
cd extensions
.\deploy\install.ps1                             # default printer
.\deploy\install.ps1 -Target biqu@192.168.68.69  # somewhere else
.\deploy\install.ps1 -DeployConfig               # also overwrite appsettings.json
```

The script publishes a **self-contained linux-arm64** build, so the CB1 needs no .NET runtime
installed, copies it up and starts the systemd unit. It needs only the .NET SDK and the OpenSSH
client that ships with Windows 10 1809 and later — **no rsync, no WSL, no bash**.

The payload lands in a temporary directory on the printer and is swapped into place at the end, so a
failed or interrupted copy never leaves a half-updated install running. `appsettings.json` holds the
pin map and calibration and is edited on the printer, so it is carried across upgrades unless
`-DeployConfig` is given.

```powershell
ssh biqu@192.168.100.81 'journalctl -u voron-extensions -f'
```

The unit runs as root because the sunxi GPIO driver maps `/dev/mem`. To run unprivileged instead, set
`Gpio:Driver` to `Default` and switch the unit to `User=biqu` with `SupplementaryGroups=gpio i2c spi`.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `Joystick hardware could not be opened` | Wrong pin name, a pin already owned by Klipper, or no `/dev/mem` access — check `Gpio:Driver` |
| `Joystick ignored: a print is printing` | Working as designed; jogging is disabled during prints |
| `Joystick idle: Klipper has not reported axis limits yet` | Klippy is still starting, or in an error state |
| `Moonraker link unavailable` | Moonraker down, or this host is not in its `trusted_clients` — set `Moonraker:ApiKey` |
| Motion stutters | Raise `LookaheadSeconds`, or lower `SampleRateHz` if the CB1 is loaded |
| Toolhead overshoots on release | Lower `LookaheadSeconds` |
| An axis moves the wrong way | Swap the two pins, or set `Invert: true` on that analog axis |

## Adding an input source

Implement `IJoystickInputSource` — `Open`, `Read(elapsedSeconds)` returning a `JogInput` of −1..1 per
axis, and `DescribeRaw()` for `--probe` — then add a case to `JoystickInputSourceFactory`. Nothing in
`JogController` changes. A USB gamepad read through `/dev/input/event*` would drop in this way, and is
the obvious next source if you ever want a wireless pendant.

## Projects

| Project | Purpose |
|---|---|
| `Voron.Extensions.Service` | systemd host, GPIO driver choice, diagnostic entry points |
| `src/Voron.Moonraker` | Websocket JSON-RPC client, reconnect, printer state, gcode |
| `src/extensions/Voron.Joystick` | Input sources, jog controller, diagnostics |
| `src/Voron.Extensibility.Abstractions` | Shared extension contracts |
