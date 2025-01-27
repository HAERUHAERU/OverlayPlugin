﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using WebSocketSharp;
using WebSocketSharp.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Advanced_Combat_Tracker;
using RainbowMage.OverlayPlugin.Overlays;

namespace RainbowMage.OverlayPlugin
{
    class WSServer
    {
        HttpServer _server;
        static bool _failed = false;
        static WSServer _inst = null;

        public static EventHandler<StateChangedArgs> OnStateChanged;

        public static void Initialize()
        {
            _inst = new WSServer();
        }

        public static void Stop()
        {
            if (_inst != null)
            {
                try
                {
                    _inst._server.Stop();
                }
                catch (Exception e)
                {
                    Log(LogLevel.Error, Resources.WSShutdownError, e);
                }
                _inst = null;
                _failed = false;

                OnStateChanged(null, new StateChangedArgs(false, false));
            }
        }

        public static bool IsRunning()
        {
            return _inst != null && _inst._server != null && _inst._server.IsListening;
        }

        public static bool IsFailed()
        {
            return _failed;
        }

        public static bool IsSSLPossible()
        {
            return File.Exists(GetCertPath());
        }

        private WSServer()
        {
            var plugin = Registry.Resolve<PluginMain>();
            var cfg = plugin.Config;
            _failed = false;

            try
            {
                var sslPath = GetCertPath();
                var secure = cfg.WSServerSSL && File.Exists(sslPath);

                _server = new HttpServer(IPAddress.Parse(cfg.WSServerIP), cfg.WSServerPort, secure);
                _server.ReuseAddress = true;
                _server.Log.Output += (LogData d, string msg) =>
                {
                    Log(LogLevel.Info, "WS: {0}: {1} {2}", d.Level.ToString(), d.Message, msg);
                };
                _server.Log.Level = WebSocketSharp.LogLevel.Info;

                if (secure)
                {
                    Log(LogLevel.Debug, Resources.WSLoadingCert, sslPath);

                    // IMPORTANT: Do *not* change the password here. This is the default password that mkcert uses.
                    // If you use a different password here, you'd have to pass it to mkcert and update the text on the WSServer tab to match.
                    _server.SslConfiguration.ServerCertificate = new X509Certificate2(sslPath, "changeit");
                    _server.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls11 | System.Security.Authentication.SslProtocols.Tls12;
                }

                _server.AddWebSocketService<SocketHandler>("/ws");
                _server.AddWebSocketService<LegacyHandler>("/MiniParse");
                _server.AddWebSocketService<LegacyHandler>("/BeforeLogLineRead");

                _server.OnGet += (object sender, HttpRequestEventArgs e) =>
                {
                    if (e.Request.RawUrl == "/")
                    {
                        var builder = new StringBuilder();
                        builder.Append(@"<!DOCTYPE html>
<html>
    <head>
        <title>OverlayPlugin WSServer</title>
    </head>
    <script type='text/javascript'>
        window.addEventListener('load', () => {
            var links = document.querySelectorAll('a');
            for (var i = 0; i < links.length; i++) {
                var data = JSON.parse(links[i].getAttribute('data-info'));
                var endpoint = data.arg === 'OVERLAY_WS' ? 'ws' : '';
                links[i].href = data.url + '?' + data.arg + '=' + location.href.replace('http', 'ws') + endpoint;
                
                if (data.url.indexOf('file://') === 0) {
                    links[i].parentNode.innerText = 'Local: ' + links[i].innerText + ': ' + links[i].href;
                }
            }
        });
    </script>
    <body>
        " + Resources.WSIndexPage + @"
        <ul>");

                        foreach (var overlay in plugin.Overlays)
                        {
                            var argName = "OVERLAY_WS";
                            if (overlay.GetType() == typeof(MiniParseOverlay))
                            {
                                var config = (MiniParseOverlayConfig)overlay.Config;
                                if (config.ActwsCompatibility)
                                {
                                    argName = "HOST_PORT";
                                }
                            }

                            var jsonInfo = JsonConvert.SerializeObject(new
                            {
                                url = overlay.Config.Url,
                                arg = argName
                            });

                            // HTML escaping
                            jsonInfo = jsonInfo.Replace("\"", "&quot;").Replace(">", "&gt;");
                            var overlayName = overlay.Name.Replace("&", "&amp;").Replace("<", "&lt;");

                            builder.Append($"<li><a href=\"#\" data-info=\"{jsonInfo}\">{overlayName}</a></li>");
                        }

                        builder.Append("</body></html>");

                        var res = e.Response;
                        res.StatusCode = 200;
                        res.ContentType = "text/html";
                        Ext.WriteContent(res, Encoding.UTF8.GetBytes(builder.ToString()));
                    }
                };

                _server.Start();
                OnStateChanged?.Invoke(this, new StateChangedArgs(true, false));
            }
            catch(Exception e)
            {
                _failed = true;
                Log(LogLevel.Error, Resources.WSStartFailed, e);
                OnStateChanged?.Invoke(this, new StateChangedArgs(false, true));
            }
        }

        public static string GetCertPath()
        {
            var path = Path.Combine(
                ActGlobals.oFormActMain.AppDataFolder.FullName,
                "Config",
                "OverlayPluginSSL.p12");

            return path;
        }

        private static void Log(LogLevel level, string msg, params object[] args)
        {
            PluginMain.Logger.Log(level, msg, args);
        }

        public class SocketHandler : WebSocketBehavior, IEventReceiver
        {
            public void HandleEvent(JObject e)
            {
                SendAsync(e.ToString(Formatting.None), (success) =>
                {
                    if (!success)
                    {
                        Log(LogLevel.Error, Resources.WSMessageSendFailed, e);
                    }
                });
            }

            protected override void OnOpen()
            {

            }

            protected override void OnMessage(MessageEventArgs e)
            {
                JObject data = null;

                try
                {
                    data = JObject.Parse(e.Data);
                }
                catch(JsonException ex)
                {
                    Log(LogLevel.Error, Resources.WSInvalidDataRecv, ex, e.Data);
                    return;
                }

                if (!data.ContainsKey("call")) return;

                var msgType = data["call"].ToString();
                if (msgType == "subscribe")
                {
                    try
                    {
                        foreach (var item in data["events"].ToList())
                        {
                            EventDispatcher.Subscribe(item.ToString(), this);
                        }
                    } catch(Exception ex)
                    {
                        Log(LogLevel.Error, Resources.WSNewSubFail, ex);
                    }

                    return;
                } else if (msgType == "unsubscribe")
                {
                    try
                    {
                        foreach (var item in data["events"].ToList())
                        {
                            EventDispatcher.Unsubscribe(item.ToString(), this);
                        }
                    } catch (Exception ex)
                    {
                        Log(LogLevel.Error, Resources.WSUnsubFail, ex);
                    }
                    return;
                }

                Task.Run(() => {
                    try
                    {
                        var response = EventDispatcher.CallHandler(data);

                        if (response == null) {
                            response = new JObject();
                            response["$isNull"] = true;
                        }

                        if (data.ContainsKey("rseq")) {
                            response["rseq"] = data["rseq"];
                        }

                        Send(response.ToString(Formatting.None));
                    } catch(Exception ex)
                    {
                        Log(LogLevel.Error, Resources.WSHandlerException, ex);
                    }
                });
            }


            protected override void OnClose(CloseEventArgs e)
            {
                EventDispatcher.UnsubscribeAll(this);
            }
        }

        private class LegacyHandler : WebSocketBehavior, IEventReceiver
        {
            protected override void OnOpen()
            {
                base.OnOpen();

                EventDispatcher.Subscribe("CombatData", this);
                EventDispatcher.Subscribe("LogLine", this);
                EventDispatcher.Subscribe("ChangeZone", this);
                EventDispatcher.Subscribe("ChangePrimaryPlayer", this);

                Send(JsonConvert.SerializeObject(new
                {
                    type = "broadcast",
                    msgtype = "SendCharName",
                    msg = new
                    {
                        charName = FFXIVRepository.GetPlayerName() ?? "YOU",
                        charID = FFXIVRepository.GetPlayerID()
                    }
                }));
            }

            protected override void OnClose(CloseEventArgs e)
            {
                base.OnClose(e);

                EventDispatcher.UnsubscribeAll(this);
            }

            public void HandleEvent(JObject e)
            {
                switch (e["type"].ToString())
                {
                    case "CombatData":
                        Send("{\"type\":\"broadcast\",\"msgtype\":\"CombatData\",\"msg\":" + e.ToString(Formatting.None) + "}");
                        break;
                    case "LogLine":
                        Send("{\"type\":\"broadcast\",\"msgtype\":\"Chat\",\"msg\":" + JsonConvert.SerializeObject(e["rawLine"].ToString()) + "}");
                        break;
                    case "ChangeZone":
                        Send("{\"type\":\"broadcast\",\"msgtype\":\"ChangeZone\",\"msg\":" + e.ToString(Formatting.None) + "}");
                        break;
                    case "ChangePrimaryPlayer":
                        Send("{\"type\":\"broadcast\",\"msgtype\":\"SendCharName\",\"msg\":" + e.ToString(Formatting.None) + "}");
                        break;
                }
            }
        }

        public class StateChangedArgs : EventArgs
        {
            public bool Running { get; private set; }
            public bool Failed { get; private set; }

            public StateChangedArgs(bool Running, bool Failed)
            {
                this.Running = Running;
                this.Failed = Failed;
            }
        }
    }
}
