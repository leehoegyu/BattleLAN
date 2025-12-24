using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BattleLAN
{
    public class VirtualLAN : IDisposable
    {
        private const int SIO_RCVALL = unchecked((int)0x98000001);
        private const int IPPROTO_UDP = 17;
        private const int IP_HDRINCL = 2;

        private Socket? socketHandle;
        private Socket? sendSocket;
        private readonly List<byte[]> receivers;
        private readonly byte[] broadcastIp;
        private readonly object lockObject = new object();
        private readonly object sendLockObject = new object();
        private readonly Queue<SocketAsyncEventArgs> receiveArgsPool;
        private readonly object receiveArgsPoolLock = new object();
        private CancellationTokenSource? cancellationTokenSource;
        private Task? captureTask;
        private bool isRunning;

        public bool IsRunning => isRunning;

        public VirtualLAN()
        {
            receivers = new List<byte[]>();
            broadcastIp = IPAddress.Parse("255.255.255.255").GetAddressBytes();
            receiveArgsPool = new Queue<SocketAsyncEventArgs>();
        }

        public void Start()
        {
            if (isRunning)
                return;

            lock (lockObject)
            {
                if (isRunning)
                    return;

                try
                {
                    WSAData wsaData = new WSAData();
                    int result = WSAStartup(0x0202, ref wsaData);
                    if (result != 0)
                    {
                        throw new Exception($"WSAStartup failed: {result}");
                    }

                    try
                    {
                        socketHandle = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                    }
                    catch (SocketException ex)
                    {
                        throw new Exception($"Failed to create raw socket. Error: {ex.SocketErrorCode}. Administrator privileges required.", ex);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Failed to create raw socket. Administrator privileges required.", ex);
                    }

                    if (socketHandle == null)
                    {
                        throw new Exception("Failed to create raw socket. Socket is null.");
                    }

                    string hostName = Dns.GetHostName();
                    IPHostEntry hostEntry = Dns.GetHostEntry(hostName);
                    IPAddress? localIp = hostEntry.AddressList
                        .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);

                    if (localIp == null)
                    {
                        throw new Exception("Unable to get host IP address");
                    }

                    try
                    {
                        IPEndPoint localEndPoint = new IPEndPoint(localIp, 6000);
                        socketHandle.Bind(localEndPoint);
                    }
                    catch (SocketException ex)
                    {
                        throw new Exception($"Failed to bind socket to {localIp}:6000. Error: {ex.SocketErrorCode}. Port may be in use.", ex);
                    }

                    if (!SetSocketToRawMode(socketHandle))
                    {
                        int errorCode = Marshal.GetLastWin32Error();
                        throw new Exception($"SetSocketToRawMode failed. Error code: {errorCode}. Administrator privileges required.");
                    }

                    try
                    {
                        sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Udp);
                        sendSocket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IP_HDRINCL, 1);
                    }
                    catch (SocketException ex)
                    {
                        throw new Exception($"Failed to create send socket. Error: {ex.SocketErrorCode}. Administrator privileges required.", ex);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Failed to create send socket. Administrator privileges required.", ex);
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        var args = new SocketAsyncEventArgs();
                        args.SetBuffer(new byte[65536], 0, 65536);
                        args.Completed += ReceiveArgs_Completed;
                        lock (receiveArgsPoolLock)
                        {
                            receiveArgsPool.Enqueue(args);
                        }
                    }

                    isRunning = true;
                    cancellationTokenSource = new CancellationTokenSource();
                    captureTask = CaptureLoop(cancellationTokenSource.Token);
                }
                catch
                {
                    CleanupConnection();
                    throw;
                }
            }
        }

        public void Stop()
        {
            if (!isRunning)
                return;

            lock (lockObject)
            {
                if (!isRunning)
                    return;

                isRunning = false;
                
                try
                {
                    socketHandle?.Close();
                }
                catch { }
                
                cancellationTokenSource?.Cancel();
                captureTask?.Wait(TimeSpan.FromSeconds(2));
                CleanupConnection();
            }
        }

        public bool AddReceiver(string ip)
        {
            if (!IPAddress.TryParse(ip, out IPAddress? ipAddress))
                return false;

            byte[] bytes = ipAddress.GetAddressBytes();

            lock (lockObject)
            {
                if (!receivers.Any(r => r.SequenceEqual(bytes)))
                {
                    receivers.Add(bytes);
                }
            }
            return true;
        }

        public void RemoveReceiver(string ip)
        {
            if (!IPAddress.TryParse(ip, out IPAddress? ipAddress))
                return;

            byte[] bytes = ipAddress.GetAddressBytes();

            lock (lockObject)
            {
                receivers.RemoveAll(r => r.SequenceEqual(bytes));
            }
        }

        public void ClearReceivers()
        {
            lock (lockObject)
            {
                receivers.Clear();
            }
        }

        public List<string> GetReceivers()
        {
            lock (lockObject)
            {
                return receivers.Select(ip => new IPAddress(ip).ToString()).ToList();
            }
        }

        private void CleanupConnection()
        {
            try
            {
                lock (sendLockObject)
                {
                    sendSocket?.Close();
                    sendSocket?.Dispose();
                    sendSocket = null;
                }

                lock (receiveArgsPoolLock)
                {
                    while (receiveArgsPool.Count > 0)
                    {
                        var args = receiveArgsPool.Dequeue();
                        args.Completed -= ReceiveArgs_Completed;
                        args.Dispose();
                    }
                }

                socketHandle?.Close();
                socketHandle?.Dispose();
                socketHandle = null;
                WSACleanup();
            }
            catch { }
        }

        private bool SetSocketToRawMode(Socket socket)
        {
            try
            {
                byte[] inValue = new byte[] { 1 };
                byte[] outValue = new byte[4];
                int bytesReturned = 0;
                int result = WSAIoctl(socket.Handle, SIO_RCVALL, inValue, inValue.Length,
                    outValue, outValue.Length, ref bytesReturned, IntPtr.Zero, IntPtr.Zero);
                
                if (result != 0)
                {
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ushort ReadNetworkUInt16(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static uint ReadNetworkUInt32(byte[] buffer, int offset)
        {
            return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
        }

        private static void WriteNetworkUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }

        private static void WriteNetworkUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }

        private ushort IPChecksum(byte[] data, int length)
        {
            uint sum = 0;

            for (int i = 0; i < length / 2; i++)
            {
                ushort value = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
                sum += value;
            }

            if (length % 2 == 1)
            {
                sum += (uint)(data[length - 1] << 8);
            }

            while (sum > 0xFFFF)
            {
                sum = (sum >> 16) + (sum & 0xFFFF);
            }

            return (ushort)(~sum & 0xFFFF);
        }

        private ushort UDPChecksum(uint saddr, uint daddr, byte[] udpHeaderBuffer, int udpHeaderOffset, ushort udpLength, byte[] data, int dataLen)
        {
            uint sum = 0;

            sum += (saddr >> 16) & 0xFFFF;
            sum += saddr & 0xFFFF;
            sum += (daddr >> 16) & 0xFFFF;
            sum += daddr & 0xFFFF;
            sum += IPPROTO_UDP;
            sum += udpLength;

            for (int i = 0; i < 4; i++)
            {
                ushort value = ReadNetworkUInt16(udpHeaderBuffer, udpHeaderOffset + i * 2);
                sum += value;
            }

            for (int i = 0; i < dataLen / 2; i++)
            {
                ushort value = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
                sum += value;
            }

            if (dataLen % 2 == 1)
            {
                sum += (uint)(data[dataLen - 1] << 8);
            }

            while (sum > 0xFFFF)
            {
                sum = (sum >> 16) + (sum & 0xFFFF);
            }

            return (ushort)(~sum & 0xFFFF);
        }

        private bool ForwardData(byte[] destHostBytes, byte[] buffer, int bufferLen)
        {
            if (sendSocket == null)
                return false;

            byte[]? modifiedBuffer = null;
            byte[]? udpData = null;

            try
            {
                IPAddress destIp = new IPAddress(destHostBytes);
                IPEndPoint destEndPoint = new IPEndPoint(destIp, 0);

                int ipHeaderLen = (buffer[0] & 0x0F) * 4;
                ushort udpLength = ReadNetworkUInt16(buffer, ipHeaderLen + 4);
                int udpDataLen = udpLength - 8;

                modifiedBuffer = ArrayPool<byte>.Shared.Rent(bufferLen);
                Array.Copy(buffer, 0, modifiedBuffer, 0, bufferLen);

                Buffer.BlockCopy(destHostBytes, 0, modifiedBuffer, 16, 4);

                WriteNetworkUInt16(modifiedBuffer, 10, 0);
                ushort ipChecksum = IPChecksum(modifiedBuffer, ipHeaderLen);
                WriteNetworkUInt16(modifiedBuffer, 10, ipChecksum);

                WriteNetworkUInt16(modifiedBuffer, ipHeaderLen + 6, 0);
                
                udpData = ArrayPool<byte>.Shared.Rent(udpDataLen);
                Array.Copy(modifiedBuffer, ipHeaderLen + 8, udpData, 0, udpDataLen);
                
                uint saddr = ReadNetworkUInt32(modifiedBuffer, 12);
                uint daddr = ReadNetworkUInt32(modifiedBuffer, 16);
                ushort udpLengthForChecksum = ReadNetworkUInt16(modifiedBuffer, ipHeaderLen + 4);
                ushort udpChecksum = UDPChecksum(saddr, daddr, modifiedBuffer, ipHeaderLen, udpLengthForChecksum, udpData, udpDataLen);
                WriteNetworkUInt16(modifiedBuffer, ipHeaderLen + 6, udpChecksum);

                lock (sendLockObject)
                {
                    if (sendSocket != null)
                    {
                        sendSocket.SendTo(modifiedBuffer, 0, bufferLen, SocketFlags.None, destEndPoint);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (modifiedBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(modifiedBuffer);
                }
                if (udpData != null)
                {
                    ArrayPool<byte>.Shared.Return(udpData);
                }
            }
        }

        private async Task CaptureLoop(CancellationToken cancellationToken)
        {
            List<byte[]> receiversCopy = new List<byte[]>();

            while (!cancellationToken.IsCancellationRequested && socketHandle != null)
            {
                byte[]? buffer = null;
                try
                {
                    var (recvLen, bufferArray) = await ReceiveAsync(cancellationToken);
                    if (recvLen <= 0)
                        continue;

                    buffer = bufferArray;

                    if (buffer[9] == IPPROTO_UDP)
                    {
                        uint dstIp = ReadNetworkUInt32(buffer, 16);
                        byte[] dstIpBytes = new byte[4];
                        WriteNetworkUInt32(dstIpBytes, 0, dstIp);

                        if (dstIpBytes.SequenceEqual(broadcastIp))
                        {
                            lock (lockObject)
                            {
                                receiversCopy.Clear();
                                foreach (var receiver in receivers)
                                {
                                    byte[] receiverCopy = new byte[receiver.Length];
                                    Array.Copy(receiver, receiverCopy, receiver.Length);
                                    receiversCopy.Add(receiverCopy);
                                }
                            }

                            foreach (byte[] receiver in receiversCopy)
                            {
                                ForwardData(receiver, buffer, recvLen);
                            }
                        }
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.Interrupted || 
                        ex.SocketErrorCode == SocketError.OperationAborted)
                    {
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                }
                finally
                {
                    if (buffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
        }

        private Task<(int recvLen, byte[] buffer)> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<(int, byte[])>(cancellationToken);
            }

            if (socketHandle == null)
                return Task.FromResult((0, Array.Empty<byte>()));

            SocketAsyncEventArgs? args = null;
            lock (receiveArgsPoolLock)
            {
                if (receiveArgsPool.Count > 0)
                {
                    args = receiveArgsPool.Dequeue();
                }
            }

            if (args == null)
            {
                args = new SocketAsyncEventArgs();
                args.SetBuffer(new byte[65536], 0, 65536);
                args.Completed += ReceiveArgs_Completed;
            }

            TaskCompletionSource<(int, byte[])> tcs = new TaskCompletionSource<(int, byte[])>();
            args.UserToken = tcs;

            bool willRaiseEvent = true;
            try
            {
                if (socketHandle == null || cancellationToken.IsCancellationRequested)
                {
                    ReturnArgsToPool(args);
                    tcs.SetCanceled();
                    return tcs.Task;
                }

                willRaiseEvent = socketHandle.ReceiveAsync(args);
            }
            catch (ObjectDisposedException)
            {
                ReturnArgsToPool(args);
                tcs.SetCanceled();
                return tcs.Task;
            }

            if (!willRaiseEvent)
            {
                ProcessReceive(args);
            }

            return tcs.Task;
        }

        private void ReceiveArgs_Completed(object? sender, SocketAsyncEventArgs e)
        {
            ProcessReceive(e);
        }

        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            var tcs = e.UserToken as TaskCompletionSource<(int, byte[])>;
            if (tcs == null)
            {
                ReturnArgsToPool(e);
                return;
            }

            if (e.SocketError == SocketError.Success)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(e.BytesTransferred);
                if (e.Buffer != null)
                {
                    Buffer.BlockCopy(e.Buffer, e.Offset, buffer, 0, e.BytesTransferred);
                }
                ReturnArgsToPool(e);
                tcs.SetResult((e.BytesTransferred, buffer));
            }
            else if (e.SocketError == SocketError.OperationAborted || e.SocketError == SocketError.Interrupted)
            {
                ReturnArgsToPool(e);
                tcs.SetCanceled();
            }
            else
            {
                ReturnArgsToPool(e);
                tcs.SetException(new SocketException((int)e.SocketError));
            }
        }

        private void ReturnArgsToPool(SocketAsyncEventArgs args)
        {
            args.UserToken = null;
            lock (receiveArgsPoolLock)
            {
                if (receiveArgsPool.Count < 8)
                {
                    receiveArgsPool.Enqueue(args);
                }
                else
                {
                    args.Completed -= ReceiveArgs_Completed;
                    args.Dispose();
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WSAData
        {
            public short wVersion;
            public short wHighVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
            public string szDescription;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
            public string szSystemStatus;
            public short iMaxSockets;
            public short iMaxUdpDg;
            public IntPtr lpVendorInfo;
        }

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int WSAStartup(short wVersionRequested, ref WSAData lpWSAData);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int WSACleanup();

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int WSAIoctl(
            IntPtr s,
            int dwIoControlCode,
            byte[] lpvInBuffer,
            int cbInBuffer,
            byte[] lpvOutBuffer,
            int cbOutBuffer,
            ref int lpcbBytesReturned,
            IntPtr lpOverlapped,
            IntPtr lpCompletionRoutine);
    }
}

