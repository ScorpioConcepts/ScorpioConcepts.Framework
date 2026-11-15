using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace ScorpioConcepts.Framework.netNUTClient
{
    /// <summary>
    /// A simple client for communicating with a UPSD daemon server
    /// </summary>
    public class UPSDClient : IDisposable
    {
        private string upsdServer;
        private string upsdHostname;
        private UInt16 upsdPort;
        private bool connected = false;
        private bool loggedin = false;
        private TcpClient client;
        private NetworkStream clientStream;

        /// <summary>
        /// Are we connected to the UPSD server
        /// </summary>
        public bool Connected { get { return this.connected; } }
        /// <summary>
        /// Are we logged in to a UPS on the UPSD server (see <see cref="Login(string)"/>)
        /// </summary>
        public bool LoggedIn { get { return this.loggedin; } }
        /// <summary>
        /// How long to wait for the UPSD server to reply to a command before giving up with a <see cref="TimeoutException"/>.
        /// The default is 10 seconds. After a timeout the connection is closed, because a late reply would otherwise be
        /// read as the reply to the next command; call <see cref="Connect"/> again to continue.
        /// </summary>
        public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Create a new client connected to the specified address
        /// </summary>
        /// <param name="upsdServer">The address of the UPSD server in the format &lt;hostname&gt;[:&lt;port&gt;]</param>
        public UPSDClient(string upsdServer)
        {
            this.upsdServer = upsdServer;
            string[] parts = upsdServer.Split(new char[] {':'}, StringSplitOptions.RemoveEmptyEntries);
            this.upsdHostname = parts[0];
            this.upsdPort = 3493;
            if( parts.Count() > 1)
            {
                if (UInt16.TryParse(parts[1], out this.upsdPort) == false)
                {
                    throw new ArgumentOutOfRangeException("upsdServer", "Error: Invalid port.");
                }
            }
        }
        /// <summary>
        /// Create a new client connected to the specified address and port
        /// </summary>
        /// <param name="upsdHostname">IP address or host name</param>
        /// <param name="upsdPort">(Optional) TCP Port UPSD is listening on (default is 3493)</param>
        public UPSDClient(string upsdHostname, UInt16 upsdPort)
        {
            this.upsdServer = upsdHostname + ":" + upsdPort.ToString();
            this.upsdHostname = upsdHostname;
            this.upsdPort = upsdPort;
        }
        /// <summary>
        /// Finalizer to make sure the network connection is closed
        /// </summary>
        ~UPSDClient()
        {
            // Only release the socket here: talking to the server from the finalizer thread
            // could block it, and an exception thrown here would end the process.
            try
            {
                if (this.client != null)
                {
                    this.client.Close();
                }
            }
            catch
            {
            }
        }
        /// <summary>
        /// Disconnect from the UPSD server and release the connection
        /// </summary>
        public void Dispose()
        {
            this.Disconnect();
            GC.SuppressFinalize(this);
        }
        /// <summary>
        /// Connect to the UPSD daemon
        /// </summary>
        public void Connect()
        {
            if (connected) return;

            this.client = new TcpClient(this.upsdHostname, this.upsdPort);
            this.clientStream = this.client.GetStream();
            this.connected = true;
        }
        /// <summary>
        /// Disconnect from the UPSD daemon, logging out first if <see cref="Login(string)"/> was used
        /// </summary>
        public void Disconnect()
        {
            if (!connected) return;

            try
            {
                if (loggedin)
                {
                    this.Logout();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is TimeoutException || ex is UPSException)
            {
                // The connection is being closed anyway, so a failed LOGOUT does not matter
            }
            finally
            {
                this.clientStream.Close();
                this.client.Close();
                this.connected = false;
                this.loggedin = false;
            }
        }
        /// <summary>
        /// Get a list of the UPS's configured on the server
        /// </summary>
        /// <returns>List of <see cref="UPS"/> on the server</returns>
        public List<UPS> ListUPS()
        {
            List<UPS> list = new List<UPS>();
            foreach (string[] upsParts in ListItems("UPS", "UPS", 3, null))
            {
                UPS temp = new UPS(upsParts[1]);
                temp.Description = upsParts[2].Replace("\"", "");
                list.Add(temp);
            }

            return list;
        }
        /// <summary>
        /// Get a list of the variables and their values for the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <returns>Dictionary of the variable names(keys) and their current values</returns>
        public Dictionary<string, string> ListUPSVar(string upsName)
        {
            Dictionary<string, string> list = new Dictionary<string, string>();
            foreach (string[] varParts in ListItems("VAR " + upsName, "VAR", 4, upsName))
            {
                list.Add(varParts[2], varParts[3].Replace("\"", ""));
            }

            return list;
        }
        /// <summary>
        /// Get a list of the read/write variables available on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <returns>A List of <see cref="UPS.VariableDescription"/> entries describing the available variables</returns>
        public List<UPS.VariableDescription> ListUPSReadWrite(string upsName)
        {
            List<UPS.VariableDescription> list = new List<UPS.VariableDescription>();
            foreach (string[] varParts in ListItems("RW " + upsName, "RW", 4, upsName))
            {
                string desc = this.GetUPSVarDescription(upsName, varParts[2]);
                string type = this.GetUPSVarType(upsName, varParts[2]).Replace("RW ", "");
                list.Add(new UPS.VariableDescription(varParts[2], desc, type, varParts[3].Replace("\"", "")));
            }

            return list;
        }
        /// <summary>
        /// Get a list of the instant commands available on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <returns>Dictionary of the instant command names(keys) and their descriptions</returns>
        public Dictionary<string, string> ListUPSCommands(string upsName)
        {
            Dictionary<string, string> list = new Dictionary<string, string>();
            foreach (string[] cmdParts in ListItems("CMD " + upsName, "CMD", 3, upsName))
            {
                list.Add(cmdParts[2], this.GetUPSCmdDescription(upsName, cmdParts[2]));
            }

            return list;
        }
        /// <summary>
        /// Get a list of the network clients connected to the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <returns>A list of IP Addresses for all currently connected clients</returns>
        public List<string> ListUPSClient(string upsName)
        {
            return ListItems("CLIENT " + upsName, "CLIENT", 3, upsName)
                .Select(parts => parts[2])
                .ToList();
        }
        /// <summary>
        /// Get the value of a specific variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.status')</param>
        /// <returns>The value of the requested variable as a string</returns>
        public string GetUPSVar(string upsName, string varName)
        {
            return GetValue("GET VAR " + upsName + " " + varName, "VAR " + upsName + " " + varName, true);
        }
        /// <summary>
        /// Gets the data type of a specific variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.status')</param>
        /// <returns></returns>
        public string GetUPSVarType(string upsName, string varName)
        {
            return GetValue("GET TYPE " + upsName + " " + varName, "TYPE " + upsName + " " + varName, false);
        }
        /// <summary>
        /// Gets the description of the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <returns>The description of the UPS as in ups.conf on the server</returns>
        public string GetUPSDescription(string upsName)
        {
            return GetValue("GET UPSDESC " + upsName, "UPSDESC " + upsName, true);
        }
        /// <summary>
        /// Gets the description of a specific variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.status')</param>
        /// <returns></returns>
        public string GetUPSVarDescription(string upsName, string varName)
        {
            return GetValue("GET DESC " + upsName + " " + varName, "DESC " + upsName + " " + varName, true);
        }
        /// <summary>
        /// Gets the description of a specific instant command on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="cmdName">Name of the instant command (for example 'load.off')</param>
        /// <returns></returns>
        public string GetUPSCmdDescription(string upsName, string cmdName)
        {
            return GetValue("GET CMDDESC " + upsName + " " + cmdName, "CMDDESC " + upsName + " " + cmdName, true);
        }
        /// <summary>
        /// Sets the Username for the connection that will be used for any command that require it
        /// </summary>
        /// <param name="authUser">Username to use</param>
        /// <returns>True if username is accepted, False if an error occurs</returns>
        public bool SetUsername(string authUser)
        {
            return SendCommand("USERNAME " + authUser, IsLineComplete) == "OK\n";
        }
        /// <summary>
        /// Sets the Password for the connection that will be used for any command that require it
        /// </summary>
        /// <param name="authPass">Password to use</param>
        /// <returns>True if password is accepted, False if an error occurs</returns>
        public bool SetPassword(string authPass)
        {
            return SendCommand("PASSWORD " + authPass, IsLineComplete) == "OK\n";
        }
        /// <summary>
        /// Set the value of a writable variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.id')</param>
        /// <param name="newValue">Value to set to</param>
        /// <returns>True if the variable change is accepted, False if an error occurs</returns>
        public bool SetUPSVariable(string upsName, string varName, string newValue)
        {
            return SendCommand("SET VAR " + upsName + " " + varName + " \"" + newValue + "\"", IsLineComplete) == "OK\n";
        }
        /// <summary>
        /// Notify the upsd server that this machine is drawing power from the specified UPS.
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <returns>True if successful, or False if an error occured</returns>
        public bool Login(string upsName)
        {
            bool ok = SendCommand("LOGIN " + upsName, IsLineComplete) == "OK\n";
            if (ok)
            {
                this.loggedin = true;
            }

            return ok;
        }
        /// <summary>
        /// Notify the upsd server that we are disconnecting
        /// </summary>
        /// <returns>True if successful, or False if an error occured</returns>
        public bool Logout()
        {
            string reply = SendCommand("LOGOUT", IsLineComplete);
            bool ok = (reply == "OK Goodbye\n") || (reply == "Goodbye...\n");
            if (ok)
            {
                this.loggedin = false;
            }

            return ok;
        }
        /// <summary>
        /// Perform and instant command on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="command">Instant Command to execute</param>
        /// <returns>True if the instant command is accepted, False if an error occurs</returns>
        public bool InstantCommand(string upsName, string command)
        {
            return InstantCommand(upsName, command, null);
        }
        /// <summary>
        /// Perform and instant command on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="command">Instant Command to execute</param>
        /// <param name="addParam">(Optional)Additional parameter to the instant command</param>
        /// <returns>True if the instant command is accepted, False if an error occurs</returns>
        public bool InstantCommand(string upsName, string command, string addParam)
        {
            string cmd = "INSTCMD " + upsName + " " + command;
            if(String.IsNullOrEmpty(addParam) == false)
            {
                cmd += " \"" + addParam + "\"";
            }

            return SendCommand(cmd, IsLineComplete) == "OK\n";
        }

        private static bool IsLineComplete(string reply)
        {
            return reply.EndsWith("\n");
        }

        /// <summary>
        /// Send a GET-style command and return the value from a single line reply of the form "&lt;prefix&gt; &lt;value&gt;"
        /// </summary>
        /// <returns>The value, or an empty string if the server closed the connection or sent a reply for something else</returns>
        private string GetValue(string command, string replyPrefix, bool removeQuotes)
        {
            string reply = SendCommand(command, IsLineComplete);
            if ((reply == null) || (reply.StartsWith(replyPrefix + " ") == false))
            {
                return string.Empty;
            }

            string value = reply.Substring(replyPrefix.Length + 1);
            if (removeQuotes)
            {
                value = value.Replace("\"", "");
            }

            return value.Trim();
        }

        /// <summary>
        /// Send "LIST &lt;listName&gt;" and return the fields of every item line of the given type
        /// </summary>
        /// <param name="listName">What to list, for example "VAR myups"</param>
        /// <param name="itemType">The first word of each item line, for example "VAR"</param>
        /// <param name="fieldCount">How many space separated fields an item line has; the last one takes the rest of the line</param>
        /// <param name="upsName">If given, only items for this UPS are returned</param>
        private List<string[]> ListItems(string listName, string itemType, int fieldCount, string upsName)
        {
            string begin = "BEGIN LIST " + listName + "\n";
            string end = "END LIST " + listName + "\n";

            // The list is complete at its END line. A reply that does not start a list is complete after one line.
            string reply = SendCommand("LIST " + listName, r =>
                r.EndsWith(end) || ((begin.StartsWith(r) == false) && (r.StartsWith(begin) == false) && r.EndsWith("\n")));

            List<string[]> items = new List<string[]>();
            if ((reply == null) || (reply.StartsWith(begin) == false))
            {
                return items;
            }

            string[] lines = reply.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int idx = 1; idx < lines.Length - 1; idx++)
            {
                string[] parts = lines[idx].Split(new char[] { ' ' }, fieldCount, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != fieldCount) continue;
                if (parts[0] != itemType) continue;
                if ((upsName != null) && (parts[1] != upsName)) continue;

                items.Add(parts);
            }

            return items;
        }

        /// <summary>
        /// Send a command and read the reply until <paramref name="isComplete"/> says it is complete
        /// </summary>
        /// <returns>The reply, or null if the server closed the connection before replying</returns>
        /// <remarks>
        /// If the reply times out, the server closes the connection, or the connection fails, the connection is
        /// dropped and <see cref="Connected"/> becomes false. A reply that arrives late can then never be read as
        /// the reply to a later command; call <see cref="Connect"/> to start a fresh connection.
        /// </remarks>
        /// <exception cref="UPSException">The server replied with an error</exception>
        /// <exception cref="TimeoutException">No complete reply arrived within <see cref="ReplyTimeout"/></exception>
        private string SendCommand(string command, Func<string, bool> isComplete)
        {
            if (connected == false)
            {
                throw new InvalidOperationException("You have to connect by calling Connect() first!");
            }

            try
            {
                byte[] data = Encoding.ASCII.GetBytes(command + "\n");
                clientStream.Write(data, 0, data.Length);

                StringBuilder reply = new StringBuilder();
                byte[] buffer = new byte[1024];
                Stopwatch timer = Stopwatch.StartNew();
                while (true)
                {
                    string text = reply.ToString();
                    if (text.StartsWith("ERR ") && text.EndsWith("\n"))
                    {
                        // A complete error reply: the stream is still in step, so the connection stays usable
                        HandleUPSDError(text);
                    }
                    if ((text.Length > 0) && isComplete(text))
                    {
                        return text;
                    }

                    TimeSpan remaining = ReplyTimeout - timer.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        // The reply may still arrive, and would then be read as the reply to the next command
                        DropConnection();

                        // Only name the command, never its arguments: PASSWORD would otherwise end up in logs
                        string commandName = command.Split(' ')[0];
                        throw new TimeoutException("No complete reply to " + commandName + " from " + upsdServer + " within " + ReplyTimeout.ToString() + "; the connection has been closed");
                    }

                    int waitMicroseconds = (int)Math.Min(remaining.TotalMilliseconds * 1000, 100000);
                    if (client.Client.Poll(waitMicroseconds, SelectMode.SelectRead))
                    {
                        // Readable with nothing to read means the server closed the connection
                        int bytesRead = clientStream.Read(buffer, 0, buffer.Length);
                        if (bytesRead == 0)
                        {
                            DropConnection();
                            return null;
                        }
                        reply.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                // A failed read or write leaves the stream in an unknown state
                DropConnection();
                throw;
            }
        }

        /// <summary>
        /// Close the connection without talking to the server, because it can no longer be trusted
        /// </summary>
        private void DropConnection()
        {
            this.connected = false;
            this.loggedin = false;
            try
            {
                this.clientStream.Close();
                this.client.Close();
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Handles all error replies from UPSD daemon and generates exceptions
        /// </summary>
        /// <param name="reply">Error reply received from server</param>
        private void HandleUPSDError(string reply)
        {
            string[] errParts = reply.TrimEnd('\r', '\n').Split(new char[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (errParts[0] != "ERR") throw new ArgumentException("Not a valid UPSD Error Message!", "reply");
            if (errParts.Length < 2) throw new ArgumentException("Not a valid UPSD Error Message!", "reply");

            UPSException.ErrorCode code;
            if (Enum.TryParse<UPSException.ErrorCode>(errParts[1].Replace('-', '_'), out code) == false)
            {
                throw new UPSException(UPSException.ErrorCode.UNKNOWN_ERROR, errParts[1] + " is not a recognized UPSD Error Code");
            }
            if (errParts.Length == 3)
            {
                throw new UPSException(code, errParts[2]);
            }

            throw new UPSException(code, string.Empty);
        }
    }
}
