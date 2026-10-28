using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MineTunnel.Common
{
    public class UpnpHelper
    {
        private string _controlUrl = null;
        private string _serviceType = null;
        private string _localIp = null;
        private string _externalIp = null;

        public bool IsAvailable { get { return !string.IsNullOrEmpty(_controlUrl); } }
        public string ExternalIp { get { return _externalIp; } }
        public string LocalIp { get { return _localIp; } }

        public UpnpHelper()
        {
            _localIp = GetLocalIpAddress();
        }

        public bool Discover(int timeoutMs = 2500)
        {
            try
            {
                string searchMessage =
                    "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n\r\n";

                byte[] data = Encoding.ASCII.GetBytes(searchMessage);
                IPEndPoint multicastEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, timeoutMs);
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    socket.SendTo(data, multicastEndpoint);

                    byte[] buffer = new byte[4096];
                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

                    while (DateTime.UtcNow < deadline)
                    {
                        try
                        {
                            EndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);
                            int received = socket.ReceiveFrom(buffer, ref senderEp);
                            string response = Encoding.ASCII.GetString(buffer, 0, received);

                            Match locationMatch = Regex.Match(response, @"LOCATION:\s*(http://[^\r\n]+)", RegexOptions.IgnoreCase);
                            if (locationMatch.Success)
                            {
                                string location = locationMatch.Groups[1].Value.Trim();
                                if (ParseDeviceDescription(location))
                                {
                                    GetExternalIpAddress();
                                    return true;
                                }
                            }
                        }
                        catch (SocketException)
                        {
                            break; // Timeout
                        }
                    }
                }
            }
            catch
            {
                // UPnP discovery failed
            }

            return false;
        }

        private bool ParseDeviceDescription(string locationUrl)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(locationUrl);
                request.Timeout = 3000;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string xml = reader.ReadToEnd();

                    // Check for WANIPConnection or WANPPPConnection
                    string[] services = new string[] {
                        "urn:schemas-upnp-org:service:WANIPConnection:1",
                        "urn:schemas-upnp-org:service:WANPPPConnection:1"
                    };

                    foreach (string sType in services)
                    {
                        int sIndex = xml.IndexOf(sType, StringComparison.OrdinalIgnoreCase);
                        if (sIndex >= 0)
                        {
                            int controlStart = xml.IndexOf("<controlURL>", sIndex, StringComparison.OrdinalIgnoreCase);
                            int controlEnd = xml.IndexOf("</controlURL>", sIndex, StringComparison.OrdinalIgnoreCase);

                            if (controlStart >= 0 && controlEnd > controlStart)
                            {
                                string relativeUrl = xml.Substring(controlStart + 12, controlEnd - (controlStart + 12)).Trim();
                                Uri baseUri = new Uri(locationUrl);
                                Uri resolvedUri = new Uri(baseUri, relativeUrl);
                                _controlUrl = resolvedUri.ToString();
                                _serviceType = sType;
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Parse error
            }
            return false;
        }

        public bool OpenPort(int port, string protocol = "TCP", string description = "MineTunnel Minecraft")
        {
            if (!IsAvailable) return false;

            try
            {
                string soapBody = string.Format(
                    "<?xml version=\"1.0\"?>\r\n" +
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                    "  <s:Body>\r\n" +
                    "    <u:AddPortMapping xmlns:u=\"{0}\">\r\n" +
                    "      <NewRemoteHost></NewRemoteHost>\r\n" +
                    "      <NewExternalPort>{1}</NewExternalPort>\r\n" +
                    "      <NewProtocol>{2}</NewProtocol>\r\n" +
                    "      <NewInternalPort>{1}</NewInternalPort>\r\n" +
                    "      <NewInternalClient>{3}</NewInternalClient>\r\n" +
                    "      <NewEnabled>1</NewEnabled>\r\n" +
                    "      <NewPortMappingDescription>{4}</NewPortMappingDescription>\r\n" +
                    "      <NewLeaseDuration>0</NewLeaseDuration>\r\n" +
                    "    </u:AddPortMapping>\r\n" +
                    "  </s:Body>\r\n" +
                    "</s:Envelope>",
                    _serviceType, port, protocol, _localIp, description);

                return ExecuteSoapAction("AddPortMapping", soapBody);
            }
            catch
            {
                return false;
            }
        }

        public bool ClosePort(int port, string protocol = "TCP")
        {
            if (!IsAvailable) return false;

            try
            {
                string soapBody = string.Format(
                    "<?xml version=\"1.0\"?>\r\n" +
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                    "  <s:Body>\r\n" +
                    "    <u:DeletePortMapping xmlns:u=\"{0}\">\r\n" +
                    "      <NewRemoteHost></NewRemoteHost>\r\n" +
                    "      <NewExternalPort>{1}</NewExternalPort>\r\n" +
                    "      <NewProtocol>{2}</NewProtocol>\r\n" +
                    "    </u:DeletePortMapping>\r\n" +
                    "  </s:Body>\r\n" +
                    "</s:Envelope>",
                    _serviceType, port, protocol);

                return ExecuteSoapAction("DeletePortMapping", soapBody);
            }
            catch
            {
                return false;
            }
        }

        private string GetExternalIpAddress()
        {
            if (!IsAvailable) return null;

            try
            {
                string soapBody = string.Format(
                    "<?xml version=\"1.0\"?>\r\n" +
                    "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                    "  <s:Body>\r\n" +
                    "    <u:GetExternalIPAddress xmlns:u=\"{0}\" />\r\n" +
                    "  </s:Body>\r\n" +
                    "</s:Envelope>",
                    _serviceType);

                string responseXml = ExecuteSoapActionWithResponse("GetExternalIPAddress", soapBody);
                if (!string.IsNullOrEmpty(responseXml))
                {
                    Match m = Regex.Match(responseXml, @"<NewExternalIPAddress>(.*?)</NewExternalIPAddress>");
                    if (m.Success)
                    {
                        _externalIp = m.Groups[1].Value.Trim();
                        return _externalIp;
                    }
                }
            }
            catch
            {
                // Fallback
            }

            return null;
        }

        private bool ExecuteSoapAction(string action, string soapBody)
        {
            string resp = ExecuteSoapActionWithResponse(action, soapBody);
            return resp != null && !resp.Contains("<faultcode>");
        }

        private string ExecuteSoapActionWithResponse(string action, string soapBody)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(_controlUrl);
                req.Method = "POST";
                req.ContentType = "text/xml; charset=\"utf-8\"";
                req.Headers.Add("SOAPACTION", string.Format("\"{0}#{1}\"", _serviceType, action));
                req.Timeout = 4000;

                byte[] bodyBytes = Encoding.UTF8.GetBytes(soapBody);
                req.ContentLength = bodyBytes.Length;

                using (Stream reqStream = req.GetRequestStream())
                {
                    reqStream.Write(bodyBytes, 0, bodyBytes.Length);
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    using (StreamReader r = new StreamReader(ex.Response.GetResponseStream()))
                    {
                        return r.ReadToEnd();
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        public static string GetLocalIpAddress()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    if (endPoint != null)
                    {
                        return endPoint.Address.ToString();
                    }
                }
            }
            catch
            {
                // Fallback
            }

            try
            {
                foreach (IPAddress ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        return ip.ToString();
                    }
                }
            }
            catch { }

            return "127.0.0.1";
        }
    }
}
