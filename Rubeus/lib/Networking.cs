using System;
using System.ComponentModel;
using System.DirectoryServices;
using System.DirectoryServices.Protocols;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading;
using SearchScope = System.DirectoryServices.Protocols.SearchScope;
using System.IO;
using System.Linq;

namespace Rubeus
{
    public class Networking
    {
        public static string GetDCName(string domainName = "")
        {
            // retrieves the current domain controller name

            // adapted from https://www.pinvoke.net/default.aspx/netapi32.dsgetdcname
            Interop.DOMAIN_CONTROLLER_INFO domainInfo;
            const int ERROR_SUCCESS = 0;
            IntPtr pDCI = IntPtr.Zero;

            int val = Interop.DsGetDcName("", domainName, 0, "", Interop.DSGETDCNAME_FLAGS.DS_DIRECTORY_SERVICE_REQUIRED |
                                                                 Interop.DSGETDCNAME_FLAGS.DS_RETURN_DNS_NAME |
                                                                 Interop.DSGETDCNAME_FLAGS.DS_IP_REQUIRED, out pDCI);
            try
            {
                if (ERROR_SUCCESS == val)
                {
                    domainInfo = (Interop.DOMAIN_CONTROLLER_INFO)Marshal.PtrToStructure(pDCI, typeof(Interop.DOMAIN_CONTROLLER_INFO));
                    string dcName = domainInfo.DomainControllerName;
                    return dcName.Trim('\\');
                }
                else
                {
                    string errorMessage = new Win32Exception((int)val).Message;
                    Console.WriteLine("\r\n[X] Error {0} retrieving domain controller : {1}", val, errorMessage);
                    if (string.IsNullOrEmpty(domainName))
                    {
                        Console.WriteLine("\n [*] Attempting to get PDC role owner to use as DC");
                        return System.DirectoryServices.ActiveDirectory.Domain.GetCurrentDomain().PdcRoleOwner.Name;
                    }
                    else
                    {
                        throw new RubeusException(errorMessage);
                    }
                }
            }
            finally
            {
                Interop.NetApiBufferFree(pDCI);
            }
        }

        public static string GetDCIP(string DCName, bool display = true, string domainName = "")
        {
            if (String.IsNullOrEmpty(DCName))
            {
                DCName = GetDCName(domainName);
            }
            Match match = Regex.Match(DCName, @"([0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(\d{1,3}\.){3}\d{1,3}");
            if (match.Success)
            {
                if (display)
                {
                    Console.WriteLine("[*] Using domain controller: {0}", DCName);
                }
                return DCName;
            }
            else
            {
                try
                {
                    // If we call GetHostAddresses with an empty string, it will return IP addresses for localhost instead of DC
                    if (String.IsNullOrEmpty(DCName))
                    {
                        Console.WriteLine("[X] Error: No domain controller could be located");
                        return null;
                    }
                    System.Net.IPAddress[] dcIPs = System.Net.Dns.GetHostAddresses(DCName);

                    foreach (System.Net.IPAddress dcIP in dcIPs)
                    {
                        if (dcIP.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || dcIP.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        {
                            if (display)
                            {
                                Console.WriteLine("[*] Using domain controller: {0} ({1})", DCName, dcIP);
                            }
                            return String.Format("{0}", dcIP);
                        }
                    }
                    Console.WriteLine("[X] Error resolving hostname '{0}' to an IP address: no IPv4 or IPv6 address found", DCName);
                    return null;
                }
                catch (Exception e)
                {
                    Console.WriteLine("[X] Error resolving hostname '{0}' to an IP address: {1}", DCName, e.Message);
                    return null;
                }
            }
        }

        public static string GetDCNameFromIP(string IP)
        {
            Match match = Regex.Match(IP, @"([0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(\d{1,3}\.){3}\d{1,3}");
            if (match.Success)
            {
                try
                {
                    System.Net.IPHostEntry DC = System.Net.Dns.GetHostEntry(IP);
                    return DC.HostName;
                }
                catch (Exception e)
                {
                    Console.WriteLine("[X] Error resolving IP address '{0}' to a name: {1}", IP, e.Message);
                    return null;
                }
            }
            return IP;
        }

        public static byte[] SendBytes(string server, int port, byte[] data)
        {
            var ipEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(server), port);
            try
            {
                using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient(ipEndPoint.AddressFamily))
                {

                    // connect to the server over The specified port
                    client.Client.Ttl = 128;
                    client.Connect(ipEndPoint);
                    BinaryReader socketReader = new BinaryReader(client.GetStream());
                    BinaryWriter socketWriter = new BinaryWriter(client.GetStream());

                    socketWriter.Write(System.Net.IPAddress.HostToNetworkOrder(data.Length));
                    socketWriter.Write(data);

                    int recordMark = System.Net.IPAddress.NetworkToHostOrder(socketReader.ReadInt32());
                    int recordSize = recordMark & 0x7fffffff;

                    if ((recordMark & 0x80000000) > 0)
                    {
                        Console.WriteLine("[X] Unexpected reserved bit set on response record mark from Domain Controller {0}:{1}, aborting", server, port);
                        return null;
                    }

                    byte[] responseRecord = socketReader.ReadBytes(recordSize);

                    if (responseRecord.Length != recordSize)
                    {
                        Console.WriteLine("[X] Incomplete record received from Domain Controller {0}:{1}, aborting", server, port);
                        return null;
                    }

                    return responseRecord;
                }
            }
            catch (System.Net.Sockets.SocketException e)
            {
                if (e.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut)
                {
                    Console.WriteLine("[X] Error connecting to {0}:{1} : {2}", server, port, e.Message);
                }
                else
                {
                    Console.WriteLine("[X] Failed to get response from Domain Controller {0}:{1} : {2}", server, port, e.Message);
                }

            }
            catch (FormatException fe)
            {
                Console.WriteLine("[X] Error parsing IP address {0} : {1}", server, fe.Message);
            }

