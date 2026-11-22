using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using TransactHub.Security;

namespace TransactHub.Ports;

// File encryption and decryption (R-03-24, R-12-12, R-12-13; Issue I-87): the runtime's adapters of p.port.encryption.v1 and
// p.port.decryption.v1, OpenPGP (RFC 4880) through BouncyCastle (BouncyCastle.Cryptography, pinned in TransactHub.Ports.csproj).
// A file the bank sends is encrypted to its recipient's public key before it leaves; a file it receives is decrypted with its
// own private key. Keys are never values: a binding names each by reference (secretRef), and the reference is read through
// the runtime's one secret interface when the file is encrypted or decrypted (R-06-22). The recipient's key is chosen by id,
// a value the bank sets (p.protect.encrypt-file.v1#recipientKey), which names a key of the bank's binding:
// `keys.<id>.secretRef`. Errors name the reference or the key id, never a key or a value.

/// <summary>A file encrypted for one recipient.</summary>
public interface IFileEncryption
{
    Task<byte[]> Encrypt(byte[] plain, string recipientKey, string fileName, CancellationToken ct = default);
}

/// <summary>A file decrypted with the bank's own key.</summary>
public interface IFileDecryption
{
    Task<byte[]> Decrypt(byte[] cipher, CancellationToken ct = default);
}

/// <summary>Why a file could not be encrypted or decrypted: never a key or a passphrase, only what was refused.</summary>
public sealed class FileProtectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>OpenPGP on byte arrays: what the adapters do once they hold a key.</summary>
public static class OpenPgp
{
    /// <summary>
    /// Encrypts to the public key's encryption subkey (or the key itself when it can encrypt): AES-256, an integrity packet
    /// (MDC), the content compressed with ZIP, one literal data packet with the file's name. Armoured when asked.
    /// </summary>
    public static byte[] Encrypt(byte[] plain, PgpPublicKey recipient, string fileName, bool armor, SecureRandom? random = null)
    {
        var gen = new PgpEncryptedDataGenerator(SymmetricKeyAlgorithmTag.Aes256, true, random ?? new SecureRandom());
        gen.AddMethod(recipient);
        using var outer = new MemoryStream();
        Stream sink = armor ? new ArmoredOutputStream(outer) : outer;
        using (var enc = gen.Open(sink, new byte[1 << 16]))
        {
            var comp = new PgpCompressedDataGenerator(CompressionAlgorithmTag.Zip);
            using (var c = comp.Open(enc))
            {
                var lit = new PgpLiteralDataGenerator();
                using var l = lit.Open(c, PgpLiteralData.Binary, fileName, plain.Length, DateTime.UnixEpoch);
                l.Write(plain, 0, plain.Length);
            }
        }
        if (armor) sink.Dispose();
        return outer.ToArray();
    }

    /// <summary>Decrypts a message encrypted to one of the secret keys given; fails when its integrity packet does not check.</summary>
    public static byte[] Decrypt(byte[] cipher, PgpSecretKeyRingBundle keys, char[] passphrase)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(cipher));
        var factory = new PgpObjectFactory(input);
        var o = factory.NextPgpObject();
        var list = o as PgpEncryptedDataList ?? factory.NextPgpObject() as PgpEncryptedDataList
            ?? throw new FileProtectionException("The file is not an OpenPGP encrypted message");
        foreach (var d in list.GetEncryptedDataObjects().OfType<PgpPublicKeyEncryptedData>())
        {
            var secret = keys.GetSecretKey(d.KeyId);
            if (secret is null) continue;
            PgpPrivateKey priv;
            try { priv = secret.ExtractPrivateKey(passphrase); }
            catch (PgpException e) { throw new FileProtectionException("The private key does not open with the passphrase its binding names", e); }
            using var clear = d.GetDataStream(priv);
            var content = new PgpObjectFactory(clear).NextPgpObject();
            if (content is PgpCompressedData cd) content = new PgpObjectFactory(cd.GetDataStream()).NextPgpObject();
            if (content is not PgpLiteralData ld) throw new FileProtectionException("The encrypted message holds no file");
            using var ms = new MemoryStream();
            ld.GetInputStream().CopyTo(ms);
            if (!d.IsIntegrityProtected() || !d.Verify()) throw new FileProtectionException("The encrypted message fails its integrity check");
            return ms.ToArray();
        }
        throw new FileProtectionException("The file is encrypted to no key of this binding");
    }

    /// <summary>Whether the bytes are an OpenPGP encrypted message, armoured or binary, by their content alone.</summary>
    public static bool IsEncrypted(byte[] data)
    {
        try
        {
            using var input = PgpUtilities.GetDecoderStream(new MemoryStream(data));
            var f = new PgpObjectFactory(input);
            var o = f.NextPgpObject();
            return o is PgpEncryptedDataList || (o is PgpMarker && f.NextPgpObject() is PgpEncryptedDataList);
        }
        catch (Exception e) when (e is IOException or PgpException or ArgumentException) { return false; }
    }

    /// <summary>The key of a public key ring (armoured or binary) that can encrypt: an encryption subkey before the primary.</summary>
    public static PgpPublicKey EncryptionKeyOf(byte[] publicKeyRing)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(publicKeyRing));
        var bundle = new PgpPublicKeyRingBundle(input);
        var keys = bundle.GetKeyRings().SelectMany(r => r.GetPublicKeys()).Where(k => k.IsEncryptionKey && !k.IsRevoked()).ToList();
        return keys.FirstOrDefault(k => !k.IsMasterKey) ?? keys.FirstOrDefault()
            ?? throw new FileProtectionException("The recipient's key ring holds no key that can encrypt");
    }

    public static PgpSecretKeyRingBundle SecretKeysOf(byte[] secretKeyRing)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(secretKeyRing));
        return new PgpSecretKeyRingBundle(input);
    }

    /// <summary>
    /// A new RSA key pair as an armoured public key ring and an armoured secret key ring protected by the passphrase. For
    /// tests and a deployment's own key ceremony: a key is made where it is used, never committed (R-06-23).
    /// </summary>
    public static (byte[] PublicRing, byte[] SecretRing) GenerateKeyPair(string identity, char[] passphrase, int bits = 2048, SecureRandom? random = null)
    {
        random ??= new SecureRandom();
        var kpg = new RsaKeyPairGenerator();
        kpg.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(0x10001), random, bits, 25));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, kpg.GenerateKeyPair(), DateTime.UtcNow);
        var secret = new PgpSecretKey(PgpSignature.DefaultCertification, pair, identity, SymmetricKeyAlgorithmTag.Aes256, passphrase, true, null, null, random);
        static byte[] Armoured(Action<Stream> write)
        {
            using var ms = new MemoryStream();
            using (var a = new ArmoredOutputStream(ms)) write(a);
            return ms.ToArray();
        }
        return (Armoured(s => secret.PublicKey.Encode(s)), Armoured(s => secret.Encode(s)));
    }
}

