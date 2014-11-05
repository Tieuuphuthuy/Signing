﻿using System;
using System.Linq;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Text;
using System.Security.Cryptography.Pkcs;

namespace PackageSigning
{
    public class Signature
    {
        private static readonly HashAlgorithm DefaultHashAlgorithm = new SHA256Managed();
        private static readonly Oid Sha256Oid = Oid.FromOidValue("2.16.840.1.101.3.4.2.1", OidGroup.HashAlgorithm);

        private SignedCms _signedCms;

        public Signer Signer { get; private set; }

        private Signature(SignedCms signedCms)
        {
            _signedCms = signedCms;

            // Read the signer
            var signerInfo = _signedCms.SignerInfos.Cast<SignerInfo>().FirstOrDefault();
            Signer = Signer.FromSignerInfo(signerInfo, _signedCms.Certificates.Cast<X509Certificate2>(), DefaultHashAlgorithm);
        }

        public async Task WriteAsync(string fileName)
        {
            using (var strm = new FileStream(fileName, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                await WriteAsync(strm);
            }
        }

        public Task WriteAsync(Stream target)
        {
            var array = ToByteArray();
            return target.WriteAsync(array, 0, array.Length);
        }

        public byte[] ToByteArray()
        {
            return _signedCms.Encode();
        }

        public static async Task<Signature> SignAsync(string targetFileName, string certificateChain, string password)
        {
            using (var strm = new FileStream(targetFileName, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var cert = new X509Certificate2(certificateChain, password);
                var chain = new X509Certificate2Collection();
                chain.Import(certificateChain, password, X509KeyStorageFlags.DefaultKeySet);
                return await SignAsync(strm, cert, chain);
            }
        }

        public static async Task<Signature> SignAsync(Stream targetData, X509Certificate2 cert, X509Certificate2Collection additionalCertificates)
        {
            return Sign(await ReadStreamToMemoryAsync(targetData), cert, additionalCertificates);
        }

        public static Signature Sign(byte[] targetData, X509Certificate2 cert, X509Certificate2Collection additionalCertificates)
        {
            // Create a content info and start a signed CMS
            var contentInfo = new ContentInfo(targetData);
            var signedCms = new SignedCms(contentInfo, detached: true);

            // Create a signer info for the signature
            var signer = new CmsSigner(SubjectIdentifierType.SubjectKeyIdentifier, cert);
            signer.DigestAlgorithm = Sha256Oid;

            // We do want the whole chain in the file, but we can't control how
            // CmsSigner builds the chain and add our additional certificates.
            // So, we tell it not to worry and we manually build the chain and
            // add it to the signer.
            signer.IncludeOption = X509IncludeOption.EndCertOnly;

            // Embed all the certificates in the CMS
            var chain = new X509Chain();
            chain.ChainPolicy.ExtraStore.AddRange(additionalCertificates);
            chain.Build(cert);
            foreach (var element in chain.ChainElements)
            {
                // Don't re-embed the signing certificate!
                if (!Equals(element.Certificate, cert))
                {
                    signer.Certificates.Add(element.Certificate);
                }
            }

            // Compute the signature!
            signedCms.ComputeSignature(signer, silent: true);

            // Load the signature in to the object
            return new Signature(signedCms);
        }

        public static async Task<Signature> VerifyAsync(Stream file, Stream signature)
        {
            return Verify(await ReadStreamToMemoryAsync(file), await ReadStreamToMemoryAsync(signature));
        }

        public static Signature Verify(byte[] file, byte[] signature)
        {
            //// The file is actually UTF-8 Base-64 Encoded, so decode that
            //var decoded = PemFormatter.Unformat(signature);

            // Load the content
            var contentInfo = new ContentInfo(file);

            // Create a signed cms and decode the signature into it
            var signedCms = new SignedCms(contentInfo, detached: true);
            signedCms.Decode(signature);

            signedCms.CheckSignature(verifySignatureOnly: true);

            // Build the signature object
            return new Signature(signedCms);
        }

        private static async Task<byte[]> ReadStreamToMemoryAsync(Stream file)
        {
            byte[] content;
            using (var strm = new MemoryStream())
            {
                await file.CopyToAsync(strm);
                await strm.FlushAsync();
                content = strm.ToArray();
            }

            return content;
        }
    }
}