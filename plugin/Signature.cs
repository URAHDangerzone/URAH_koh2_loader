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
        /// <summary>RSAKeyValue XML (tools/sign_plugins.py pub).</summary>
        private static readonly string[] BuiltIn =
        {
            // release key, 2026-10-17 (keys/release_key.xml in URAH_koh2_knowledge)
            "<RSAKeyValue><Modulus>u5h4QnoqnX7dTAyYpKiZvaIIqU6GTM6kse58GuSRUwh9gLGSI+7NJOjTujhyx+3cAN/C3giclYQhSJJMDs8FdXTzhsYm7ps0JR/5SHvwEfU5lUKuj9ndPVg64x3Fb4qe9P7bS33T0SfYe8nXoLQ4Mignf1yWAcJ2LTFxlAvK8MTCe6g+Z3k0WeDYM5GSlFCttumHW7wSUUsoKbEUlpDY6Ftgw7PyviKXUB7xCMoaK96hXgIaDETYEYjcJFPRpo6JryIgt6Mb89/SaXKUSnL8c8MZUOmYWPxu8QQLOl6PCnYKy2TMQhuRZ59lnmnROnXuhQq3cfQApI/24v01OCVNMCrgdzXgFdVh7sL7CWhSvCYpSc5YME7gq1SUBd7JUcFAS4w8Cn9cD6USNA8GE5RayyicZ3y6Wzd2ZRN1bde5jQtYcCNUXuz4gzyL6/tid24Uw2OvlS3FQlz0Mxe7n3ou3vLNdbaC5k8JyMY0kRJZGY2AzVzvUC1wx4hYfHpAqzyX</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>",
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
