using System.Security.Cryptography;
using System.Text;
using Telemetry.Agent.Configuration;

namespace Telemetry.Agent.Security;

public sealed class AesGcmDataProtectionService : IDataProtectionService
{
    private const byte BlobVersion = 1;
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly byte[] ApplicationEntropy = Encoding.UTF8.GetBytes("EndpointTelemetry.Agent.MasterKey.v1");
    private static readonly object KeyFileLock = new();
    private readonly string _keyPath;

    public AesGcmDataProtectionService(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _keyPath = options.MasterKeyPath;
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var tag = new byte[TagLength];
        var ciphertext = new byte[plaintext.Length];

        using (var aes = new AesGcm(GetMasterKey(), TagLength))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        var blob = new byte[1 + NonceLength + TagLength + ciphertext.Length];
        blob[0] = BlobVersion;
        nonce.CopyTo(blob, 1);
        tag.CopyTo(blob, 1 + NonceLength);
        ciphertext.CopyTo(blob, 1 + NonceLength + TagLength);
        return blob;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> associatedData = default)
    {
        if (protectedData.Length < 1 + NonceLength + TagLength || protectedData[0] != BlobVersion)
        {
            throw new CryptographicException("Protected data has an invalid format.");
        }

        var nonce = protectedData.Slice(1, NonceLength);
        var tag = protectedData.Slice(1 + NonceLength, TagLength);
        var ciphertext = protectedData[(1 + NonceLength + TagLength)..];
        var plaintext = new byte[ciphertext.Length];

        using (var aes = new AesGcm(GetMasterKey(), TagLength))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }

        return plaintext;
    }

    private byte[] GetMasterKey()
    {
        lock (KeyFileLock)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_keyPath))!;
            Directory.CreateDirectory(directory);

            if (File.Exists(_keyPath))
            {
                return UnwrapMasterKey(File.ReadAllBytes(_keyPath));
            }

            var masterKey = RandomNumberGenerator.GetBytes(KeyLength);
            var wrappedKey = WrapMasterKey(masterKey);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_keyPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(wrappedKey);
                    stream.Flush(flushToDisk: true);
                }

                SetPrivateFilePermissions(temporaryPath);

                try
                {
                    File.Move(temporaryPath, _keyPath);
                }
                catch (IOException) when (File.Exists(_keyPath))
                {
                    File.Delete(temporaryPath);
                    CryptographicOperations.ZeroMemory(masterKey);
                    return UnwrapMasterKey(File.ReadAllBytes(_keyPath));
                }

                return masterKey;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(masterKey);
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                throw;
            }
        }
    }

    private static byte[] WrapMasterKey(ReadOnlySpan<byte> masterKey)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Protect(masterKey.ToArray(), ApplicationEntropy, DataProtectionScope.LocalMachine);
        }

        var wrappingKey = DeriveWrappingKey();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var tag = new byte[TagLength];
            var ciphertext = new byte[masterKey.Length];
            using (var aes = new AesGcm(wrappingKey, TagLength))
            {
                aes.Encrypt(nonce, masterKey, ciphertext, tag, ApplicationEntropy);
            }

            var wrapped = new byte[1 + NonceLength + TagLength + ciphertext.Length];
            wrapped[0] = BlobVersion;
            nonce.CopyTo(wrapped, 1);
            tag.CopyTo(wrapped, 1 + NonceLength);
            ciphertext.CopyTo(wrapped, 1 + NonceLength + TagLength);
            return wrapped;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    private static byte[] UnwrapMasterKey(ReadOnlySpan<byte> wrappedKey)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Unprotect(wrappedKey.ToArray(), ApplicationEntropy, DataProtectionScope.LocalMachine);
        }

        if (wrappedKey.Length != 1 + NonceLength + TagLength + KeyLength || wrappedKey[0] != BlobVersion)
        {
            throw new CryptographicException("The protected master key has an invalid format.");
        }

        var wrappingKey = DeriveWrappingKey();
        try
        {
            var masterKey = new byte[KeyLength];
            using (var aes = new AesGcm(wrappingKey, TagLength))
            {
                aes.Decrypt(
                    wrappedKey.Slice(1, NonceLength),
                    wrappedKey[(1 + NonceLength + TagLength)..],
                    wrappedKey.Slice(1 + NonceLength, TagLength),
                    masterKey,
                    ApplicationEntropy);
            }

            return masterKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    private static byte[] DeriveWrappingKey()
    {
        var machineIdentity = Encoding.UTF8.GetBytes(Environment.MachineName);
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            machineIdentity,
            KeyLength,
            ApplicationEntropy,
            Encoding.UTF8.GetBytes("EndpointTelemetry.Agent.LinuxMasterKey.v1"));
    }

    private static void SetPrivateFilePermissions(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
