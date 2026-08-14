/* SPDX-License-Identifier: GPL-3.0-only */
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace DOL.GS
{
    public static class DAoCCmsBridgeConfig
    {
        private const string RelativeConfigPath = "config/daoc_cms_bridge.conf";
        private static readonly object Sync = new object();
        private static bool _loaded;
        private static bool _valid;
        private static string _cmsApiUrl = string.Empty;
        private static string _sharedSecret = string.Empty;
        private static int _bridgePort;
        private static string _configPath = string.Empty;
        private static string _lastError = string.Empty;

        public static string CmsApiUrl { get { return _cmsApiUrl; } }
        public static string SharedSecret { get { return _sharedSecret; } }
        public static int BridgePort { get { return _bridgePort; } }
        public static string ConfigPath { get { return _configPath; } }
        public static string LastError { get { return _lastError; } }

        public static bool TryLoad(out string error)
        {
            lock (Sync)
            {
                if (!_loaded)
                    Load();

                error = _lastError;
                return _valid;
            }
        }

        private static void Load()
        {
            _loaded = true;
            _valid = false;
            _cmsApiUrl = string.Empty;
            _sharedSecret = string.Empty;
            _bridgePort = 0;
            _lastError = string.Empty;
            _configPath = ResolveConfigPath();

            try
            {
                if (!File.Exists(_configPath))
                {
                    _lastError = "Configuration file not found: " + _configPath;
                    return;
                }

                Dictionary<string, string> values = Parse(File.ReadAllLines(_configPath));
                string cmsApiUrl;
                string sharedSecret;
                string bridgePortText;

                if (!values.TryGetValue("CmsApiUrl", out cmsApiUrl)
                    || string.IsNullOrWhiteSpace(cmsApiUrl))
                {
                    _lastError = "CmsApiUrl is missing in " + _configPath;
                    return;
                }

                Uri apiUri;
                if (!Uri.TryCreate(cmsApiUrl, UriKind.Absolute, out apiUri)
                    || (apiUri.Scheme != Uri.UriSchemeHttp && apiUri.Scheme != Uri.UriSchemeHttps))
                {
                    _lastError = "CmsApiUrl must be an absolute HTTP or HTTPS URL in " + _configPath;
                    return;
                }

                if (!values.TryGetValue("SharedSecret", out sharedSecret)
                    || string.IsNullOrWhiteSpace(sharedSecret)
                    || sharedSecret.IndexOf("CHANGE_ME", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _lastError = "SharedSecret is missing or still contains a placeholder in " + _configPath;
                    return;
                }

                int bridgePort;
                if (!values.TryGetValue("BridgePort", out bridgePortText)
                    || !int.TryParse(bridgePortText, out bridgePort)
                    || bridgePort < 1
                    || bridgePort > 65535)
                {
                    _lastError = "BridgePort must be between 1 and 65535 in " + _configPath;
                    return;
                }

                _cmsApiUrl = apiUri.AbsoluteUri;
                _sharedSecret = sharedSecret;
                _bridgePort = bridgePort;
                _valid = true;
            }
            catch (Exception ex)
            {
                _lastError = "Could not read " + _configPath + ": " + ex.Message;
            }
        }

        private static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string sourceLine in lines)
            {
                string line = sourceLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                    continue;

                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                values[key] = value;
            }

            return values;
        }

        private static string ResolveConfigPath()
        {
            string relativePath = RelativeConfigPath.Replace('/', Path.DirectorySeparatorChar);
            string baseDirectoryPath = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                relativePath));
            if (File.Exists(baseDirectoryPath))
                return baseDirectoryPath;

            string workingDirectoryPath = Path.GetFullPath(Path.Combine(
                Environment.CurrentDirectory,
                relativePath));
            if (File.Exists(workingDirectoryPath))
                return workingDirectoryPath;

            string configuredRoot = ResolveConfiguredServerRoot();
            if (!string.IsNullOrWhiteSpace(configuredRoot))
            {
                string configuredPath = Path.GetFullPath(Path.Combine(configuredRoot, relativePath));
                if (File.Exists(configuredPath))
                    return configuredPath;
            }

            // The executable directory is the documented deployment root and
            // produces the most useful missing-file error when no candidate exists.
            return baseDirectoryPath;
        }

        private static string ResolveConfiguredServerRoot()
        {
            try
            {
                PropertyInfo instanceProperty = typeof(GameServer).GetProperty(
                    "Instance",
                    BindingFlags.Public | BindingFlags.Static);
                object server = instanceProperty == null ? null : instanceProperty.GetValue(null);
                PropertyInfo configurationProperty = server == null
                    ? null
                    : server.GetType().GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance);
                object configuration = configurationProperty == null ? null : configurationProperty.GetValue(server);
                PropertyInfo rootProperty = configuration == null
                    ? null
                    : configuration.GetType().GetProperty("RootDirectory", BindingFlags.Public | BindingFlags.Instance);
                string root = rootProperty == null ? null : rootProperty.GetValue(configuration) as string;

                if (!string.IsNullOrWhiteSpace(root))
                    return Path.GetFullPath(root);
            }
            catch
            {
                // Use the process base directory when the core exposes no root directory.
            }

            return null;
        }
    }
}