            return null;
        }

        public static DirectoryEntry GetLdapSearchRoot(System.Net.NetworkCredential cred, string OUName, string domainController, string domain)
        {
            DirectoryEntry directoryObject = null;
            string ldapPrefix = "";
            string ldapOu = "";

            //If we have a DC then use that instead of the domain name so that this works if user doesn't have
            //name resolution working but specified the IP of a DC
            if (!String.IsNullOrEmpty(domainController))
            {
                ldapPrefix = domainController;
            }
            else if (!String.IsNullOrEmpty(domain)) //If we don't have a DC then use the domain name (if we have one)
            {
                ldapPrefix = domain;
            }
            else if (cred != null) //If we don't have a DC or a domain name but have credentials, get domain name from them
            {
                ldapPrefix = cred.Domain;
            }

            if (!String.IsNullOrEmpty(OUName))
            {
                ldapOu = OUName.Replace("ldap", "LDAP").Replace("LDAP://", "");
            }
            else if (!String.IsNullOrEmpty(domain))
            {
                ldapOu = String.Format("DC={0}", domain.Replace(".", ",DC="));
            }

            //If no DC, domain, credentials, or OU were specified
            if (String.IsNullOrEmpty(ldapPrefix) && String.IsNullOrEmpty(ldapOu))
            {
                directoryObject = new DirectoryEntry();

            }
            else //If we have a prefix (DC or domain), an OU path, or both
            {
                string bindPath = "";
                if (!String.IsNullOrEmpty(ldapPrefix))
                {
                    bindPath = String.Format("LDAP://{0}", ldapPrefix);
                }
                if (!String.IsNullOrEmpty(ldapOu))
                {
                    if (!String.IsNullOrEmpty(bindPath))
                    {
                        bindPath = String.Format("{0}/{1}", bindPath, ldapOu);
                    }
                    else
                    {
                        bindPath = String.Format("LDAP://{0}", ldapOu);
                    }
                }

                directoryObject = new DirectoryEntry(bindPath);
            }

            if (cred != null)
            {
                // if we're using alternate credentials for the connection
                string userDomain = String.Format("{0}\\{1}", cred.Domain, cred.UserName);
                directoryObject.Username = userDomain;
                directoryObject.Password = cred.Password;

                // Removed credential validation check because it just caused problems and doesn't gain us anything (if invalid
                // credentials are specified, the LDAP search will fail with "Logon failure: bad username or password" anyway)

                //string contextTarget = "";
                //if (!string.IsNullOrEmpty(domainController))
                //{
                //    contextTarget = domainController;
                //}
                //else
                //{
                //    contextTarget = cred.Domain;
                //}

                //using (PrincipalContext pc = new PrincipalContext(ContextType.Domain, contextTarget))
                //{
                //    if (!pc.ValidateCredentials(cred.UserName, cred.Password))
                //    {
                //        throw new Exception(String.Format("\r\n[X] Credentials supplied for '{0}' are invalid!", userDomain));
                //    }
                //    else
                //    {
                //        Console.WriteLine("[*] Using alternate creds  : {0}", userDomain);
                //    }
                //}
            }
            return directoryObject;
        }

