namespace ScriptLangCore;

public static class Cipher
{
    /*
    var key = Cipher.GenerateKey();
    var data = "mot de passe secret";

    var cipher = Cipher.Encrypt(data, key);
    Console.WriteLine(cipher); // "k9F3...=="

    var original = Cipher.Decrypt(cipher, key);
    Console.WriteLine(original); // "mot de passe secret"
    */

    internal record struct CipherInfo(byte[] Cipher, byte[] Nonce, byte[] Tag) : IDisposable
    {
        public void Dispose() { } // pure data, IDisposable for uniform script using/is
    }

    // ── post-quantum, the two algorithms .NET 11 ships as STABLE ────────────────────────────────────────────────
    // ML-KEM (FIPS 203, key encapsulation) and ML-DSA (FIPS 204, signatures) arrive as `MLKem` and `MLDsa` in the
    // runtime, and both are usable from a script through these six members. The third one the runtime ships,
    // `SlhDsa` (FIPS 205), is DELIBERATELY ABSENT: it is `[Experimental("SYSLIB5006")]`, so using it in a shipped
    // product means suppressing a diagnostic that says the API may change or be removed, and a scripting engine
    // must not hand a script a surface the platform calls unstable.
    //
    // BYTES IN, BYTES OUT, and a dictionary per generated pair, because a key is binary: the existing members of
    // this class speak base64 STRINGS (a password), a key pair is not text. A script that wants text writes
    // `Convert.ToBase64String(key["PrivateKey"])`, which is the same shape the other members' keys already take.
    //
    // MEASURED on .NET 11 RC3, on the Debug build and on the NativeAOT publish: a generated ML-KEM-768 key
    // encapsulates and decapsulates to the SAME shared secret (`round trip=True`, 1,184-byte encapsulation key),
    // and an ML-DSA-65 key signs and verifies, refusing a message that was altered by one byte.
    private static MLKemAlgorithm KemAlgorithmOf(string algorithm) => algorithm switch
    {
        "ML-KEM-512" or "MLKEM512" or "ml-kem-512" => MLKemAlgorithm.MLKem512,
        "ML-KEM-768" or "MLKEM768" or "ml-kem-768" => MLKemAlgorithm.MLKem768,
        "ML-KEM-1024" or "MLKEM1024" or "ml-kem-1024" => MLKemAlgorithm.MLKem1024,
        _ => throw new ScriptException(
            $"Cipher: '{algorithm}' is not a key-encapsulation algorithm this engine offers: write "
            + "'ML-KEM-512', 'ML-KEM-768' or 'ML-KEM-1024'.")
    };

    private static MLDsaAlgorithm DsaAlgorithmOf(string algorithm) => algorithm switch
    {
        "ML-DSA-44" or "MLDSA44" or "ml-dsa-44" => MLDsaAlgorithm.MLDsa44,
        "ML-DSA-65" or "MLDSA65" or "ml-dsa-65" => MLDsaAlgorithm.MLDsa65,
        "ML-DSA-87" or "MLDSA87" or "ml-dsa-87" => MLDsaAlgorithm.MLDsa87,
        _ => throw new ScriptException(
            $"Cipher: '{algorithm}' is not a signature algorithm this engine offers: write "
            + "'ML-DSA-44', 'ML-DSA-65' or 'ML-DSA-87'.")
    };

