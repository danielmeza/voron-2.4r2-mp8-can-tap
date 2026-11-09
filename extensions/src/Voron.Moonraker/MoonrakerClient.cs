using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace Voron.Moonraker
{
    /// <summary>
    /// Minimal JSON-RPC 2.0 transport over Moonraker's websocket. One instance owns one socket;
    /// reconnects are the caller's job (see <see cref="PrinterLink"/>).
    /// </summary>
    public sealed class MoonrakerClient : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly MoonrakerOptions _options;
        private readonly ILogger _log;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ClientWebSocket? _socket;
        private Task? _receiveTask;
        private int _nextId;
        private int _disposed;

        public MoonrakerClient(MoonrakerOptions options, ILogger log)
        {
            _options = options;
            _log = log;
        }

        /// <summary>Raised for every server-initiated message (<c>notify_*</c>), with the raw params element.</summary>
        public event Action<string, JsonElement>? NotificationReceived;

        /// <summary>Completes when the socket drops, for any reason.</summary>
        public Task Closed => _closed.Task;

        public bool IsConnected => _socket?.State == WebSocketState.Open;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (_socket is not null)
            {
                throw new InvalidOperationException("This client has already been connected. Create a new instance.");
            }

            var uri = _options.WebSocketUri;
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                var token = await RequestOneshotTokenAsync(cancellationToken).ConfigureAwait(false);
                uri = new Uri($"{uri}?token={Uri.EscapeDataString(token)}");
            }

            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            try
            {
                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            _socket = socket;
            _receiveTask = Task.Run(() => ReceiveLoopAsync(socket), CancellationToken.None);
            _log.LogInformation("Connected to Moonraker at {Uri}", _options.WebSocketUri);
        }

        /// <summary>Issues a JSON-RPC call and waits for the matching reply.</summary>
        public async Task<JsonElement> CallAsync(
            string method,
            object? parameters,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null)
        {
            var socket = _socket ?? throw new InvalidOperationException("Not connected.");
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;

            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(
                    new JsonRpcRequest("2.0", method, parameters, id),
                    SerializerOptions);

                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout ?? _options.RequestTimeout);
                using (timeoutSource.Token.Register(
                    static state => ((TaskCompletionSource<JsonElement>)state!).TrySetCanceled(),
                    completion))
                {
                    return await completion.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Moonraker did not answer '{method}' within {timeout ?? _options.RequestTimeout}.");
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private async Task<string> RequestOneshotTokenAsync(CancellationToken cancellationToken)
        {
            using var http = new HttpClient { BaseAddress = _options.HttpUri, Timeout = _options.RequestTimeout };
            http.DefaultRequestHeaders.Add("X-Api-Key", _options.ApiKey);

            var json = await http.GetStringAsync("access/oneshot_token", cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("result").GetString()
                ?? throw new MoonrakerException("Moonraker returned an empty one-shot token.");
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            var message = new MemoryStream();
            Exception? failure = null;

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                    Dispatch(text);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                message.Dispose();
                FailPending(failure ?? new MoonrakerException("The Moonraker websocket closed."));
                _closed.TrySetResult();

                if (failure is not null && _disposed == 0)
                {
                    _log.LogWarning(failure, "Moonraker websocket dropped.");
                }
            }
        }

        private void Dispatch(string text)
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(text);
                root = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Discarding malformed message from Moonraker.");
                return;
            }

            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
            {
                if (!_pending.TryRemove(id, out var completion))
                {
                    return;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
                    var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var parsed) ? parsed : 0;
                    completion.TrySetException(new MoonrakerException(message ?? "Unknown Moonraker error.", code));
                    return;
                }

                completion.TrySetResult(root.TryGetProperty("result", out var result) ? result : default);
                return;
            }

            if (root.TryGetProperty("method", out var methodElement) && methodElement.GetString() is { } method)
            {
                root.TryGetProperty("params", out var parameters);
                try
                {
                    NotificationReceived?.Invoke(method, parameters);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "A handler for Moonraker notification '{Method}' threw.", method);
                }
            }
        }

        private void FailPending(Exception exception)
        {
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var completion))
                {
                    completion.TrySetException(exception);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            var socket = _socket;
            if (socket is not null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", closeTimeout.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Ignoring error while closing the Moonraker websocket.");
                }

                socket.Dispose();
            }

            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Ignoring error from the Moonraker receive loop during shutdown.");
                }
            }

            _sendLock.Dispose();
            _closed.TrySetResult();
        }

        private sealed record JsonRpcRequest(
            [property: JsonPropertyName("jsonrpc")] string JsonRpc,
            [property: JsonPropertyName("method")] string Method,
            [property: JsonPropertyName("params")] object? Params,
            [property: JsonPropertyName("id")] int Id);
    }
}
