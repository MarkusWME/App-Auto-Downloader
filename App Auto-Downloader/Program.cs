using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;

namespace App_Auto_Downloader
{
    class Program
    {
        private const string DEFAULT_OUTPUT_DIR = "AppDownload";
        private const bool DEFAULT_DONT_CHECK_SIMPLE_DOWNLOAD = false;
        private const int DEFAULT_MAX_RECURSION_DEPTH = 10;

        private static string outputDirectory;
        private static bool dontCheckSimpleDownload;
        private static int maxRecursionDepth;

        private static Dictionary<string, string> currentVersions = new Dictionary<string, string>();
        private static Regex urlRegex = new Regex("href=[\"']((https?)?([A-Za-z0-9 \\-._~\\/?#\\[\\]@:$&'\\(\\)*+,;=%!]*))[\"'][\\s\\\\>]");
        private static Regex urlUnescapedRegex = new Regex("href=((https?)?([A-Za-z0-9 \\-._~\\/?#\\[\\]@:$&'\\(\\)*+,;=%!]*))[\\s\\\\>]");
        private static Regex srcRegex = new Regex("src=[\"']((https?)?([A-Za-z0-9 \\-._~\\/?#\\[\\]@:$&'\\(\\)*+,;=%!]*))[\"'][\\s\\\\>]");

        private enum DownloadType
        {
            ConfirmDownload,
            DontCheckFormat,
            Recursive,
            ScanPage,
            SimpleDownload,
            SimpleParameterDownload
        }

        private struct AppElement
        {
            public int RemoveHash { get; set; }
            public string Name { get; set; }
            public string Format { get; set; }
            public string ExtractFormat { get; set; }
            public Regex Extract { get; set; }
            public string Link { get; set; }
            public List<string> Keywords { get; set; }
            public List<string> Filters { get; set; }
            public Dictionary<string, string> Commands { get; set; }
            public Dictionary<string, string> RequestHeaders { get; set; }
            public DownloadType Type { get; set; }

            public AppElement(string name = "", string format = "", string link = "")
            {
                RemoveHash = 0;
                Name = name;
                Format = format;
                ExtractFormat = "";
                Extract = null;
                Link = link;
                Keywords = new List<string>();
                Filters = new List<string>();
                Commands = new Dictionary<string, string>();
                RequestHeaders = new Dictionary<string, string>();
                Type = DownloadType.ScanPage;
            }
        }

        public static void Main(string[] args)
        {
            outputDirectory = DEFAULT_OUTPUT_DIR;
            dontCheckSimpleDownload = DEFAULT_DONT_CHECK_SIMPLE_DOWNLOAD;
            maxRecursionDepth = DEFAULT_MAX_RECURSION_DEPTH;
            int downloaded = 0;
            int ignored = 0;
            AppElement[] apps = ReadConfig();
            WriteInfo("Loaded app list: " + apps.Length + " apps found");
            if (apps.Length > 0)
            {
                try
                {
                    if (!Directory.Exists(outputDirectory))
                    {
                        Directory.CreateDirectory(outputDirectory);
                    }
                }
                catch (Exception e)
                {
                    WriteError("Failed to create output directory: " + e.Message);
                }
                ReadCurrentVersions();
                List<string> downloadLinks;
                string downloadLink;
                for (int i = 0; i < apps.Length; i++)
                {
                    WriteAction("Checking application \"" + apps[i].Name + "\" (" + apps[i].Link + ")");
                    downloadLinks = GetDownloadLinks(apps[i]);
                    if (downloadLinks.Count == 1)
                    {
                        downloadLink = downloadLinks[0];
                        if (CheckFile(apps[i], downloadLink))
                        {
                            downloaded++;
                        }
                        else
                        {
                            ignored++;
                        }
                    }
                    else
                    {
                        WriteError("Could not identify download link for " + apps[i].Name);
                        WriteInfo("Edit keywords to get better results! (" + downloadLinks.Count + " links found)");
                        for (int j = 0; j < downloadLinks.Count; j++)
                        {
                            WriteInfo("Link " + (j + 1) + ": " + downloadLinks[j]);
                        }
                        ignored++;
                    }

                }
                WriteInfo("Downloaded " + downloaded + " apps, no update needed or possible for " + ignored + " apps - total " + apps.Length + " apps");
                SaveCurrentVersions();
            }
            WriteInfo("App Auto-Downloader finished");
            #if DEBUG
                Console.ReadLine();
            #endif
        }

