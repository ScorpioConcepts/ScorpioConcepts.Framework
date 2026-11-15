using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Net;

namespace ScorpioConcepts.Framework.LogServer
{
    /// <summary>
    /// Simple TCP/IP Log Server that can be connected to by any telnet type application to watch the log file
    /// </summary>
    public class LogServer
    {
        /// <summary>
        /// Delegate for the onClientConnect Event
        /// </summary>
        /// <param name="client">Address of Client that connected</param>
        public delegate void ClientConnectedEvent(string client);
        /// <summary>
        /// Delegate for the onClientDisconnect Event
        /// </summary>
        /// <param name="client">Address of Client that disconnected</param>
        public delegate void ClientDisconnectedEvent(string client);
        /// <summary>
        /// Delegate for the onServerException Event
        /// </summary>
        /// <param name="ex">Exception that occurred and was not handled</param>
        public delegate void LogServerExceptionEvent(Exception ex);

        /// <summary>
        /// Occurs when a new client connects to the server
        /// </summary>
        public event ClientConnectedEvent onClientConnect;
        /// <summary>
        /// Occurs when a client disconnects from the server
        /// </summary>
        public event ClientDisconnectedEvent onClientDisconnect;
        /// <summary>
        /// Occurs when a exception occurs in the log server and is not handled internally
        /// </summary>
        public event LogServerExceptionEvent onServerException;

        private TcpListener tcp_server;
        private Thread server_thread;
        private volatile bool running = false;

        // Only the server thread uses this list
        private List<LogServer_Client> clients;
        // Client threads report that they ended through this queue, and the server thread removes them
        private ConcurrentQueue<LogServer_Client> discon_clients;

        private string greeting;

        // Guards logLines and unsentLines, which Log() changes from the caller's thread
        private readonly object logLock = new object();
        private Queue<string> logLines;
        private Queue<string> unsentLines;

        private int maxLogSize;

        /// <summary>
        /// Create a standard log server on port 446
        /// </summary>
        /// <param name="greetingMessage">Greeting to send to clients when they connect</param>
        /// <param name="logSize">Number of lines to keep in memory and return initially</param>
        public LogServer(string greetingMessage, int logSize)
            : this(446, greetingMessage, logSize)
        { }

        /// <summary>
        /// Create a standard log server on a custom port
        /// </summary>
        /// <param name="port">The port to listen on</param>
        /// <param name="greetingMessage">Greeting to send to clients when they connect</param>
        /// <param name="logSize">Number of lines to keep in memory and return initially</param>
        public LogServer(int port, string greetingMessage, int logSize)
        {
            this.tcp_server = new TcpListener(IPAddress.Any, port);
            this.server_thread = new Thread(this._exec);
            this.clients = new List<LogServer_Client>();
            this.discon_clients = new ConcurrentQueue<LogServer_Client>();

            this.greeting = greetingMessage;
            this.logLines = new Queue<string>();
            this.unsentLines = new Queue<string>();
            this.maxLogSize = logSize;
        }

        /// <summary>
        /// Start the Log Server
        /// </summary>
        public void Start()
        {
            // Set this before the thread starts, so a Stop() straight after Start() is not undone by the new thread
            this.running = true;
            this.server_thread.Start();
        }

        /// <summary>
        /// Stop the Log Server
        /// </summary>
        public void Stop()
        {
            this.running = false;
        }

        /// <summary>
        /// Join the Log Server and wait for it to shutdown
        /// </summary>
        public void Join()
        {
            this.server_thread.Join();
        }

        /// <summary>
        /// Is the Log Server Active
        /// </summary>
        public bool Active
        {
            get { return this.running; }
        }

        /// <summary>
        /// Log a new line to the clients
        /// </summary>
        /// <param name="line">Any text to be sent to the Log Client</param>
        public void Log(string line)
        {
            lock (logLock)
            {
                this.logLines.Enqueue(line);
                if (this.logLines.Count > maxLogSize)
                {
                    this.logLines.Dequeue();
                }
                this.unsentLines.Enqueue(line);
            }
        }

        /// <summary>
        /// Returns a copy of the Log History
        /// </summary>
        public string[] LogHistory
        {
            get
            {
                lock (logLock)
                {
                    return this.logLines.ToArray();
                }
            }
        }

        private void _exec()
        {
            try
            {
                this.tcp_server.Start();

                while (this.running == true)
                {
                    while (this.tcp_server.Pending())
                    {
                        TcpClient client = this.tcp_server.AcceptTcpClient();

                        LogServer_Client cl = new LogServer_Client(client);
                        cl.onThreadEnd += new LogServer_Client.ThreadEnd(cl_onThreadEnd);

                        lock (logLock)
                        {
                            // Deliver pending lines to the existing clients first. Otherwise a line logged just
                            // before this client connected would reach it twice: in the history and again as new.
                            sendUnsentLines();

                            cl.Send(greeting);
                            foreach (string line in this.logLines)
                            {
                                cl.Send(line);
                            }
                            this.clients.Add(cl);
                        }

                        cl.Start();
                        if (this.onClientConnect != null)
                        {
                            this.onClientConnect(cl.Address);
                        }
                    }

                    lock (logLock)
                    {
                        sendUnsentLines();
                    }

                    LogServer_Client disconnected;
                    while (discon_clients.TryDequeue(out disconnected))
                    {
                        clients.Remove(disconnected);
                    }

                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                if (this.onServerException != null)
                {
                    this.onServerException(ex);
                }
            }
            finally
            {
                foreach (LogServer_Client cl in this.clients)
                {
                    cl.Stop();
                    cl.Join();
                }

                this.tcp_server.Stop();
                this.running = false;
            }
        }

        /// <summary>
        /// Queue every unsent line on every client. Must be called while holding logLock.
        /// </summary>
        private void sendUnsentLines()
        {
            foreach (string line in this.unsentLines)
            {
                foreach (LogServer_Client cl in this.clients)
                {
                    cl.Send(line);
                }
            }

            this.unsentLines.Clear();
        }

        private void cl_onThreadEnd(LogServer_Client sender)
        {
            // Runs on the client's thread, so hand the client over to the server thread for removal
            discon_clients.Enqueue(sender);
            if (this.onClientDisconnect != null)
            {
                this.onClientDisconnect(sender.Address);
            }
        }
    }
}
