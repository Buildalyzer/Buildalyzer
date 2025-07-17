using System.Security.Cryptography;

namespace Buildalyzer;

/// <summary>Generates Project <see cref="Guid"/>'s.</summary>
/// <remarks>
/// See: https://datatracker.ietf.org/doc/html/rfc4122.
/// </remarks>
internal static class ProjectGuid
{
    /// <summary>The namespace for URL's.</summary>
    private static readonly Guid UrlNamespace = Guid.Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

    /// <summary>Generates a <see cref="Guid"/> based on the <see cref="SHA1"/> hash of the name.</summary>
    public static Guid Create(string? name)
    {
        byte[] guid = Hash(Encoding.UTF8.GetBytes(name ?? string.Empty));

        // set the four most significant bits (bits 12 through 15) of the time_hi_and_version field to the appropriate 4-bit version number from Section 4.1.3 (step 8)
        guid[6] = (byte)((guid[6] & 0x0F) | 0x50);

        // set the two most significant bits (bits 6 and 7) of the clock_seq_hi_and_reserved to zero and one, respectively (step 10)
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);

        // convert the resulting UUID to local byte order (step 13)
        SwapBytes(guid);
        return new Guid(guid);
    }

    /// <summary>Generates a <see cref="SHA1"/> hash.</summary>
    [Pure]
    private static byte[] Hash(byte[] bytes)
    {
       // convert the namespace UUID to network order (step 3)
        var ns = UrlNamespace.ToByteArray();
        SwapBytes(ns);

        // compute the hash of the name space ID concatenated with the name (step 4)
        using var sha1 = SHA1.Create();
        sha1.TransformBlock(ns, 0, ns.Length, null, 0);
        sha1.TransformFinalBlock(bytes, 0, bytes.Length);
        return sha1.Hash![..16];
    }

    /// <summary>Converts a GUID (expressed as a byte array) to/from network order (MSB-first).</summary>
    private static void SwapBytes(byte[] bytes)
    {
        Swap(bytes, 0, 3);
        Swap(bytes, 1, 2);
        Swap(bytes, 4, 5);
        Swap(bytes, 6, 7);

        static void Swap(byte[] bs, int l, int r) => (bs[r], bs[l]) = (bs[l], bs[r]);
    }
}
