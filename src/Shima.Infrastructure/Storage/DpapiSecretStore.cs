using System.Security.Cryptography;

namespace Shima.Infrastructure.Storage;

public interface ISecureSecretStore
{
    byte[] ProtectForCurrentUser(byte[] plaintext);

    byte[] UnprotectForCurrentUser(byte[] ciphertext);
}

public sealed class DpapiSecretStore : ISecureSecretStore
{
    public byte[] ProtectForCurrentUser(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    public byte[] UnprotectForCurrentUser(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }
}
