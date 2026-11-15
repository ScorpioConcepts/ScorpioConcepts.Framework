using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScorpioConcepts.Framework.LogServer
{
    /// <summary>
    /// One connected log viewer. Lines are queued by <see cref="Send"/> and written by an async loop that
    /// sleeps until there is something to send; a second loop only watches for the client disconnecting.
    /// </summary>
    internal sealed class LogServerClient : IDisposable
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly TcpClient tcp;
        private readonly NetworkStream stream;
        private readonly int maxQueuedLines;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();

        // Guards pending, skipped, wake and closed
        private readonly object queueLock = new object();
        private readonly Queue<string> pending = new Queue<string>();
        private int skipped;
        private TaskCompletionSource<bool>? wake;
        private bool closed;

        private Task completion = Task.CompletedTask;

        internal LogServerClient(TcpClient tcp, int maxQueuedLines)
        {
            this.tcp = tcp;
            this.stream = tcp.GetStream();
            this.maxQueuedLines = maxQueuedLines;
            this.Address = tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }

        /// <summary>The remote client's address and port</summary>
        internal string Address { get; }

        /// <summary>Lines waiting to be written; never more than the queue limit</summary>
        internal int QueuedLines
        {
            get
            {
                lock (queueLock)
                {
                    return pending.Count;
                }
            }
        }

        /// <summary>Completes once both loops have ended and the connection is closed</summary>
        internal Task Completion => completion;

        /// <summary>
        /// Queue a line for this client. When the client is not keeping up and the queue is full,
        /// the oldest line is dropped, and the client is told how many it missed.
        /// </summary>
        internal void Send(string line)
        {
            TaskCompletionSource<bool>? toWake;
            lock (queueLock)
            {
                if (closed) return;

                pending.Enqueue(line);
                if (pending.Count > maxQueuedLines)
                {
                    pending.Dequeue();
                    skipped++;
                }
                toWake = wake;
                wake = null;
            }

            toWake?.TrySetResult(true);
        }

        /// <summary>Start the read and write loops; <paramref name="onClosed"/> runs once when the client is gone</summary>
        internal void Start(Action<LogServerClient> onClosed)
        {
            completion = RunAsync(onClosed);
        }

        /// <summary>Close the connection. Lines still queued are dropped.</summary>
        internal void Close()
        {
            TaskCompletionSource<bool>? toWake;
            lock (queueLock)
            {
                if (closed) return;
                closed = true;
                toWake = wake;
                wake = null;
            }

            stop.Cancel();
            toWake?.TrySetResult(false);
            // Closing the socket also ends a pending read on runtimes that ignore the cancellation token
            tcp.Dispose();
        }

        private async Task RunAsync(Action<LogServerClient> onClosed)
        {
            Task reader = ReadUntilClosedAsync();
            Task writer = WriteAsync();
            await Task.WhenAny(reader, writer).ConfigureAwait(false);

            // Whichever loop ended first, the connection is finished
            Close();
            await Task.WhenAll(reader, writer).ConfigureAwait(false);
            Dispose();
            onClosed(this);
        }

        /// <summary>Close the connection and release its resources</summary>
        public void Dispose()
        {
            Close();
            stop.Dispose();
        }

        private async Task ReadUntilClosedAsync()
        {
            // Anything the client types is ignored; reading only tells us when it disconnects
            byte[] buffer = new byte[1024];
            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, stop.Token).ConfigureAwait(false);
                    if (read == 0) return;
                }
            }
            catch (Exception ex) when (IsConnectionEnd(ex))
            {
            }
        }

        private async Task WriteAsync()
        {
            try
            {
                while (true)
                {
                    string[]? lines = null;
                    int skippedLines = 0;
                    Task? waitForLines = null;
                    lock (queueLock)
                    {
                        if (closed) return;
                        if (pending.Count == 0 && skipped == 0)
                        {
                            wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            waitForLines = wake.Task;
                        }
                        else
                        {
                            lines = pending.ToArray();
                            pending.Clear();
                            skippedLines = skipped;
                            skipped = 0;
                        }
                    }

                    if (waitForLines != null)
                    {
                        await waitForLines.ConfigureAwait(false);
                        continue;
                    }

                    StringBuilder text = new StringBuilder();
                    if (skippedLines > 0)
                    {
                        text.Append("*** ").Append(skippedLines.ToString(CultureInfo.InvariantCulture))
                            .Append(" lines skipped because this client was not keeping up ***\r\n");
                    }
                    foreach (string line in lines!)
                    {
                        text.Append(line).Append("\r\n");
                    }

                    byte[] data = Utf8.GetBytes(text.ToString());
                    await stream.WriteAsync(data, 0, data.Length, stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsConnectionEnd(ex))
            {
            }
        }

        private static bool IsConnectionEnd(Exception ex)
        {
            return ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is OperationCanceledException;
        }
    }
}
