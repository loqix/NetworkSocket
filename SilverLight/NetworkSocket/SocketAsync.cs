﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Threading;
using NetworkSocket.Interfaces;
using System.Diagnostics;

namespace NetworkSocket
{
    /// <summary>
    /// 异步Socket对象 
    /// 提供异步发送和接收方法
    /// </summary>
    /// <typeparam name="T">PacketBase派生类型</typeparam>
    [DebuggerDisplay("RemoteEndPoint = {RemoteEndPoint}")]
    public class SocketAsync<T> : ISocketAsync<T> where T : PacketBase
    {
        /// <summary>
        /// 接收或发送缓冲区大小
        /// </summary>
        private const int BUFFER_SIZE = 1024 * 8;
        /// <summary>
        /// 接收和发送的缓冲区
        /// </summary>
        private byte[] argsBuffer = new byte[BUFFER_SIZE * 2];

        /// <summary>
        /// 发送参数
        /// </summary>
        private SocketAsyncEventArgs sendArg = new SocketAsyncEventArgs();
        /// <summary>
        /// 接收参数
        /// </summary>
        private SocketAsyncEventArgs recvArg = new SocketAsyncEventArgs();

        /// <summary>
        /// 发送的数据
        /// </summary>
        private ByteBuilder sendBuilder = new ByteBuilder();
        /// <summary>
        /// 接收到的未处理数据
        /// </summary>
        private ByteBuilder recvBuilder = new ByteBuilder();

        /// <summary>
        /// socket
        /// </summary>
        private volatile Socket socket;
        /// <summary>
        /// 是否正在异步发送中
        /// 0为已发停止发送，非0为发送中
        /// </summary>
        private int isSending = 0;
        /// <summary>
        /// 重置排它锁
        /// </summary>
        private object resetSyncRoot = new object();


        /// <summary>
        /// 发送数据的委托
        /// </summary>
        internal Action<T> SendHandler { get; set; }
        /// <summary>
        /// 处理和分析收到的数据的委托
        /// </summary>
        internal Func<ByteBuilder, T> ReceiveHandler { get; set; }

        /// <summary>
        /// 接收一个数据包委托
        /// </summary>
        internal Action<T> RecvCompleteHandler;
        /// <summary>
        /// 连接断开委托   
        /// </summary>
        internal Action DisconnectHandler;


        /// <summary>
        /// 获取动态数据字典
        /// </summary>
        public dynamic TagBag { get; private set; }

        /// <summary>
        /// 获取远程终结点
        /// </summary>
        public IPEndPoint RemoteEndPoint { get; private set; }

        /// <summary>
        /// 获取是否已连接到远程端
        /// </summary>
        public bool IsConnected
        {
            get
            {
                return this.socket != null && this.socket.Connected;
            }
        }

        /// <summary>
        /// 异步Socket
        /// </summary>  
        internal SocketAsync()
        {
            this.sendArg.SetBuffer(this.argsBuffer, 0, BUFFER_SIZE);
            this.sendArg.Completed += new EventHandler<SocketAsyncEventArgs>(this.IO_Completed);

            this.recvArg.SetBuffer(this.argsBuffer, BUFFER_SIZE, BUFFER_SIZE);
            this.recvArg.Completed += new EventHandler<SocketAsyncEventArgs>(this.IO_Completed);

            this.TagBag = new TagBag();
        }


        /// <summary>
        /// 将Socket对象与此对象绑定
        /// </summary>
        /// <param name="socket">套接字</param>
        internal void BindSocket(Socket socket)
        {
            this.socket = socket;
            this.RemoteEndPoint = (IPEndPoint)this.socket.RemoteEndPoint;
            this.recvArg.SocketError = SocketError.Success;
            this.sendArg.SocketError = SocketError.Success;
            this.SetKeepAlive(socket);
        }

        /// <summary>
        /// 设置客户端的心跳包
        /// </summary>
        /// <param name="socket">客户端</param>
        private void SetKeepAlive(Socket socket)
        {
#if !SILVERLIGHT
            var inOptionValue = new byte[12];
            var outOptionValue = new byte[12];

            ByteConverter.ToBytes(1, true).CopyTo(inOptionValue, 0);
            ByteConverter.ToBytes(5 * 1000, true).CopyTo(inOptionValue, 4);
            ByteConverter.ToBytes(5 * 1000, true).CopyTo(inOptionValue, 8);

            try
            {
                socket.IOControl(IOControlCode.KeepAliveValues, inOptionValue, outOptionValue);
            }
            catch (NotSupportedException)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, inOptionValue);
            }
            catch (NotImplementedException)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, inOptionValue);
            }
            catch (Exception)
            {
            }
