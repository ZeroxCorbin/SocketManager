#define TRACE

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;



namespace SocketManagerNS
{
    public class SocketManager : IDisposable
    {
        //Public
        //public class SocketStateEventArgs : EventArgs
        //{
        //    public bool State { get; }
        //    public SocketStateEventArgs(bool state) => State = state;
        //}
        //public class SocketMessageEventArgs : EventArgs
        //{
        //    public string Message { get; }
        //    public SocketMessageEventArgs(string message) => Message = message;
        //}
        public class ListenClientConnectedEventArgs : EventArgs
        {
            public TcpClient Client { get; }
            public ListenClientConnectedEventArgs(TcpClient client) => Client = client;
        }

        public delegate void ConnectedEventHandler(object sender, bool state);
        public event ConnectedEventHandler ConnectState;

        //public delegate void AsyncReceiveStateEventHandler(object sender, bool state);
        //public event AsyncReceiveStateEventHandler ReceiveAsyncState;

        public delegate void ListenStateEventHandler(object sender, bool state);
        public event ListenStateEventHandler ListenState;

        public delegate void ListenClientConnectedEventHandler(object sender, ListenClientConnectedEventArgs data);
        public event ListenClientConnectedEventHandler ListenClientConnected;

        public delegate void ErrorEventHandler(object sender, Exception data);
        public event ErrorEventHandler Error;

        public delegate void DataReceivedEventHandler(object sender, string data);
        public event DataReceivedEventHandler DataReceived;

        public delegate void MessageReceivedEventHandler(object sender, string message, string pattern);
        public event MessageReceivedEventHandler MessageReceived;

        //Public
        public ConnectionSettings ConnectionSettings { get; set; }

        //Public Read-only
        public bool IsConnected
        {
            get
            {
                if (Client != null)
                    return Client.Connected;
                else
                    return false;
            }
        }
        public bool IsListening { get; private set; }
        public volatile bool IsReceivingAsync = false;

        public bool IsException { get; private set; } = false;
        public Exception Exception { get; private set; }

        //Private
        private TcpClient Client { get; set; }
        private object ClientLockObject { get; set; } = new object();

        //private NetworkStream TheClientStream { get; set; } = null;
        private NetworkStream ClientStream => Client != null ? Client.GetStream() : null;

        public void Flush()
        {
            lock (ClientStreamReadLockObject)
            {
                while (ClientStream.DataAvailable)
                {
                    byte[] drop = new byte[4098];
                    ClientStream.Read(drop, 0, 4098);
                }
            }

        }
        private object ClientStreamReadLockObject { get; set; } = new object();
        private object ClientStreamWriteLockObject { get; set; } = new object();

        private TcpListener Server { get; set; }
        private object ListenLockObject { get; set; } = new object();

        private object ReceiveAsyncLockObject { get; set; } = new object();

        //Public
        public SocketManager() { }
        public SocketManager(TcpClient client) => Client = client;
        public SocketManager(string connectionString)
        {
            if (!ConnectionSettings.ValidateConnectionString(connectionString))
            {
                Error?.Invoke(this, new Exception($"Invalid Connection String: {connectionString}"));
                return;
            }

            ConnectionSettings = new ConnectionSettings(connectionString);
        }
        public SocketManager(ConnectionSettings connectionSettings)
        {
            if (!ConnectionSettings.ValidateConnectionString(connectionSettings.ConnectionString))
            {
                Error?.Invoke(this, new Exception($"Invalid Connection String: {connectionSettings.ConnectionString}"));
                return;
            }

            ConnectionSettings = connectionSettings;
        }

        public SocketManager(string connectionString, ErrorEventHandler error = null, ConnectedEventHandler connectSate = null, DataReceivedEventHandler dataReceived = null, ListenStateEventHandler listenState = null, ListenClientConnectedEventHandler listenClientConnected = null)
        {
            if (!ConnectionSettings.ValidateConnectionString(connectionString))
            {
                Error += error;
                Error?.Invoke(this, new Exception($"Invalid Connection String: {connectionString}"));
                return;
            }

            ConnectionSettings = new ConnectionSettings(connectionString);

            ConnectState += connectSate;
            DataReceived += dataReceived;
            ListenState += listenState;
            ListenClientConnected += listenClientConnected;
        }

