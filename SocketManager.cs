using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SocketManagerNS
{
    public class SocketManager : GroupedTaskQueue, IDisposable
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

        public delegate void AsyncReceiveStateEventHandler(object sender, bool state);
        public event AsyncReceiveStateEventHandler ReceiveAsyncState;

        public delegate void ListenStateEventHandler(object sender, bool state);
        public event ListenStateEventHandler ListenState;

        public delegate void ListenClientConnectedEventHandler(object sender, ListenClientConnectedEventArgs data);
        public event ListenClientConnectedEventHandler ListenClientConnected;

        public delegate void ErrorEventHandler(object sender, Exception data);
        public event ErrorEventHandler Error;

        public delegate void DataReceivedEventHandler(object sender, string data);
        public event DataReceivedEventHandler DataReceived;

        //Public
        public string ConnectionString { get; set; }
        public string IPAddressString => ConnectionString.Split(':')[0];
        public string PortString => ConnectionString.Split(':')[1];

        public IPAddress IPAddress => IPAddress.Parse(ConnectionString.Split(':')[0]);
        public int Port => int.Parse(ConnectionString.Split(':')[1]);

        //Public Static
        public static string GenerateConnectionString(string ip, int port) => $"{ip}:{port}";
        public static string GenerateConnectionString(IPAddress ip, int port) => $"{ip}:{port}";
        public static bool ValidateConnectionString(string connectionString)
        {
            if (connectionString.Count(c => c == ':') < 1) return false;
            string[] spl = connectionString.Split(':');

            if (!IPAddress.TryParse(spl[0], out IPAddress ip)) return false;

            if (!int.TryParse(spl[1], out int port)) return false;

            return true;
        }

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
        public bool IsReceivingAsync { get; private set; } = false;
        public bool IsError { get; private set; } = false;

        //Private
        private TcpClient Client { get; set; }
        private object ClientLockObject { get; set; } = new object();

        private NetworkStream TheClientStream { get; set; } = null;
        private NetworkStream ClientStream
        {
            get
            {
                if (TheClientStream == null)
                {
                    if (Client != null)
                    {
                        TheClientStream = Client.GetStream();
                        return TheClientStream;
                    }
                    else
                        return null;
                }
                else
                    return TheClientStream;
            }
        }
        private object ClientStreamReadLockObject { get; set; } = new object();
        private object ClientStreamWriteLockObject { get; set; } = new object();

        private TcpListener Server { get; set; }
        private object ListenLockObject { get; set; } = new object();

        private object ReceiveAsyncLockObject { get; set; } = new object();

        //Public
        public SocketManager(TcpClient client) => Client = client;
        public SocketManager(string connectionString)
        {
            if (!ValidateConnectionString(connectionString))
            {
                this.QueueTask(false, new Action(() => Error?.Invoke(this, new Exception($"Invalid Connection String: {connectionString}"))));
                return;
            }

            ConnectionString = connectionString;
        }
        public SocketManager(string connectionString, ErrorEventHandler error = null, ConnectedEventHandler connectSate = null, DataReceivedEventHandler dataReceived = null, ListenStateEventHandler listenState = null, ListenClientConnectedEventHandler listenClientConnected = null)
        {
            if (!ValidateConnectionString(connectionString))
            {
                Error += error;
                this.QueueTask(false, new Action(() => Error?.Invoke(this, new Exception($"Invalid Connection String: {connectionString}"))));
                return;
            }

            ConnectionString = connectionString;

            ConnectState += connectSate;
            DataReceived += dataReceived;
            ListenState += listenState;
            ListenClientConnected += listenClientConnected;
        }

        private void InternalError(object sender, Exception data)
        {
            IsError = true;
            this.QueueTask(false, new Action(() => Error?.Invoke(this, data)));
            this.QueueTask(false, new Action(() => ConnectState?.Invoke(this, false)));
        }

        public bool Connect(int timeout = 3000)
        {
            lock (ClientLockObject)
            {
                Client = new TcpClient()
                {
                    ReceiveTimeout = timeout + 1,
                    SendTimeout = timeout + 1,
                };

                IAsyncResult ar = Client.BeginConnect(IPAddress, Port, null, null);
                WaitHandle wh = ar.AsyncWaitHandle;
                try
                {
                    if (ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeout), true))
                        Client.EndConnect(ar);
                }
                catch (Exception ex)
                {
                    InternalError(Client, ex);
                }
                finally
                {
                    wh.Close();
                    wh.Dispose();
                }

                if (Client.Connected)
                    this.QueueTask("State", true, new Action(() => ConnectState?.Invoke(this, true)));
                else
                    this.QueueTask("State", true, new Action(() => ConnectState?.Invoke(this, false)));

                return Client.Connected;
            }
        }
        public void Close()
        {
            lock (ClientLockObject)
            {
                StopReceiveAsync();
                StopListen();

                Client?.Close();
                Client = null;

                TheClientStream = null;
            }

            this.QueueTask("State", true, new Action(() => ConnectState?.Invoke(this, false)));
        }

        public bool Listen()
        {
            try
            {
                if (Server != null)
                    Server.Stop();

                Server = new TcpListener(IPAddress, Port);
                Server.Start();

                ThreadPool.QueueUserWorkItem(new WaitCallback(ListenThread_DoWork));

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

        public bool ReceiveAsync(string messageTerminator = "\n")
        {
            if (IsReceivingAsync) return true;

            if (ClientStream == null) return false;

            ThreadPool.QueueUserWorkItem(new WaitCallback(ReceiveAsyncThread_DoWork), messageTerminator);

            return true;
        }
        public void StopReceiveAsync(bool force = false)
        {
            if (!force) if (this.DataReceived != null) return;

            IsReceivingAsync = false;
            lock (ReceiveAsyncLockObject) { }
        }

        //Read Strings
        public string Read(char untilChar, uint timeout = 1000) => Read(untilChar.ToString(), timeout);
        public string Read(string untilString = "\n", uint timeout = 2000)
        {
            if (string.IsNullOrEmpty(untilString))
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

                            if (!string.IsNullOrEmpty(untilString))
                            {
                                if (sb.ToString().EndsWith(untilString))
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
                    return string.Empty;
                }
#if TRACE
                if (sb.Length > 0)
                    Console.WriteLine($"r: {sb.ToString().Trim('\r', '\n')}");
#endif
                return sb.ToString();
            }
        }

        public byte[] ReadBytes(char untilChar = '\n', uint timeout = 1000)
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
                //#if TRACE
                //                if (sb.Count() > 0)
                //                    Console.WriteLine($"r: {sb.ToString().Trim('\r', '\n')}");
                //#endif
                return sb.ToArray();
            }
        }

        //Write
        public bool Write(string msg)
        {
            lock (ClientStreamWriteLockObject)
            {
#if TRACE
                Console.WriteLine($"w: {msg.Trim('\r', '\n')}");
#endif
                try
                {
                    if (ClientStream == null) return false;
                    if (!ClientStream.CanWrite) return false;

                    byte[] buffer_ot = System.Text.ASCIIEncoding.ASCII.GetBytes(msg);

                    //StringToBytes(msg, ref buffer_ot);
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

        private void ReceiveAsyncThread_DoWork(object sender)
        {
            lock (ReceiveAsyncLockObject)
            {
                IsReceivingAsync = true;
                this.QueueTask(false, new Action(() => ReceiveAsyncState?.Invoke(this, true)));

                try
                {
                    string msg;
                    while (IsReceivingAsync)
                    {
                        msg = Read((string)sender);
                        if (msg.Length > 0)
                            DataReceived?.Invoke(this, msg);

                        if (!DetectConnection())
                            throw new Exception("Client disconnect detected internally.");

                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    InternalError(this, ex);
                }

                IsReceivingAsync = false;
                this.QueueTask(false, new Action(() => ReceiveAsyncState?.Invoke(this, false)));
            }
        }
        private void ListenThread_DoWork(object sender)
        {
            lock (ListenLockObject)
            {
                try
                {
                    IsListening = true;
                    this.QueueTask(false, new Action(() => ListenState?.Invoke(this, true)));

                    while (IsListening)
                    {
                        if (Server.Pending())
                        {
                            TcpClient cl = Server.AcceptTcpClient();
                            this.QueueTask(false, new Action(() => ListenClientConnected?.Invoke(Server, new ListenClientConnectedEventArgs(cl))));
                        }
                        Thread.Sleep(10);
                    }

                    Server.Stop();
                    Server = null;

                    this.QueueTask(false, new Action(() => ListenState?.Invoke(this, false)));
                }
                catch (Exception ex)
                {
                    IsListening = false;

                    Server?.Stop();
                    Server = null;

                    InternalError(Server, ex);

                    this.QueueTask(false, new Action(() => ListenState?.Invoke(this, false)));
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
                    ReceiveAsyncState = null;
                    ListenClientConnected = null;
                    ListenState = null;
                    Error = null;
                    // TODO: dispose managed state (managed objects).
                    Close();
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