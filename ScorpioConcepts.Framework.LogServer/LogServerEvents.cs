using System;

namespace ScorpioConcepts.Framework.LogServer
{
    /// <summary>
    /// Data for the <see cref="LogServer.ClientConnected"/> and <see cref="LogServer.ClientDisconnected"/> events
    /// </summary>
    public sealed class LogClientEventArgs : EventArgs
    {
        /// <summary>
        /// Create the event data
        /// </summary>
        /// <param name="address">The client's address and port, for example "192.168.1.20:51234"</param>
        public LogClientEventArgs(string address)
        {
            Address = address;
        }

        /// <summary>
        /// The client's address and port, for example "192.168.1.20:51234"
        /// </summary>
        public string Address { get; }
    }

    /// <summary>
    /// Data for the <see cref="LogServer.ServerError"/> event
    /// </summary>
    public sealed class LogServerErrorEventArgs : EventArgs
    {
        /// <summary>
        /// Create the event data
        /// </summary>
        /// <param name="exception">The error that stopped the server, or kept it from starting</param>
        public LogServerErrorEventArgs(Exception exception)
        {
            Exception = exception;
        }

        /// <summary>
        /// The error that stopped the server, or kept it from starting
        /// </summary>
        public Exception Exception { get; }
    }
}
