﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Diagnostics;
using System.Collections.Concurrent;
using NetworkSocket.Policies;
using NetworkSocket;
using System.IO;

namespace Server
{
    class _Run
    {
        static void Main(string[] args)
        {
            Console.Title = "FastServer";

            // fastServer服务
            var fastServer = new FastServer();
            fastServer.StartListen(4502);
            Console.WriteLine("FastServer服务启动：" + fastServer.LocalEndPoint);


            // 生成fastServer的代理代码
            var demoPath = Environment.CurrentDirectory;
            while (demoPath.Contains("Server"))
            {
                demoPath = Path.GetDirectoryName(demoPath);
            }
            fastServer.ToProxyCode().WriteToFile(Path.Combine(demoPath, "ClientApp\\FastServerProxyBase.cs"));
                 

            while (true)
            {
                Console.ReadLine();
            }
        }
    }
}
