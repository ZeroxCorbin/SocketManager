using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SocketManagerNS
{
    public class SocketManager : IDisposable
    {
        //Public
        public class SocketStateEventArgs : EventArgs
        {
            public bool State { get; }
            public SocketStateEventArgs(bool state) => State = state;
        }
        public class SocketMessageEventArgs : EventArgs
        {
            public string Message { get; }
            public SocketMessageEventArgs(string message) => Message = message;
        }
        public class ListenClientConnectedEventArgs : EventArgs
        {
            public TcpClient Client { get; }
            public ListenClientConnectedEventArgs(TcpClient client) => Client = client;
        }

        public delegate void ConnectedEventHandler(object sender, SocketStateEventArgs data);
        public event ConnectedEventHandler ConnectState;

        public delegate void ListenStateEventHandler(object sender, SocketStateEventArgs data);
        public event ListenStateEventHandler ListenState;

        public delegate void ListenClientConnectedEventHandler(object sender, ListenClientConnectedEventArgs data);
        public event ListenClientConnectedEventHandler ListenClientConnected;

        public delegate void ErrorEventHandler(object sender, Exception data);
        public event ErrorEventHandler Error;

        public delegate void DataReceivedEventHandler(object sender, SocketMessageEventArgs data);
        public event DataReceivedEventHandler DataReceived;

        //Public
        public string ConnectionString { get; private set; }
        public string IPAddress
        {
            get
            {
                if (ConnectionString.Count(c => c == ':') != 1) return string.Empty;
                return ConnectionString.Split(':')[0];
            }
        }
        public int Port
        {
            get
            {
                if (ConnectionString.Count(c => c == ':') != 1) return 0;
                return int.Parse(ConnectionString.Split(':')[1]);
            }
        }

        //Public Read-only
        public int BufferSize { get; private set; } = 1024;
        public int ConnectTimeout { get; private set; } = 3000;
        public int SendTimeout { get; private set; } = 500;
        public int RecieveTimeout { get; private set; } = 500;
        public bool IsConnected { get { if (Client != null) return Client.Connected; else return false; } }
        public bool IsListening { get; private set; }
        public bool IsAsyncReceiveRunning { get; private set; } = true;
        public bool IsError { get; private set; } = false;
        public Exception ErrorException { get; private set; }

        //Private
        private TcpClient Client { get; set; }
        private NetworkStream ClientStream { get; set; }
        private TcpListener Server { get; set; }
        private object LockObject { get; set; } = new object();

        //Public Static
        public static string GenerateConnectionString(string ip, int port) => ip + ":" + port.ToString();
        public static bool ValidateConnectionString(string connectionString)
        {
            if (connectionString.Count(c => c == ':') != 1) return false;
            string[] spl = connectionString.Split(':');

            if (!System.Net.IPAddress.TryParse(spl[0], out IPAddress ip)) return false;

            if (!int.TryParse(spl[1], out int port)) return false;

            return true;
        }

        //Public
        public SocketManager() { }
        public SocketManager(string connectionString) => ConnectionString = connectionString;
        public SocketManager(string connectionString, ErrorEventHandler error = null, ConnectedEventHandler connectSate = null, DataReceivedEventHandler dataReceived = null, ListenStateEventHandler listenState = null, ListenClientConnectedEventHandler listenClientConnected = null)
        {
            ConnectionString = connectionString;

            Error += error;
            ConnectState += connectSate;
            DataReceived += dataReceived;
            ListenState += listenState;
            ListenClientConnected += listenClientConnected;
        }
        
        public bool Connect(bool withTimeout = true, int timeout = 3000)
        {
            ConnectTimeout = timeout;

            bool result = false;
            try
            {
                if (withTimeout)
                {
                    result = ConnectWithTimeout();
                    return result;
                }

                Client = new TcpClient();
                Client.Connect(IPAddress, Port);
                ClientStream = Client.GetStream();

                result = true;
                return result;
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
                return result;
            }
            finally
            {
                if (!result)
                    CleanSock();
                else
                    ConnectState?.Invoke(this, new SocketStateEventArgs(true));
            }
        }
        public void Disconnect()
        {
            CleanSock();
            ConnectState?.Invoke(this, new SocketStateEventArgs(true));
        }
        public bool Listen()
        {
            bool result = false;
            if (IsConnected)
            {
                try
                {
                    IPAddress localAddr = System.Net.IPAddress.Parse(IPAddress);
                    Server = new TcpListener(localAddr, Port);
                    Server.Start();

                    IsListening = true;
                    ThreadPool.QueueUserWorkItem(new WaitCallback(AsyncListenerThread_DoWork));

                    result = true;
                    return result;
                }
                catch (Exception ex)
                {
                    Error?.Invoke(this, ex);

                    result = false;
                    return result;
                }
                finally
                {
                    if (!result)
                        Server = null;
                    else
                        ListenState?.Invoke(this, new SocketStateEventArgs(true));
                }
            }
            else
                return false;
        }
        public void StopListen()
        {
            if (IsListening)
            {
                IsListening = false;
                Thread.Sleep(100);
                ListenState?.Invoke(this, new SocketStateEventArgs(false));
            }

            try
            {
                Server?.Stop();
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
            }

            Server = null;
        }

        public bool StartRecieveAsync()
        {
            if (ClientStream == null) return false;

            IsAsyncReceiveRunning = true;

            ThreadPool.QueueUserWorkItem(new WaitCallback(AsyncReceiveThread_DoWork));

            return true;
        }
        public bool StartRecieveAsync(TcpClient client)
        {
            if (ClientStream != null) return false;

            bool result = false;
            if (IsConnected)
            {
                try
                {
                    Client = client;
                    ClientStream = Client.GetStream();

                    IsListening = true;
                    ThreadPool.QueueUserWorkItem(new WaitCallback(AsyncListenerThread_DoWork));

                    result = true;
                    return result;
                }
                catch (Exception ex)
                {
                    Error?.Invoke(this, ex);

                    result = false;
                    return result;
                }
                finally
                {
                    if (!result)
                        Server = null;
                    else
                    {
                        IsAsyncReceiveRunning = true;

                        ThreadPool.QueueUserWorkItem(new WaitCallback(AsyncReceiveThread_DoWork));
                    }
                }
            }
            else
                return false;
        }
        public void StopRecieveAsync()
        {
            IsAsyncReceiveRunning = false;
            Thread.Sleep(100);
        }

        public string Read()
        {
            int timeout = 45000; //ms
            Stopwatch sw = new Stopwatch();
            StringBuilder completeMessage = new System.Text.StringBuilder();

            try
            {
                sw.Start();
                lock (LockObject)
                {
                    while (ClientStream.CanRead && ClientStream.DataAvailable)
                    {
                        byte[] readBuffer = new byte[BufferSize];
                        int numberOfBytesRead = 0;

                        // Fill byte array with data from SocketManager1 stream
                        numberOfBytesRead = ClientStream.Read(readBuffer, 0, readBuffer.Length);

                        // Convert the number of bytes received to a string and
                        // concatenate to complete message
                        completeMessage.AppendFormat("{0}", System.Text.Encoding.ASCII.GetString(readBuffer, 0, numberOfBytesRead));

                        sw.Stop();
                        if (sw.ElapsedMilliseconds >= timeout)
                            throw new TimeoutException();
                    }
                }
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine(ex);
                throw;
            }

            return completeMessage.ToString();
        }
        public string Read(string endString)
        {
            int timeout = 45000; //ms
            Stopwatch sw = new Stopwatch();
            StringBuilder completeMessage = new System.Text.StringBuilder();

            // Read until find the given string argument or hit timeout
            sw.Start();
            do
            {
                // Convert the number of bytes received to a string and
                // concatenate to complete message
                completeMessage.AppendFormat("{0}", Read());
            }
            while (!completeMessage.ToString().Contains(endString) &&
                   sw.ElapsedMilliseconds < timeout);
            sw.Stop();

            if (sw.ElapsedMilliseconds >= timeout)
                throw new TimeoutException();

            return completeMessage.ToString();
        }
        public string ReadLine()
        {
            int timeout = 45000; //ms
            Stopwatch sw = new Stopwatch();
            StringBuilder completeMessage = new System.Text.StringBuilder();

            try
            {
                sw.Start();
                lock (LockObject)
                {
                    if (ClientStream.CanRead && ClientStream.DataAvailable)
                    {
                        int readBuffer = 0;
                        char singleChar = '~';

                        while (singleChar != '\n' && singleChar != '\r')
                        {
                            // Read the first byte of the stream
                            readBuffer = ClientStream.ReadByte();

                            //Start converting and appending the bytes into a message
                            singleChar = (char)readBuffer;

                            completeMessage.AppendFormat("{0}", singleChar.ToString());

                            if (sw.ElapsedMilliseconds >= timeout)
                                throw new TimeoutException();

                        }
                        sw.Stop();
                    }
                }
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine(ex);
                throw;
            }
            return completeMessage.ToString();
        }
        public string ReadMessage()
        {
            int timeout = 45000; //ms
            Stopwatch sw = new Stopwatch();
            StringBuilder completeMessage = new System.Text.StringBuilder();

            sw.Start();
            // Read until find the given string argument or hit timeout
            do
            {
                // Convert the number of bytes received to a string and
                // concatenate to complete message
                completeMessage.AppendFormat("{0}", Read());
                Thread.Sleep(5);
            }
            while (ClientStream.DataAvailable &&
                   sw.ElapsedMilliseconds < timeout);
            sw.Stop();

            if (sw.ElapsedMilliseconds >= timeout)
                throw new TimeoutException();

            return completeMessage.ToString();
        }
        public byte[] ReadBytes(bool waitAvailable = false)
        {
            int timeout = 3000; //ms
            Stopwatch sw = new Stopwatch();
            List<byte> ret = new List<byte>();


            try
            {
                sw.Start();
                lock (LockObject)
                {
                    long st = sw.ElapsedTicks;
                    while (waitAvailable & ClientStream.CanRead & !ClientStream.DataAvailable)
                    {

                    }
                    long end = sw.ElapsedTicks - st;
                    while (ClientStream.CanRead & ClientStream.DataAvailable)
                    {
                        byte[] readBuffer = new byte[BufferSize];
                        int numberOfBytesRead = 0;
                        // Fill byte array with data from SocketManager1 stream
                        numberOfBytesRead += ClientStream.Read(readBuffer, 0, readBuffer.Length);

                        for (int i = 0; i < numberOfBytesRead; i++)
                            ret.Add(readBuffer[i]);

                        sw.Stop();
                        if (sw.ElapsedMilliseconds >= timeout)
                            throw new TimeoutException();
                    }
                }
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine(ex);
                throw;
            }
            return ret.ToArray();
        }
        public string[] MessageParse(string message)
        {
            string[] messages = message.Split('\n', '\r');

            List<string> _messages = new List<string>();

            foreach (string item in messages)
            {
                if (!String.IsNullOrEmpty(item))
                {
                    _messages.Add(item);
                }
            }
            messages = _messages.ToArray();
            return messages;
        }

        public bool Write(string msg)
        {
            byte[] buffer_ot = new byte[BufferSize];
            try
            {
                lock (LockObject)
                {
                    StringToBytes(msg, ref buffer_ot);
                    ClientStream.Write(buffer_ot, 0, buffer_ot.Length);
                    Bzero(buffer_ot);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
            return true;
        }
        public bool Write(string msg, int waitTime)
        {
            if (Write(msg))
            {
                Thread.Sleep(waitTime);
                return true;
            }
            else
                return false; ;
        }
        public bool Write(byte[] msg)
        {
            try
            {
                lock (LockObject)
                {
                    ClientStream.Write(msg, 0, msg.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
            return true;
        }

        public byte[] WriteRead(byte[] msg)
        {
            int timeout = 3000; //ms
            Stopwatch sw = new Stopwatch();
            List<byte> ret = new List<byte>();

            try
            {
                sw.Start();
                lock (LockObject)
                {
                    ClientStream.Write(msg, 0, msg.Length);

                    while (ClientStream.CanRead & !ClientStream.DataAvailable)
                    {
                        if (sw.ElapsedMilliseconds >= timeout)
                            throw new TimeoutException();
                    }

                    while (ClientStream.CanRead & ClientStream.DataAvailable)
                    {
                        byte[] readBuffer = new byte[BufferSize];
                        int numberOfBytesRead = 0;
                        // Fill byte array with data from SocketManager1 stream
                        numberOfBytesRead += ClientStream.Read(readBuffer, 0, readBuffer.Length);

                        for (int i = 0; i < numberOfBytesRead; i++)
                            ret.Add(readBuffer[i]);

                        sw.Stop();
                        if (sw.ElapsedMilliseconds >= timeout)
                            throw new TimeoutException();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return new byte[1];
            }
            return ret.ToArray();
        }

        //Private
        private bool ConnectWithTimeout()
        {
            Client = new TcpClient();

            bool connected = false;
            IAsyncResult ar = Client.BeginConnect(IPAddress, Port, null, null);
            System.Threading.WaitHandle wh = ar.AsyncWaitHandle;
            try
            {
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(ConnectTimeout), false))
                {
                    Client.Close();
                    connected = false;
                }
                else
                    connected = true;
            }
            finally
            {
                if (!connected)
                    CleanSock();
                else
                    ClientStream = Client.GetStream();

                wh.Close();
            }
            return connected;
        }
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

        private void AsyncReceiveThread_DoWork(object sender)
        {
            try
            {
                string msg;
                while (IsAsyncReceiveRunning)
                {
                    msg = ReadMessage();
                    if (msg.Length > 0)
                        DataReceived?.Invoke(this, new SocketMessageEventArgs(msg));

                    if (!DetectConnection())
                        Disconnect();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
        private void AsyncListenerThread_DoWork(object sender)
        {
            while (IsListening)
                if (Server.Pending())
                    ListenClientConnected?.Invoke(Server, new ListenClientConnectedEventArgs(Server.AcceptTcpClient()));
        }

        private void CleanSock()
        {
            StopRecieveAsync();

            try
            {
                StopListen();

                ClientStream?.Close();
                Client?.Close();

                ClientStream?.Dispose();
                Client?.Dispose();
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
            }
            finally
            {
                ClientStream = null;
                Client = null;
            }
        }

        private void Bzero(byte[] buff)
        {
            for (int i = 0; i < buff.Length; i++)
            {
                buff[i] = 0;
            }
        }
        public byte[] StringToBytes(string msg) => System.Text.ASCIIEncoding.ASCII.GetBytes(msg);
        private void StringToBytes(string msg, ref byte[] buffer)
        {
            Bzero(buffer);
            buffer = System.Text.ASCIIEncoding.ASCII.GetBytes(msg);
        }
        private string BytesToString(byte[] buffer)
        {
            string msg = System.Text.ASCIIEncoding.ASCII.GetString(buffer, 0, buffer.Length);
            return msg;
        }


        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    Client?.Dispose();
                    ClientStream?.Dispose();
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