        private void InternalError(object sender, Exception data)
        {
            IsException = true;
            this.Exception = data;

            Error?.Invoke(this, data);
            ConnectState?.Invoke(this, false);
        }
        private void ClearInternalError()
        {
            IsException = false;
            this.Exception = null;
        }
        public bool Connect(string ipAddress,int port, int timeout = 3000)
        {
            lock (ClientLockObject)
            {
                ClearInternalError();
                Client = new TcpClient()
                {
                    ReceiveTimeout = timeout + 1,
                    SendTimeout = timeout + 1,
                };
                WaitHandle wh = null;
                try
                {
                    IAsyncResult ar = Client.BeginConnect(IPAddress.Parse(ipAddress), port, null, null);
                    wh = ar.AsyncWaitHandle;

                    if (ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeout), true))
                        Client.EndConnect(ar);
                }
                catch (Exception ex)
                {
                    InternalError(Client, ex);
                }
                finally
                {
                    wh?.Close();
                    wh?.Dispose();
                }

                if (Client.Connected)
                    Task.Run(() => ConnectState?.Invoke(this, true));
                else
                    Task.Run(() => ConnectState?.Invoke(this, false));

                return Client.Connected;
            }
        }
        public bool Connect(int timeout = 3000)
        {
            lock (ClientLockObject)
            {
                ClearInternalError();
                Client = new TcpClient()
                {
                    ReceiveTimeout = timeout + 1,
                    SendTimeout = timeout + 1,
                };
                WaitHandle wh = null;
                try
                {
                    IAsyncResult ar = Client.BeginConnect(ConnectionSettings.IPAddress, ConnectionSettings.Port, null, null);
                    wh = ar.AsyncWaitHandle;

                    if (ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeout), true))
                        Client.EndConnect(ar);
                }
                catch (Exception ex)
                {
                    InternalError(Client, ex);
                }
                finally
                {
                    wh?.Close();
                    wh?.Dispose();
                }

                if (Client.Connected)
                    Task.Run(() => ConnectState?.Invoke(this, true));
                else
                    Task.Run(() => ConnectState?.Invoke(this, false));

                return Client.Connected;
            }
        }
        public void Close()
        {
            lock (ClientLockObject)
            {

                StopReceiveAsync(true);

                lock (ClientStreamReadLockObject)
                {

                    StopListen();
                    
                    Client?.Close();
                    Client = null;

                    Task.Run(() => ConnectState?.Invoke(this, false));
                }
            }
        }

        public bool Listen()
        {
            try
            {
                ClearInternalError();

                if (Server != null)
                    Server.Stop();

                Server = new TcpListener(ConnectionSettings.IPAddress, ConnectionSettings.Port);
                Server.Start();

                Task.Run(() => ListenThread_DoWork());

                return true;
            }
            catch (Exception ex)
            {
                InternalError(Server, ex);
                return false;
            }
        }
        public void StopListen()
        {
            IsListening = false;
            lock (ListenLockObject) { }
        }

        public bool StartReceiveAsync(char messageTerminator = '\n')
        {
            if (IsReceivingAsync)
                return true;

            if (ClientStream == null)
                return false;


            ThreadPool.QueueUserWorkItem(new WaitCallback(ReceiveAsyncThread_DoWork), messageTerminator);

            return true;
        }
        public void StopReceiveAsync(bool force = false)
        {
            var timeout = TimeSpan.FromMilliseconds(50);
            bool lockTaken = false;
            while (!lockTaken)
                try
                {
                    Monitor.TryEnter(ReceiveAsyncLockObject, timeout, ref lockTaken);
                    if (!lockTaken)
                        IsReceivingAsync = false;
                }
                finally
                {
                    // Ensure that the lock is released.
                    if (lockTaken)
                    {
                        Monitor.Exit(ReceiveAsyncLockObject);
                    }
                }
        }
        private void ReceiveAsyncThread_DoWork(object sender)
        {
            lock (ReceiveAsyncLockObject)
            {
                IsReceivingAsync = true;

                try
                {
                    char c = (char)sender;
                    string msg;
                    while (IsReceivingAsync)
                    {
                        if (ClientStream.DataAvailable)
                        {
                            msg = Read(c);
                            if (msg.Length > 0)
                            {
                                DataReceived?.Invoke(this, msg);
#if TRACE
                                Console.Write(msg);
#endif
                            }
                        }
                        else
                        {
                            if (!DetectConnection())
                                throw new Exception("Client disconnect detected internally.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    InternalError(this, ex);
                }

                IsReceivingAsync = false;
            }
        }

        ///// <summary>
        ///// Receive data until the last charater matches the messageTerminator.
        ///// </summary>
        ///// <param name="messageTerminator"></param>
        ///// <returns>True if the thread started.</returns>
        //public bool StartReceiveMessages(char messageTerminator = '\n')
        //{
        //    if (IsReceivingAsync)
        //        return true;

        //    if (ClientStream == null)
        //        return false;

        //    ThreadPool.QueueUserWorkItem(new WaitCallback(ReceiveMessages_Terminator_Thread_DoWork), messageTerminator);

        //    return true;
        //}
        //private void ReceiveMessages_Terminator_Thread_DoWork(object state)
        //{
        //    lock (ReceiveAsyncLockObject)
        //    {
        //        IsReceivingAsync = true;
        //        this.QueueTask(true, new Action(() => ReceiveAsyncState?.Invoke(this, true)));

        //        try
        //        {
        //            char c = (char)state;

        //            string msg;
        //            while (IsReceivingAsync)
        //            {
        //                msg = Read(c);
        //                if (msg.Length > 0)
        //                    this.QueueTask(false, new Action(() => DataReceived?.Invoke(this, msg)));
        //                else
        //                    if (!DetectConnection())
        //                        throw new Exception("Client disconnect detected internally.");
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            InternalError(this, ex);
        //        }

        //        IsReceivingAsync = false;
        //        this.QueueTask(true, new Action(() => ReceiveAsyncState?.Invoke(this, false)));
        //    }
        //}

        /// <summary>
        /// Receive messages with a start and end pattern.
        /// All values between the start and end patterns will be included I.e. (?s)(.*?)
        /// All values outside the pattern will be discared.
        /// Avoid using start and end of string patterns.
        /// </summary>
        /// <param name="startRegexPattern">Example: To find "$" use "[$]"</param>
        /// <param name="endRegexPattern">Example: To find "*0F" use "[*][A-Z0-9][A-Z0-9]"</param>
        /// <returns>True if the thread started.</returns>
        public bool StartReceiveMessages(string startRegexPattern, string endRegexPattern)
        {
            if (IsReceivingAsync)
                return true;

            if (ClientStream == null)
                return false;

            ThreadPool.QueueUserWorkItem(new WaitCallback(ReceiveMessages_Regex_Thread_DoWork), new string[] { startRegexPattern, endRegexPattern });

            return true;
        }
        private void ReceiveMessages_Regex_Thread_DoWork(object state)
        {
            lock (ReceiveAsyncLockObject)
            {
                IsReceivingAsync = true;

                Stopwatch sw = new Stopwatch();
                sw.Start();
                try
                {
                    Regex reg = new Regex($"{((string[])state)[0]}(?s)(.*?){((string[])state)[1]}");

                    string msg = string.Empty;
                    while (IsReceivingAsync)
                    {
                        if ((msg += Read()).Length > 0)
                        {
                            foreach (Match match in reg.Matches(msg))
                            {
                                MessageReceived?.Invoke(this, match.Value, reg.ToString());
                                msg = string.Empty;
                                sw.Restart();
                            }
                        }
                        else
                        {
                            if (!DetectConnection())
                                throw new Exception("Client disconnect detected internally.");
                        }

                        if (sw.ElapsedMilliseconds > 1000)
                        {
                            msg = string.Empty;
                            sw.Restart();

                            Thread.Sleep(1);
                        }
                    }
                }
                catch (Exception ex)
                {
                    InternalError(this, ex);
                }

                IsReceivingAsync = false;
            }
        }

        /// <summary>
        /// Read data from the client stream.
        /// </summary>
        /// <param name="bufferSize">More than the size of the expected data. (bytes)</param>
        /// <returns>Data from the client stream or string.Empty on error.</returns>
        public string Read(int bufferSize = 1000000)
        {
            lock (ClientStreamReadLockObject)
            {
                if (ClientStream == null) return string.Empty;

                if (ClientStream.CanRead)
                    if (ClientStream.DataAvailable)
                    {
                        byte[] buf = new byte[bufferSize];
                        int len = ClientStream.Read(buf, 0, bufferSize);
                        if (len > 0)
                        {
                            string msg = Encoding.UTF8.GetString(buf, 0, len);
#if TRACE
                                                        Console.Write(msg);
#endif
                            return msg;
                        }

                    }

                return string.Empty;
            }
        }
        /// <summary>
        /// Read data from the client stream until the last charater matches the messageTerminator.
        /// If the messageTerminator is not found a timeout will occur.
        /// </summary>
        /// <param name="messageTerminator"></param>
        /// <param name="bufferSize">More than the size of the expected data. (bytes)</param>
        /// <param name="timeout">How long to wait for the messageTerminator. (ms)</param>
        /// <returns>Data from the client stream or string.Empty on error. Returns what data could be read on a timeout.</returns>
        public string Read(char messageTerminator, int bufferSize = 1000000, uint timeout = 1000)
        {
            if (ClientStream == null)
                return string.Empty;

            lock (ClientStreamReadLockObject)
            {
                Stopwatch sw = new Stopwatch();
                StringBuilder sb = new StringBuilder();

                sw.Restart();
                while (ClientStream.CanRead)
                {
                    if (ClientStream.DataAvailable)
                    {
                        byte[] buf = new byte[bufferSize];

                        int len = ClientStream.Read(buf, 0, bufferSize);

                        if (len > 0)
                        {
                            sb.Append(Encoding.UTF8.GetString(buf, 0, len).ToCharArray());
                            if (buf[len - 1] == messageTerminator)
                                break;
                            sw.Restart();
                        }
                    }
                    if (sw.ElapsedMilliseconds >= timeout)
                        return sb.ToString();

                    if (!ClientStream.DataAvailable)
                        Thread.Sleep(1);
                }
#if TRACE
                Console.Write(sb.ToString());
#endif
                return sb.ToString();
            }
        }

        //Read Strings
        //public string Read(char untilChar = '\n', uint timeout = 1000)
        //{
        //    lock (ClientStreamReadLockObject)
        //    {
        //        Stopwatch sw = new Stopwatch();
        //        StringBuilder sb = new StringBuilder(10000);

        //        try
        //        {
        //            if (ClientStream == null) return string.Empty;
        //            sw.Start();
        //            int b;
        //            while (ClientStream.CanRead)
        //            {
        //                while (ClientStream.DataAvailable)
        //                {
        //                    b = ClientStream.ReadByte();

        //                    if (b > -1)
        //                    {
        //                        sb.Append((char)b);
        //                        sw.Restart();

        //                        if (b == untilChar)
        //                            return sb.ToString();
        //                    }
        //                    if (sw.ElapsedMilliseconds >= timeout)
        //                        return sb.ToString();
        //                }
        //                Thread.Sleep(1);

        //                if (ClientStream == null) return string.Empty;
        //            }
        //            sw.Stop();
        //        }
        //        catch (Exception ex)
        //        {
        //            InternalError(ClientStream, ex);
        //            return string.Empty;
        //        }

        //        return sb.ToString();
        //    }
        //}
        public string Read(string untilString, uint timeout = 2000)
        {
            if (untilString == null)
                untilString = string.Empty;

            lock (ClientStreamReadLockObject)
            {
                Stopwatch sw = new Stopwatch();
                StringBuilder sb = new StringBuilder();

                try
                {
                    if (ClientStream == null) return string.Empty;

                    sw.Start();
                    while (ClientStream.CanRead)
                    {
                        if (ClientStream.DataAvailable)
                        {
                            int b = ClientStream.ReadByte();

                            if (b > -1)
                            {
                                sb.Append((char)b);
                                sw.Restart();
                            }

                            if (sb.ToString().EndsWith(untilString))
                                break;
                        }

                        if (sw.ElapsedMilliseconds >= timeout)
                            break;

                        if (!ClientStream.DataAvailable)
                            Thread.Sleep(1);

                        if (ClientStream == null) return string.Empty;
                    }
                    sw.Stop();
                }
                catch (Exception ex)
                {
                    InternalError(ClientStream, ex);
                    return string.Empty;
                }

#if TRACE
                Console.Write(sb.ToString());
#endif
                return sb.ToString();
            }
        }

        public byte[] ReadBytes(uint timeout = 1000)
        {
            lock (ClientStreamReadLockObject)
            {
                Stopwatch sw = new Stopwatch();

                try
                {
                    if (ClientStream == null) return new byte[0];

                    sw.Start();
                    while (ClientStream.CanRead)
                    {
                        if (ClientStream.DataAvailable)
                        {
                            byte[] buf = new byte[1000];

                            int len = ClientStream.Read(buf, 0, 1000);

                            if (len > 0)
                            {
                                byte[] ret = new byte[len];
                                Array.Copy(buf, ret, len);
                                return ret;
                            }
                        }
                        if (sw.ElapsedMilliseconds >= timeout)
                            return new byte[0];

                        if (!ClientStream.DataAvailable)
                            Thread.Sleep(1);
                    }
                    sw.Stop();
                }
                catch (Exception ex)
                {
                    InternalError(ClientStream, ex);
                    return new byte[0];
                }

                return new byte[0];
            }
        }
        public byte[] ReadBytes(char untilChar, uint timeout = 1000)
        {
            lock (ClientStreamReadLockObject)
            {
                Stopwatch sw = new Stopwatch();
                List<byte> sb = new List<byte>();

                try
                {
                    if (ClientStream == null) return sb.ToArray();

                    sw.Start();
                    while (ClientStream.CanRead)
                    {
                        int b = -1;
                        if (ClientStream.DataAvailable)
                        {
                            b = ClientStream.ReadByte();

                            if (b > -1)
                            {
                                sb.Add((byte)b);
                                sw.Restart();
                            }
                            if (untilChar != 0)
                            {
                                if ((char)b == untilChar)
                                    break;
                            }
                        }

                        if (sw.ElapsedMilliseconds >= timeout)
                            break;

                        if (!ClientStream.DataAvailable)
                            Thread.Sleep(1);
                    }
                    sw.Stop();
                }
                catch (Exception ex)
                {
                    InternalError(ClientStream, ex);
                    return sb.ToArray();
                }

                return sb.ToArray();
            }
        }

        //Write
        public bool Write(string msg)
        {
            lock (ClientStreamWriteLockObject)
            {
                try
                {
                    if (ClientStream == null)
                        return false;
                    if (!ClientStream.CanWrite)
                        return false;

                    byte[] buffer_ot = ASCIIEncoding.ASCII.GetBytes(msg);
#if TRACE
                    Console.Write(msg);
#endif
                    ClientStream.Write(buffer_ot, 0, buffer_ot.Length);
                }
                catch (Exception ex)
                {
                    InternalError(ClientStream, ex);
                    return false;
                }

                return true;
            }
        }
        public bool Write(byte[] msg)
        {
            lock (ClientStreamWriteLockObject)
            {
                try
                {
                    if (ClientStream == null) return false;
                    if (!ClientStream.CanWrite) return false;

                    ClientStream.Write(msg, 0, msg.Length);
                }
                catch (Exception ex)
                {
                    InternalError(ClientStream, ex);
                    return false;
                }

                return true;
            }
        }

        //Private
        private bool DetectConnection()
        {
            if (Client == null) return false;

            // Detect if client disconnected
            if (Client.Client.Poll(0, SelectMode.SelectRead))
            {
                byte[] buff = new byte[1];
                if (Client.Client.Receive(buff, SocketFlags.Peek) == 0)
                {
                    // Client disconnected
                    return false;
                }
                else
                {
                    return true;
                }
            }
            return true;
        }


        private void ListenThread_DoWork()
        {
            lock (ListenLockObject)
            {
                try
                {
                    IsListening = true;
                    ListenState?.Invoke(this, true);

                    while (IsListening)
                    {
                        if (Server.Pending())
                        {
                            TcpClient cl = Server.AcceptTcpClient();
                            ListenClientConnected?.Invoke(Server, new ListenClientConnectedEventArgs(cl));
                        }
                        Thread.Sleep(10);
                    }

                    Server.Stop();
                    Server = null;

                    ListenState?.Invoke(this, false);
                }
                catch (Exception ex)
                {
                    IsListening = false;

                    Server?.Stop();
                    Server = null;

                    InternalError(Server, ex);

                    ListenState?.Invoke(this, false);
                }
            }
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    ConnectState = null;
                    DataReceived = null;
                    ListenClientConnected = null;
                    ListenState = null;
                    Error = null;
                    // TODO: dispose managed state (managed objects).
                    Close();

                    Client?.Close();
                    Client = null;
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~SocketManager() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {

            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}