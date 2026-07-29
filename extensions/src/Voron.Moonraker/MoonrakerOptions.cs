namespace Voron.Moonraker
{
    /// <summary>
    /// Connection settings for the Moonraker instance that fronts Klipper.
    /// </summary>
    public sealed class MoonrakerOptions
    {
        public const string SectionName = "Moonraker";

        /// <summary>Host running Moonraker. The extension service normally runs on the same CB1.</summary>
        public string Host { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 7125;

        /// <summary>
        /// Only needed when this host is not in Moonraker's <c>trusted_clients</c>. When set, the
        /// key is exchanged for a one-shot token before the websocket is opened.
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>How long to wait for a JSON-RPC reply before giving up.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>First reconnect delay; doubles up to <see cref="MaxReconnectDelay"/>.</summary>
        public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

        public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

        public Uri WebSocketUri => new($"ws://{Host}:{Port}/websocket");

        public Uri HttpUri => new($"http://{Host}:{Port}/");
    }
}
