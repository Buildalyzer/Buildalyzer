using System.Security.Cryptography;

namespace Buildalyzer.Differential.Tests;

/// <summary>
/// Generates a strong-name key (<c>.snk</c>) at test time so signing scenarios do not need a
/// checked-in key. The file is a CryptoAPI <c>PRIVATEKEYBLOB</c> — the format csc expects from
/// <c>/keyfile:</c> — built from a fresh RSA key.
/// </summary>
internal static class StrongNameKey
{
    public static void Write(string path)
    {
        using RSA rsa = RSA.Create(2048);
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: true);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        int bitLength = p.Modulus!.Length * 8;

        // PUBLICKEYSTRUC / BLOBHEADER
        writer.Write((byte)0x07);       // PRIVATEKEYBLOB
        writer.Write((byte)0x02);       // CUR_BLOB_VERSION
        writer.Write((ushort)0);        // reserved
        writer.Write((uint)0x00002400); // CALG_RSA_SIGN

        // RSAPUBKEY
        writer.Write((uint)0x32415352); // "RSA2"
        writer.Write((uint)bitLength);

        // Public exponent as a little-endian uint32.
        byte[] exponent = new byte[4];
        for (int i = 0; i < p.Exponent!.Length; i++)
        {
            exponent[i] = p.Exponent[p.Exponent.Length - 1 - i];
        }
        writer.Write(exponent);

        // The remaining components are stored little-endian (RSAParameters are big-endian).
        WriteReversed(writer, p.Modulus);
        WriteReversed(writer, p.P!);
        WriteReversed(writer, p.Q!);
        WriteReversed(writer, p.DP!);
        WriteReversed(writer, p.DQ!);
        WriteReversed(writer, p.InverseQ!);
        WriteReversed(writer, p.D!);

        writer.Flush();
        File.WriteAllBytes(path, stream.ToArray());
    }

    private static void WriteReversed(BinaryWriter writer, byte[] bigEndian)
    {
        for (int i = bigEndian.Length - 1; i >= 0; i--)
        {
            writer.Write(bigEndian[i]);
        }
    }
}
