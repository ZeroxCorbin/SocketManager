using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        public string ConnectionString { get; private set; }
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
        public bool IsConnected { get { if (Client != null) return Client.Connected; else return false; } }
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
                if(TheClientStream == null)
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
                Task.Run(() => Error?.Invoke(this, new Exception($"Invalid Connection String: {connectionString}")));
                return;
            }

            ConnectionString = connectionString;
        }
        public SocketManager(string connectionString, ErrorEventHandler error = null, ConnectedEventHandler connectSate = null, DataReceivedEventHandler dataReceived = null, ListenStateEventHandler listenState = null, ListenClientConnectedEventHandler listenClientConnected = null)
        {
            if (!ValidateConnectionString(connectionString))
            {
                Error += error;
                Task.Run(() => Error?.Invoke(this, new Exception($"Invalid Connection String: {connectionString}")));
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
            Task.Run(() => Error?.Invoke(this, data));
            Task.Run(() => ConnectState?.Invoke(this, false));
        }

        public bool Connect(int timeout = 3000)
        {
            lock (ClientLockObject)
            {
                if (Client != null)
                {
                    if (Client.Connected)
                        return true;

                    ClientStream?.Close();
                    Client?.Close();
                    Client?.Dispose();

                    Client = null;
                }

                Client = new TcpClient()
                {
                    ReceiveTimeout = timeout + 1,
                    SendTimeout = timeout + 1,
                };

                bool connected = false;

                IAsyncResult ar = Client.BeginConnect(IPAddress, Port, null, null);
                WaitHandle wh = ar.AsyncWaitHandle;
                try
                {
                    if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeout), true))
                        connected = false;
                    else
                    {
                        Client.EndConnect(ar);
                        connected = true;
                    }
                }
                catch (Exception ex)
                {
                    InternalError(Client, ex);
                    connected = false;
                }
                finally
                {
                    wh.Close();
                    wh.Dispose();
                }

                if (connected)
                    Task.Run(() => ConnectState?.Invoke(this, true));
                else
                    Task.Run(() => ConnectState?.Invoke(this,false));

                return connected;
            }
        }
        public void Close()
        {
            lock (ClientLockObject)
            {
                StopReceiveAsync();
                StopListen();

                Client?.Close();
            }

            Task.Run(() => ConnectState?.Invoke(this, false));
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

        public bool ReceiveAsync(string messageTerminator = "\r\n")
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
        public string Read(string untilString = "\r\n", uint timeout = 1000)
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

                            if(b > -1)
                            {
                                sb.Append((char)b);
                                sw.Restart();
                            }
                        }

                        if(!string.IsNullOrEmpty(untilString))
                            if (sb.ToString().EndsWith(untilString))
                                break;

                        if (sw.ElapsedMilliseconds >= timeout)
                            break;
                    }
                    sw.Stop();
                }
                catch (Exception ex)
                {
                    InternalError(ClientStream, ex);
                    return string.Empty;
                }

                Console.Write(sb.ToString());

                return sb.ToString();
            }
        }

        //Read Bytes
        //public byte[] ReadBytes()
        //{
        //    lock (ClientStreamReadLockObject)
        //    {
        //        List<byte> ret = new List<byte>();

        //        try
        //        {
        //            if (ClientStream == null) return new byte[0];

        //            while (ClientStream.CanRead && ClientStream.DataAvailable)
        //            {
        //                byte[] buffer = new byte[BufferSize];

        //                int readBytes = ClientStream.Read(buffer, 0, buffer.Length);

        //                for (int i = 0; i < readBytes; i++)
        //                    ret.Add(buffer[i]);
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            InternalError(ClientStream, ex);
        //            return new byte[0];
        //        }

        //        return ret.ToArray();
        //    }
        //}
        //public byte[] ReadBytes(int untilTimeout)
        //{
        //    lock (ClientStreamReadLockObject)
        //    {
        //        Stopwatch sw = new Stopwatch();
        //        List<byte> ret = new List<byte>();

        //        try
        //        {
        //            if (ClientStream == null) return new byte[0];

        //            sw.Start();
        //            while (ClientStream.CanRead)
        //            {
        //                byte[] buffer = new byte[BufferSize];

        //                int readBytes = ClientStream.Read(buffer, 0, buffer.Length);

        //                for (int i = 0; i < readBytes; i++)
        //                    ret.Add(buffer[i]);

        //                if (readBytes > 0) sw.Restart();
        //                if (sw.ElapsedMilliseconds >= untilTimeout)
        //                    break;
        //            }
        //            sw.Stop();
        //        }
        //        catch (Exception ex)
        //        {
        //            InternalError(ClientStream, ex);
        //            return new byte[0];
        //        }

        //        return ret.ToArray();
        //    }
        //}
        //public byte[] ReadBytes(char untilChar = '\n', uint timeout = 1000)
        //{
        //    lock (ClientStreamReadLockObject)
        //    {
        //        Stopwatch sw = new Stopwatch();
        //        List<byte> ret = new List<byte>();

        //        try
        //        {
        //            if (ClientStream == null) return new byte[0];

        //            sw.Start();
        //            while (ClientStream.CanRead)
        //            {
        //                byte[] buffer = new byte[BufferSize];

        //                int readBytes = ClientStream.Read(buffer, 0, buffer.Length);

        //                for (int i = 0; i < readBytes; i++)
        //                {
        //                    ret.Add(buffer[i]);
        //                    if (buffer[i] == untilChar)
        //                        return ret.ToArray();
        //                }

        //                if (readBytes > 0) sw.Restart();
        //                if (sw.ElapsedMilliseconds >= timeout)
        //                    break;
        //            }
        //            sw.Stop();
        //        }
        //        catch (Exception ex)
        //        {
        //            InternalError(ClientStream, ex);
        //            return new byte[0];
        //        }

        //        return ret.ToArray();
        //    }
        //}

        //Write
        public bool Write(string msg)
        {
            lock (ClientStreamWriteLockObject)
            {
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

        //Utility
        //public string[] MessageSplit(string message)
        //{
        //    List<string> messages = new List<string>();
        //    foreach (string item in message.Split('\n', '\r'))
        //        if (!String.IsNullOrEmpty(item))
        //            messages.Add(item);

        //    if (messages.Count() > 0)
        //        return messages.ToArray();
        //    else
        //        return new string[] { message.Trim('\n', '\r') };
        //}

        //Private
        private bool DetectConnection()
        {
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
                Task.Run(() => ReceiveAsyncState?.Invoke(this, true));

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
                    }
                }
                catch (Exception ex)
                {
                    InternalError(this, ex);
                }

                IsReceivingAsync = false;
                Task.Run(() => ReceiveAsyncState?.Invoke(this, false));
            }
        }
        private void ListenThread_DoWork(object sender)
        {
            lock (ListenLockObject)
            {
                try
                {
                    IsListening = true;

                    Task.Run(() => ListenState?.Invoke(this, true));

                    while (IsListening)
                    {
                        if (Server.Pending())
                        {
                            TcpClient cl = Server.AcceptTcpClient();
                            Task.Run(() => ListenClientConnected?.Invoke(Server, new ListenClientConnectedEventArgs(cl)));
                        }
                            
                    }

                    Server.Stop();
                    Server = null;

                    Task.Run(() => ListenState?.Invoke(this, false));
                }
                catch (Exception ex)
                {
                    InternalError(Server, ex);
                    IsListening = false;
                    Task.Run(() => ListenState?.Invoke(this, false));
                }
            }
        }

        //private byte[] GetBuffer(int size)
        //{
        //    byte[] ret = new byte[size];

        //    for (int i = 0; i < ret.Length; i++)
        //        ret[i] = 0;

        //    return ret;
        //}
        //public byte[] StringToBytes(string msg) => System.Text.ASCIIEncoding.ASCII.GetBytes(msg);

        //private string BytesToString(byte[] buffer)
        //{
        //    string msg = System.Text.ASCIIEncoding.ASCII.GetString(buffer, 0, buffer.Length);
        //    return msg;
        //}


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
                    Client?.Dispose();
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