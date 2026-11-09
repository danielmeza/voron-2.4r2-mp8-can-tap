using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Voron.Joystick.Input
{
    /// <summary>
    /// An on-screen joystick served over HTTP, for testing the jog loop with no hardware attached.
    /// It is a source like any other, so the controller, safety gates and gcode stream under test
    /// are exactly the ones the real stick will use — only the input hardware is swapped out.
    ///
    /// The browser is not trusted to keep sending: if no update arrives within
    /// <see cref="MockJoystickOptions.InputTimeoutMs"/> the input reads neutral, so a closed tab or a
    /// dropped wifi link stops the toolhead instead of leaving it running.
    /// </summary>
    internal sealed class MockJoystickSource : IJoystickInputSource
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly MockJoystickOptions _options;
        private readonly Func<string> _statusJson;
        private readonly bool _zEnabled;
        private readonly ILogger _log;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private HttpListener? _listener;
        private CancellationTokenSource? _stopping;
        private Task? _acceptLoop;
        private volatile Command _command = Command.Neutral;
        private string? _page;

        public MockJoystickSource(MockJoystickOptions options, Func<string> statusJson, bool zEnabled, ILogger log)
        {
            _options = options;
            _statusJson = statusJson;
            _zEnabled = zEnabled;
            _log = log;
        }

        public string Description => $"virtual joystick on {_options.Prefix}";

        public void Open()
        {
            _page = LoadPage();

            var listener = new HttpListener();
            listener.Prefixes.Add(_options.Prefix);

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                throw new InvalidOperationException(
                    $"Could not listen on '{_options.Prefix}'. Check the port is free and the prefix ends with '/'. {ex.Message}",
                    ex);
            }

            _listener = listener;
            _stopping = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _stopping.Token));

            _log.LogInformation("Virtual joystick UI is at {Prefix}", _options.Prefix);
        }

        public JogInput Read(double elapsedSeconds)
        {
            var command = _command;

            // Watchdog: a browser that stops talking must not leave the toolhead moving.
            if (_clock.Elapsed.TotalSeconds - command.AtSeconds > _options.InputTimeoutMs / 1000d)
            {
                return JogInput.Neutral;
            }

            // Honour ZMode:None here too, so the mock refuses exactly what the real stick would.
            return new JogInput(command.X, command.Y, _zEnabled ? command.Z : 0d, command.Precision);
        }

        public string DescribeRaw()
        {
            var command = _command;
            var age = _clock.Elapsed.TotalSeconds - command.AtSeconds;
            var stale = age > _options.InputTimeoutMs / 1000d;

            return string.Format(
                CultureInfo.InvariantCulture,
                "virtual X={0:+0.00;-0.00; 0.00} Y={1:+0.00;-0.00; 0.00} Z={2:+0.00;-0.00; 0.00} precision={3} age={4:0.00}s{5}",
                command.X,
                command.Y,
                command.Z,
                command.Precision,
                age,
                stale ? " (stale -> neutral)" : string.Empty);
        }

        private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Virtual joystick listener error.");
                    continue;
                }

                try
                {
                    await HandleAsync(context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Virtual joystick request failed.");
                }
                finally
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Ignoring error while closing a virtual joystick response.");
                    }
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;

            switch (path)
            {
                case "":
                case "/index.html":
                    await WriteAsync(context, "text/html; charset=utf-8", _page ?? string.Empty).ConfigureAwait(false);
                    return;

                case "/status":
                    await WriteAsync(context, "application/json", _statusJson()).ConfigureAwait(false);
                    return;

                case "/input" when context.Request.HttpMethod == "POST":
                    await ReadCommandAsync(context).ConfigureAwait(false);
                    await WriteAsync(context, "application/json", "{\"ok\":true}").ConfigureAwait(false);
                    return;

                default:
                    context.Response.StatusCode = 404;
                    await WriteAsync(context, "text/plain", "not found").ConfigureAwait(false);
                    return;
            }
        }

        private async Task ReadCommandAsync(HttpListenerContext context)
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            try
            {
                var payload = JsonSerializer.Deserialize<CommandPayload>(body, JsonOptions);
                if (payload is null)
                {
                    return;
                }

                _command = new Command(
                    Sanitize(payload.X),
                    Sanitize(payload.Y),
                    Sanitize(payload.Z),
                    payload.Precision,
                    _clock.Elapsed.TotalSeconds);
            }
            catch (JsonException ex)
            {
                _log.LogDebug(ex, "Discarding malformed virtual joystick input.");
            }
        }

        private static double Sanitize(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, -1d, 1d) : 0d;

        private static async Task WriteAsync(HttpListenerContext context, string contentType, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }

        private static string LoadPage()
        {
            const string resource = "Voron.Joystick.MockUi.html";

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded resource '{resource}' is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public void Dispose()
        {
            _stopping?.Cancel();

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Ignoring error while stopping the virtual joystick listener.");
            }

            try
            {
                _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Ignoring error while waiting for the virtual joystick listener.");
            }

            _stopping?.Dispose();
        }

        /// <summary>
        /// A reference type on purpose: the HTTP thread publishes it and the jog loop reads it, and
        /// only a reference assignment is guaranteed atomic (a struct cannot be volatile).
        /// </summary>
        private sealed record Command(double X, double Y, double Z, bool Precision, double AtSeconds)
        {
            // Timestamped far in the past so a source that has never been posted to reads neutral.
            public static Command Neutral { get; } = new(0d, 0d, 0d, false, double.NegativeInfinity);
        }

        private sealed record CommandPayload(double X, double Y, double Z, bool Precision);
    }
}