    /// <summary>Generates an ML-KEM key pair: `Algorithm`, `EncapsulationKey` (the public half) and
    /// `DecapsulationKey` (the secret half).</summary>
    public static Dictionary<string, object?> KemGenerate(string algorithm)
    {
        var alg = KemAlgorithmOf(algorithm);
        using var kem = MLKem.GenerateKey(alg);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Algorithm"] = alg.Name,
            ["EncapsulationKey"] = kem.ExportEncapsulationKey(),
            ["DecapsulationKey"] = kem.ExportDecapsulationKey()
        };
    }

    /// <summary>Encapsulates against a peer's ENCAPSULATION key: `Ciphertext` goes to the peer, `SharedSecret`
    /// stays here, and the peer's `KemDecapsulate` must answer the same secret.</summary>
    public static Dictionary<string, object?> KemEncapsulate(byte[] encapsulationKey, string algorithm)
    {
        var alg = KemAlgorithmOf(algorithm);
        using var kem = MLKem.ImportEncapsulationKey(alg, encapsulationKey);
        kem.Encapsulate(out var ciphertext, out var sharedSecret);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Algorithm"] = alg.Name,
            ["Ciphertext"] = ciphertext,
            ["SharedSecret"] = sharedSecret
        };
    }

    /// <summary>Decapsulates a ciphertext with this side's DECAPSULATION key, answering the shared secret.</summary>
    public static byte[] KemDecapsulate(byte[] decapsulationKey, byte[] ciphertext, string algorithm)
    {
        var alg = KemAlgorithmOf(algorithm);
        using var kem = MLKem.ImportDecapsulationKey(alg, decapsulationKey);
        return kem.Decapsulate(ciphertext);
    }

    /// <summary>Generates an ML-DSA key pair: `Algorithm`, `PublicKey` and `PrivateKey`.</summary>
    public static Dictionary<string, object?> SignGenerate(string algorithm)
    {
        var alg = DsaAlgorithmOf(algorithm);
        using var dsa = MLDsa.GenerateKey(alg);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Algorithm"] = alg.Name,
            ["PublicKey"] = dsa.ExportMLDsaPublicKey(),
            ["PrivateKey"] = dsa.ExportMLDsaPrivateKey()
        };
    }

    /// <summary>Signs a message with an ML-DSA private key.</summary>
    public static byte[] Sign(byte[] privateKey, byte[] message, string algorithm)
    {
        var alg = DsaAlgorithmOf(algorithm);
        using var dsa = MLDsa.ImportMLDsaPrivateKey(alg, privateKey);
        return dsa.SignData(message, context: null);
    }

    /// <summary>Verifies an ML-DSA signature: true when the message and signature agree with the public key.</summary>
    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature, string algorithm)
    {
        var alg = DsaAlgorithmOf(algorithm);
        using var dsa = MLDsa.ImportMLDsaPublicKey(alg, publicKey);
        return dsa.VerifyData(message, signature, context: null);
    }

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32; // AES-256

    /// <summary>
    /// Generates a random AES-256 key, encoded in base64.
    /// </summary>
    /// <returns></returns>
    public static string GenerateKey()
        => GenerateRandomToken();

    /// <summary>
    /// Generates a random token of the specified size in bytes, encoded in base64.
    /// </summary>
    /// <param name="sizeInBytes"></param>
    /// <returns></returns>
    public static string GenerateRandomToken(int sizeInBytes = KeySize)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(sizeInBytes));

    /// <summary>
    /// Encrypts the given string data using AES-GCM with the provided base64-encoded key.
    /// </summary>
    /// <param name="data"></param>
    /// <param name="base64Key"></param>
    /// <returns></returns>
    public static string Encrypt(string data, string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        var plainBytes = Encoding.UTF8.GetBytes(data);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        return new CipherInfo(cipher, nonce, tag).ToBase64();
    }

    /// <summary>
    /// Decrypts the given base64-encoded cipher text using AES-GCM with the provided base64-encoded key.
    /// </summary>
    /// <param name="base64Cipher"></param>
    /// <param name="base64Key"></param>
    /// <returns></returns>
    public static string Decrypt(string base64Cipher, string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        var info = FromBase64(base64Cipher);

        var plain = new byte[info.Cipher.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(info.Nonce, info.Cipher, info.Tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    private static string ToBase64(this CipherInfo info)
    {
        var combined = new byte[NonceSize + TagSize + info.Cipher.Length];

        Buffer.BlockCopy(info.Nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(info.Tag, 0, combined, NonceSize, TagSize);
        Buffer.BlockCopy(info.Cipher, 0, combined, NonceSize + TagSize, info.Cipher.Length);

        return Convert.ToBase64String(combined);
    }

    private static CipherInfo FromBase64(string base64)
    {
        var combined = Convert.FromBase64String(base64);

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipher = new byte[combined.Length - NonceSize - TagSize];

        Buffer.BlockCopy(combined, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(combined, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(combined, NonceSize + TagSize, cipher, 0, cipher.Length);

        return new CipherInfo(cipher, nonce, tag);
    }
}