using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SocketManagerNS
{
    public class ConnectionSettings
    {
        //Public
        public string ConnectionString { get; set; } = string.Empty;

        public string IPAddressString => ConnectionString.Split(':')[0];
        public string PortString => ConnectionString.Contains(":") ? ConnectionString.Split(':')[1] : string.Empty;

        public System.Net.IPAddress IPAddress => IsIPAddressValid() ? System.Net.IPAddress.Parse(ConnectionString.Split(':')[0]) : null;
        public int Port => IsPortValid() ? int.Parse(ConnectionString.Split(':')[1]) : 0;
        private bool IsIPAddressValid()
        {
            System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex(@"^((0|1[0-9]{0,2}|2[0-9]?|2[0-4][0-9]|25[0-5]|[3-9][0-9]?)\.){3}(0|1[0-9]{0,2}|2[0-9]?|2[0-4][0-9]|25[0-5]|[3-9][0-9]?)$");
            return regex.IsMatch(ConnectionString.Split(':')[0]);
        }
        private bool IsPortValid()
        {
            if (ConnectionString.Contains(":"))
            {
                System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex(@"^([0-9]{1,4}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5])$");
                return regex.IsMatch(ConnectionString.Split(':')[1]);
            }
            else
                return false;
        }

        public ConnectionSettings(string connectionString) { ConnectionString = connectionString; }
        //Public Static
        public static string GenerateConnectionString(string ip, int port) => $"{ip}:{port}";
        public static string GenerateConnectionString(System.Net.IPAddress ip, int port) => $"{ip}:{port}";
        public static bool ValidateConnectionString(string connectionString)
        {
            if (connectionString == null) return false;

            if (connectionString.Count(c => c == ':') < 1) return false;
            string[] spl = connectionString.Split(':');

            if (!System.Net.IPAddress.TryParse(spl[0], out System.Net.IPAddress ip)) return false;

            if (!int.TryParse(spl[1], out int port)) return false;

            return true;
        }

    }
}
