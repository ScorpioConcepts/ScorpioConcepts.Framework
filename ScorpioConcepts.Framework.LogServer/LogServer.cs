using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScorpioConcepts.Framework.LogServer
{
    /// <summary>
    /// A simple TCP log server: any telnet client that connects sees recent log lines, then every new one as it is logged.
    /// </summary>
    /// <remarks>
    /// The server has no authentication or encryption, and by default it listens on every network interface
    /// (<see cref="IPAddress.Any"/>), so anyone who can reach the machine can read the log. Never run it like that in
    /// production: pass <see cref="IPAddress.Loopback"/> to only allow connections from the same machine, or a specific
    /// internal address.
    /// </remarks>
    public sealed class LogServer : IDisposable
    {
        /// <summary>The port used when none is given. It is above 1023, so it needs no root on Linux.</summary>
        public const int DefaultPort = 7446;
        /// <summary>The default for <see cref="MaxClients"/></summary>
        public const int DefaultMaxClients = 32;
        /// <summary>The default for <see cref="MaxQueuedLines"/></summary>
        public const int DefaultMaxQueuedLines = 10000;

        private static readonly byte[] TooManyClientsMessage =
            Encoding.ASCII.GetBytes("Too many clients are connected to this log server. Try again later.\r\n");

        private readonly TcpListener listener;
        private readonly string greeting;
        private readonly int maxLogSize;

        // Guards logLines and clients: holding it while a client is added, and while a line is logged,
        // means a new client gets every line exactly once, in its history or as a new line.
        private readonly object logLock = new object();
        private readonly Queue<string> logLines = new Queue<string>();
        private readonly HashSet<LogServerClient> clients = new HashSet<LogServerClient>();

        private volatile bool running;
        private int started;
        private int maxClients = DefaultMaxClients;
        private int maxQueuedLines = DefaultMaxQueuedLines;
        private IPEndPoint? localEndpoint;
        private Task acceptLoop = Task.CompletedTask;

        /// <summary>
        /// Occurs when a new client connects to the server
        /// </summary>
        public event EventHandler<LogClientEventArgs>? ClientConnected;
        /// <summary>
        /// Occurs when a client disconnects, or is disconnected because the server stopped
        /// </summary>
        public event EventHandler<LogClientEventArgs>? ClientDisconnected;
        /// <summary>
        /// Occurs when the server cannot start, for example because its port is in use, or stops because of an error
        /// </summary>
        public event EventHandler<LogServerErrorEventArgs>? ServerError;

        /// <summary>
        /// Create a log server on <see cref="DefaultPort"/>, listening on every network interface.
        /// Not for production: see the remarks on <see cref="LogServer"/>.
        /// </summary>
        /// <param name="greetingMessage">Greeting to send to clients when they connect</param>
        /// <param name="logSize">Number of lines to keep in memory and send to new clients</param>
        public LogServer(string greetingMessage, int logSize)
            : this(IPAddress.Any, DefaultPort, greetingMessage, logSize)
        { }

        /// <summary>
        /// Create a log server on a custom port, listening on every network interface.
        /// Not for production: see the remarks on <see cref="LogServer"/>.
        /// </summary>
        /// <param name="port">The port to listen on, or 0 for any free port (see <see cref="LocalEndpoint"/>)</param>
        /// <param name="greetingMessage">Greeting to send to clients when they connect</param>
        /// <param name="logSize">Number of lines to keep in memory and send to new clients</param>
        public LogServer(int port, string greetingMessage, int logSize)
            : this(IPAddress.Any, port, greetingMessage, logSize)
        { }

        /// <summary>
        /// Create a log server listening on a specific address and port
        /// </summary>
        /// <param name="address">The address to listen on, such as <see cref="IPAddress.Loopback"/> for this machine only</param>
        /// <param name="port">The port to listen on, or 0 for any free port (see <see cref="LocalEndpoint"/>)</param>
        /// <param name="greetingMessage">Greeting to send to clients when they connect</param>
        /// <param name="logSize">Number of lines to keep in memory and send to new clients</param>
        public LogServer(IPAddress address, int port, string greetingMessage, int logSize)
        {
            if (address == null) throw new ArgumentNullException(nameof(address));
            if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
            {
                throw new ArgumentOutOfRangeException(nameof(port), port, "The port must be from 0 to 65535.");
            }
            if (logSize < 0) throw new ArgumentOutOfRangeException(nameof(logSize), logSize, "The log size cannot be negative.");

            this.listener = new TcpListener(address, port);
            this.greeting = greetingMessage ?? throw new ArgumentNullException(nameof(greetingMessage));
            this.maxLogSize = logSize;
        }

        /// <summary>
        /// The most clients that can be connected at once. Further connections are told the server is full and
        /// closed. The default is <see cref="DefaultMaxClients"/>.
        /// </summary>
        public int MaxClients
        {
            get { return maxClients; }
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), value, "At least one client must be allowed.");
                maxClients = value;
            }
        }

        /// <summary>
        /// The most lines queued for one client that is not keeping up. When a client's queue is full its oldest
        /// lines are dropped, and it is told how many it missed, so a client that stops reading cannot make the
        /// server's memory grow without bound. The default is <see cref="DefaultMaxQueuedLines"/>.
        /// </summary>
        public int MaxQueuedLines
        {
            get { return maxQueuedLines; }
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), value, "At least one line must be allowed.");
                maxQueuedLines = value;
            }
        }

        /// <summary>
        /// The address and port the server is listening on, once <see cref="Start"/> has opened it; useful with port 0
        /// </summary>
        public IPEndPoint? LocalEndpoint => localEndpoint;

        /// <summary>
        /// Is the Log Server Active
        /// </summary>
        public bool Active => running;

        /// <summary>
        /// How many clients are connected
        /// </summary>
        public int ConnectedClients
        {
            get
            {
                lock (logLock)
                {
                    return clients.Count;
                }
            }
        }

        /// <summary>
        /// Start the Log Server. When this returns, the server is already accepting connections.
        /// If the port cannot be opened, <see cref="ServerError"/> is raised before this returns,
        /// and <see cref="Active"/> stays false.
        /// </summary>
        /// <exception cref="InvalidOperationException">The server was already started; a server can only be started once.</exception>
        public void Start()
        {
            if (Interlocked.Exchange(ref started, 1) == 1)
            {
                throw new InvalidOperationException("The log server has already been started. Create a new LogServer to start again.");
            }

            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                RaiseServerError(ex);
                return;
            }

            localEndpoint = (IPEndPoint)listener.LocalEndpoint;
            running = true;
            acceptLoop = AcceptLoopAsync();
        }

        /// <summary>
        /// Stop the Log Server and disconnect every client. Call <see cref="Join"/> to wait until it has finished.
        /// </summary>
        public void Stop()
        {
            running = false;
            // Ends the pending accept, which then closes every client
            listener.Stop();
        }

        /// <summary>
        /// Wait for the Log Server to shut down after <see cref="Stop"/>
        /// </summary>
        public void Join()
        {
            acceptLoop.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Stop the Log Server and wait for it to shut down
        /// </summary>
        public void Dispose()
        {
            Stop();
            Join();
        }

        /// <summary>
        /// Log a new line to the clients
        /// </summary>
        /// <param name="line">Any text to be sent to the Log Client</param>
        public void Log(string line)
        {
            if (line == null) throw new ArgumentNullException(nameof(line));

            lock (logLock)
            {
                logLines.Enqueue(line);
                while (logLines.Count > maxLogSize)
                {
                    logLines.Dequeue();
                }
                foreach (LogServerClient client in clients)
                {
                    client.Send(line);
                }
            }
        }

        /// <summary>
        /// Returns a copy of the Log History, oldest line first
        /// </summary>
        public IReadOnlyList<string> LogHistory
        {
            get
            {
                lock (logLock)
                {
                    return logLines.ToArray();
                }
            }
        }

        /// <summary>The number of lines queued for each connected client, for tests</summary>
        internal int[] QueuedLinesPerClient()
        {
            lock (logLock)
            {
                return clients.Select(c => c.QueuedLines).ToArray();
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Any failure of the accept loop is reported through ServerError, which is this class's documented error channel.")]
        private async Task AcceptLoopAsync()
        {
            try
            {
                while (running)
                {
                    TcpClient tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Accept(tcp);
                }
            }
            catch (Exception) when (!running)
            {
                // Stop() closed the listener, which ends the pending accept
            }
            catch (Exception ex)
            {
                running = false;
                RaiseServerError(ex);
            }
            finally
            {
                running = false;
                listener.Stop();

                LogServerClient[] remaining;
                lock (logLock)
                {
                    remaining = clients.ToArray();
                }
                foreach (LogServerClient client in remaining)
                {
                    client.Close();
                }
                await Task.WhenAll(remaining.Select(c => c.Completion)).ConfigureAwait(false);
            }
        }

        private void Accept(TcpClient tcp)
        {
            LogServerClient? client = null;
            lock (logLock)
            {
                if (running && clients.Count < maxClients)
                {
                    client = new LogServerClient(tcp, maxQueuedLines);
                    client.Send(greeting);
                    foreach (string line in logLines)
                    {
                        client.Send(line);
                    }
                    clients.Add(client);
                }
            }

            if (client == null)
            {
                _ = RefuseAsync(tcp);
                return;
            }

            ClientConnected?.Invoke(this, new LogClientEventArgs(client.Address));
            client.Start(OnClientClosed);
        }

        private static async Task RefuseAsync(TcpClient tcp)
        {
            try
            {
                NetworkStream stream = tcp.GetStream();
                await stream.WriteAsync(TooManyClientsMessage, 0, TooManyClientsMessage.Length).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                // The client went away first; nothing to tell it
            }
            finally
            {
                tcp.Dispose();
            }
        }

        private void OnClientClosed(LogServerClient client)
        {
            bool removed;
            lock (logLock)
            {
                removed = clients.Remove(client);
            }

            if (removed)
            {
                ClientDisconnected?.Invoke(this, new LogClientEventArgs(client.Address));
            }
        }

        private void RaiseServerError(Exception ex)
        {
            ServerError?.Invoke(this, new LogServerErrorEventArgs(ex));
        }
    }
}
