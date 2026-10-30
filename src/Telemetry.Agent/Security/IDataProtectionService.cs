namespace Telemetry.Agent.Security;

public interface IDataProtectionService
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default);

    byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> associatedData = default);
}