        private static List<string> GetDownloadLinks(AppElement application)
        {
            List<string> downloadLinks = new List<string>();
            string downloadLink;
            try
            {
                switch (application.Type)
                {
                    case DownloadType.SimpleDownload:
                    case DownloadType.SimpleParameterDownload:
                        downloadLinks.Add(application.Link);
                        break;
                    default:
                        ServicePointManager.Expect100Continue = true;
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                        ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
                        using (WebClient webClient = new WebClient())
                        {
                            foreach (string requestHeader in application.RequestHeaders.Keys)
                            {
                                webClient.Headers.Set(requestHeader, application.RequestHeaders[requestHeader]);
                            }
                            string html = webClient.DownloadString(application.Link);
                            MatchCollection matches = null;
                            if (application.Commands.ContainsKey("FIND"))
                            {
                                matches = new Regex(application.Commands["FIND"]).Matches(html);
                            }
                            if (matches == null || matches.Count == 0)
                            {
                                matches = (application.Commands.ContainsKey("GET") ? new Regex(application.Commands["GET"] + "=[\"']((https?)?([A-Za-z0-9\\-._~\\/?#\\[\\]@:$&'\\(\\)*+,;=%!]*))[\"']") : urlRegex).Matches(html);
                            }
                            if (matches == null || matches.Count == 0)
                            {
                                matches = urlUnescapedRegex.Matches(html);
                            }
                            foreach (Match match in matches)
                            {
                                if (application.Type == DownloadType.DontCheckFormat || application.Type == DownloadType.Recursive || match.Groups[1].ToString().EndsWith("." + application.Format))
                                {
                                    if (application.Commands.ContainsKey("SETHOST"))
                                    {
                                        downloadLink = application.Commands["SETHOST"] + match.Groups[1].ToString();
                                    }
                                    else
                                    {
                                        downloadLink = CorrectLink(application, match);
                                    }
                                    if (application.Commands.ContainsKey("APPEND"))
                                    {
                                        downloadLink += application.Commands["APPEND"];
                                    }
                                    if (application.Commands.ContainsKey("REPLACE"))
                                    {
                                        string[] replacement = application.Commands["REPLACE"].Split(';');
                                        for (int i = 0; i < replacement.Length; i += 2)
                                        {
                                            if (i + replacement.Length >= 2)
                                            {
                                                if (replacement[i + 1].StartsWith("GROUP"))
                                                {
                                                    int groupIndex = Convert.ToInt32(replacement[i + 1].Substring(5));
                                                    downloadLink = downloadLink.Replace(replacement[i], match.Groups[groupIndex].ToString());
                                                }
                                                else
                                                {
                                                    downloadLink = downloadLink.Replace(replacement[i], replacement[i + 1]);
                                                }
                                            }
                                        }
                                    }
                                    if (!downloadLinks.Contains(downloadLink))
                                    {
                                        downloadLinks.Add(downloadLink);
                                    }
                                }
                            }
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                WriteError("Failed to get download link: " + e.Message);
            }
            string keyword;
            bool hasRemovable = false;
            foreach (string link in downloadLinks.ToArray())
            {
                for (int i = 0; i < application.Keywords.Count; i++)
                {
                    keyword = application.Keywords[i];
                    if (keyword.Length > 0)
                    {
                        if (keyword.StartsWith("! "))
                        {
                            if (keyword.Length > 2 && link.Contains(keyword.Substring(2)))
                            {
                                downloadLinks.Remove(link);
                            }
                        }
                        else if (keyword.StartsWith("\\"))
                        {
                            hasRemovable = true;
                            if (keyword.Length > 2 && keyword.Contains(" ") && !link.Contains(keyword.Substring(keyword.IndexOf(" ") + 1)))
                            {
                                downloadLinks.Remove(link);
                            }
                        }
                        else if (keyword.StartsWith("+"))
                        {
                            hasRemovable = true;
                        }
                        else if (!link.Contains(keyword))
                        {
                            downloadLinks.Remove(link);
                        }
                    }
                }
            }
            if (hasRemovable)
            {
                for (int i = 0; i < application.Keywords.Count; i++)
                {
                    if (application.Keywords[i].StartsWith("\\"))
                    {
                        application.Keywords[i] = application.Keywords[i].Substring(1);
                        if (!application.Keywords[i].StartsWith("\\"))
                        {
                            application.Keywords.RemoveAt(i--);
                        }
                    }
                    else if (application.Keywords[i].StartsWith("+"))
                    {
                        if (application.Keywords[i].Length > 2)
                        {
                            if (application.Keywords[i][1] == ' ')
                            {
                                application.Keywords[i] = application.Keywords[i].Substring(2);
                            }
                            else
                            {
                                application.Keywords[i] = application.Keywords[i].Substring(1);
                            }
                        }
                        else
                        {
                            application.Keywords.RemoveAt(i);
                        }
                    }
                }
            }
            if (downloadLinks.Count > 1)
            {
                string resultLink = "";
                string tempLink;
                string[] version = null;
                string[] currentVersion = null;
                bool newer;
                int currentLength;
                foreach (string link in downloadLinks)
                {
                    newer = false;
                    tempLink = new Uri(link).PathAndQuery;
                    foreach (string filter in application.Filters)
                    {
                        tempLink = tempLink.Replace(filter, "");
                    }
                    tempLink = tempLink.Replace("/", "").Replace("_", "").Replace("." + application.Format, "");
                    if (version == null)
                    {
                        resultLink = link;
                        version = tempLink.Split('.');
                    }
                    else
                    {
                        try
                        {
                            Convert.ToInt32(tempLink);
                        }
                        catch
                        {
                            continue;
                        }
                        currentVersion = tempLink.Split('.');
                        if (currentVersion.Length > version.Length)
                        {
                            currentLength = version.Length;
                            Array.Resize(ref version, currentVersion.Length);
                            for (int j = currentLength; j < version.Length; j++)
                            {
                                version[j] = "0";
                            }
                        }
                        else if (version.Length > currentVersion.Length)
                        {
                            currentLength = currentVersion.Length;
                            Array.Resize(ref currentVersion, version.Length);
                            for (int j = currentLength; j < currentVersion.Length; j++)
                            {
                                currentVersion[j] = "0";
                            }
                        }
                        for (int j = 0; j < version.Length; j++)
                        {
                            if (currentVersion[j].Length > version[j].Length)
                            {
                                newer = true;
                                break;
                            }
                            else if (currentVersion[j].Length == version[j].Length)
                            {
                                for (int k = 0; k < version[j].Length; k++)
                                {
                                    if (currentVersion[j][k] > version[j][k])
                                    {
                                        newer = true;
                                        break;
                                    }
                                    else if (currentVersion[j][k] < version[j][k])
                                    {
                                        break;
                                    }
                                }
                            }
                            if (!newer)
                            {
                                break;
                            }
                        }
                    }
                    if (newer)
                    {
                        resultLink = link;
                        version = currentVersion;
                    }
                }
                if (resultLink != "")
                {
                    downloadLinks.Clear();
                    downloadLinks.Add(resultLink);
                }
            }
            return downloadLinks;
        }

        private static string CorrectLink(AppElement application, Match match)
        {
            string downloadLink;
            if (match.Groups[2].Success)
            {
                downloadLink = match.Groups[1].ToString();
            }
            else
            {
                Uri url = new Uri(application.Link);
                downloadLink = match.Groups[1].ToString();
                if (downloadLink.StartsWith("//"))
                {
                    downloadLink = url.Scheme + ":" + downloadLink;
                }
                else
                {
                    downloadLink = url.Scheme + "://" + url.Host + (downloadLink.StartsWith("/") ? "" : url.AbsolutePath.Substring(0, url.AbsolutePath.LastIndexOf("/") + 1)) + downloadLink;
                }
            }
            return downloadLink;
        }

        private static AppElement[] ReadConfig()
        {
            List<AppElement> apps = new List<AppElement>();
            string configFilePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\config.cfg";
            if (File.Exists(configFilePath))
            {
                bool inAppAutoDownloader = false;
                bool inKeywords = false;
                bool inFilters = false;
                bool inCommands = false;
                bool inRequestHeaders = false;
                string section = "";
                string element = "";
                string requestHeader = "";
                AppElement currentApp = new AppElement("");
                try
                {
                    using (XmlReader xmlReader = XmlReader.Create(configFilePath))
                    {
                        while (xmlReader.Read())
                        {
                            switch (xmlReader.NodeType)
                            {
                                case XmlNodeType.Element:
                                    if (inAppAutoDownloader)
                                    {
                                        if (section.Length == 0)
                                        {
                                            section = xmlReader.Name;
                                        }
                                        else if (section == "Applications" && xmlReader.Name == "Application")
                                        {
                                            currentApp = new AppElement("");
                                        }
                                        else if (!inKeywords && xmlReader.Name == "Keywords")
                                        {
                                            inKeywords = true;
                                        }
                                        else if (!inKeywords && xmlReader.Name == "Filters")
                                        {
                                            inFilters = true;
                                        }
                                        else if (!inKeywords && xmlReader.Name == "Commands")
                                        {
                                            inCommands = true;
                                        }
                                        else if (!inRequestHeaders && xmlReader.Name == "RequestHeaders")
                                        {
                                            inRequestHeaders = true;
                                        }
                                        else if (inRequestHeaders && xmlReader.Name == "RequestHeader")
                                        {
                                            requestHeader = "";
                                        }
                                        else
                                        {
                                            element = xmlReader.Name;
                                        }
                                    }
                                    else if (xmlReader.Name == "AppAutoDownloader")
                                    {
                                        inAppAutoDownloader = true;
                                    }
                                    break;
                                case XmlNodeType.Text:
                                    if (inAppAutoDownloader)
                                    {
                                        switch (section)
                                        {
                                            case "Settings":
                                                switch (element)
                                                {
                                                    case "OutputDirectory":
                                                        outputDirectory = xmlReader.Value;
                                                        break;
                                                    case "DontCheckSimpleDownload":
                                                        dontCheckSimpleDownload = Convert.ToBoolean(xmlReader.Value);
                                                        break;
                                                    case "MaxRecursionDepth":
                                                        maxRecursionDepth = Convert.ToInt32(xmlReader.Value);
                                                        break;
                                                }
                                                break;
                                            case "Applications":
                                                switch (element)
                                                {
                                                    case "Name":
                                                        if (inRequestHeaders)
                                                        {
                                                            requestHeader = xmlReader.Value;
                                                        }
                                                        else
                                                        {
                                                            currentApp.Name = xmlReader.Value;
                                                        }
                                                        break;
                                                    case "FileFormat":
                                                        currentApp.Format = xmlReader.Value;
                                                        break;
                                                    case "ExtractFormat":
                                                        currentApp.ExtractFormat = xmlReader.Value;
                                                        break;
                                                    case "Extract":
                                                        currentApp.Extract = new Regex(xmlReader.Value);
                                                        break;
                                                    case "Link":
                                                        currentApp.Link = xmlReader.Value;
                                                        break;
                                                    case "DownloadType":
                                                        currentApp.Type = (xmlReader.Value == "ConfirmDownload" ? DownloadType.ConfirmDownload : (xmlReader.Value == "DontCheckFormat" ? DownloadType.DontCheckFormat : (xmlReader.Value == "Recursive" ? DownloadType.Recursive : (xmlReader.Value == "SimpleDownload" ? DownloadType.SimpleDownload : (xmlReader.Value == "SimpleParameterDownload" ? DownloadType.SimpleParameterDownload : DownloadType.ScanPage)))));
                                                        break;
                                                    case "RemoveHash":
                                                        try
                                                        {
                                                            currentApp.RemoveHash = Convert.ToInt32(xmlReader.Value);
                                                        }
                                                        catch
                                                        {
                                                            WriteInfo("The RemoveHash value could not be converted!");
                                                        }
                                                        break;
                                                    case "Keyword":
                                                        if (inKeywords)
                                                        {
                                                            currentApp.Keywords.Add(xmlReader.Value);
                                                        }
                                                        break;
                                                    case "Filter":
                                                        if (inFilters)
                                                        {
                                                            currentApp.Filters.Add(xmlReader.Value);
                                                        }
                                                        break;
                                                    case "Command":
                                                        if (inCommands)
                                                        {
                                                            int splitIndex = xmlReader.Value.IndexOf(" ");
                                                            if (xmlReader.Value.Substring(0, splitIndex) == "REPLACE")
                                                            {
                                                                if (!currentApp.Commands.ContainsKey("REPLACE"))
                                                                {
                                                                    currentApp.Commands.Add(xmlReader.Value.Substring(0, splitIndex), xmlReader.Value.Substring(splitIndex + 1));
                                                                }
                                                                else
                                                                {
                                                                    currentApp.Commands["REPLACE"] = currentApp.Commands["REPLACE"] + ";" + xmlReader.Value.Substring(splitIndex + 1);
                                                                }
                                                            }
                                                            else
                                                            {
                                                                currentApp.Commands.Add(xmlReader.Value.Substring(0, splitIndex), xmlReader.Value.Substring(splitIndex + 1));
                                                            }
                                                        }
                                                        break;
                                                    case "Value":
                                                        if (inRequestHeaders && requestHeader.Length > 0)
                                                        {
                                                            currentApp.RequestHeaders.Add(requestHeader, xmlReader.Value);
                                                            requestHeader = "";
                                                        }
                                                        break;
                                                }
                                                break;
                                        }
                                    }
                                    break;
                                case XmlNodeType.EndElement:
                                    if (inAppAutoDownloader)
                                    {
                                        if ((xmlReader.Name == "Settings") || (xmlReader.Name == "Applications"))
                                        {
                                            section = "";
                                        }
                                        else if (xmlReader.Name == "Application")
                                        {
                                            apps.Add(currentApp);
                                        }
                                        else if (xmlReader.Name == "Keywords")
                                        {
                                            inKeywords = false;
                                        }
                                        else if (xmlReader.Name == "Filters")
                                        {
                                            inFilters = false;
                                        }
                                        else if (xmlReader.Name == "Commands")
                                        {
                                            inCommands = false;
                                        }
                                        else if (xmlReader.Name == "RequestHeaders")
                                        {
                                            inRequestHeaders = false;
                                        }
                                        else if (xmlReader.Name == "RequestHeader")
                                        {
                                            requestHeader = "";
                                        }
                                    }
                                    break;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    WriteError("Failed to read config file: " + e.Message);
                }
            }
            else
            {
                WriteAction("No configuration file (config.cfg) found - creating a new one");
                try
                {
                    using (FileStream fileStream = new FileStream(configFilePath, FileMode.Create))
                    {
                        using (StreamWriter streamWriter = new StreamWriter(fileStream))
                        {
                            using (XmlTextWriter xmlWriter = new XmlTextWriter(streamWriter))
                            {
                                xmlWriter.Formatting = Formatting.Indented;
                                xmlWriter.Indentation = 4;
                                xmlWriter.WriteStartDocument();
                                xmlWriter.WriteStartElement("AppAutoDownloader");
                                xmlWriter.WriteStartElement("Settings");
                                xmlWriter.WriteElementString("OutputDirectory", DEFAULT_OUTPUT_DIR);
                                xmlWriter.WriteEndElement();
                                xmlWriter.WriteStartElement("Applications");
                                xmlWriter.WriteComment("<Application>");
                                xmlWriter.WriteComment("\t<Name>Example Application</Name>");
                                xmlWriter.WriteComment("\t<FileFormat>exe</FileFormat>");
                                xmlWriter.WriteComment("\t<Link>http://example-app.com/downloads</Link>");
                                xmlWriter.WriteComment("\t<DownloadType>ScanPage</DownloadType>");
                                xmlWriter.WriteComment("\t<Keywords>");
                                xmlWriter.WriteComment("\t\t<Keyword>x64</Keyword>");
                                xmlWriter.WriteComment("\t</Keywords>");
                                xmlWriter.WriteComment("</Application>");
                                xmlWriter.WriteEndElement();
                                xmlWriter.WriteEndElement();
                                xmlWriter.WriteEndDocument();
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    WriteError("Failed to write new config file: " + e.Message);
                }
            }
            return apps.ToArray();
        }

        private static void ReadCurrentVersions()
        {
            try
            {
                using (XmlReader xmlReader = XmlReader.Create(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\current-versions.xml"))
                {
                    string application = "";
                    while (xmlReader.Read())
                    {
                        switch (xmlReader.NodeType)
                        {
                            case XmlNodeType.Element:
                                if (xmlReader.Name != "Applications")
                                {
                                    application = xmlReader.Name;
                                }
                                break;
                            case XmlNodeType.Text:
                                if (application.Length > 0)
                                {
                                    currentVersions.Add(application.Substring(4).Replace("Plus", "+"), xmlReader.Value);
                                }
                                break;
                            case XmlNodeType.EndElement:
                                application = "";
                                break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                WriteError("An error occured while reading the current application versions: " + e.Message);
            }
        }

        private static void SaveCurrentVersions()
        {
            try
            {
                using (FileStream fileStream = new FileStream(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\current-versions.xml", FileMode.Create))
                {
                    using (StreamWriter streamWriter = new StreamWriter(fileStream))
                    {
                        using (XmlTextWriter xmlWriter = new XmlTextWriter(streamWriter))
                        {
                            xmlWriter.Formatting = Formatting.Indented;
                            xmlWriter.Indentation = 4;
                            xmlWriter.WriteStartDocument();
                            xmlWriter.WriteStartElement("Applications");
                            foreach (string application in currentVersions.Keys)
                            {
                                xmlWriter.WriteElementString("APP_" + application.Replace(" ", "_").Replace("+", "Plus"), currentVersions[application]);
                            }
                            xmlWriter.WriteEndElement();
                            xmlWriter.WriteEndDocument();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                WriteError("Failed to write current version file: " + e.Message);
            }
        }

        private static bool CheckFile(AppElement application, string downloadLink)
        {
            string currentVersionName = application.Name.Replace(" ", "_");
            if (currentVersions.ContainsKey(currentVersionName))
            {
                if (GetSavedLink(downloadLink, application.RemoveHash, ref application) == currentVersions[currentVersionName])
                {
                    if ((File.Exists(Path.GetFullPath(outputDirectory) + "\\" + application.Name + "." + (application.ExtractFormat == "" ? application.Format : application.ExtractFormat))) || (application.ExtractFormat == "FOLDER" && Directory.Exists(Path.GetFullPath(outputDirectory) + "\\" + application.Name)))
                    {
                        WriteSuccess("No update needed");
                        return false;
                    }
                }
                if (GetSavedLink(downloadLink, application.RemoveHash, ref application) != "")
                {
                    currentVersions.Remove(application.Name);
                }
            }
            if ((application.Type != DownloadType.SimpleDownload || dontCheckSimpleDownload) && (!application.Commands.ContainsKey("RELOAD") || application.Commands["RELOAD"] == "false") && !currentVersions.ContainsKey(application.Name))
            {
                currentVersions.Add(application.Name, GetSavedLink(downloadLink, application.RemoveHash, ref application));
            }
            return DownloadFile(application, Path.GetFullPath(outputDirectory) + "\\" + application.Name + "." + application.Format, downloadLink);
        }

        private static bool DownloadFile(AppElement application, string filePath, string downloadLink)
        {
            WriteInfo("Starting download from " + downloadLink);
            try
            {
                using (WebClient webClient = new WebClient())
                {
                    foreach (string requestHeader in application.RequestHeaders.Keys)
                    {
                        webClient.Headers.Set(requestHeader, application.RequestHeaders[requestHeader]);
                    }
                    switch (application.Type)
                    {
                        case DownloadType.ConfirmDownload:
                            webClient.Headers.Add(HttpRequestHeader.Referer, downloadLink);
                            webClient.DownloadFile(downloadLink, filePath);
                            if (ExtractFile(ref application, ref filePath))
                            {
                                WriteSuccess("Successfully updated file");
                            }
                            else
                            {
                                WriteInfo("Archive content could not be extracted");
                            }
                            return true;
                        case DownloadType.Recursive:
                            downloadLink = GetDownloadLinkRecursive(application, downloadLink, 0);
                            if (downloadLink != "")
                            {
                                foreach (string requestHeader in application.RequestHeaders.Keys)
                                {
                                    webClient.Headers.Set(requestHeader, application.RequestHeaders[requestHeader]);
                                }
                                if (application.Commands.ContainsKey("LINK") && application.Commands["LINK"] == "RECURSIVE")
                                {
                                    WriteInfo("Checking version");
                                    if (currentVersions[application.Name] == downloadLink && File.Exists(filePath))
                                    {
                                        WriteInfo("No update needed");
                                        return true;
                                    }
                                    currentVersions.Remove(application.Name);
                                    currentVersions.Add(application.Name, downloadLink);
                                }
                                WriteInfo("Starting download from " + downloadLink);
                                webClient.DownloadFile(downloadLink, filePath);
                                if (ExtractFile(ref application, ref filePath))
                                {
                                    WriteSuccess("Successfully updated file");
                                }
                                else
                                {
                                    WriteInfo("Archive content could not be extracted");
                                }
                                return true;
                            }
                            WriteError("Recursion could not find a download link");
                            return false;
                        case DownloadType.SimpleParameterDownload:
                            NameValueCollection parameters = new NameValueCollection();
                            foreach (string parameter in application.RequestHeaders.Keys)
                            {
                                if (application.RequestHeaders[parameter] == "LATEST")
                                {
                                    MatchCollection matches = new Regex("name=\"" + parameter + "\".*value=\"([^\"]*)\"").Matches(webClient.DownloadString(application.Link));
                                    int latest = 0;
                                    int current = 0;
                                    foreach (Match match in matches)
                                    {
                                        current = Convert.ToInt32(match.Groups[1].ToString());
                                        if (current > latest)
                                        {
                                            latest = current;
                                        }
                                    }
                                    WriteInfo("Found latest: " + latest);
                                    parameters.Add(parameter, latest + "");
                                }
                                else
                                {
                                    parameters.Add(parameter, application.RequestHeaders[parameter]);
                                }
                            }
                            byte[] response = webClient.UploadValues(downloadLink, "POST", parameters);
                            using (FileStream stream = new FileStream(filePath, FileMode.Create))
                            {
                                using (BinaryWriter writer = new BinaryWriter(stream))
                                {
                                    writer.Write(response);
                                    writer.Flush();
                                }
                            }
                            if (ExtractFile(ref application, ref filePath))
                            {
                                WriteSuccess("Successfully updated file");
                            }
                            else
                            {
                                WriteInfo("Archive content could not be extracted");
                            }
                            return true;
                        default:
                            webClient.DownloadFile(downloadLink, filePath);
                            if (ExtractFile(ref application, ref filePath))
                            {
                                WriteSuccess("Successfully updated file");
                            }
                            else
                            {
                                WriteInfo("Archive content could not be extracted");
                            }
                            return true;
                    }
                }
            }
            catch (Exception e)
            {
                WriteError("An error occured while downloading the file: " + e.Message);
            }
            return false;
        }

        private static string GetDownloadLinkRecursive(AppElement application, string downloadLink, int recursionLevel)
        {
            WriteInfo("Recursion level " + recursionLevel + ": checking " + downloadLink);
            HttpWebRequest request = HttpWebRequest.CreateHttp(downloadLink);
            request.Referer = application.Link;
            if (application.RequestHeaders.ContainsKey("User-Agent"))
            {
                request.UserAgent = application.RequestHeaders["User-Agent"];
            }
            if (application.RequestHeaders.ContainsKey("Cookie"))
            {
                request.CookieContainer = new CookieContainer();
                request.CookieContainer.SetCookies(new Uri(downloadLink), application.RequestHeaders["Cookie"]);
            }
            request.AllowAutoRedirect = true;
            HttpWebResponse response = (HttpWebResponse) request.GetResponse();
            if (!application.RequestHeaders.ContainsKey("No-Rewrite") || application.RequestHeaders["No-Rewrite"] != "1")
            {
                downloadLink = response.ResponseUri.AbsoluteUri;
            }
            else
            {
                downloadLink = new Uri(downloadLink).PathAndQuery;
                downloadLink = response.ResponseUri.Scheme + "://" + response.ResponseUri.Host + (downloadLink.StartsWith("/") ? "" : response.ResponseUri.AbsolutePath.Substring(0, response.ResponseUri.AbsolutePath.LastIndexOf("/") + 1)) + downloadLink;
            }
            WriteInfo("Found content type \"" + response.ContentType + "\"");
            if (application.RequestHeaders.ContainsKey("Referer"))
            {
                request.Referer = downloadLink;
            }
            if (response.ContentType.Contains("octet-stream") || response.ContentType.Contains("application/x-msdownload") || response.ContentType.Contains("application/x-msdos-program") || (application.Commands.ContainsKey("DLOVERRIDE") && downloadLink.Contains(application.Commands["DLOVERRIDE"])))
            {
                WriteInfo("Download link found");
                return downloadLink;
            }
            if (response.ContentType.Contains("text/html"))
            {
                if (recursionLevel >= maxRecursionDepth)
                {
                    response.Dispose();
                    WriteError("Reached maximum recursion level - branch aborted");
                    return "";
                }
                application.Link = downloadLink;
                List<string> downloadLinks = GetDownloadLinks(application);
                using (Stream stream = response.GetResponseStream())
                {
                    StreamReader reader = new StreamReader(stream);
                    MatchCollection matches = srcRegex.Matches(reader.ReadToEnd());
                    foreach (Match match in matches)
                    {
                        downloadLink = CorrectLink(application, match);
                        if (!downloadLinks.Contains(downloadLink))
                        {
                            downloadLinks.Add(downloadLink);
                        }
                    }
                }
                response.Dispose();
                foreach (string link in downloadLinks)
                {
                    application.Link = link;
                    downloadLink = GetDownloadLinkRecursive(application, link, recursionLevel + 1);
                    if (downloadLink != "")
                    {
                        return downloadLink;
                    }
                }
            }
            response.Dispose();
            return "";
        }

        private static bool ExtractFile(ref AppElement application, ref string filePath)
        {
            if (application.ExtractFormat == "")
            {
                return true;
            }
            string extractedFilePath = Path.GetFullPath(outputDirectory) + "\\" + application.Name;
            if (application.ExtractFormat == "FOLDER")
            {
                extractedFilePath += "\\";
                Directory.CreateDirectory(extractedFilePath);
            }
            else
            {
                extractedFilePath += "." + application.ExtractFormat;
            }
            try
            {
                switch (application.Format)
                {
                    case "zip":
                        using (ZipArchive archive = ZipFile.OpenRead(filePath))
                        {
                            foreach (ZipArchiveEntry file in archive.Entries)
                            {
                                if (application.Extract.IsMatch(file.FullName))
                                {
                                    WriteInfo("Starting extraction of \"" + file.FullName + "\"");
                                    try
                                    {
                                        if (application.ExtractFormat == "FOLDER")
                                        {
                                            string directory = Path.GetDirectoryName(extractedFilePath + file.FullName);
                                            if (!Directory.Exists(directory))
                                            {
                                                Directory.CreateDirectory(directory);
                                            }
                                            if (Path.GetFileName(extractedFilePath + file.FullName) != "")
                                            {
                                                if (File.Exists(extractedFilePath + file.FullName))
                                                {
                                                    File.Delete(extractedFilePath + file.FullName);
                                                }
                                                file.ExtractToFile(extractedFilePath + file.FullName);
                                            }
                                        }
                                        else
                                        {
                                            if (File.Exists(extractedFilePath))
                                            {
                                                File.Delete(extractedFilePath);
                                            }
                                            file.ExtractToFile(extractedFilePath);
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        WriteInfo("The file in the archive could not be extracted (" + e.Message + ")");
                                    }
                                }
                            }
                        }
                        break;
                }
                File.Delete(filePath);
            }
            catch (Exception e)
            {
                WriteInfo("The archive file could not be processed (" + e.Message + ")");
            }
            return File.Exists(extractedFilePath) || Directory.Exists(extractedFilePath);
        }

        private static string GetSavedLink(string downloadLink, int removeHash, ref AppElement application)
        {
            if (application.Commands.ContainsKey("LINK") && application.Commands["LINK"] == "RECURSIVE")
            {
                return "";
            }
            if (application.Type == DownloadType.SimpleParameterDownload)
            {
                foreach (string value in application.RequestHeaders.Values)
                {
                    if (value == "LATEST")
                    {
                        return "";
                    }
                }
            }
            if (application.Commands.ContainsKey("REMOVEHASH"))
            {
                string[] targets = application.Commands["REMOVEHASH"].Split(';');
                if (targets.Length >= 2)
                {
                    int firstIndex = downloadLink.IndexOf(targets[0]) + targets[0].Length;
                    int secondIndex = downloadLink.IndexOf(targets[1], firstIndex + 1);
                    downloadLink = downloadLink.Substring(0, firstIndex) + downloadLink.Substring(secondIndex);
                }
            }
            for (int i = 0; i < removeHash; i++)
            {
                if (application.Commands.ContainsKey("HASH"))
                {
                    downloadLink = downloadLink.Substring(0, downloadLink.LastIndexOf(application.Commands["HASH"]));
                }
                else
                {
                    downloadLink = downloadLink.Substring(0, downloadLink.LastIndexOf('/'));
                }
            }
            return downloadLink;
        }

        private static void WriteInfo(string message)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("[INFO] " + message);
        }

        private static void WriteSuccess(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[SUCCESS] " + message);
        }

        private static void WriteAction(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[ACTION] " + message);
        }

        private static void WriteError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERROR] " + message);
        }
    }
}