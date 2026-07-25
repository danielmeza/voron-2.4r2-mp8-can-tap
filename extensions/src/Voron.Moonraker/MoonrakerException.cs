namespace Voron.Moonraker
{
    /// <summary>Raised when Moonraker answers a JSON-RPC call with an error object.</summary>
    public sealed class MoonrakerException : Exception
    {
        public MoonrakerException(string message, int code = 0)
            : base(message)
        {
            Code = code;
        }

        public int Code { get; }
    }
}
