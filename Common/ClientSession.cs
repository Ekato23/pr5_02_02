using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Common
{
    public class ClientSession
    {
        public string UniqueCode { get; set; } 
        public DateTime ConnectTime { get; set; } 
        public IPAddress ClientIp { get; set; } 
        public int ClientPort { get; set; } 
        public Socket Socket { get; set; } 
        public DateTime LastConfirmationTime { get; set; }
        public bool IsLicensed { get; set; }
    }
}
