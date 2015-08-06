﻿using Newtonsoft.Json;
using SlackAPI.Utilities;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace SlackAPI
{
    public class SlackSocket
    {
        LockFreeQueue<string> sendingQueue;
        int currentlySending;
        CancellationTokenSource cts;

        Dictionary<int, Action<string>> callbacks;
        internal ClientWebSocket socket;
        int currentId;

        Dictionary<string, Dictionary<string, Delegate>> routes;
        public bool Connected { get { return socket != null && socket.State == WebSocketState.Open; } }

        //This would be done for hinting but I don't think we really need this.

        static Dictionary<string, Dictionary<string, Type>> routing;
        static SlackSocket()
        {
            routing = new Dictionary<string, Dictionary<string, Type>>();
            foreach (Assembly assy in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assy.GlobalAssemblyCache)
                    foreach (Type t in assy.GetTypes())
                        foreach (SlackSocketRouting route in t.GetCustomAttributes<SlackSocketRouting>())
                        {
                            if (!routing.ContainsKey(route.Type))
                                routing.Add(route.Type, new Dictionary<string, Type>()
                            {
                                {route.SubType ?? "null", t}
                            });
                            else
                                if (!routing[route.Type].ContainsKey(route.SubType ?? "null"))
                                    routing[route.Type].Add(route.SubType ?? "null", t);
                                else
                                    throw new InvalidProgramException("Cannot have two socket message types with the same type and subtype!");
                        }
            }
        }

		public SlackSocket(LoginResponse loginDetails, object routingTo, Action onConnected = null)
        {
            BuildRoutes(routingTo);
            socket = new ClientWebSocket();
            callbacks = new Dictionary<int, Action<string>>();
            sendingQueue = new LockFreeQueue<string>();
            currentId = 1;

            cts = new CancellationTokenSource();
            socket.ConnectAsync(new Uri(string.Format("{0}?svn_rev={1}&login_with_boot_data-0-{2}&on_login-0-{2}&connect-1-{2}", loginDetails.url, loginDetails.svn_rev, DateTime.Now.Subtract(new DateTime(1970, 1, 1)).TotalSeconds)), cts.Token).Wait();
            if(onConnected != null)
                onConnected();
            SetupReceiving();
        }

        void BuildRoutes(object routingTo)
        {
            routes = new Dictionary<string, Dictionary<string, Delegate>>();

            Type routingToType = routingTo.GetType();
            Type slackMessage = typeof(SlackSocketMessage);
            foreach (MethodInfo m in routingTo.GetType().GetMethods(BindingFlags.Instance | BindingFlags.InvokeMethod | BindingFlags.FlattenHierarchy | BindingFlags.NonPublic | BindingFlags.Public))
            {
                ParameterInfo[] parameters = m.GetParameters();
                if (parameters.Length != 1) continue;
                if (parameters[0].ParameterType.IsSubclassOf(slackMessage))
                {
                    Type t = parameters[0].ParameterType;
                    foreach (SlackSocketRouting route in t.GetCustomAttributes<SlackSocketRouting>())
                    {
                        Type genericAction = typeof(Action<>).MakeGenericType(parameters[0].ParameterType);
                        Delegate d = Delegate.CreateDelegate(genericAction, routingTo, m, false);
                        if (d == null)
                        {
                            System.Diagnostics.Debug.WriteLine(string.Format("Couldn't create delegate for {0}.{1}", routingToType.FullName, m.Name));
                            continue;
                        }
                        if (!routes.ContainsKey(route.Type))
                            routes.Add(route.Type, new Dictionary<string, Delegate>());
                        if (!routes[route.Type].ContainsKey(route.SubType ?? "null"))
                            routes[route.Type].Add(route.SubType ?? "null", d);
                        else
                            routes[route.Type][route.SubType ?? "null"] = Delegate.Combine(routes[route.Type][route.SubType ?? "null"], d);
                    }
                }
            }
        }

        public void Send<K>(SlackSocketMessage message, Action<K> callback)
            where K : SlackSocketMessage
        {
            int sendingId = Interlocked.Increment(ref currentId);
            message.id = sendingId;
            callbacks.Add(sendingId, (c) =>
            {
                K obj = JsonConvert.DeserializeObject<K>(c, new JavascriptDateTimeConverter());
                callback(obj);
            });
            Send(message);
        }

        public void Send(SlackSocketMessage message)
        {
            if (message.id == 0)
                message.id = Interlocked.Increment(ref currentId);
            //socket.Send(JsonConvert.SerializeObject(message));

			if (string.IsNullOrEmpty(message.type)){
                IEnumerable<SlackSocketRouting> routes = message.GetType().GetCustomAttributes<SlackSocketRouting>();

                SlackSocketRouting route = null;
                foreach (SlackSocketRouting r in routes)
                {
                    route = r;
                }
                if (route == null) throw new InvalidProgramException("Cannot send without a proper route!");
                else
                {
                    message.type = route.Type;
                    message.subtype = route.SubType;
                }
            }

			sendingQueue.Push(JsonConvert.SerializeObject(message, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            if (Interlocked.CompareExchange(ref currentlySending, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(HandleSending);
        }

        public void BindCallback<K>(Action<K> callback)
        {
            Type t = typeof(K);

            foreach (SlackSocketRouting route in t.GetCustomAttributes<SlackSocketRouting>())
            {
                if (!routes.ContainsKey(route.Type))
                    routes.Add(route.Type, new Dictionary<string, Delegate>());
                if (!routes[route.Type].ContainsKey(route.SubType ?? "null"))
                    routes[route.Type].Add(route.SubType ?? "null", callback);
                else
                    routes[route.Type][route.SubType ?? "null"] = Delegate.Combine(routes[route.Type][route.SubType ?? "null"], callback);
            }
        }

        public void UnbindCallback<K>(Action<K> callback)
        {
            Type t = typeof(K);
            foreach (SlackSocketRouting route in t.GetCustomAttributes<SlackSocketRouting>())
            {
                Delegate d = routes.ContainsKey(route.Type) ? (routes.ContainsKey(route.SubType ?? "null") ? routes[route.Type][route.SubType ?? "null"] : null) : null;
                if (d != null)
                {
                    Delegate newd = Delegate.Remove(d, callback);
                    routes[route.Type][route.SubType ?? "null"] = newd;
                }
            }
        }

        void SetupReceiving()
        {
            Task.Factory.StartNew(
                async () =>
                {
                    List<byte[]> buffers = new List<byte[]>();
                    byte[] bytes = new byte[1024];
                    buffers.Add(bytes);
                    ArraySegment<byte> buffer = new ArraySegment<byte>(bytes);
                    while (socket.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cts.Token);

                        if (!result.EndOfMessage && buffer.Count == buffer.Array.Length)
                        {
                            bytes = new byte[1024];
                            buffers.Add(bytes);
                            buffer = new ArraySegment<byte>(bytes);
                            continue;
                        }

                        string data = string.Join("", buffers.Select((c) => Encoding.UTF8.GetString(c)));
                        SlackSocketMessage message = null;
                        try
                        {
                            message = JsonConvert.DeserializeObject<SlackSocketMessage>(data, new JavascriptDateTimeConverter());
                        }
                        catch (JsonSerializationException jsonExcep)
                        {
                            continue;
                        }

                        if (message == null)
                            continue;
                        else
                        {
                            HandleMessage(message, data);
                            buffers = new List<byte[]>();
                            bytes = new byte[1024];
                            buffer = new ArraySegment<byte>(bytes);
                        }
                    }
                }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        void HandleMessage(SlackSocketMessage message, string data)
        {
            if (callbacks.ContainsKey(message.reply_to))
                callbacks[message.reply_to](data);
            else if (routes.ContainsKey(message.type) && routes[message.type].ContainsKey(message.subtype ?? "null"))
            {
                object o = null;
                if (routing.ContainsKey(message.type) && routing[message.type].ContainsKey(message.subtype ?? "null"))
                    o = JsonConvert.DeserializeObject(data, routing[message.type][message.subtype ?? "null"], new JavascriptDateTimeConverter());
                else
                {
                    //I believe this method is slower than the former. If I'm wrong we can just use this instead. :D
                    Type t = routes[message.type][message.subtype ?? "null"].Method.GetParameters()[0].ParameterType;
                    o = JsonConvert.DeserializeObject(data, t, new JavascriptDateTimeConverter());
                }
                routes[message.type][message.subtype ?? "null"].DynamicInvoke(o);
            }
            else
                System.Diagnostics.Debug.WriteLine(string.Format("No valid route for {0} - {1}", message.type, message.subtype ?? "null"));
        }

        void HandleSending(object stateful)
        {
            string message;
            while (sendingQueue.Pop(out message) && socket.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
            {
                byte[] sending = Encoding.UTF8.GetBytes(message);
                ArraySegment<byte> buffer = new ArraySegment<byte>(sending);
                socket.SendAsync(buffer, WebSocketMessageType.Text, true, cts.Token).Wait();
            }

            currentlySending = 0;
        }

		public void Close()
		{
			this.socket.Abort();
		}
    }

    public class SlackSocketMessage
    {
        public int id;
        public int reply_to;
        public string type;
        public string subtype;
        public bool ok;
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public class SlackSocketRouting : Attribute
    {
        public string Type;
        public string SubType;
        public SlackSocketRouting(string type, string subtype = null)
        {
            this.Type = type;
            this.SubType = subtype;
        }
    }
}