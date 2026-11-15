using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScorpioConcepts.Framework.netNUTClient
{
    /// <summary>
    /// A client for upsd, the network server of Network UPS Tools (NUT).
    /// </summary>
    /// <remarks>
    /// Every command has a synchronous and an async version. One client can be used from several threads:
    /// commands are sent one at a time, each waiting for the previous one's reply.
    /// </remarks>
    public class UPSDClient : IDisposable
    {
        private readonly string upsdServer;
        private readonly string upsdHostname;
        private readonly ushort upsdPort;

        // One command at a time: upsd answers in order, so interleaved commands would get each other's replies
        private readonly SemaphoreSlim commandLock = new SemaphoreSlim(1, 1);

        private TcpClient? client;
        private Stream? stream;
        private volatile bool connected;
        private volatile bool loggedin;
        private volatile bool encrypted;

        /// <summary>
        /// Are we connected to the UPSD server
        /// </summary>
        public bool Connected { get { return this.connected; } }
        /// <summary>
        /// Are we logged in to a UPS on the UPSD server (see <see cref="Login(string)"/>)
        /// </summary>
        public bool LoggedIn { get { return this.loggedin; } }
        /// <summary>
        /// Is the connection encrypted with TLS (see <see cref="StartTls(string)"/>)
        /// </summary>
        public bool IsEncrypted { get { return this.encrypted; } }
        /// <summary>
        /// How long to wait for the UPSD server to reply to a command before giving up with a <see cref="TimeoutException"/>.
        /// The default is 10 seconds. After a timeout the connection is closed, because a late reply would otherwise be
        /// read as the reply to the next command; call <see cref="Connect"/> again to continue.
        /// </summary>
        public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>
        /// How long <see cref="Connect"/> waits for the server to accept the connection before giving up with a
        /// <see cref="TimeoutException"/>. The default is 10 seconds.
        /// </summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>
        /// Checks the server's certificate when <see cref="StartTls(string)"/> is used. When not set, the certificate
        /// must be trusted by this machine and match the host name. Set this to accept a certificate this machine does
        /// not trust, such as a self-signed one, by checking its thumbprint.
        /// </summary>
        public RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }

        /// <summary>
        /// Create a new client for the specified server
        /// </summary>
        /// <param name="upsdServer">The address of the UPSD server: "host", "host:port", an IPv6 address such as "::1",
        /// or an IPv6 address with a port such as "[::1]:3493". The default port is 3493.</param>
        /// <exception cref="ArgumentException">The address is empty or malformed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The port is not a number from 1 to 65535.</exception>
        public UPSDClient(string upsdServer)
        {
            NutProtocol.ParseServer(upsdServer, nameof(upsdServer), out this.upsdHostname, out this.upsdPort);
            this.upsdServer = upsdServer;
        }
        /// <summary>
        /// Create a new client for the specified host and port
        /// </summary>
        /// <param name="upsdHostname">IP address or host name</param>
        /// <param name="upsdPort">TCP Port UPSD is listening on (normally 3493)</param>
        public UPSDClient(string upsdHostname, ushort upsdPort)
        {
            if (string.IsNullOrWhiteSpace(upsdHostname))
            {
                throw new ArgumentException("The host name cannot be empty.", nameof(upsdHostname));
            }
            this.upsdHostname = upsdHostname;
            this.upsdPort = upsdPort;
            this.upsdServer = (upsdHostname.IndexOf(':') >= 0 ? "[" + upsdHostname + "]" : upsdHostname)
                + ":" + upsdPort.ToString(CultureInfo.InvariantCulture);
        }
        /// <summary>
        /// Finalizer to make sure the network connection is closed
        /// </summary>
        ~UPSDClient()
        {
            Dispose(false);
        }
        /// <summary>
        /// Disconnect from the UPSD server and release the connection
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        /// <summary>
        /// Disconnect from the UPSD server and release the connection
        /// </summary>
        /// <param name="disposing">True when called from <see cref="Dispose()"/>, false from the finalizer</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.Disconnect();
            }
            else
            {
                // Only release the socket here: talking to the server from the finalizer thread could block it
                DropConnection();
            }
        }

        /// <summary>
        /// Connect to the UPSD daemon
        /// </summary>
        /// <exception cref="TimeoutException">The server did not accept the connection within <see cref="ConnectTimeout"/>.</exception>
        public void Connect()
        {
            ConnectAsync().GetAwaiter().GetResult();
        }
        /// <summary>
        /// Connect to the UPSD daemon
        /// </summary>
        /// <exception cref="TimeoutException">The server did not accept the connection within <see cref="ConnectTimeout"/>.</exception>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            await commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (connected) return;

                TcpClient tcp = new TcpClient();
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(ConnectTimeout);
                    try
                    {
#if NET
                        await tcp.ConnectAsync(this.upsdHostname, this.upsdPort, timeout.Token).ConfigureAwait(false);
#else
                        // ConnectAsync has no cancellation here, so closing the socket is what ends a pending connect
                        using (timeout.Token.Register(() => tcp.Dispose()))
                        {
                            await tcp.ConnectAsync(this.upsdHostname, this.upsdPort).ConfigureAwait(false);
                        }
                        timeout.Token.ThrowIfCancellationRequested();
#endif
                    }
                    catch (Exception ex) when (timeout.IsCancellationRequested && !(ex is ArgumentException))
                    {
                        tcp.Dispose();
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException("Connecting to " + upsdServer + " was canceled.", ex, cancellationToken);
                        }
                        throw new TimeoutException("Could not connect to " + upsdServer + " within " + ConnectTimeout.ToString() + ".", ex);
                    }
                    catch
                    {
                        tcp.Dispose();
                        throw;
                    }
                }

                this.client = tcp;
                this.stream = tcp.GetStream();
                this.encrypted = false;
                this.loggedin = false;
                this.connected = true;
            }
            finally
            {
                commandLock.Release();
            }
        }

        /// <summary>
        /// Encrypt the connection with TLS, using the protocol's STARTTLS command. Call this straight after
        /// <see cref="Connect"/>, before sending a password. The server must have a certificate configured.
        /// </summary>
        /// <param name="targetHost">The host name the server's certificate must match. Defaults to the host connected to.</param>
        /// <exception cref="UPSException">The server refused, for example with FEATURE-NOT-CONFIGURED when it has no certificate.
        /// The connection is still usable, unencrypted.</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">The server's certificate was not accepted.
        /// The connection is closed.</exception>
        public void StartTls(string? targetHost = null)
        {
            StartTlsAsync(targetHost).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Encrypt the connection with TLS, using the protocol's STARTTLS command. Call this straight after
        /// <see cref="ConnectAsync"/>, before sending a password. The server must have a certificate configured.
        /// </summary>
        /// <param name="targetHost">The host name the server's certificate must match. Defaults to the host connected to.</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <exception cref="UPSException">The server refused, for example with FEATURE-NOT-CONFIGURED when it has no certificate.
        /// The connection is still usable, unencrypted.</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">The server's certificate was not accepted.
        /// The connection is closed.</exception>
        public async Task StartTlsAsync(string? targetHost = null, CancellationToken cancellationToken = default)
        {
            await commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (encrypted)
                {
                    throw new InvalidOperationException("The connection is already encrypted.");
                }

                string? reply = await SendLockedAsync("STARTTLS", IsLineComplete, cancellationToken).ConfigureAwait(false);
                if (reply == null)
                {
                    throw new IOException("The server closed the connection instead of starting TLS.");
                }
                if (reply != "OK STARTTLS\n")
                {
                    DropConnection();
                    throw new IOException("The server did not start TLS. It replied: " + reply.TrimEnd('\r', '\n'));
                }

                SslStream ssl = new SslStream(this.stream!, false, CertificateValidationCallback);
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(ReplyTimeout);
                    try
                    {
#if NET
                        SslClientAuthenticationOptions options = new SslClientAuthenticationOptions
                        {
                            TargetHost = targetHost ?? this.upsdHostname,
                            RemoteCertificateValidationCallback = CertificateValidationCallback,
                        };
                        await ssl.AuthenticateAsClientAsync(options, timeout.Token).ConfigureAwait(false);
#else
                        using (timeout.Token.Register(DropConnection))
                        {
                            await ssl.AuthenticateAsClientAsync(targetHost ?? this.upsdHostname).ConfigureAwait(false);
                        }
                        timeout.Token.ThrowIfCancellationRequested();
#endif
                    }
                    catch (Exception ex)
                    {
                        // Half a TLS handshake leaves nothing usable behind
                        ssl.Dispose();
                        DropConnection();
                        if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException("The TLS handshake with " + upsdServer + " did not finish within " + ReplyTimeout.ToString() + ".", ex);
                        }
                        throw;
                    }
                }

                this.stream = ssl;
                this.encrypted = true;
            }
            finally
            {
                commandLock.Release();
            }
        }

        /// <summary>
        /// Disconnect from the UPSD daemon, logging out first if <see cref="Login(string)"/> was used
        /// </summary>
        public void Disconnect()
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        /// <summary>
        /// Disconnect from the UPSD daemon, logging out first if <see cref="LoginAsync"/> was used
        /// </summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (!connected) return;

            try
            {
                if (loggedin)
                {
                    await LogoutAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is TimeoutException
                || ex is UPSException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // The connection is being closed anyway, so a failed LOGOUT does not matter
            }
            finally
            {
                DropConnection();
            }
        }

        /// <summary>
        /// Get a list of the UPS's configured on the server
        /// </summary>
        /// <returns>List of <see cref="UPS"/> on the server</returns>
        public List<UPS> ListUPS()
        {
            return ListUPSAsync().GetAwaiter().GetResult();
        }
        /// <summary>
        /// Get a list of the UPS's configured on the server
        /// </summary>
        /// <returns>List of <see cref="UPS"/> on the server</returns>
        public async Task<List<UPS>> ListUPSAsync(CancellationToken cancellationToken = default)
        {
            List<UPS> list = new List<UPS>();
            foreach (List<string> item in await ListItemsAsync("UPS", "UPS", 3, null, cancellationToken).ConfigureAwait(false))
            {
                list.Add(new UPS(item[1]) { Description = item[2] });
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
            return ListUPSVarAsync(upsName).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Get a list of the variables and their values for the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>Dictionary of the variable names(keys) and their current values</returns>
        public async Task<Dictionary<string, string>> ListUPSVarAsync(string upsName, CancellationToken cancellationToken = default)
        {
            NutProtocol.Word(upsName, nameof(upsName));
            Dictionary<string, string> list = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (List<string> item in await ListItemsAsync("VAR " + upsName, "VAR", 4, upsName, cancellationToken).ConfigureAwait(false))
            {
                list[item[2]] = item[3];
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
            return ListUPSReadWriteAsync(upsName).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Get a list of the read/write variables available on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>A List of <see cref="UPS.VariableDescription"/> entries describing the available variables</returns>
        public async Task<List<UPS.VariableDescription>> ListUPSReadWriteAsync(string upsName, CancellationToken cancellationToken = default)
        {
            NutProtocol.Word(upsName, nameof(upsName));
            List<UPS.VariableDescription> list = new List<UPS.VariableDescription>();
            foreach (List<string> item in await ListItemsAsync("RW " + upsName, "RW", 4, upsName, cancellationToken).ConfigureAwait(false))
            {
                string desc = await GetUPSVarDescriptionAsync(upsName, item[2], cancellationToken).ConfigureAwait(false);
                string type = await GetUPSVarTypeAsync(upsName, item[2], cancellationToken).ConfigureAwait(false);
                if (type.StartsWith("RW ", StringComparison.Ordinal))
                {
                    type = type.Substring(3);
                }
                list.Add(new UPS.VariableDescription(item[2], desc, type, item[3]));
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
            return ListUPSCommandsAsync(upsName).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Get a list of the instant commands available on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>Dictionary of the instant command names(keys) and their descriptions</returns>
        public async Task<Dictionary<string, string>> ListUPSCommandsAsync(string upsName, CancellationToken cancellationToken = default)
        {
            NutProtocol.Word(upsName, nameof(upsName));
            Dictionary<string, string> list = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (List<string> item in await ListItemsAsync("CMD " + upsName, "CMD", 3, upsName, cancellationToken).ConfigureAwait(false))
            {
                list[item[2]] = await GetUPSCmdDescriptionAsync(upsName, item[2], cancellationToken).ConfigureAwait(false);
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
            return ListUPSClientAsync(upsName).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Get a list of the network clients connected to the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>A list of IP Addresses for all currently connected clients</returns>
        public async Task<List<string>> ListUPSClientAsync(string upsName, CancellationToken cancellationToken = default)
        {
            NutProtocol.Word(upsName, nameof(upsName));
            List<string> list = new List<string>();
            foreach (List<string> item in await ListItemsAsync("CLIENT " + upsName, "CLIENT", 3, upsName, cancellationToken).ConfigureAwait(false))
            {
                list.Add(item[2]);
            }

            return list;
        }

        /// <summary>
        /// Get the value of a specific variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.status')</param>
        /// <returns>The value of the requested variable as a string</returns>
        public string GetUPSVar(string upsName, string varName)
        {
            return GetUPSVarAsync(upsName, varName).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Get the value of a specific variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.status')</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>The value of the requested variable as a string</returns>
        public Task<string> GetUPSVarAsync(string upsName, string varName, CancellationToken cancellationToken = default)
        {
            string prefix = "VAR " + NutProtocol.Word(upsName, nameof(upsName)) + " " + NutProtocol.Word(varName, nameof(varName));
            return GetValueAsync("GET " + prefix, prefix, cancellationToken);
        }

        /// <summary>
        /// Gets the data type of a specific variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.status')</param>
        /// <returns>The type words, for example "RW STRING:20"</returns>
        public string GetUPSVarType(string upsName, string varName)
        {
            return GetUPSVarTypeAsync(upsName, varName).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Gets the data type of a specific variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.status')</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>The type words, for example "RW STRING:20"</returns>
        public Task<string> GetUPSVarTypeAsync(string upsName, string varName, CancellationToken cancellationToken = default)
        {
            string prefix = "TYPE " + NutProtocol.Word(upsName, nameof(upsName)) + " " + NutProtocol.Word(varName, nameof(varName));
            return GetValueAsync("GET " + prefix, prefix, cancellationToken);
        }

        /// <summary>
        /// Gets the description of the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <returns>The description of the UPS as in ups.conf on the server</returns>
        public string GetUPSDescription(string upsName)
        {
            return GetUPSDescriptionAsync(upsName).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Gets the description of the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>The description of the UPS as in ups.conf on the server</returns>
        public Task<string> GetUPSDescriptionAsync(string upsName, CancellationToken cancellationToken = default)
        {
            string prefix = "UPSDESC " + NutProtocol.Word(upsName, nameof(upsName));
            return GetValueAsync("GET " + prefix, prefix, cancellationToken);
        }

        /// <summary>
        /// Gets the description of a specific variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.status')</param>
        /// <returns>The description of the variable</returns>
        public string GetUPSVarDescription(string upsName, string varName)
        {
            return GetUPSVarDescriptionAsync(upsName, varName).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Gets the description of a specific variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.status')</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>The description of the variable</returns>
        public Task<string> GetUPSVarDescriptionAsync(string upsName, string varName, CancellationToken cancellationToken = default)
        {
            string prefix = "DESC " + NutProtocol.Word(upsName, nameof(upsName)) + " " + NutProtocol.Word(varName, nameof(varName));
            return GetValueAsync("GET " + prefix, prefix, cancellationToken);
        }

        /// <summary>
        /// Gets the description of a specific instant command on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="cmdName">Name of the instant command (for example 'load.off')</param>
        /// <returns>The description of the instant command</returns>
        public string GetUPSCmdDescription(string upsName, string cmdName)
        {
            return GetUPSCmdDescriptionAsync(upsName, cmdName).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Gets the description of a specific instant command on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="cmdName">Name of the instant command (for example 'load.off')</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>The description of the instant command</returns>
        public Task<string> GetUPSCmdDescriptionAsync(string upsName, string cmdName, CancellationToken cancellationToken = default)
        {
            string prefix = "CMDDESC " + NutProtocol.Word(upsName, nameof(upsName)) + " " + NutProtocol.Word(cmdName, nameof(cmdName));
            return GetValueAsync("GET " + prefix, prefix, cancellationToken);
        }

        /// <summary>
        /// Sets the Username for the connection that will be used for any command that require it
        /// </summary>
        /// <param name="authUser">Username to use</param>
        /// <returns>True if username is accepted, False if an error occurs</returns>
        public bool SetUsername(string authUser)
        {
            return SetUsernameAsync(authUser).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Sets the Username for the connection that will be used for any command that require it
        /// </summary>
        /// <param name="authUser">Username to use</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>True if username is accepted, False if an error occurs</returns>
        public Task<bool> SetUsernameAsync(string authUser, CancellationToken cancellationToken = default)
        {
            return SendOkAsync("USERNAME " + NutProtocol.Word(authUser, nameof(authUser)), cancellationToken);
        }

        /// <summary>
        /// Sets the Password for the connection that will be used for any command that require it.
        /// The password is sent as plain text unless <see cref="StartTls(string)"/> was used first.
        /// </summary>
        /// <param name="authPass">Password to use</param>
        /// <returns>True if password is accepted, False if an error occurs</returns>
        public bool SetPassword(string authPass)
        {
            return SetPasswordAsync(authPass).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Sets the Password for the connection that will be used for any command that require it.
        /// The password is sent as plain text unless <see cref="StartTlsAsync"/> was used first.
        /// </summary>
        /// <param name="authPass">Password to use</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>True if password is accepted, False if an error occurs</returns>
        public Task<bool> SetPasswordAsync(string authPass, CancellationToken cancellationToken = default)
        {
            return SendOkAsync("PASSWORD " + NutProtocol.QuoteIfNeeded(authPass, nameof(authPass)), cancellationToken);
        }

        /// <summary>
        /// Set the value of a writable variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.id')</param>
        /// <param name="newValue">Value to set to. Any text without line breaks; quotes and backslashes are escaped.</param>
        /// <returns>True if the variable change is accepted, False if an error occurs</returns>
        public bool SetUPSVariable(string upsName, string varName, string newValue)
        {
            return SetUPSVariableAsync(upsName, varName, newValue).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Set the value of a writable variable on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="varName">Name of the variable (for example 'ups.id')</param>
        /// <param name="newValue">Value to set to. Any text without line breaks; quotes and backslashes are escaped.</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>True if the variable change is accepted, False if an error occurs</returns>
        public Task<bool> SetUPSVariableAsync(string upsName, string varName, string newValue, CancellationToken cancellationToken = default)
        {
            return SendOkAsync("SET VAR " + NutProtocol.Word(upsName, nameof(upsName)) + " " + NutProtocol.Word(varName, nameof(varName))
                + " " + NutProtocol.Quote(newValue, nameof(newValue)), cancellationToken);
        }

        /// <summary>
        /// Notify the upsd server that this machine is drawing power from the specified UPS.
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <returns>True if successful, or False if an error occured</returns>
        public bool Login(string upsName)
        {
            return LoginAsync(upsName).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Notify the upsd server that this machine is drawing power from the specified UPS.
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>True if successful, or False if an error occured</returns>
        public async Task<bool> LoginAsync(string upsName, CancellationToken cancellationToken = default)
        {
            bool ok = await SendOkAsync("LOGIN " + NutProtocol.Word(upsName, nameof(upsName)), cancellationToken).ConfigureAwait(false);
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
            return LogoutAsync().GetAwaiter().GetResult();
        }
        /// <summary>
        /// Notify the upsd server that we are disconnecting
        /// </summary>
        /// <returns>True if successful, or False if an error occured</returns>
        public async Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
        {
            string? reply = await SendAsync("LOGOUT", IsLineComplete, cancellationToken).ConfigureAwait(false);
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
            return InstantCommandAsync(upsName, command, null).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Perform and instant command on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="command">Instant Command to execute</param>
        /// <param name="addParam">(Optional)Additional parameter to the instant command</param>
        /// <returns>True if the instant command is accepted, False if an error occurs</returns>
        public bool InstantCommand(string upsName, string command, string? addParam)
        {
            return InstantCommandAsync(upsName, command, addParam).GetAwaiter().GetResult();
        }
        /// <summary>
        /// Perform and instant command on the specified UPS
        /// </summary>
        /// <param name="upsName">Name of the UPS</param>
        /// <param name="command">Instant Command to execute</param>
        /// <param name="addParam">(Optional)Additional parameter to the instant command</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        /// <returns>True if the instant command is accepted, False if an error occurs</returns>
        public Task<bool> InstantCommandAsync(string upsName, string command, string? addParam = null, CancellationToken cancellationToken = default)
        {
            string cmd = "INSTCMD " + NutProtocol.Word(upsName, nameof(upsName)) + " " + NutProtocol.Word(command, nameof(command));
            if (string.IsNullOrEmpty(addParam) == false)
            {
                cmd += " " + NutProtocol.Quote(addParam!, nameof(addParam));
            }

            return SendOkAsync(cmd, cancellationToken);
        }

        private static readonly char[] LineSeparator = { '\n' };

        private static bool IsLineComplete(StringBuilder reply)
        {
            // SendLockedAsync only asks once the reply ends with a newline, so one line is always complete
            return true;
        }

        private async Task<bool> SendOkAsync(string command, CancellationToken cancellationToken)
        {
            return await SendAsync(command, IsLineComplete, cancellationToken).ConfigureAwait(false) == "OK\n";
        }

        /// <summary>
        /// Send a GET-style command and return the value from a single line reply of the form "&lt;prefix&gt; &lt;value&gt;"
        /// </summary>
        /// <returns>The value, or an empty string if the server closed the connection or sent a reply for something else</returns>
        private async Task<string> GetValueAsync(string command, string replyPrefix, CancellationToken cancellationToken)
        {
            string? reply = await SendAsync(command, IsLineComplete, cancellationToken).ConfigureAwait(false);
            if (reply == null)
            {
                return string.Empty;
            }

            List<string> expected = NutProtocol.Tokenize(replyPrefix);
            List<string> words = NutProtocol.Tokenize(reply);
            if (words.Count <= expected.Count)
            {
                return string.Empty;
            }
            for (int i = 0; i < expected.Count; i++)
            {
                if (words[i] != expected[i])
                {
                    return string.Empty;
                }
            }

            return string.Join(" ", words.GetRange(expected.Count, words.Count - expected.Count));
        }

        /// <summary>
        /// Send "LIST &lt;listName&gt;" and return the words of every item line of the given type
        /// </summary>
        /// <param name="listName">What to list, for example "VAR myups"; its words are already checked</param>
        /// <param name="itemType">The first word of each item line, for example "VAR"</param>
        /// <param name="wordCount">How many words an item line has</param>
        /// <param name="upsName">If given, only items for this UPS are returned</param>
        /// <param name="cancellationToken">Cancels the request; the connection is then closed.</param>
        private async Task<List<List<string>>> ListItemsAsync(string listName, string itemType, int wordCount, string? upsName, CancellationToken cancellationToken)
        {
            string begin = "BEGIN LIST " + listName + "\n";
            string end = "END LIST " + listName + "\n";

            // The list is complete at its END line. A reply whose first line does not start the list is complete at once.
            string? reply = await SendAsync("LIST " + listName,
                r => EndsWith(r, end) || StartsWith(r, begin) == false,
                cancellationToken).ConfigureAwait(false);

            List<List<string>> items = new List<List<string>>();
            if ((reply == null) || (reply.StartsWith(begin, StringComparison.Ordinal) == false))
            {
                return items;
            }

            string[] lines = reply.Split(LineSeparator, StringSplitOptions.RemoveEmptyEntries);
            for (int idx = 1; idx < lines.Length - 1; idx++)
            {
                List<string> words = NutProtocol.Tokenize(lines[idx]);
                if (words.Count != wordCount) continue;
                if (words[0] != itemType) continue;
                if ((upsName != null) && (words[1] != upsName)) continue;

                items.Add(words);
            }

            return items;
        }

        private async Task<string?> SendAsync(string command, Func<StringBuilder, bool> isComplete, CancellationToken cancellationToken)
        {
            await commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await SendLockedAsync(command, isComplete, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                commandLock.Release();
            }
        }

        /// <summary>
        /// Send a command and read the reply until <paramref name="isComplete"/> says it is complete.
        /// The caller must hold <see cref="commandLock"/>.
        /// </summary>
        /// <returns>The reply, or null if the server closed the connection before replying</returns>
        /// <remarks>
        /// If the reply times out, is canceled, the server closes the connection, or the connection fails, the
        /// connection is dropped and <see cref="Connected"/> becomes false. A reply that arrives late can then never
        /// be read as the reply to a later command; call <see cref="Connect"/> to start a fresh connection.
        /// </remarks>
        /// <exception cref="UPSException">The server replied with an error. The connection stays usable.</exception>
        /// <exception cref="TimeoutException">No complete reply arrived within <see cref="ReplyTimeout"/>.</exception>
        private async Task<string?> SendLockedAsync(string command, Func<StringBuilder, bool> isComplete, CancellationToken cancellationToken)
        {
            Stream? current = this.stream;
            if ((connected == false) || (current == null))
            {
                throw new InvalidOperationException("You have to connect by calling Connect() first!");
            }

            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(ReplyTimeout);
                // Some runtimes ignore cancellation once a read has started; closing the connection ends it either way
                using (timeout.Token.Register(DropConnection))
                {
                    try
                    {
                        byte[] data = Encoding.UTF8.GetBytes(command + "\n");
                        await current.WriteAsync(data, 0, data.Length, timeout.Token).ConfigureAwait(false);
                        await current.FlushAsync(timeout.Token).ConfigureAwait(false);

                        // A decoder keeps a multi-byte character that is split across two reads in one piece
                        Decoder decoder = Encoding.UTF8.GetDecoder();
                        byte[] buffer = new byte[4096];
                        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
                        StringBuilder reply = new StringBuilder();
                        while (true)
                        {
                            int bytesRead = await current.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                            if (bytesRead == 0)
                            {
                                // The server closed the connection
                                DropConnection();
                                return null;
                            }

                            int charCount = decoder.GetChars(buffer, 0, bytesRead, chars, 0);
                            reply.Append(chars, 0, charCount);

                            // Replies always end in a newline; only look at the text once one has arrived
                            if ((reply.Length == 0) || (reply[reply.Length - 1] != '\n'))
                            {
                                continue;
                            }
                            if (StartsWith(reply, "ERR "))
                            {
                                // A complete error reply: the stream is still in step, so the connection stays usable
                                HandleUPSDError(reply.ToString());
                            }
                            if (isComplete(reply))
                            {
                                return reply.ToString();
                            }
                        }
                    }
                    catch (Exception ex) when (!(ex is UPSException) && timeout.IsCancellationRequested)
                    {
                        DropConnection();
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(CommandName(command) + " was canceled; the connection has been closed.", ex, cancellationToken);
                        }

                        // Only name the command, never its arguments: PASSWORD would otherwise end up in logs
                        throw new TimeoutException("No complete reply to " + CommandName(command) + " from " + upsdServer
                            + " within " + ReplyTimeout.ToString() + "; the connection has been closed", ex);
                    }
                    catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
                    {
                        // A failed read or write leaves the stream in an unknown state
                        DropConnection();
                        throw;
                    }
                }
            }
        }

        private static string CommandName(string command)
        {
            int space = command.IndexOf(' ');
            return space < 0 ? command : command.Substring(0, space);
        }

        private static bool StartsWith(StringBuilder text, string value)
        {
            if (text.Length < value.Length) return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (text[i] != value[i]) return false;
            }
            return true;
        }

        private static bool EndsWith(StringBuilder text, string value)
        {
            int offset = text.Length - value.Length;
            if (offset < 0) return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (text[offset + i] != value[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Close the connection without talking to the server, because it can no longer be trusted
        /// </summary>
        private void DropConnection()
        {
            this.connected = false;
            this.loggedin = false;
            this.encrypted = false;
            Stream? oldStream = Interlocked.Exchange(ref this.stream, null);
            TcpClient? oldClient = Interlocked.Exchange(ref this.client, null);
            try
            {
                oldStream?.Dispose();
                oldClient?.Dispose();
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                // Already broken; there is nothing more to release
            }
        }

        /// <summary>
        /// Handles all error replies from UPSD daemon and generates exceptions
        /// </summary>
        /// <param name="reply">Error reply received from server</param>
        private static void HandleUPSDError(string reply)
        {
            List<string> words = NutProtocol.Tokenize(reply);
            if (words.Count < 2 || words[0] != "ERR")
            {
                throw new ArgumentException("Not a valid UPSD Error Message!", nameof(reply));
            }

            string message = string.Join(" ", words.GetRange(2, words.Count - 2));
            string name = words[1].Replace('-', '_');
            // Enum.TryParse also accepts numbers, which are not error names, so only names starting with a letter count
            if (name.Length == 0 || !char.IsLetter(name[0])
                || !Enum.TryParse(name, false, out UPSException.ErrorCode code)
                || !Enum.IsDefined(typeof(UPSException.ErrorCode), code))
            {
                throw new UPSException(UPSException.ErrorCode.UNKNOWN_ERROR, words[1] + " is not a recognized UPSD Error Code");
            }

            throw new UPSException(code, message);
        }
    }
}
