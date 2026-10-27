using System.IO.Compression;
using System.Text;

namespace WinUI3Shell.Services.Mc;

/// <summary>NBT 标签类型（数值与 Java 版一致）。</summary>
public enum NbtType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12,
}

/// <summary>最小 NBT 树：保留完整结构与顺序，可无损读写 level.dat。
/// 字符串按 Java 的 modified UTF-8 编解码（中文等 BMP 字符与标准 UTF-8 一致）。</summary>
public sealed class NbtTag
{
    public NbtType Type { get; }
    public string Name { get; set; }
    /// <summary>基本类型装箱值 / byte[] / int[] / long[] / List&lt;NbtTag&gt;（Compound 与 List 共用）。</summary>
    public object? Value { get; set; }
    /// <summary>List 的元素类型；空列表写入时依赖它，读取时保存。</summary>
    public NbtType ListElementType { get; set; }

    public NbtTag(NbtType type, string name, object? value = null)
    {
        Type = type;
        Name = name;
        Value = value;
    }

    /// <summary>Compound 的子标签列表。</summary>
    public List<NbtTag> Children => (List<NbtTag>)Value!;

    /// <summary>按名字取 Compound 子标签，不存在返回 null。</summary>
    public NbtTag? this[string name]
    {
        get
        {
            if (Type != NbtType.Compound) return null;
            foreach (var child in Children)
            {
                if (child.Name == name) return child;
            }
            return null;
        }
    }

    /// <summary>按名字替换/追加子标签（同名保留原位置）。</summary>
    public void SetChild(NbtTag child)
    {
        var children = Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i].Name == child.Name)
            {
                children[i] = child;
                return;
            }
        }
        children.Add(child);
    }

    /// <summary>取 Compound 子标签，不存在或类型不对时新建并写入。</summary>
    public NbtTag GetOrAddCompound(string name)
    {
        var existing = this[name];
        if (existing is { Type: NbtType.Compound }) return existing;
        var created = new NbtTag(NbtType.Compound, name, new List<NbtTag>());
        SetChild(created);
        return created;
    }

    public long GetLong() => Value switch
    {
        byte b => b,
        short s => s,
        int i => i,
        long l => l,
        float f => (long)f,
        double d => (long)d,
        _ => 0,
    };

    public int GetInt() => (int)GetLong();
    public bool GetBool() => GetLong() != 0;
    public string GetString() => Value as string ?? "";

    public static NbtTag ByteTag(string name, byte value) => new(NbtType.Byte, name, value);
    public static NbtTag IntTag(string name, int value) => new(NbtType.Int, name, value);
    public static NbtTag StringTag(string name, string value) => new(NbtType.String, name, value);
}