#endif
        }

        /// <summary>
        /// 开始接收数据
        /// </summary>
        internal void BeginReceive()
        {
            if (this.socket.ReceiveAsync(this.recvArg) == false)
            {
                this.ProcessReceive(this.recvArg);
            }
        }

        /// <summary>
        /// 将重置的未绑定Socket之前的状态
        /// 包括释放socket对象，重置相关参数
        /// 如果已重置过，将返回false
        /// </summary>
        /// <returns></returns>
        internal bool Reset()
        {
            lock (this.resetSyncRoot)
            {
                if (this.socket == null)
                {
                    return false;
                }

                try
                {
                    this.socket.Shutdown(SocketShutdown.Both);
                }
                finally
                {
                    this.socket.Dispose();
                    this.socket = null;
                }
                // 关闭socket前重置相关数据
                this.isSending = 0;
                this.recvBuilder.Clear();
                this.sendBuilder.Clear();
                (this.TagBag as TagBag).Clear();
                this.RemoteEndPoint = null;
                return true;
            }
        }


        /// <summary>
        /// Socket IO完成事件
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="arg">参数</param>
        private void IO_Completed(object sender, SocketAsyncEventArgs arg)
        {
            switch (arg.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    this.ProcessReceive(arg);
                    break;

                case SocketAsyncOperation.Send:
                    this.ProcessSend(arg);
                    break;

#if !SILVERLIGHT
                case SocketAsyncOperation.Disconnect:
                    this.DisconnectHandler();
                    break;
#endif
                default:
                    break;
            }
        }

        /// <summary>
        /// 处理Socket接收的数据
        /// </summary>
        /// <param name="arg">接收参数</param>
        private void ProcessReceive(SocketAsyncEventArgs arg)
        {
            if (arg.BytesTransferred == 0 || arg.SocketError != SocketError.Success)
            {
                this.DisconnectHandler();
                return;
            }

            lock (this.recvBuilder.SyncRoot)
            {
                T packet = null;
                this.recvBuilder.Add(arg.Buffer, arg.Offset, arg.BytesTransferred);
                while ((packet = this.ReceiveHandler(this.recvBuilder)) != null)
                {
                    this.RecvCompleteHandler(packet);
                }
            }

            // 检测是否已手动关闭Socket
            if (this.socket == null)
            {
                this.DisconnectHandler();
            }
            else if (this.socket.ReceiveAsync(arg) == false)
            {
                this.ProcessReceive(arg);
            }
        }

        /// <summary>
        /// 处理发送数据后的socket
        /// </summary>
        /// <param name="arg">发送参数</param>
        private void ProcessSend(SocketAsyncEventArgs arg)
        {
            if (arg.SocketError == SocketError.Success)
            {
                this.SendAsync();
            }
            else
            {
                this.DisconnectHandler();
            }
        }

        /// <summary>
        /// 异步发送数据
        /// </summary>
        /// <param name="packet">数据包</param>
        public void Send(T packet)
        {
            this.SendHandler(packet);
            if (packet == null)
            {
                return;
            }

            var bytes = packet.ToByteArray();
            if (bytes == null)
            {
                return;
            }

            lock (this.sendBuilder.SyncRoot)
            {
                this.sendBuilder.Add(bytes);
            }

            if (Interlocked.CompareExchange(ref this.isSending, 1, 0) == 0)
            {
                this.ProcessSend(this.sendArg);
            }
        }


        /// <summary>
        /// 将发送数据拆开分批异步发送
        /// 因为不能对SendArg连续调用SendAsync方法      
        /// </summary>
        private void SendAsync()
        {
            lock (this.sendBuilder.SyncRoot)
            {
                int length = Math.Min(BUFFER_SIZE, this.sendBuilder.Length);
                if (length == 0)
                {
                    Interlocked.Exchange(ref this.isSending, length);
                    return;
                }

                this.sendBuilder.CutTo(this.sendArg.Buffer, this.sendArg.Offset, length);
                // 重算缓存区大小
                if (length != this.sendArg.Count)
                {
                    this.sendArg.SetBuffer(this.sendArg.Offset, length);
                }
                // 异步发送，等待系统通知
                if (this.IsConnected && this.socket.SendAsync(this.sendArg) == false)
                {
                    this.ProcessSend(this.sendArg);
                }
            }
        }

        /// <summary>
        /// 字符串显示
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return this.RemoteEndPoint == null ? string.Empty : this.RemoteEndPoint.ToString();
        }


        #region IDisponse成员

        /// <summary>
        /// 获取是否已释放
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 关闭和释放所有相关资源
        /// </summary>
        public void Dispose()
        {
            if (this.IsDisposed == false)
            {
                this.Dispose(true);
                GC.SuppressFinalize(this);
            }
            this.IsDisposed = true;
        }

        /// <summary>
        /// 析构函数
        /// </summary>
        ~SocketAsync()
        {
            this.Dispose(false);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        /// <param name="disposing">是否也释放托管资源</param>
        protected virtual void Dispose(bool disposing)
        {
            this.Reset();
            this.sendArg.Dispose();
            this.recvArg.Dispose();

            if (disposing)
            {
                this.isSending = 0;
                this.RemoteEndPoint = null;
                this.recvBuilder = null;
                this.sendBuilder = null;
                this.TagBag = null;
                this.resetSyncRoot = null;
            }
        }
        #endregion
    }
}
