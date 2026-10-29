using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>servers.dat 中的一项服务器。</summary>
public sealed class ServerEntry
{
    public string Name = "";
    public string Ip = "";
    /// <summary>服务器图标（base64 文本，可缺省）。</summary>
    public string Icon = "";
    /// <summary>是否接受服务器资源包（可缺省，默认 false）。</summary>
    public bool AcceptTextures;
}

/// <summary>服务器状态查询结果。</summary>
public sealed record ServerStatus(bool Online, string Version, int OnlinePlayers, int MaxPlayers, string Motd);

/// <summary>服务器列表（servers.dat）：未压缩 NBT 读写 + MC 服务器状态协议 Ping。
/// servers.dat 是未压缩的 NBT（根 Compound { servers: List&lt;Compound&gt; }），
/// 与 level.dat 的 gzip 格式不同，所以这里自带最小读写实现（大端序）。</summary>
public static class Servers
{
    public static string FilePath(string gameDir) => Path.Combine(gameDir, "servers.dat");

    // ========== servers.dat 读写 ==========

    /// <summary>读取服务器列表；文件不存在或损坏时返回空列表，不抛异常。</summary>
    public static List<ServerEntry> Load(string gameDir)
    {
        var list = new List<ServerEntry>();
        string path = FilePath(gameDir);
        if (!File.Exists(path)) return list;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            var root = ReadRoot(reader);
            if (root == null || root.Type != NbtType.Compound) return list;
            var servers = root["servers"];
            if (servers == null || servers.Type != NbtType.List) return list;
            foreach (var item in servers.Children)
            {
                if (item.Type != NbtType.Compound) continue;
                var entry = new ServerEntry
                {
                    Name = item["name"]?.GetString() ?? "",
                    Ip = item["ip"]?.GetString() ?? "",
                    Icon = item["icon"]?.GetString() ?? "",
                    AcceptTextures = item["acceptTextures"]?.GetBool() ?? false,
                };
                if (entry.Ip.Length == 0) continue;
                if (entry.Name.Length == 0) entry.Name = entry.Ip;
                list.Add(entry);
            }
        }
        catch
        {
            // 损坏的 servers.dat 当作空列表，避免整个页面打不开
        }
        return list;
    }

    /// <summary>写入服务器列表（先写 .tmp 再替换，避免写一半损坏原文件）。</summary>
    public static void Save(string gameDir, List<ServerEntry> list)
    {
        string path = FilePath(gameDir);
        Directory.CreateDirectory(gameDir);
        string tmp = path + ".tmp";

        var root = new NbtTag(NbtType.Compound, "");
        root.Value = new List<NbtTag>();
        var servers = new NbtTag(NbtType.List, "servers");
        servers.Value = new List<NbtTag>();
        servers.ListElementType = NbtType.Compound;

        foreach (var entry in list)
        {
            var item = new NbtTag(NbtType.Compound, "");
            item.Value = new List<NbtTag>();
            item.Children.Add(NbtTag.StringTag("name", entry.Name));
            item.Children.Add(NbtTag.StringTag("ip", entry.Ip));
            if (entry.Icon.Length > 0) item.Children.Add(NbtTag.StringTag("icon", entry.Icon));
            if (entry.AcceptTextures) item.Children.Add(NbtTag.ByteTag("acceptTextures", 1));
            servers.Children.Add(item);
        }
        root.Children.Add(servers);

        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((byte)root.Type);
            WriteString(writer, root.Name);
            WritePayload(writer, root);
        }
        File.Move(tmp, path, true);
    }

    private static NbtTag? ReadRoot(BinaryReader reader)
    {
        var type = (NbtType)reader.ReadByte();
        if (type == NbtType.End) return null;
        string name = ReadString(reader);
        return ReadPayload(reader, type, name);
    }

    private static NbtTag ReadPayload(BinaryReader reader, NbtType type, string name)
    {
        switch (type)
        {
            case NbtType.Byte:
                return new NbtTag(type, name, reader.ReadByte());
            case NbtType.String:
                return new NbtTag(type, name, ReadString(reader));
            case NbtType.List:
            {
                var elementType = (NbtType)reader.ReadByte();
                int count = ReadInt(reader);
                if (count < 0 || count > 100000) throw new InvalidDataException("servers.dat 列表长度非法");
                var items = new List<NbtTag>(count);
                for (int i = 0; i < count; i++)
                {
                    items.Add(ReadPayload(reader, elementType, ""));
                }
                return new NbtTag(type, name, items) { ListElementType = elementType };
            }
            case NbtType.Compound:
            {
                var children = new List<NbtTag>();
                while (true)
                {
                    var childType = (NbtType)reader.ReadByte();
                    if (childType == NbtType.End) break;
                    string childName = ReadString(reader);
                    children.Add(ReadPayload(reader, childType, childName));
                }
                return new NbtTag(type, name, children);
            }
            default:
                // servers.dat 只用到 Byte/String/List/Compound；其它类型视为损坏
                throw new InvalidDataException($"servers.dat 含不支持的 NBT 类型: {type}");
        }
    }

    private static void WritePayload(BinaryWriter writer, NbtTag tag)
    {
        switch (tag.Type)
        {
            case NbtType.Byte:
                writer.Write(Convert.ToByte(tag.Value ?? (byte)0));
                break;
            case NbtType.String:
                WriteString(writer, tag.GetString());
                break;
            case NbtType.List:
            {
                var items = tag.Children;
                var elementType = tag.ListElementType;
                if (elementType == NbtType.End && items.Count > 0) elementType = items[0].Type;
                writer.Write((byte)elementType);
                WriteInt(writer, items.Count);
                foreach (var item in items) WritePayload(writer, item);
                break;
            }
            case NbtType.Compound:
                foreach (var child in tag.Children)
                {
                    writer.Write((byte)child.Type);
                    WriteString(writer, child.Name);
                    WritePayload(writer, child);
                }
                writer.Write((byte)NbtType.End);
                break;
            default:
                throw new InvalidDataException($"无法写入 servers.dat 的 NBT 类型: {tag.Type}");
        }
    }

    private static void WriteInt(BinaryWriter writer, int value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static int ReadInt(BinaryReader reader) =>
        (reader.ReadByte() << 24) | (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();

    private static void WriteString(BinaryWriter writer, string value)
    {
        // 中文等 BMP 字符的标准 UTF-8 与 Java modified UTF-8 一致，常规服务器名足够用
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("servers.dat 字符串过长");
        writer.Write((byte)(bytes.Length >> 8));
        writer.Write((byte)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = (reader.ReadByte() << 8) | reader.ReadByte();
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    // ========== 服务器状态 Ping（MC 服务器列表协议） ==========

    /// <summary>查询服务器状态；连接失败或超时返回 Online=false，不抛异常。</summary>
    public static async Task<ServerStatus?> PingAsync(string ip, int timeoutMs = 3000)
    {
        var (host, port) = SplitAddress(ip);
        if (host.Length == 0) return Offline();

        using var cts = new CancellationTokenSource(Math.Max(timeoutMs, 300));
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            await client.ConnectAsync(host, port).WaitAsync(cts.Token);
            using var stream = client.GetStream();
            await WriteHandshakeAsync(stream, host, port, cts.Token);
            return ParseStatus(await ReadStatusJsonAsync(stream, cts.Token));
        }
        catch
        {
            // 超时 / 拒绝连接 / DNS 失败 / JSON 损坏都按离线处理
            return Offline();
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static ServerStatus Offline() => new(false, "", 0, 0, "");

    /// <summary>拆分 host:port；没有冒号时默认 25565。</summary>
    private static (string Host, int Port) SplitAddress(string address)
    {
        string text = (address ?? "").Trim();
        if (text.Length == 0) return ("", 25565);
        int colon = text.LastIndexOf(':');
        if (colon > 0 && colon == text.IndexOf(':') && colon < text.Length - 1)
        {
            if (int.TryParse(text[(colon + 1)..], out int port) && port > 0 && port <= 65535)
            {
                return (text[..colon], port);
            }
        }
        return (text, 25565);
    }

    private static async Task WriteHandshakeAsync(NetworkStream stream, string host, int port, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x00);              // 包 ID：Handshake
        WriteVarInt(ms, -1);             // 协议版本：-1 = 未指定（状态查询服务端接受）
        var hostBytes = Encoding.UTF8.GetBytes(host);
        WriteVarInt(ms, hostBytes.Length);
        ms.Write(hostBytes, 0, hostBytes.Length);
        ms.WriteByte((byte)(port >> 8));
        ms.WriteByte((byte)(port & 0xFF));
        WriteVarInt(ms, 1);              // nextState = 1（status）
        await WritePacketAsync(stream, ms.ToArray(), ct);

        // Status Request：空包（只有包 ID 0x00）
        await WritePacketAsync(stream, new byte[] { 0x00 }, ct);
    }

    private static async Task WritePacketAsync(NetworkStream stream, byte[] body, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WriteVarInt(ms, body.Length);
        ms.Write(body, 0, body.Length);
        await stream.WriteAsync(ms.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>读 Status Response：VarInt 长度 + 包 ID + VarInt 字符串长度 + JSON。</summary>
    private static async Task<string> ReadStatusJsonAsync(NetworkStream stream, CancellationToken ct)
    {
        int length = await ReadVarIntAsync(stream, ct);
        if (length <= 0 || length > 1 << 20) throw new InvalidDataException("服务器状态响应长度非法");
        var buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, length - read), ct);
            if (n <= 0) throw new IOException("服务器提前关闭了连接");
            read += n;
        }
        int pos = 0;
        ReadVarIntFromBuffer(buffer, ref pos); // 包 ID（0x00）
        int strLen = ReadVarIntFromBuffer(buffer, ref pos);
        int take = Math.Clamp(strLen, 0, buffer.Length - pos);
        return Encoding.UTF8.GetString(buffer, pos, take);
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken ct)
    {
        int result = 0;
        int shift = 0;
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one, ct);
            if (n <= 0) throw new IOException("服务器提前关闭了连接");
            int b = one[0];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("服务器返回的 VarInt 过长");
        }
    }

    private static int ReadVarIntFromBuffer(byte[] buffer, ref int pos)
    {
        int result = 0;
        int shift = 0;
        while (pos < buffer.Length)
        {
            int b = buffer[pos++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("服务器返回的 VarInt 过长");
        }
        throw new InvalidDataException("服务器状态响应不完整");
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        while (true)
        {
            if ((value & ~0x7F) == 0)
            {
                stream.WriteByte((byte)value);
                return;
            }
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value = (int)((uint)value >> 7);
        }
    }

    /// <summary>解析状态 JSON：版本名、在线人数、上限、MOTD 纯文本。</summary>
    private static ServerStatus ParseStatus(string json)
    {
        var obj = JsonNode.Parse(json)?.AsObject();
        string version = obj?["version"]?["name"]?.GetValue<string>() ?? "";
        int online = obj?["players"]?["online"]?.GetValue<int>() ?? 0;
        int max = obj?["players"]?["max"]?.GetValue<int>() ?? 0;
        string motd = TextOf(obj?["description"]);
        return new ServerStatus(true, version, online, max, motd);
    }

    /// <summary>把 description 拼成纯文本：支持字符串、{text:...}、{extra:[...]}、{translate:...}。</summary>
    private static string TextOf(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue value && value.TryGetValue<string>(out var plain)) return plain;
        if (node is JsonObject obj)
        {
            var sb = new StringBuilder();
            if (obj["text"] is JsonNode text) sb.Append(TextOf(text));
            if (obj["translate"] is JsonValue key && key.TryGetValue<string>(out var keyText)) sb.Append(keyText);
            if (obj["extra"] is JsonArray extra)
            {
                foreach (var item in extra) sb.Append(TextOf(item));
            }
            return sb.ToString();
        }
        return "";
    }
}
