using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Net.Sockets;

namespace ScorpioConcepts.Framework.LogServer
{
    internal class LogServer_Client
    {
        internal delegate void ThreadEnd(LogServer_Client sender);

        private Thread client_thread;
        private TcpClient client;
        private NetworkStream stream;
        private string address;
        private volatile bool terminate = false;

        // Guards unsentLines, which the server thread fills through Send()
        private readonly object sendLock = new object();
        private Queue<string> unsentLines;

        internal event ThreadEnd onThreadEnd;

        internal LogServer_Client(TcpClient client)
        {
            this.client = client;
            this.client_thread = new Thread(this._exec);
            this.stream = client.GetStream();
            this.address = client.Client.RemoteEndPoint.ToString();
            this.unsentLines = new Queue<string>();
        }

        internal void Start()
        {
            this.client_thread.Start();
        }
        internal void Stop()
        {
            this.terminate = true;
        }
        internal void Join()
        {
            this.client_thread.Join(10000);
        }

        private void _exec()
        {
            byte[] buffer = new byte[1024 * 32];

            while (this.terminate == false)
            {
                try
                {
                    if (this.client.Client.Poll(100, SelectMode.SelectRead) == true)
                    {
                        // Anything the client sends is discarded; it is only read to drain the socket.
                        // Readable with no data, or a zero-byte read, means the client has disconnected.
                        if ((this.stream.DataAvailable == false) || (this.stream.Read(buffer, 0, buffer.Length) == 0))
                        {
                            this.terminate = true;
                            break;
                        }
                    }

                    string[] lines = null;
                    lock (sendLock)
                    {
                        if (this.unsentLines.Count > 0)
                        {
                            lines = this.unsentLines.ToArray();
                            this.unsentLines.Clear();
                        }
                    }

                    if (lines != null)
                    {
                        foreach (string line in lines)
                        {
                            byte[] lineData = Encoding.ASCII.GetBytes(line + "\r\n");
                            this.stream.Write(lineData, 0, lineData.Length);
                        }
                    }

                    Thread.Sleep(50);
                }
                catch
                {
                    // A failed read or write means the connection is broken, and it will not recover
                    this.terminate = true;
                }
            }

            if (this.onThreadEnd != null)
            {
                this.onThreadEnd(this);
            }

            this.stream.Close(10);
            this.client.Close();
        }

        /// <summary>
        /// The remote client address
        /// </summary>
        public string Address
        {
            get { return this.address; }
        }

        /// <summary>
        /// Is this connection still active
        /// </summary>
        public bool Active
        {
            get { return !this.terminate; }
        }

        /// <summary>
        /// Queue up a line to send to the client
        /// </summary>
        /// <param name="line"></param>
        internal void Send(string line)
        {
            lock (sendLock)
            {
                this.unsentLines.Enqueue(line);
            }
        }
    }
}