/// <summary>
/// The <c>openpgp</c> adapter of <c>p.port.encryption.v1</c>. Settings: <c>keys.&lt;id&gt;.secretRef</c>, the recipient's public key
/// ring by reference, for each key id the bank may name; <c>armor</c>, "true" for ASCII-armoured output (default binary).
/// </summary>
public sealed class OpenPgpEncryption(PortBinding binding, ISecretResolver? secrets = null) : IFileEncryption
{
    public const string Name = "openpgp";
    public const string Port = "p.port.encryption.v1";
    readonly ISecretResolver secrets = secrets ?? SecretResolver.FromEnvironment();

    /// <summary>The reference of a key id's public key in the binding; a key id the binding does not name is refused.</summary>
    public string KeyReference(string recipientKey)
    {
        var setting = $"keys.{recipientKey}.secretRef";
        if (!binding.Settings.TryGetValue(setting, out var r) || r.Length == 0)
            throw new PortBindingException($"The {binding.Adapter} binding of {binding.Port} for {binding.Tenant} has no '{setting}': the key id {recipientKey} names no key");
        if (!SecretRef.IsRef(r)) throw new PortBindingException($"The {binding.Adapter} binding of {binding.Port} for {binding.Tenant}: {setting} must be a reference ({SecretRef.Forms})");
        return r;
    }

    public async Task<byte[]> Encrypt(byte[] plain, string recipientKey, string fileName, CancellationToken ct = default)
    {
        var reference = KeyReference(recipientKey);
        var ring = await secrets.Resolve(reference, ct);
        PgpPublicKey key;
        try { key = OpenPgp.EncryptionKeyOf(System.Text.Encoding.UTF8.GetBytes(ring.Reveal())); }
        catch (Exception e) when (e is IOException or PgpException or ArgumentException) { throw new FileProtectionException($"Secret {reference} is not an OpenPGP public key", e); }
        var armor = string.Equals(binding.Setting("armor", "false"), "true", StringComparison.OrdinalIgnoreCase);
        return OpenPgp.Encrypt(plain, key, fileName, armor);
    }
}

/// <summary>
/// The <c>openpgp</c> adapter of <c>p.port.decryption.v1</c>, the pair of <see cref="OpenPgpEncryption"/>. Settings:
/// <c>privateKeyRef</c>, the bank's secret key ring by reference; <c>secretRef</c>, its passphrase by reference.
/// </summary>
public sealed class OpenPgpDecryption(PortBinding binding, ISecretResolver? secrets = null) : IFileDecryption
{
    public const string Name = "openpgp";
    public const string Port = "p.port.decryption.v1";
    readonly ISecretResolver secrets = secrets ?? SecretResolver.FromEnvironment();

    public async Task<byte[]> Decrypt(byte[] cipher, CancellationToken ct = default)
    {
        var keyRef = binding.Required("privateKeyRef");
        var passRef = binding.Required("secretRef");
        var ring = await secrets.Resolve(keyRef, ct);
        var pass = await secrets.Resolve(passRef, ct);
        PgpSecretKeyRingBundle keys;
        try { keys = OpenPgp.SecretKeysOf(System.Text.Encoding.UTF8.GetBytes(ring.Reveal())); }
        catch (Exception e) when (e is IOException or PgpException or ArgumentException) { throw new FileProtectionException($"Secret {keyRef} is not an OpenPGP secret key ring", e); }
        return OpenPgp.Decrypt(cipher, keys, pass.Reveal().ToCharArray());
    }
}

/// <summary>The adapter a binding names, as <see cref="CoreBankingAdapters"/> does for core banking.</summary>
public static class FileProtectionAdapters
{
    public static IFileEncryption Encryption(PortBinding b, ISecretResolver? secrets = null) => b.Adapter switch
    {
        OpenPgpEncryption.Name when b.Port == OpenPgpEncryption.Port => new OpenPgpEncryption(b, secrets),
        _ => throw new PortBindingException($"{b.Port} for {b.Tenant} names adapter {b.Adapter}, which encrypts no file here"),
    };

    public static IFileDecryption Decryption(PortBinding b, ISecretResolver? secrets = null) => b.Adapter switch
    {
        OpenPgpDecryption.Name when b.Port == OpenPgpDecryption.Port => new OpenPgpDecryption(b, secrets),
        _ => throw new PortBindingException($"{b.Port} for {b.Tenant} names adapter {b.Adapter}, which this runtime does not build (Issue I-67)"),
    };
}
