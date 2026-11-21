using System.Security.Cryptography;
using System.Text;

namespace LiteBrowser;

/// <summary>
/// Проверка цифровой подписи CRX3 — так же, как это делает Chrome.
/// Подпись разработчика привязана к ID расширения: ID = первые 16 байт SHA-256 его открытого ключа.
/// Если архив подменили по дороге или магазин отдал другое расширение — проверка не пройдёт.
/// </summary>
static class CrxVerifier
{
    static readonly byte[] Prefix = Encoding.ASCII.GetBytes("CRX3 SignedData\0");

    public static void Verify(byte[] crx, string expectedId)
    {
        if (crx.Length < 12 || crx[0] != 'C' || crx[1] != 'r' || crx[2] != '2' || crx[3] != '4')
            throw new InvalidDataException("Файл расширения не в формате CRX");
        if (BitConverter.ToUInt32(crx, 4) != 3)
            throw new InvalidDataException("Устаревший формат CRX2 без надёжной подписи — установка отменена");
        int headerLen = checked((int)BitConverter.ToUInt32(crx, 8));
        if (12 + headerLen > crx.Length) throw new InvalidDataException("Повреждённый заголовок CRX");
        var header = new ReadOnlySpan<byte>(crx, 12, headerLen);
        var archive = new ReadOnlySpan<byte>(crx, 12 + headerLen, crx.Length - 12 - headerLen);

        var rsa = new List<(byte[] key, byte[] sig)>();
        var ecdsa = new List<(byte[] key, byte[] sig)>();
        byte[]? signedHeaderData = null;
        foreach (var (field, value) in Fields(header))
        {
            if (field == 2) rsa.Add(Proof(value));
            else if (field == 3) ecdsa.Add(Proof(value));
            else if (field == 10000) signedHeaderData = value;
        }
        if (signedHeaderData == null) throw new InvalidDataException("В CRX нет подписанных данных");

        byte[]? crxId = null;
        foreach (var (field, value) in Fields(signedHeaderData)) if (field == 1) crxId = value;
        if (crxId == null || crxId.Length != 16) throw new InvalidDataException("В CRX нет ID расширения");
        if (IdToString(crxId) != expectedId)
            throw new InvalidDataException("Магазин отдал другое расширение (ID не совпадает) — установка отменена");

        // Подписываемые данные: префикс + длина + signed_header_data + архив
        var payload = new byte[Prefix.Length + 4 + signedHeaderData.Length + archive.Length];
        Prefix.CopyTo(payload, 0);
        BitConverter.GetBytes((uint)signedHeaderData.Length).CopyTo(payload, Prefix.Length);
        signedHeaderData.CopyTo(payload, Prefix.Length + 4);
        archive.CopyTo(payload.AsSpan(Prefix.Length + 4 + signedHeaderData.Length));

        bool developerProof = false;
        foreach (var (key, sig) in rsa)
        {
            using var k = RSA.Create();
            k.ImportSubjectPublicKeyInfo(key, out _);
            if (!k.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("Подпись расширения не сходится — файл повреждён или подменён");
            if (SHA256.HashData(key).AsSpan(0, 16).SequenceEqual(crxId)) developerProof = true;
        }
        foreach (var (key, sig) in ecdsa)
        {
            using var k = ECDsa.Create();
            k.ImportSubjectPublicKeyInfo(key, out _);
            if (!k.VerifyData(payload, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new CryptographicException("Подпись расширения не сходится — файл повреждён или подменён");
            if (SHA256.HashData(key).AsSpan(0, 16).SequenceEqual(crxId)) developerProof = true;
        }
        if (!developerProof) throw new CryptographicException("Нет подписи разработчика расширения — установка отменена");
    }

    static string IdToString(byte[] id)
    {
        var sb = new StringBuilder(32);
        foreach (var b in id) { sb.Append((char)('a' + (b >> 4))); sb.Append((char)('a' + (b & 15))); }
        return sb.ToString();
    }

    static (byte[] key, byte[] sig) Proof(byte[] msg)
    {
        byte[]? key = null, sig = null;
        foreach (var (f, v) in Fields(msg)) { if (f == 1) key = v; else if (f == 2) sig = v; }
        if (key == null || sig == null) throw new InvalidDataException("Повреждённая подпись CRX");
        return (key, sig);
    }

    /// <summary>Минимальный разбор protobuf: только поля с длиной (wire type 2), остальные пропускаем.</summary>
    static List<(int field, byte[] value)> Fields(ReadOnlySpan<byte> data)
    {
        var res = new List<(int, byte[])>();
        int i = 0;
        while (i < data.Length)
        {
            ulong tag = Varint(data, ref i);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: Varint(data, ref i); break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                case 2:
                    int len = checked((int)Varint(data, ref i));
                    if (len < 0 || i + len > data.Length) throw new InvalidDataException("Повреждённый заголовок CRX");
                    res.Add((field, data.Slice(i, len).ToArray()));
                    i += len;
                    break;
                default: throw new InvalidDataException("Повреждённый заголовок CRX");
            }
        }
        return res;
    }

    static ulong Varint(ReadOnlySpan<byte> d, ref int i)
    {
        ulong v = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (i >= d.Length) throw new InvalidDataException("Повреждённый заголовок CRX");
            byte b = d[i++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
        }
        throw new InvalidDataException("Повреждённый заголовок CRX");
    }
}
