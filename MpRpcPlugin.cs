using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MissionPlanner.Plugin;
using MpRpc.Router;
using MpRpc.Services;
using MpRpc.Transport;

namespace MpRpc
{
    public class MpRpcPlugin : Plugin
    {
        private WebSocketGateway _gateway;
        private RpcCommandRouter _router;
        private RpcServiceRegistry _services;

        public override string Author => "Ren Otsuki";

        public override string Name => "MpRpc";

        public override string Version => "0.1.0";

        public override bool Init()
        {
            return true;
        }

        public override bool Loaded()
        {
            _router = new RpcCommandRouter();
            _services = new RpcServiceRegistry();
            _services.RegisterAll(_router);
            _gateway = new WebSocketGateway("http://127.0.0.1:18081/rpc/", _router);
            _gateway.Start();
            return true;
        }

        public override bool Exit()
        {
            _gateway?.Dispose();
            _gateway = null;
            _services = null;
            _router = null;
            return true;
        }
    }
}