/// <summary>level.dat 读写：gzip 压缩的 NBT，Big-Endian。</summary>
public static class NbtFile
{
    public static NbtTag Read(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);
        var type = (NbtType)reader.ReadByte();
        if (type == NbtType.End) throw new InvalidDataException("NBT 根标签为空");
        string name = ReadString(reader);
        return ReadPayload(reader, type, name);
    }

    public static void Write(string path, NbtTag root)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new BinaryWriter(gzip);
        writer.Write((byte)root.Type);
        WriteString(writer, root.Name);
        WritePayload(writer, root);
    }

    // ========== 读取 ==========

    private static NbtTag ReadPayload(BinaryReader reader, NbtType type, string name)
    {
        switch (type)
        {
            case NbtType.Byte:
                return new NbtTag(type, name, reader.ReadByte());
            case NbtType.Short:
                return new NbtTag(type, name, (short)ReadUShort(reader));
            case NbtType.Int:
                return new NbtTag(type, name, ReadInt(reader));
            case NbtType.Long:
                return new NbtTag(type, name, ReadLong(reader));
            case NbtType.Float:
                return new NbtTag(type, name, ReadFloat(reader));
            case NbtType.Double:
                return new NbtTag(type, name, ReadDouble(reader));
            case NbtType.ByteArray:
                return new NbtTag(type, name, reader.ReadBytes(ReadLength(reader)));
            case NbtType.String:
                return new NbtTag(type, name, ReadString(reader));
            case NbtType.List:
            {
                var elementType = (NbtType)reader.ReadByte();
                int count = ReadLength(reader);
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
            case NbtType.IntArray:
            {
                int count = ReadLength(reader);
                var values = new int[count];
                for (int i = 0; i < count; i++) values[i] = ReadInt(reader);
                return new NbtTag(type, name, values);
            }
            case NbtType.LongArray:
            {
                int count = ReadLength(reader);
                var values = new long[count];
                for (int i = 0; i < count; i++) values[i] = ReadLong(reader);
                return new NbtTag(type, name, values);
            }
            default:
                throw new InvalidDataException($"未知 NBT 类型: {type}");
        }
    }

    private static int ReadLength(BinaryReader reader)
    {
        int length = ReadInt(reader);
        if (length < 0) throw new InvalidDataException("NBT 长度非法");
        return length;
    }

    private static ushort ReadUShort(BinaryReader reader) =>
        (ushort)((reader.ReadByte() << 8) | reader.ReadByte());

    private static int ReadInt(BinaryReader reader) =>
        (reader.ReadByte() << 24) | (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();

    private static long ReadLong(BinaryReader reader)
    {
        long value = 0;
        for (int i = 0; i < 8; i++) value = (value << 8) | reader.ReadByte();
        return value;
    }

    private static float ReadFloat(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        Array.Reverse(bytes);
        return BitConverter.ToSingle(bytes, 0);
    }

    private static double ReadDouble(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(8);
        Array.Reverse(bytes);
        return BitConverter.ToDouble(bytes, 0);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = ReadUShort(reader);
        return DecodeModifiedUtf8(reader.ReadBytes(length));
    }

    // ========== 写入 ==========

    private static void WritePayload(BinaryWriter writer, NbtTag tag)
    {
        switch (tag.Type)
        {
            case NbtType.Byte:
                writer.Write(Convert.ToByte(tag.Value ?? (byte)0));
                break;
            case NbtType.Short:
                WriteUShort(writer, (ushort)Convert.ToInt16(tag.Value ?? (short)0));
                break;
            case NbtType.Int:
                WriteInt(writer, Convert.ToInt32(tag.Value ?? 0));
                break;
            case NbtType.Long:
                WriteLong(writer, Convert.ToInt64(tag.Value ?? 0L));
                break;
            case NbtType.Float:
                WriteFloat(writer, Convert.ToSingle(tag.Value ?? 0f));
                break;
            case NbtType.Double:
                WriteDouble(writer, Convert.ToDouble(tag.Value ?? 0d));
                break;
            case NbtType.ByteArray:
            {
                var bytes = (byte[])(tag.Value ?? Array.Empty<byte>());
                WriteInt(writer, bytes.Length);
                writer.Write(bytes);
                break;
            }
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
            case NbtType.IntArray:
            {
                var values = (int[])(tag.Value ?? Array.Empty<int>());
                WriteInt(writer, values.Length);
                foreach (int value in values) WriteInt(writer, value);
                break;
            }
            case NbtType.LongArray:
            {
                var values = (long[])(tag.Value ?? Array.Empty<long>());
                WriteInt(writer, values.Length);
                foreach (long value in values) WriteLong(writer, value);
                break;
            }
            default:
                throw new InvalidDataException($"无法写入 NBT 类型: {tag.Type}");
        }
    }

    private static void WriteUShort(BinaryWriter writer, ushort value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteInt(BinaryWriter writer, int value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteLong(BinaryWriter writer, long value)
    {
        for (int shift = 56; shift >= 0; shift -= 8) writer.Write((byte)(value >> shift));
    }

    private static void WriteFloat(BinaryWriter writer, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        Array.Reverse(bytes);
        writer.Write(bytes);
    }

    private static void WriteDouble(BinaryWriter writer, double value)
    {
        var bytes = BitConverter.GetBytes(value);
        Array.Reverse(bytes);
        writer.Write(bytes);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = EncodeModifiedUtf8(value);
        if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("NBT 字符串过长");
        WriteUShort(writer, (ushort)bytes.Length);
        writer.Write(bytes);
    }

    // ========== Java modified UTF-8 ==========

    private static string DecodeModifiedUtf8(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        int i = 0;
        while (i < bytes.Length)
        {
            int b = bytes[i++];
            if (b < 0x80)
            {
                builder.Append((char)b);
            }
            else if ((b & 0xE0) == 0xC0)
            {
                if (i >= bytes.Length) break;
                int b2 = bytes[i++];
                builder.Append((char)(((b & 0x1F) << 6) | (b2 & 0x3F)));
            }
            else if ((b & 0xF0) == 0xE0)
            {
                if (i + 1 >= bytes.Length) break;
                int b2 = bytes[i++];
                int b3 = bytes[i++];
                builder.Append((char)(((b & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F)));
            }
            else if ((b & 0xF8) == 0xF0)
            {
                // 少数工具按标准 UTF-8 写入的 4 字节补充字符，也兼容读取
                if (i + 2 >= bytes.Length) break;
                int b2 = bytes[i++];
                int b3 = bytes[i++];
                int b4 = bytes[i++];
                int codePoint = ((b & 0x07) << 18) | ((b2 & 0x3F) << 12) | ((b3 & 0x3F) << 6) | (b4 & 0x3F);
                if (codePoint is > 0xFFFF and <= 0x10FFFF)
                {
                    builder.Append(char.ConvertFromUtf32(codePoint));
                }
                else
                {
                    builder.Append('\uFFFD');
                }
            }
            else
            {
                builder.Append('\uFFFD');
            }
        }
        return builder.ToString();
    }

    private static byte[] EncodeModifiedUtf8(string value)
    {
        using var stream = new MemoryStream(value.Length * 2);
        foreach (char c in value)
        {
            if (c == '\0')
            {
                stream.WriteByte(0xC0);
                stream.WriteByte(0x80);
            }
            else if (c < 0x80)
            {
                stream.WriteByte((byte)c);
            }
            else if (c < 0x800)
            {
                stream.WriteByte((byte)(0xC0 | (c >> 6)));
                stream.WriteByte((byte)(0x80 | (c & 0x3F)));
            }
            else
            {
                stream.WriteByte((byte)(0xE0 | (c >> 12)));
                stream.WriteByte((byte)(0x80 | ((c >> 6) & 0x3F)));
                stream.WriteByte((byte)(0x80 | (c & 0x3F)));
            }
        }
        return stream.ToArray();
    }
}