        public static List<IDictionary<String, Object>> GetLdapQuery(System.Net.NetworkCredential cred, string OUName, string domainController, string domain, string filter, bool ldaps = false)
        {
            var ActiveDirectoryObjects = new List<IDictionary<String, Object>>();
            if (String.IsNullOrEmpty(domainController))
            {
                domainController = Networking.GetDCName(domain); //if domain is null, this will try to find a DC in current user's domain
            }
            if (String.IsNullOrEmpty(domainController))
            {
                throw new RubeusException("Unable to retrieve domain controller name from domain. Try specifying a DC manually");
            }

            if (ldaps)
            {
                LdapConnection ldapConnection = null;
                SearchResponse response = null;
                List<SearchResultEntry> result = new List<SearchResultEntry>();
                // perhaps make this dynamic?
                int maxResultsToRequest = 1000;

                try
                {
                    var serverId = new LdapDirectoryIdentifier(domainController, 636);
                    ldapConnection = new LdapConnection(serverId, cred);
                    ldapConnection.SessionOptions.SecureSocketLayer = true;
                    ldapConnection.SessionOptions.VerifyServerCertificate += delegate { return true; };
                    ldapConnection.Bind();
                }
                catch (Exception ex)
                {
                    if (ex.InnerException != null)
                    {
                        throw new RubeusException(String.Format("Error binding to LDAP server: {0}", ex.InnerException.Message));
                    }
                    else
                    {
                        throw new RubeusException(String.Format("Error binding to LDAP server: {0}", ex.Message));
                    }
                }

                if (String.IsNullOrEmpty(OUName))
                {
                    OUName = String.Format("DC={0}", domain.Replace(".", ",DC="));
                }

                try
                {
                    Console.WriteLine("[*] Searching path '{0}' for '{1}'", OUName, filter);
                    PageResultRequestControl pageRequestControl = new PageResultRequestControl(maxResultsToRequest);
                    PageResultResponseControl pageResponseControl;
                    SearchRequest request = new SearchRequest(OUName, filter, SearchScope.Subtree, null);
                    request.Controls.Add(pageRequestControl);
                    while (true)
                    {
                        response = (SearchResponse)ldapConnection.SendRequest(request);
                        foreach (SearchResultEntry entry in response.Entries)
                        {
                            result.Add(entry);
                        }
                        pageResponseControl = (PageResultResponseControl)response.Controls[0];
                        if (pageResponseControl.Cookie.Length == 0)
                            break;
                        pageRequestControl.Cookie = pageResponseControl.Cookie;
                    }
                }
                catch (Exception ex)
                {
                    throw new RubeusException(String.Format("Error executing LDAP query: {0}", ex.Message));
                }

                if (response.ResultCode == ResultCode.Success)
                {
                    ActiveDirectoryObjects = Helpers.GetADObjects(result);
                }
                else
                {
                    throw new RubeusException(String.Format("Error executing LDAP query: LDAP search request returned error code {0}", response.ResultCode.ToString()));
                }
            }
            else // If not using LDAPS
            {
                using (DirectoryEntry directoryObject = Networking.GetLdapSearchRoot(cred, OUName, domainController, domain))
                {
                    // check to ensure that the bind worked correctly
                    try
                    {
                        string dirPath = directoryObject.Path;
                        if (String.IsNullOrEmpty(dirPath))
                        {
                            Console.WriteLine("[*] Searching the current domain for '{0}'", filter);
                        }
                        else
                        {
                            Console.WriteLine("[*] Searching path '{0}' for '{1}'", dirPath, filter);
                        }
                    }
                    catch (DirectoryServicesCOMException ex)
                    {
                        if (!String.IsNullOrEmpty(OUName))
                        {
                            throw new RubeusException(String.Format("Error validating the domain searcher for bind path \"{0}\" : {1}", OUName, ex.Message));
                        }
                        else
                        {
                            throw new RubeusException(String.Format("Error validating the domain searcher: {0}", ex.Message));
                        }
                    }
                    using (DirectorySearcher searcher = new DirectorySearcher(directoryObject))
                    {
                        // enable LDAP paged search to get all results, by pages of 1000 items
                        searcher.PageSize = 1000;
                        searcher.Filter = filter;
                        try
                        {
                            using (SearchResultCollection results = searcher.FindAll())
                            {
                                if (results.Count == 0)
                                {
                                    Console.WriteLine("[X] No results returned by LDAP!");
                                }
                                else
                                {
                                    ActiveDirectoryObjects = Helpers.GetADObjects(results);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (ex.InnerException != null)
                            {
                                throw new RubeusException(String.Format("Error executing the domain searcher: {0}", ex.InnerException.Message));
                            }
                            else
                            {
                                throw new RubeusException(String.Format("Error executing the domain searcher: {0}", ex.Message));
                            }
                        }
                    }
                }
            }

            return ActiveDirectoryObjects;
        }

        // implementation adapted from https://github.com/tevora-threat/SharpView
        public static Dictionary<string, Dictionary<string, Object>> GetGptTmplContent(string path, System.Net.NetworkCredential credentials)
        {
            Dictionary<string, Dictionary<string, Object>> IniObject = new Dictionary<string, Dictionary<string, Object>>();
            string sysvolPath = String.Format("\\\\{0}\\SYSVOL", (new System.Uri(path).Host));
            string username = null;
            string password = null;
            if (credentials != null)
            {
                if (!String.IsNullOrEmpty(credentials.Domain))
                {
                    username = credentials.Domain + "\\" + credentials.UserName;
                }
                else
                {
                    username = credentials.UserName;
                }

                password = credentials.Password;
            }

            int result = AddRemoteConnection(null, sysvolPath, username, password);
            if (result != (int)Interop.SystemErrorCodes.ERROR_SUCCESS)
            {
                // What went wrong? I guess the caller will never know
                return null;
            }

            if (System.IO.File.Exists(path))
            {
                var content = File.ReadAllLines(path);
                var CommentCount = 0;
                var Section = "";
                foreach (var line in content)
                {
                    if (Regex.IsMatch(line, @"^\[(.+)\]"))
                    {
                        Section = Regex.Split(line, @"^\[(.+)\]")[1].Trim();
                        Section = Regex.Replace(Section, @"\s+", "");
                        IniObject[Section] = new Dictionary<string, object>();
                        CommentCount = 0;
                    }
                    else if (Regex.IsMatch(line, @"^(;.*)$"))
                    {
                        var Value = Regex.Split(line, @"^(;.*)$")[1].Trim();
                        CommentCount = CommentCount + 1;
                        var Name = @"Comment" + CommentCount;
                        IniObject[Section][Name] = Value;
                    }
                    else if (Regex.IsMatch(line, @"(.+?)\s*=(.*)"))
                    {
                        var matches = Regex.Split(line, @"=");
                        var Name = Regex.Replace(matches[0].Trim(), @"\s+", "");
                        var Value = Regex.Replace(matches[1].Trim(), @"\s+", "");
                        // var Values = Value.Split(',').Select(x => x.Trim());

                        // if ($Values -isnot [System.Array]) { $Values = @($Values) }

                        IniObject[Section][Name] = Value;
                    }
                }
            }

            result = RemoveRemoteConnection(null, sysvolPath);

            return IniObject;
        }


        // Comment by VbScrub:
        // So many questions...
        // Why are we using a list when we only ever add one item to it?
        // Why don't we just use "new" like normal instead of "Activator.CreateInstance(typeof(Interop.NetResource))" ?
        // Why are all of the arguments optional when we need at least one of them for this to work?
        // Why do we set the RemoteName property to the exact same value twice?
        public static int AddRemoteConnection(string host = null, string path = null, string user = null, string password = null)
        {
            var NetResourceInstance = Activator.CreateInstance(typeof(Interop.NetResource)) as Interop.NetResource;
            List<string> paths = new List<string>();
            int returnResult = 0;

            if (host != null)
            {
                string targetComputerName = host.Trim('\\');
                paths.Add(String.Format("\\\\{0}\\IPC$", targetComputerName));
            }
            else
            {
                paths.Add(path);
            }

            foreach (string targetPath in paths)
            {
                NetResourceInstance.RemoteName = targetPath;
                NetResourceInstance.ResourceType = Interop.ResourceType.Disk;

                NetResourceInstance.RemoteName = targetPath;

                Console.WriteLine("[*] Attempting to mount: {0}", targetPath);


                int result = Interop.WNetAddConnection2(NetResourceInstance, password, user, 4);

                if (result == (int)Interop.SystemErrorCodes.ERROR_SUCCESS)
                {
                    Console.WriteLine("[*] {0} successfully mounted", targetPath);
                }
                else
                {
                    Console.WriteLine("[X] Error mounting {0} error code {1} ({2})", targetPath, (Interop.SystemErrorCodes)result, result);
                    returnResult = result;
                }
            }
            return returnResult;
        }

        public static int RemoveRemoteConnection(string host = null, string path = null)
        {

            List<string> paths = new List<string>();
            int returnResult = 0;

            if (host != null)
            {
                string targetComputerName = host.Trim('\\');
                paths.Add(String.Format("\\\\{0}\\IPC$", targetComputerName));
            }
            else
            {
                paths.Add(path);
            }

            foreach (string targetPath in paths)
            {
                Console.WriteLine("[*] Attempting to unmount: {0}", targetPath);
                int result = Interop.WNetCancelConnection2(targetPath, 0, true);

                if (result == 0)
                {
                    Console.WriteLine("[*] {0} successfully unmounted", targetPath);
                }
                else
                {
                    Console.WriteLine("[X] Error unmounting {0}", targetPath);
                    returnResult = result;
                }
            }
            return returnResult;
        }
    }
}

