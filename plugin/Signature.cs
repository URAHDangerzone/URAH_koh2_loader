using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx.Logging;

namespace URAH_Loader
{
    /// <summary>
    /// The trusted public keys and the check of manifest.sig: RSA PKCS#1 v1.5 over SHA-256, what
    /// `openssl dgst -sha256 -sign` and tools/sign_plugins.py write.
    ///
    /// Built in: the release key (private half in the CI secret URAH_SIGNING_KEY, a copy offline). If it
    /// leaks, a loader update replaces it: until players update, the old loader keeps trusting it either way.
    /// Extra keys from BepInEx/config/urah.loader/trusted_keys/*.xml are for development: writing there
    /// needs the same access as dropping a DLL into BepInEx/plugins, so it opens nothing new.
    /// </summary>
    internal sealed class Signature
    {
        /// <summary>RSAKeyValue XML (tools/sign_plugins.py pub). Filled in step 2 of PLUGIN_LOADER_PLAN.md.</summary>
        private static readonly string[] BuiltIn =
        {
            // release key
        };

        private readonly List<KeyValuePair<string, string>> keys = new List<KeyValuePair<string, string>>();

        public int Count => keys.Count;

        public Signature(string trustedKeysDir, ManualLogSource log)
        {
            foreach (string xml in BuiltIn)
            {
                Add("built-in", xml, log);
            }
            if (Directory.Exists(trustedKeysDir))
            {
                foreach (string file in Directory.GetFiles(trustedKeysDir, "*.xml").OrderBy(f => f, StringComparer.Ordinal))
                {
                    if (Add(Path.GetFileName(file), File.ReadAllText(file), log))
                    {
                        log.LogWarning($"Also trusting key {Path.GetFileName(file)} from {trustedKeysDir} (development)");
                    }
                }
            }
        }

        private bool Add(string name, string xml, ManualLogSource log)
        {
            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.FromXmlString(xml); // fails early on a broken key
                }
                keys.Add(new KeyValuePair<string, string>(name, xml));
                return true;
            }
            catch (Exception e)
            {
                log.LogError($"Key {name} unreadable, ignored: {e.Message}");
                return false;
            }
        }

        public bool Verify(byte[] data, byte[] sig)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(data);
            }
            string oid = CryptoConfig.MapNameToOID("SHA256");
            foreach (KeyValuePair<string, string> key in keys)
            {
                try
                {
                    using (var rsa = new RSACryptoServiceProvider())
                    {
                        rsa.FromXmlString(key.Value);
                        if (rsa.VerifyHash(hash, oid, sig))
                        {
                            return true;
                        }
                    }
                }
                catch (CryptographicException)
                {
                    // wrong key size for this signature: not this key
                }
            }
            return false;
        }
    }
}
