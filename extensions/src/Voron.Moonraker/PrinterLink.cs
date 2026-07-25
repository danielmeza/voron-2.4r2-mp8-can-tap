using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Voron.Moonraker
{
    /// <summary>
    /// Keeps a live websocket to Moonraker: reconnects, re-subscribes after a klippy restart, tracks
    /// the printer objects we care about, and runs gcode. Everything else in the service reads
    /// <see cref="Snapshot"/> and calls <see cref="RunGcodeAsync"/>.
    /// </summary>
    public sealed class PrinterLink : IHostedService, IAsyncDisposable
    {
        private static readonly object SubscribeRequest = new
        {
            objects = new Dictionary<string, string[]>
            {
                ["toolhead"] = ["homed_axes", "axis_minimum", "axis_maximum"],
                ["gcode_move"] = ["gcode_position", "homing_origin"],
                ["print_stats"] = ["state"],
            },
        };

        private readonly MoonrakerOptions _options;
        private readonly ILogger<PrinterLink> _log;
        private readonly object _sync = new();

        private volatile PrinterSnapshot _snapshot = PrinterSnapshot.Empty;
        private volatile MoonrakerClient? _client;
        private CancellationTokenSource? _stopping;
        private Task? _runLoop;
        private int _disposed;

        public PrinterLink(IOptions<MoonrakerOptions> options, ILogger<PrinterLink> log)
        {
            _options = options.Value;
            _log = log;
        }

        /// <summary>Latest known printer state. Never null; <see cref="PrinterSnapshot.KlippyReady"/> says whether it is trustworthy.</summary>
        public PrinterSnapshot Snapshot => _snapshot;

        /// <summary>True when the socket is up, klippy is ready and the subscription is live.</summary>
        public bool IsOnline => _client is { IsConnected: true } && _snapshot.KlippyReady;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _stopping = new CancellationTokenSource();
            _runLoop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopping?.Cancel();

            if (_runLoop is not null)
            {
                try
                {
                    await _runLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown deadline hit; the loop is cancelled and the socket is disposed below.
                }
            }
        }

        /// <summary>Sends a gcode script and waits for klippy to accept it.</summary>
        public async Task RunGcodeAsync(string script, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            var client = _client ?? throw new MoonrakerException("Not connected to Moonraker.");
            await client.CallAsync("printer.gcode.script", new { script }, cancellationToken, timeout)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Asks Klipper for the current gcode-space position instead of trusting the pushed snapshot.
        /// Used when seeding a jog session, where a stale position would jump the toolhead.
        /// </summary>
        public async Task<Vec3?> QueryGcodePositionAsync(CancellationToken cancellationToken)
        {
            var client = _client;
            if (client is null)
            {
                return null;
            }

            var request = new
            {
                objects = new Dictionary<string, string[]>
                {
                    ["gcode_move"] = ["gcode_position", "homing_origin"],
                },
            };

            var result = await client.CallAsync("printer.objects.query", request, cancellationToken)
                .ConfigureAwait(false);

            if (!result.TryGetProperty("status", out var status))
            {
                return null;
            }

            ApplyStatus(status);
            return _snapshot.GcodePosition;
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var delay = _options.ReconnectDelay;

            while (!cancellationToken.IsCancellationRequested)
            {
                MoonrakerClient? client = null;
                try
                {
                    client = new MoonrakerClient(_options, _log);
                    client.NotificationReceived += OnNotification;

                    await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    _client = client;

                    await WaitForKlippyAsync(client, cancellationToken).ConfigureAwait(false);

                    var subscription = await client
                        .CallAsync("printer.objects.subscribe", SubscribeRequest, cancellationToken)
                        .ConfigureAwait(false);

                    if (subscription.TryGetProperty("status", out var status))
                    {
                        ApplyStatus(status);
                    }

                    SetKlippyReady(true);
                    _log.LogInformation(
                        "Printer link ready. State={State} HomedAxes='{Homed}' Limits={Min}..{Max}",
                        _snapshot.PrintState,
                        _snapshot.HomedAxes,
                        _snapshot.AxisMinimum,
                        _snapshot.AxisMaximum);

                    delay = _options.ReconnectDelay;
                    await client.Closed.WaitAsync(cancellationToken).ConfigureAwait(false);
                    _log.LogWarning("Lost the Moonraker connection; reconnecting.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        "Moonraker link unavailable ({Reason}); retrying in {Delay:0.#}s.",
                        ex.Message,
                        delay.TotalSeconds);
                }
                finally
                {
                    SetKlippyReady(false);
                    _client = null;

                    if (client is not null)
                    {
                        client.NotificationReceived -= OnNotification;
                        await client.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxReconnectDelay.Ticks));
            }
        }

        private async Task WaitForKlippyAsync(MoonrakerClient client, CancellationToken cancellationToken)
        {
            var logged = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                var info = await client.CallAsync("server.info", null, cancellationToken).ConfigureAwait(false);
                var state = info.TryGetProperty("klippy_state", out var element) ? element.GetString() : null;

                if (string.Equals(state, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!logged)
                {
                    _log.LogInformation("Waiting for klippy (state '{State}').", state ?? "unknown");
                    logged = true;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }

        private void OnNotification(string method, JsonElement parameters)
        {
            switch (method)
            {
                case "notify_status_update":
                    if (parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() > 0)
                    {
                        ApplyStatus(parameters[0]);
                    }

                    break;

                case "notify_klippy_shutdown":
                case "notify_klippy_disconnected":
                    _log.LogWarning("Klippy went away ({Method}); jogging is disabled until it returns.", method);
                    SetKlippyReady(false);

                    // Drop the socket so the run loop rebuilds the subscription from scratch.
                    _ = _client?.DisposeAsync().AsTask();
                    break;

                case "notify_klippy_ready":
                    _log.LogInformation("Klippy reported ready; refreshing the subscription.");
                    _ = _client?.DisposeAsync().AsTask();
                    break;
            }
        }

        private void ApplyStatus(JsonElement status)
        {
            lock (_sync)
            {
                _snapshot = _snapshot.Merge(status);
            }
        }

        private void SetKlippyReady(bool ready)
        {
            lock (_sync)
            {
                _snapshot = _snapshot with { KlippyReady = ready };
            }
        }

        public async ValueTask DisposeAsync()
        {
            // The container tracks this instance twice — once as a singleton and once as the
            // IHostedService it hands back — so disposal has to be idempotent.
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _stopping?.Cancel();

            if (_runLoop is not null)
            {
                try
                {
                    await _runLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
            }

            if (_client is { } client)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            _stopping?.Dispose();
        }
    }
}
