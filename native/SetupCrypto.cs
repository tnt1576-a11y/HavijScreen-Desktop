using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HavijScreen.Native;

internal sealed record CodeKeys(string Code, string Route, byte[] Key);
internal sealed record SetupEnvelope(int hs, string iv, string data);
internal static class SetupCrypto
{
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    const string Context = "havijscreen-native-v1";
    internal static string Id(int length = 16) => Base64(RandomNumberGenerator.GetBytes(length));
    internal static string Base64(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    internal static byte[] Decode(string text)
    {
        if (text.Length > 180000 || !Regex.IsMatch(text,"^[A-Za-z0-9_-]+$")) throw new InvalidOperationException("Invalid connection data.");
        return Convert.FromBase64String(text.Replace('-','+').Replace('_','/') + new string('=',(4-text.Length%4)%4));
    }
    internal static string NewCode() => string.Join('-',RandomNumberGenerator.GetBytes(16).Select(b => Alphabet[b & 31]).Chunk(4).Select(chars => new string(chars)));
    internal static CodeKeys Keys(string code)
    {
        string raw = string.Concat(code.ToUpperInvariant().Where(c => c != '-' && !char.IsWhiteSpace(c)));
        if (raw.Length != 16 || raw.Any(c => !Alphabet.Contains(c))) throw new InvalidOperationException("Enter the 16-character room code.");
        string route = "hsn1-room-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Context+":"+raw)).AsSpan(0,20)).ToLowerInvariant();
        return new(string.Join('-',raw.Chunk(4).Select(chars => new string(chars))),route,Derive(Encoding.UTF8.GetBytes(raw),"room"));
    }
    internal static string GuestRoute() => "hsn1-guest-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    static byte[] Derive(ReadOnlySpan<byte> material, string info)
    {
        byte[] result = new byte[32]; HKDF.DeriveKey(HashAlgorithmName.SHA256,material,result,Encoding.UTF8.GetBytes(Context),Encoding.UTF8.GetBytes(info)); return result;
    }
    internal static string PublicKey(ECDiffieHellman pair)
    {
        var parameters = pair.ExportParameters(false); byte[] bytes = new byte[65]; bytes[0] = 4;
        parameters.Q.X!.CopyTo(bytes,1); parameters.Q.Y!.CopyTo(bytes,33); return Base64(bytes);
    }
    internal static byte[] PairKey(ECDiffieHellman pair, string publicKey, string request)
    {
        if (publicKey.Length != 87 || !ValidId(request)) throw new InvalidOperationException("Invalid connection key.");
        byte[] raw = Decode(publicKey);
        if (raw.Length != 65 || raw[0] != 4) throw new InvalidOperationException("Invalid connection key.");
        using var other = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = raw.AsSpan(1,32).ToArray(), Y = raw.AsSpan(33,32).ToArray() } });
        byte[] secret = pair.DeriveRawSecretAgreement(other.PublicKey);
        try { return Derive(secret,"connection:"+request); } finally { CryptographicOperations.ZeroMemory(secret); }
    }
    internal static SetupEnvelope Seal(byte[] key, string src, string dst, object body)
    {
        var data = JsonSerializer.SerializeToElement(body);
        var values = data.EnumerateObject().ToDictionary(p => p.Name,p => (object?)p.Value);
        values["nonce"] = Id(); values["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(values);
        if (plain.Length > 130000) throw new InvalidOperationException("Connection message is too large.");
        byte[] iv = RandomNumberGenerator.GetBytes(12), encrypted = new byte[plain.Length+16];
        using var aes = new AesGcm(key,16);
        aes.Encrypt(iv,plain,encrypted.AsSpan(0,plain.Length),encrypted.AsSpan(plain.Length,16),Encoding.UTF8.GetBytes(Context+"|"+src+"|"+dst));
        return new(1,Base64(iv),Base64(encrypted));
    }
    internal static JsonElement Open(byte[] key, string src, string dst, JsonElement envelope)
    {
        if (envelope.Int("hs") != 1) throw new InvalidOperationException("Invalid connection envelope.");
        string ivText = envelope.Text("iv",16), dataText = envelope.Text("data",174000);
        if (ivText.Length != 16) throw new InvalidOperationException("Invalid connection envelope.");
        byte[] iv = Decode(ivText), data = Decode(dataText);
        if (data.Length < 16) throw new InvalidOperationException("Invalid connection envelope.");
        byte[] plain = new byte[data.Length-16];
        using var aes = new AesGcm(key,16);
        aes.Decrypt(iv,data.AsSpan(0,plain.Length),data.AsSpan(plain.Length),plain,Encoding.UTF8.GetBytes(Context+"|"+src+"|"+dst));
        using var parsed = JsonDocument.Parse(plain,new JsonDocumentOptions { MaxDepth = 24 }); var message = parsed.RootElement;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), at = message.Long("at");
        if (!ValidId(message.Text("nonce",22)) || at < now-120000 || at > now+120000) throw new InvalidOperationException("Stale connection message.");
        return message.Clone();
    }
    internal static bool ValidId(string text) => text.Length == 22 && Regex.IsMatch(text,"^[A-Za-z0-9_-]{22}$");
    internal static bool ValidName(string text) => text.Trim().Length is > 0 and <= 32 && !text.Any(char.IsControl);
}
internal static class JsonFields
{
    internal static string Text(this JsonElement item, string key, int limit = 128) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key,out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is string text && text.Length <= limit ? text : "";
    internal static int Int(this JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key,out var value) && value.TryGetInt32(out int result) ? result : 0;
    internal static long Long(this JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key,out var value) && value.TryGetInt64(out long result) ? result : 0;
    internal static bool Bool(this JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key,out var value) && value.ValueKind == JsonValueKind.True;
    internal static JsonElement Item(this JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key,out var value) ? value : default;
}
